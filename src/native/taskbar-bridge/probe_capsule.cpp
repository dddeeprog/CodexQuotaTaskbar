#include "probe_capsule.h"
#include "probe_safety.h"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#ifdef GetCurrentTime
#undef GetCurrentTime
#endif
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <unordered_map>
#include <utility>
#include <vector>

namespace cq::bridge {
namespace wux = winrt::Windows::UI::Xaml;
namespace wuxc = winrt::Windows::UI::Xaml::Controls;
namespace wuxm = winrt::Windows::UI::Xaml::Media;

bool run_authorized_capsule_insertion(
    const std::shared_ptr<CleanupAuthorityGate>& authority,
    AuthorizedCapsuleInsertion insertion,
    void* context) noexcept {
  if (!authority || !insertion) {
    return false;
  }
  auto mutation_authority = authority->try_acquire_mutation();
  return mutation_authority &&
         insertion(context, std::move(*mutation_authority));
}

struct ProbeCapsuleManager::Impl final
    : std::enable_shared_from_this<ProbeCapsuleManager::Impl> {
  enum class ReleasePhase : std::uint8_t {
    open,
    claimed,
    released,
  };

  class OperationLease final {
   public:
    OperationLease() noexcept = default;
    OperationLease(const OperationLease&) = delete;
    OperationLease& operator=(const OperationLease&) = delete;
    OperationLease(OperationLease&& other) noexcept
        : owner_(std::move(other.owner_)) {}
    OperationLease& operator=(OperationLease&& other) noexcept {
      if (this != &other) {
        release();
        owner_ = std::move(other.owner_);
      }
      return *this;
    }
    ~OperationLease() { release(); }

   private:
    friend struct Impl;
    explicit OperationLease(std::shared_ptr<Impl> owner) noexcept
        : owner_(std::move(owner)) {}
    void release() noexcept {
      if (!owner_) {
        return;
      }
      auto owner = std::move(owner_);
      std::lock_guard lock(owner->mutex);
      if (!owner->active_operations) {
        owner->retention_fault.store(true, std::memory_order_release);
        return;
      }
      --owner->active_operations;
    }

    std::shared_ptr<Impl> owner_;
  };

  struct ChildColumn final {
    wux::FrameworkElement element{nullptr};
    int column = 0;
  };

  struct GridSnapshot final {
    std::vector<wuxc::ColumnDefinition> columns;
    std::vector<ChildColumn> child_columns;

    void capture(const wuxc::Grid& parent) {
      for (const auto& column : parent.ColumnDefinitions()) {
        columns.push_back(column);
      }
      for (const auto& child : parent.Children()) {
        if (auto element = child.try_as<wux::FrameworkElement>()) {
          child_columns.push_back({element, wuxc::Grid::GetColumn(element)});
        }
      }
    }
  };

  struct Record final {
    std::uint64_t id = 0;
    std::uint64_t anchor_handle = 0;
    wux::FrameworkElement anchor{nullptr};
    wuxc::Grid parent{nullptr};
    wux::UIElement root{nullptr};
    std::shared_ptr<CleanupAuthorityGate> cleanup_authority;
    std::shared_ptr<CleanupAuthorizationEvidenceSource> evidence_source;
    std::unique_ptr<CapsuleCleanupRecord> cleanup_record;
    wux::DispatcherTimer timer{nullptr};
    winrt::event_token timer_token{};
    GridSnapshot snapshot;
    bool timer_stopped = false;
    bool timer_handler_registered = false;
    bool timer_handler_removed = false;
    bool mutation_started = false;
    bool cleaned = false;
    bool abandon_on_final_release = false;
    std::function<CapsuleCleanupStatus()> cleanup_for_test;
    std::shared_ptr<void> retained_owner_for_test;
    std::mutex cleanup_mutex;
  };

  struct RecordDeleter final {
    void operator()(Record* record) const noexcept {
      if (record && !record->abandon_on_final_release) {
        delete record;
      }
    }
  };

  struct PrimitiveRecord final {
    std::shared_ptr<CleanupAuthorityGate> cleanup_authority;
    std::shared_ptr<CleanupAuthorizationEvidenceSource> evidence_source;
    std::shared_ptr<CapsuleCleanupAccess> access;
    std::unique_ptr<CapsuleCleanupRecord> cleanup_record;
    std::mutex cleanup_mutex;
  };

  static std::uintptr_t object_identity(
      const winrt::Windows::Foundation::IInspectable& object) noexcept {
    if (!object) {
      return 0;
    }
    winrt::com_ptr<IUnknown> identity;
    auto* inspectable =
        reinterpret_cast<IInspectable*>(winrt::get_abi(object));
    if (!inspectable ||
        FAILED(inspectable->QueryInterface(IID_PPV_ARGS(identity.put())))) {
      return 0;
    }
    return reinterpret_cast<std::uintptr_t>(identity.get());
  }

  class RecordAccess final : public CapsuleCleanupAccess {
   public:
    explicit RecordAccess(Record& record) noexcept : record_(record) {}

    CapsuleParentRelationship capsule_parent_relationship()
        noexcept override {
      try {
        if (!record_.parent) {
          return CapsuleParentRelationship::access_failure;
        }
        if (!record_.root) {
          return CapsuleParentRelationship::detached;
        }
        const auto current_parent = wuxm::VisualTreeHelper::GetParent(record_.root);
        if (!current_parent) {
          return CapsuleParentRelationship::detached;
        }
        const auto current_identity = object_identity(current_parent);
        const auto recorded_identity = object_identity(record_.parent);
        if (!current_identity || !recorded_identity) {
          return CapsuleParentRelationship::access_failure;
        }
        return current_identity == recorded_identity
                   ? CapsuleParentRelationship::recorded_parent
                   : CapsuleParentRelationship::different_parent;
      } catch (...) {
        return CapsuleParentRelationship::access_failure;
      }
    }

    bool remove_capsule_from_recorded_parent() noexcept override {
      try {
        if (capsule_parent_relationship() !=
            CapsuleParentRelationship::recorded_parent) {
          return false;
        }
        auto children = record_.parent.Children();
        std::uint32_t index = 0;
        if (!children.IndexOf(record_.root, index)) {
          return false;
        }
        children.RemoveAt(index);
        return true;
      } catch (...) {
        return false;
      }
    }

    bool clear_columns() noexcept override {
      try {
        record_.parent.ColumnDefinitions().Clear();
        return true;
      } catch (...) {
        return false;
      }
    }

    bool append_column(std::size_t index) noexcept override {
      try {
        if (index >= record_.snapshot.columns.size()) {
          return false;
        }
        record_.parent.ColumnDefinitions().Append(
            record_.snapshot.columns[index]);
        return true;
      } catch (...) {
        return false;
      }
    }

    OriginalChildState original_child_state(
        std::size_t index) noexcept override {
      try {
        if (index >= record_.snapshot.child_columns.size()) {
          return OriginalChildState::access_failure;
        }
        const auto& saved = record_.snapshot.child_columns[index];
        if (!saved.element) {
          return OriginalChildState::access_failure;
        }
        const auto current_parent = wuxm::VisualTreeHelper::GetParent(saved.element);
        if (!current_parent) {
          return OriginalChildState::missing;
        }
        const auto current_identity = object_identity(current_parent);
        const auto recorded_identity = object_identity(record_.parent);
        if (!current_identity || !recorded_identity) {
          return OriginalChildState::access_failure;
        }
        if (current_identity != recorded_identity) {
          return OriginalChildState::identity_mismatch;
        }
        std::uint32_t ignored = 0;
        return record_.parent.Children().IndexOf(saved.element, ignored)
                   ? OriginalChildState::present_exact
                   : OriginalChildState::identity_mismatch;
      } catch (...) {
        return OriginalChildState::access_failure;
      }
    }

    bool restore_original_child_column(std::size_t index) noexcept override {
      try {
        if (index >= record_.snapshot.child_columns.size()) {
          return false;
        }
        const auto& saved = record_.snapshot.child_columns[index];
        wuxc::Grid::SetColumn(saved.element, saved.column);
        return true;
      } catch (...) {
        return false;
      }
    }

   private:
    Record& record_;
  };

  mutable std::mutex mutex;
  std::unordered_map<DWORD, std::vector<std::shared_ptr<Record>>> records;
  std::vector<std::shared_ptr<PrimitiveRecord>> primitive_records;
  std::uint64_t next_id = 1;
  ReleasePhase release_phase = ReleasePhase::open;
  std::size_t active_operations = 0;
  std::uint64_t next_release_generation = 1;
  std::uint64_t claimed_release_generation = 0;
  std::atomic<bool> retention_fault{false};
  std::atomic<ManagerRemoveSnapshotObserver> remove_snapshot_observer{nullptr};
  std::atomic<void*> remove_snapshot_context{nullptr};
  std::atomic<void (*)(void*) noexcept> attach_admission_observer{nullptr};
  std::atomic<void*> attach_admission_context{nullptr};

  [[nodiscard]] std::optional<OperationLease> try_begin_operation() noexcept {
    auto owner = weak_from_this().lock();
    if (!owner) {
      return std::nullopt;
    }
    std::lock_guard lock(mutex);
    if (release_phase != ReleasePhase::open ||
        active_operations == (std::numeric_limits<std::size_t>::max)()) {
      if (active_operations ==
          (std::numeric_limits<std::size_t>::max)()) {
        retention_fault.store(true, std::memory_order_release);
      }
      return std::nullopt;
    }
    ++active_operations;
    return OperationLease{std::move(owner)};
  }

  [[nodiscard]] std::size_t tracked_element_count() const noexcept {
    if (retention_fault.load(std::memory_order_acquire)) {
      return (std::numeric_limits<std::size_t>::max)();
    }
    std::lock_guard lock(mutex);
    std::size_t count = 0;
    for (const auto& [thread_id, thread_records] : records) {
      (void)thread_id;
      if (thread_records.size() >
          (std::numeric_limits<std::size_t>::max)() - count) {
        return (std::numeric_limits<std::size_t>::max)();
      }
      count += thread_records.size();
    }
    if (primitive_records.size() >
        (std::numeric_limits<std::size_t>::max)() - count) {
      return (std::numeric_limits<std::size_t>::max)();
    }
    return count + primitive_records.size();
  }

  void notify_attach_admission_for_test() noexcept {
    const auto observer =
        attach_admission_observer.load(std::memory_order_acquire);
    if (observer) {
      observer(attach_admission_context.load(std::memory_order_acquire));
    }
  }

  static CapsuleTransactionResult route_cleanup_trigger(
      CapsuleCleanupRecord& cleanup_record,
      CapsuleCleanupTrigger trigger,
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access,
      const CleanupAuthorityGate::TransitionPermit* transition_permit =
          nullptr) noexcept {
    switch (trigger) {
      case CapsuleCleanupTrigger::timer:
        return cleanup_record.on_timer(evidence_source, access);
      case CapsuleCleanupTrigger::explicit_removal:
        return cleanup_record.on_explicit_removal(evidence_source, access);
      case CapsuleCleanupTrigger::shutdown:
        return transition_permit
                   ? cleanup_record.on_transition_shutdown(
                         *transition_permit, evidence_source, access)
                   : cleanup_record.on_shutdown(evidence_source, access);
      case CapsuleCleanupTrigger::rollback:
        return cleanup_record.on_rollback(evidence_source, access);
    }
    return CapsuleTransactionResult::retryable_failure;
  }

  static CapsuleCleanupStatus run_cleanup(
      Record& record,
      CapsuleCleanupTrigger trigger,
      const CleanupAuthorityGate::TransitionPermit* transition_permit =
          nullptr) noexcept {
    std::lock_guard cleanup_lock(record.cleanup_mutex);
    if (record.cleaned) {
      return CapsuleCleanupStatus::complete;
    }
    if (record.cleanup_for_test) {
      const auto status = record.cleanup_for_test();
      if (status == CapsuleCleanupStatus::complete) {
        record.cleaned = true;
      }
      return status;
    }

    if (transition_permit &&
        (trigger != CapsuleCleanupTrigger::shutdown ||
         !record.cleanup_authority ||
         !transition_permit->valid_for(*record.cleanup_authority))) {
      return CapsuleCleanupStatus::retryable_failure;
    }

    try {
      if (!record.cleanup_record || !record.cleanup_record->valid() ||
          !record.evidence_source) {
        return CapsuleCleanupStatus::retryable_failure;
      }
      RecordAccess access(record);
      const auto result = route_cleanup_trigger(
          *record.cleanup_record, trigger, *record.evidence_source, access,
          transition_permit);
      if (result == CapsuleTransactionResult::retryable_failure) {
        return CapsuleCleanupStatus::retryable_failure;
      }

      if (record.timer && !record.timer_stopped) {
        record.timer.Stop();
        record.timer_stopped = true;
      }
      if (record.timer && record.timer_handler_registered &&
          !record.timer_handler_removed) {
        record.timer.Tick(record.timer_token);
        record.timer_token = {};
        record.timer_handler_removed = true;
      }
    } catch (...) {
      return CapsuleCleanupStatus::retryable_failure;
    }
    record.cleaned = true;
    return CapsuleCleanupStatus::complete;
  }

  static CapsuleCleanupStatus run_primitive_cleanup(
      PrimitiveRecord& record,
      CapsuleCleanupTrigger trigger,
      const CleanupAuthorityGate::TransitionPermit* transition_permit =
          nullptr) noexcept {
    std::lock_guard cleanup_lock(record.cleanup_mutex);
    if (!record.cleanup_authority || !record.evidence_source ||
        !record.access || !record.cleanup_record ||
        !record.cleanup_record->valid() ||
        (transition_permit &&
         !transition_permit->valid_for(*record.cleanup_authority))) {
      return CapsuleCleanupStatus::retryable_failure;
    }
    const auto result = route_cleanup_trigger(
        *record.cleanup_record, trigger, *record.evidence_source,
        *record.access, transition_permit);
    return result == CapsuleTransactionResult::retryable_failure
               ? CapsuleCleanupStatus::retryable_failure
               : CapsuleCleanupStatus::complete;
  }

  [[nodiscard]] CapsuleCleanupStatus cleanup_primitive_records(
      CapsuleCleanupTrigger trigger,
      const CleanupAuthorityGate::TransitionPermit* transition_permit =
          nullptr) noexcept {
    auto operation = try_begin_operation();
    if (!operation) {
      return CapsuleCleanupStatus::retryable_failure;
    }
    std::vector<std::shared_ptr<PrimitiveRecord>> pending;
    try {
      std::lock_guard lock(mutex);
      pending.reserve(primitive_records.size());
      for (const auto& record : primitive_records) {
        if (!transition_permit ||
            (record->cleanup_authority &&
             transition_permit->valid_for(*record->cleanup_authority))) {
          pending.push_back(record);
        }
      }
    } catch (...) {
      return CapsuleCleanupStatus::retryable_failure;
    }
    auto aggregate = CapsuleCleanupStatus::complete;
    for (const auto& record : pending) {
      if (run_primitive_cleanup(*record, trigger, transition_permit) !=
          CapsuleCleanupStatus::complete) {
        aggregate = CapsuleCleanupStatus::retryable_failure;
        continue;
      }
      std::lock_guard lock(mutex);
      std::erase(primitive_records, record);
    }
    return aggregate;
  }

  [[nodiscard]] CapsuleCleanupStatus remove(DWORD thread_id,
                                             std::uint64_t id,
                                             CapsuleCleanupTrigger trigger,
                                             const CleanupAuthorityGate::
                                                 TransitionPermit*
                                                     transition_permit =
                                                          nullptr)
      noexcept {
    auto operation = try_begin_operation();
    if (!operation) {
      return CapsuleCleanupStatus::retryable_failure;
    }
    std::shared_ptr<Record> record;
    {
      std::lock_guard lock(mutex);
      const auto found = records.find(thread_id);
      if (found == records.end()) {
        return CapsuleCleanupStatus::complete;
      }
      auto& thread_records = found->second;
      const auto item = std::find_if(
          thread_records.begin(), thread_records.end(),
          [id](const auto& candidate) { return candidate->id == id; });
      if (item == thread_records.end()) {
        return CapsuleCleanupStatus::complete;
      }
      record = *item;
    }
    const auto snapshot_observer =
        remove_snapshot_observer.load(std::memory_order_acquire);
    if (snapshot_observer) {
      snapshot_observer(
          remove_snapshot_context.load(std::memory_order_acquire));
    }
    const auto status = run_cleanup(*record, trigger, transition_permit);
    if (status != CapsuleCleanupStatus::complete) {
      return status;
    }
    std::shared_ptr<Record> removed;
    {
      std::lock_guard lock(mutex);
      const auto found = records.find(thread_id);
      if (found == records.end()) {
        return CapsuleCleanupStatus::complete;
      }
      auto& thread_records = found->second;
      const auto item = std::find_if(
          thread_records.begin(), thread_records.end(),
          [id, &record](const auto& candidate) {
            return candidate == record && candidate->id == id;
          });
      if (item != thread_records.end()) {
        removed = std::move(*item);
        thread_records.erase(item);
      }
      if (thread_records.empty()) {
        records.erase(found);
      }
    }
    return CapsuleCleanupStatus::complete;
  }
};

void ProbeCapsuleManager::destroy_impl(Impl* state) noexcept {
  if (!state) {
    return;
  }
  bool safe_to_destroy = false;
  {
    std::lock_guard lock(state->mutex);
    safe_to_destroy =
        state->release_phase == Impl::ReleasePhase::released ||
        (state->release_phase == Impl::ReleasePhase::open &&
         state->active_operations == 0 &&
         !state->retention_fault.load(std::memory_order_acquire) &&
         state->records.empty() && state->primitive_records.empty());
  }
  if (safe_to_destroy) {
    delete state;
  }
}

ProbeCapsuleManager::ReleasePermit::ReleasePermit(
    std::shared_ptr<Impl> state,
    std::uint64_t generation) noexcept
    : state_(std::move(state)), generation_(generation) {}

ProbeCapsuleManager::ReleasePermit::ReleasePermit(
    ReleasePermit&& other) noexcept
    : state_(std::move(other.state_)),
      generation_(std::exchange(other.generation_, 0)) {}

ProbeCapsuleManager::ReleasePermit&
ProbeCapsuleManager::ReleasePermit::operator=(ReleasePermit&& other) noexcept {
  if (this != &other) {
    cancel();
    state_ = std::move(other.state_);
    generation_ = std::exchange(other.generation_, 0);
  }
  return *this;
}

ProbeCapsuleManager::ReleasePermit::~ReleasePermit() { cancel(); }

ProbeCapsuleManager::ReleasePermit::operator bool() const noexcept {
  return state_ && generation_;
}

void ProbeCapsuleManager::ReleasePermit::cancel() noexcept {
  auto state = std::move(state_);
  const auto generation = std::exchange(generation_, 0);
  if (!state || !generation) {
    return;
  }
  std::lock_guard lock(state->mutex);
  if (state->release_phase == Impl::ReleasePhase::claimed &&
      state->claimed_release_generation == generation) {
    state->release_phase = Impl::ReleasePhase::open;
    state->claimed_release_generation = 0;
  } else {
    state->retention_fault.store(true, std::memory_order_release);
    if (state->release_phase == Impl::ReleasePhase::open) {
      state->release_phase = Impl::ReleasePhase::claimed;
      state->claimed_release_generation = generation;
    }
  }
}

ProbeCapsuleManager::ProbeCapsuleManager() noexcept {
  try {
    auto* const state = new (std::nothrow) Impl();
    if (state) {
      impl_ = std::shared_ptr<Impl>(state, &ProbeCapsuleManager::destroy_impl);
    }
  } catch (...) {
  }
}

ProbeCapsuleManager::~ProbeCapsuleManager() {
  if (impl_) {
    (void)release();
    impl_.reset();
  }
}

bool ProbeCapsuleManager::attach_record_for_test(
    DWORD owner_thread_id,
    std::function<CapsuleCleanupStatus()> cleanup,
    std::shared_ptr<void> retained_owner,
    std::uint64_t& record_id) noexcept {
  const auto state = impl_;
  if (!state || !owner_thread_id || !cleanup || !retained_owner) {
    return false;
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return false;
  }
  state->notify_attach_admission_for_test();
  try {
    auto record = std::make_shared<Impl::Record>();
    record->cleanup_for_test = std::move(cleanup);
    record->retained_owner_for_test = std::move(retained_owner);
    {
      std::lock_guard lock(state->mutex);
      if (state->release_phase != Impl::ReleasePhase::open ||
          !state->next_id) {
        return false;
      }
      record->id = state->next_id++;
      record_id = record->id;
      state->records[owner_thread_id].push_back(record);
    }
    return true;
  } catch (...) {
    return false;
  }
}

CapsuleCleanupStatus ProbeCapsuleManager::remove_record_for_test(
    DWORD owner_thread_id,
    std::uint64_t record_id) noexcept {
  const auto state = impl_;
  if (!state) {
    return CapsuleCleanupStatus::complete;
  }
  auto operation = state->try_begin_operation();
  return operation
             ? state->remove(owner_thread_id, record_id,
                             CapsuleCleanupTrigger::explicit_removal)
             : CapsuleCleanupStatus::retryable_failure;
}

bool ProbeCapsuleManager::mutex_available_for_test() const noexcept {
  const auto state = impl_;
  if (!state) {
    return false;
  }
  if (!state->mutex.try_lock()) {
    return false;
  }
  state->mutex.unlock();
  return true;
}

bool ProbeCapsuleManager::set_remove_snapshot_observer_for_test(
    ManagerRemoveSnapshotObserver observer,
    void* context) noexcept {
  const auto state = impl_;
  if (!state || (observer && !context)) {
    return false;
  }
  state->remove_snapshot_context.store(context, std::memory_order_release);
  state->remove_snapshot_observer.store(observer, std::memory_order_release);
  return true;
}

bool ProbeCapsuleManager::set_attach_admission_observer_for_test(
    void (*observer)(void* context) noexcept,
    void* context) noexcept {
  const auto state = impl_;
  if (!state || (observer && !context)) {
    return false;
  }
  if (observer) {
    state->attach_admission_context.store(context,
                                          std::memory_order_release);
    state->attach_admission_observer.store(observer,
                                           std::memory_order_release);
  } else {
    state->attach_admission_observer.store(nullptr,
                                           std::memory_order_release);
    state->attach_admission_context.store(nullptr,
                                          std::memory_order_release);
  }
  return true;
}

bool ProbeCapsuleManager::set_retention_fault_for_test(bool fault) noexcept {
  const auto state = impl_;
  if (!state) {
    return false;
  }
  state->retention_fault.store(fault, std::memory_order_release);
  return true;
}

bool ProbeCapsuleManager::attach_primitive_record_for_test(
    CleanupAuthorizationBinding binding,
    std::shared_ptr<CleanupAuthorityGate> authority,
    std::shared_ptr<CleanupAuthorizationEvidenceSource> evidence_source,
    std::shared_ptr<CapsuleCleanupAccess> access) noexcept {
  const auto state = impl_;
  if (!state || !authority || !evidence_source || !access) {
    return false;
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return false;
  }
  state->notify_attach_admission_for_test();
  try {
    auto record = std::make_shared<Impl::PrimitiveRecord>();
    record->cleanup_authority = authority;
    record->evidence_source = std::move(evidence_source);
    record->access = std::move(access);
    record->cleanup_record = std::make_unique<CapsuleCleanupRecord>(
        std::move(binding), std::move(authority), 0, 0);
    if (!record->cleanup_record->valid()) {
      return false;
    }
    std::lock_guard lock(state->mutex);
    if (state->release_phase != Impl::ReleasePhase::open) {
      return false;
    }
    state->primitive_records.push_back(std::move(record));
    return true;
  } catch (...) {
    return false;
  }
}

CapsuleCleanupResult
ProbeCapsuleManager::trigger_primitive_records_for_test(
    CapsuleCleanupTrigger trigger) noexcept {
  const auto state = impl_;
  if (!state) {
    return {};
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return {CapsuleCleanupStatus::retryable_failure,
            (std::numeric_limits<std::size_t>::max)()};
  }
  const auto status = state->cleanup_primitive_records(trigger);
  return {status, state->tracked_element_count()};
}

bool ProbeCapsuleManager::insert(std::uint64_t anchor_handle,
                                 IInspectable* anchor_object,
                                 IInspectable* parent_object,
                                 CleanupAuthorizationBinding binding,
                                 std::shared_ptr<CleanupAuthorityGate> authority,
                                 std::shared_ptr<
                                     CleanupAuthorizationEvidenceSource>
                                     evidence_source,
                                 CleanupAuthorityGate::MutationLease
                                     mutation_authority) noexcept {
  const auto state = impl_;
  if (!state || !anchor_object || !parent_object || !authority ||
      !mutation_authority.valid_for(*authority) || !evidence_source) {
    return false;
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return false;
  }
  state->notify_attach_admission_for_test();

  std::shared_ptr<Impl::Record> record;
  try {
    winrt::Windows::Foundation::IInspectable anchor_inspectable{nullptr};
    winrt::Windows::Foundation::IInspectable parent_inspectable{nullptr};
    winrt::copy_from_abi(anchor_inspectable, anchor_object);
    winrt::copy_from_abi(parent_inspectable, parent_object);
    auto anchor = anchor_inspectable.try_as<wux::FrameworkElement>();
    auto parent = parent_inspectable.try_as<wuxc::Grid>();
    if (!anchor || !parent) {
      return false;
    }

    {
      std::lock_guard lock(state->mutex);
      const auto found = state->records.find(GetCurrentThreadId());
      if (state->release_phase != Impl::ReleasePhase::open) {
        return false;
      }
      if (found != state->records.end() &&
          std::any_of(found->second.begin(), found->second.end(),
                      [anchor_handle](const auto& existing) {
                        return existing->anchor_handle == anchor_handle;
                      })) {
        return false;
      }
    }

    record = std::shared_ptr<Impl::Record>(new Impl::Record(),
                                           Impl::RecordDeleter{});
    const DWORD thread_id = GetCurrentThreadId();
    {
      std::lock_guard lock(state->mutex);
      if (state->release_phase != Impl::ReleasePhase::open ||
          !state->next_id) {
        return false;
      }
      record->id = state->next_id++;
    }
    binding.record_key = record->id;
    if (binding.owner_thread_id != thread_id ||
        binding.anchor_identity != Impl::object_identity(anchor) ||
        binding.grid_identity != Impl::object_identity(parent) ||
        !cleanup_binding_is_well_formed(binding)) {
      return false;
    }
    record->anchor_handle = anchor_handle;
    record->anchor = anchor;
    record->parent = parent;
    record->cleanup_authority = authority;
    record->evidence_source = std::move(evidence_source);
    record->snapshot.capture(parent);
    record->cleanup_record = std::make_unique<CapsuleCleanupRecord>(
        std::move(binding), std::move(authority),
        record->snapshot.columns.size(),
        record->snapshot.child_columns.size());
    if (!record->cleanup_record->valid()) {
      return false;
    }

    auto children = parent.Children();
    std::uint32_t anchor_index = 0;
    if (!children.IndexOf(anchor.as<wux::UIElement>(), anchor_index)) {
      return false;
    }

    auto definitions = parent.ColumnDefinitions();
    int anchor_column = (std::max)(0, wuxc::Grid::GetColumn(anchor));
    const auto target_column = static_cast<std::uint32_t>(
        (std::min)(anchor_column, static_cast<int>(definitions.Size())));

    record->mutation_started = true;
    wuxc::ColumnDefinition probe_column;
    probe_column.Width(wux::GridLengthHelper::Auto());
    definitions.InsertAt(target_column, probe_column);
    for (const auto& child : children) {
      if (auto element = child.try_as<wux::FrameworkElement>()) {
        const int column = wuxc::Grid::GetColumn(element);
        if (column >= static_cast<int>(target_column)) {
          wuxc::Grid::SetColumn(element, column + 1);
        }
      }
    }

    wuxc::Border root;
    root.Width(kProbeCapsuleWidthDip);
    root.Height(kProbeCapsuleHeightDip);
    root.CornerRadius({18.0, 18.0, 18.0, 18.0});
    root.Background(wuxm::SolidColorBrush({220, 31, 31, 38}));
    root.BorderBrush(wuxm::SolidColorBrush({96, 255, 255, 255}));
    root.BorderThickness({1.0, 1.0, 1.0, 1.0});

    wuxc::TextBlock label;
    label.Text(kProbeCapsuleText);
    label.FontSize(12.0);
    label.Foreground(wuxm::SolidColorBrush({255, 245, 245, 250}));
    label.HorizontalAlignment(wux::HorizontalAlignment::Center);
    label.VerticalAlignment(wux::VerticalAlignment::Center);
    root.Child(label);

    wuxc::Grid::SetColumn(root, static_cast<int>(target_column));
    wuxc::Grid::SetRow(root, wuxc::Grid::GetRow(anchor));
    wuxc::Canvas::SetZIndex(root, 10000);
    record->root = root;
    children.InsertAt(anchor_index, root);

    const std::uint64_t id = record->id;

    wux::DispatcherTimer timer;
    timer.Interval(std::chrono::milliseconds(kProbeCapsuleDisplayMilliseconds));
    record->timer = timer;
    record->timer_token = record->timer.Tick(
        [manager = state, thread_id, id](const auto&, const auto&) noexcept {
          (void)manager->remove(thread_id, id,
                                CapsuleCleanupTrigger::timer);
        });
    record->timer_handler_registered = true;

    record->timer.Start();
    {
      std::lock_guard lock(state->mutex);
      if (state->release_phase != Impl::ReleasePhase::open) {
        return false;
      }
      state->records[thread_id].push_back(record);
    }
    return true;
  } catch (...) {
    if (record && record->mutation_started) {
      const auto status =
          Impl::run_cleanup(*record, CapsuleCleanupTrigger::rollback);
      if (status != CapsuleCleanupStatus::complete && record->id != 0) {
        try {
          std::lock_guard lock(state->mutex);
          state->records[GetCurrentThreadId()].push_back(record);
        } catch (...) {
          state->retention_fault.store(true, std::memory_order_release);
          record->abandon_on_final_release = true;
        }
      }
    }
    return false;
  }
}

CapsuleCleanupResult ProbeCapsuleManager::remove_anchor_on_current_thread(
    std::uint64_t anchor_handle) noexcept {
  const auto state = impl_;
  if (!state) {
    return {};
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return {CapsuleCleanupStatus::retryable_failure,
            (std::numeric_limits<std::size_t>::max)()};
  }

  const DWORD thread_id = GetCurrentThreadId();
  std::vector<std::uint64_t> ids;
  try {
    std::lock_guard lock(state->mutex);
    const auto found = state->records.find(thread_id);
    if (found == state->records.end()) {
      return {};
    }
    ids.reserve(found->second.size());
    for (const auto& record : found->second) {
      if (record->anchor_handle == anchor_handle) {
        ids.push_back(record->id);
      }
    }
  } catch (...) {
    return {CapsuleCleanupStatus::retryable_failure,
            state->tracked_element_count()};
  }
  CapsuleCleanupStatus status = CapsuleCleanupStatus::complete;
  for (const auto id : ids) {
    if (state->remove(thread_id, id,
                      CapsuleCleanupTrigger::explicit_removal) !=
        CapsuleCleanupStatus::complete) {
      status = CapsuleCleanupStatus::retryable_failure;
    }
  }
  return {status, state->tracked_element_count()};
}

CapsuleCleanupResult ProbeCapsuleManager::cleanup_current_thread() noexcept {
  const auto state = impl_;
  if (!state) {
    return {};
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return {CapsuleCleanupStatus::retryable_failure,
            (std::numeric_limits<std::size_t>::max)()};
  }

  const DWORD thread_id = GetCurrentThreadId();
  std::vector<std::uint64_t> ids;
  try {
    std::lock_guard lock(state->mutex);
    const auto found = state->records.find(thread_id);
    if (found == state->records.end()) {
      return {};
    }
    ids.reserve(found->second.size());
    for (const auto& record : found->second) {
      ids.push_back(record->id);
    }
  } catch (...) {
    return {CapsuleCleanupStatus::retryable_failure,
            state->tracked_element_count()};
  }
  CapsuleCleanupStatus status = CapsuleCleanupStatus::complete;
  for (const auto id : ids) {
    if (state->remove(thread_id, id, CapsuleCleanupTrigger::shutdown) !=
        CapsuleCleanupStatus::complete) {
      status = CapsuleCleanupStatus::retryable_failure;
    }
  }
  if (state->cleanup_primitive_records(CapsuleCleanupTrigger::shutdown) !=
      CapsuleCleanupStatus::complete) {
    status = CapsuleCleanupStatus::retryable_failure;
  }
  return {status, state->tracked_element_count()};
}

CapsuleCleanupResult
ProbeCapsuleManager::cleanup_current_thread_for_transition(
    const CleanupAuthorityGate::TransitionPermit& permit) noexcept {
  const auto state = impl_;
  if (!state) {
    return {};
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return {CapsuleCleanupStatus::retryable_failure,
            (std::numeric_limits<std::size_t>::max)()};
  }

  const DWORD thread_id = GetCurrentThreadId();
  std::vector<std::uint64_t> ids;
  try {
    std::lock_guard lock(state->mutex);
    const auto found = state->records.find(thread_id);
    if (found != state->records.end()) {
      ids.reserve(found->second.size());
      for (const auto& record : found->second) {
        if (record->cleanup_authority &&
            permit.valid_for(*record->cleanup_authority)) {
          ids.push_back(record->id);
        }
      }
    }
  } catch (...) {
    return {CapsuleCleanupStatus::retryable_failure,
            state->tracked_element_count()};
  }
  CapsuleCleanupStatus status = CapsuleCleanupStatus::complete;
  for (const auto id : ids) {
    if (state->remove(thread_id, id, CapsuleCleanupTrigger::shutdown,
                      &permit) != CapsuleCleanupStatus::complete) {
      status = CapsuleCleanupStatus::retryable_failure;
    }
  }
  if (state->cleanup_primitive_records(CapsuleCleanupTrigger::shutdown,
                                       &permit) !=
      CapsuleCleanupStatus::complete) {
    status = CapsuleCleanupStatus::retryable_failure;
  }
  return {status, state->tracked_element_count()};
}

std::size_t ProbeCapsuleManager::tracked_element_count() const noexcept {
  const auto state = impl_;
  if (!state) {
    return 0;
  }
  auto operation = state->try_begin_operation();
  if (!operation) {
    return (std::numeric_limits<std::size_t>::max)();
  }
  return state->tracked_element_count();
}

std::optional<ProbeCapsuleManager::ReleasePermit>
ProbeCapsuleManager::try_claim_release() noexcept {
  const auto state = impl_;
  if (!state) {
    return std::nullopt;
  }
  std::lock_guard lock(state->mutex);
  if (state->release_phase != Impl::ReleasePhase::open ||
      state->active_operations != 0 ||
      state->retention_fault.load(std::memory_order_acquire) ||
      !state->records.empty() || !state->primitive_records.empty() ||
      !state->next_release_generation ||
      state->next_release_generation ==
          (std::numeric_limits<std::uint64_t>::max)()) {
    return std::nullopt;
  }
  const auto generation = state->next_release_generation++;
  state->release_phase = Impl::ReleasePhase::claimed;
  state->claimed_release_generation = generation;
  return ReleasePermit{state, generation};
}

bool ProbeCapsuleManager::release_claim_matches(
    const ReleasePermit& permit) const noexcept {
  const auto state = impl_;
  if (!state || permit.state_.get() != state.get() || !permit.generation_) {
    return false;
  }
  std::lock_guard lock(state->mutex);
  return state->release_phase == Impl::ReleasePhase::claimed &&
         state->claimed_release_generation == permit.generation_;
}

void ProbeCapsuleManager::finalize_release(ReleasePermit&& permit) noexcept {
  const auto owner_state = impl_;
  auto state = std::move(permit.state_);
  const auto generation = std::exchange(permit.generation_, 0);
  if (!state || !generation || !owner_state ||
      state.get() != owner_state.get()) {
    const auto poison = [generation](const std::shared_ptr<Impl>& target) {
      if (!target) {
        return;
      }
      std::lock_guard lock(target->mutex);
      target->retention_fault.store(true, std::memory_order_release);
      if (target->release_phase == Impl::ReleasePhase::open) {
        target->release_phase = Impl::ReleasePhase::claimed;
        target->claimed_release_generation =
            generation ? generation
                       : (std::numeric_limits<std::uint64_t>::max)();
      }
    };
    poison(state);
    if (owner_state.get() != state.get()) {
      poison(owner_state);
    }
    return;
  }

  std::lock_guard lock(state->mutex);
  if (state->release_phase == Impl::ReleasePhase::claimed &&
      state->claimed_release_generation == generation) {
    state->release_phase = Impl::ReleasePhase::released;
    state->claimed_release_generation = 0;
    return;
  }

  state->retention_fault.store(true, std::memory_order_release);
  if (state->release_phase == Impl::ReleasePhase::open) {
    state->release_phase = Impl::ReleasePhase::claimed;
    state->claimed_release_generation = generation;
  }
}

std::uint64_t ProbeCapsuleManager::claim_release_for_test() noexcept {
  auto permit = try_claim_release();
  if (!permit) {
    return 0;
  }
  const auto generation = permit->generation_;
  permit->state_.reset();
  permit->generation_ = 0;
  return generation;
}

bool ProbeCapsuleManager::cancel_release_for_test(
    std::uint64_t generation) noexcept {
  const auto state = impl_;
  if (!state || !generation) {
    return false;
  }
  std::lock_guard lock(state->mutex);
  if (state->release_phase != Impl::ReleasePhase::claimed ||
      state->claimed_release_generation != generation) {
    return false;
  }
  state->release_phase = Impl::ReleasePhase::open;
  state->claimed_release_generation = 0;
  return true;
}

bool ProbeCapsuleManager::commit_release_for_test(
    std::uint64_t generation) noexcept {
  const auto state = impl_;
  if (!state || !generation) {
    return false;
  }
  std::lock_guard lock(state->mutex);
  if (state->release_phase != Impl::ReleasePhase::claimed ||
      state->claimed_release_generation != generation) {
    return false;
  }
  state->release_phase = Impl::ReleasePhase::released;
  state->claimed_release_generation = 0;
  return true;
}

bool ProbeCapsuleManager::release() noexcept {
  const auto state = impl_;
  if (!state) {
    return true;
  }
  {
    std::lock_guard lock(state->mutex);
    if (state->release_phase == Impl::ReleasePhase::released) {
      return true;
    }
  }
  auto permit = try_claim_release();
  if (!permit || !release_claim_matches(*permit)) {
    return false;
  }
  finalize_release(std::move(*permit));
  return true;
}

}  // namespace cq::bridge
