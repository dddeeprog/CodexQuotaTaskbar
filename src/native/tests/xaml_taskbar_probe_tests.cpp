#include "xaml_taskbar_probe.h"
#include "xaml_taskbar_probe_test_peer.h"
#include "bridge_runtime.h"
#include "probe_capsule.h"
#include "probe_safety.h"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <xamlOM.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <functional>
#include <iostream>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <optional>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {

using cq::bridge::ProbeDecision;
using cq::bridge::VisualNodeFacts;
using cq::bridge::evaluate_visual_node;

class FakeBridgeOperations final : public cq::bridge::BridgeOperations {
 public:
  bool acquire_shutdown_event() noexcept override { return true; }
  bool acquire_quiesced_event() noexcept override { return true; }
  bool acquire_ready_event() noexcept override { return true; }
  bool initialize_xaml_threads() noexcept override { return true; }
  bool initialize_xaml_diagnostics() noexcept override { return true; }
  bool advise_watcher() noexcept override { return true; }
  cq::bridge::WorkSignal wait_for_work() noexcept override {
    return cq::bridge::WorkSignal::failure;
  }
  bool retry_initialize_xaml_threads() noexcept override { return false; }
  cq::bridge::ProbeCleanupResult cancel_delayed_retry() noexcept override {
    return {};
  }
  cq::bridge::ProbeCleanupResult unadvise_watcher() noexcept override {
    return {};
  }
  cq::bridge::ProbeCleanupResult remove_all_capsules() noexcept override {
    return {};
  }
  cq::bridge::ProbeCleanupResult uninitialize_xaml_threads()
      noexcept override {
    return {};
  }
  cq::bridge::ProbeResourceCounts resource_counts() const noexcept override {
    return {};
  }
  bool prepare_for_unload() noexcept override { return true; }
  void signal_quiesced() noexcept override {}
  void signal_ready() noexcept override {}
  void close_control_handles() noexcept override {}
  void self_unload() noexcept override {}
};

class ProbeBackedOperations final : public cq::bridge::BridgeOperations {
 public:
  bool acquire_shutdown_event() noexcept override { return true; }
  bool acquire_quiesced_event() noexcept override { return true; }
  bool acquire_ready_event() noexcept override { return true; }
  bool initialize_xaml_threads() noexcept override { return true; }
  bool initialize_xaml_diagnostics() noexcept override { return true; }
  bool advise_watcher() noexcept override { return true; }
  cq::bridge::WorkSignal wait_for_work() noexcept override {
    return cq::bridge::WorkSignal::shutdown;
  }
  bool retry_initialize_xaml_threads() noexcept override { return false; }
  cq::bridge::ProbeCleanupResult cancel_delayed_retry() noexcept override {
    return probe ? probe->cancel_pending_work()
                 : cq::bridge::ProbeCleanupResult{};
  }
  cq::bridge::ProbeCleanupResult unadvise_watcher() noexcept override {
    return probe ? probe->unadvise_watcher()
                 : cq::bridge::ProbeCleanupResult{};
  }
  cq::bridge::ProbeCleanupResult remove_all_capsules() noexcept override {
    return probe ? probe->remove_all_capsules()
                 : cq::bridge::ProbeCleanupResult{};
  }
  cq::bridge::ProbeCleanupResult uninitialize_xaml_threads()
      noexcept override {
    return probe ? probe->uninitialize_taskbar_threads()
                 : cq::bridge::ProbeCleanupResult{};
  }
  cq::bridge::ProbeResourceCounts resource_counts() const noexcept override {
    return probe ? probe->resource_counts()
                 : cq::bridge::ProbeResourceCounts{};
  }
  bool prepare_for_unload() noexcept override {
    return probe && probe->release_for_unload();
  }
  void signal_quiesced() noexcept override { quiesced_signaled = true; }
  void signal_ready() noexcept override {}
  void close_control_handles() noexcept override { handles_closed = true; }
  void self_unload() noexcept override { unload_called = true; }

  cq::bridge::XamlTaskbarProbe* probe = nullptr;
  bool quiesced_signaled = false;
  bool handles_closed = false;
  bool unload_called = false;
};

class FakeSite final : public IXamlDiagnostics, public IVisualTreeService {
 public:
  ~FakeSite() {
    if (callback_) {
      callback_->Release();
    }
  }

  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interface_id,
                                             void** object) noexcept override {
    ++query_interface_calls;
    if (on_query_interface) {
      on_query_interface();
    }
    auto query_failures =
        query_interface_failures_remaining.load(std::memory_order_relaxed);
    while (query_failures &&
           !query_interface_failures_remaining.compare_exchange_weak(
               query_failures, query_failures - 1,
               std::memory_order_relaxed)) {
    }
    if (query_failures) {
      return E_NOINTERFACE;
    }
    if (!object) {
      return E_POINTER;
    }
    *object = nullptr;
    if (interface_id == __uuidof(IUnknown) ||
        interface_id == __uuidof(IXamlDiagnostics)) {
      *object = static_cast<IXamlDiagnostics*>(this);
      AddRef();
      return S_OK;
    }
    if (interface_id == __uuidof(IVisualTreeService)) {
      *object = static_cast<IVisualTreeService*>(this);
      AddRef();
      return S_OK;
    }
    return E_NOINTERFACE;
  }

  ULONG STDMETHODCALLTYPE AddRef() noexcept override {
    if (on_add_ref) {
      on_add_ref();
    }
    return references_.fetch_add(1, std::memory_order_relaxed) + 1;
  }

  ULONG STDMETHODCALLTYPE Release() noexcept override {
    const auto remaining =
        references_.fetch_sub(1, std::memory_order_relaxed) - 1;
    if (on_release) {
      on_release();
    }
    return remaining;
  }

  HRESULT STDMETHODCALLTYPE GetDispatcher(IInspectable** value) override {
    return fail_output(value);
  }
  HRESULT STDMETHODCALLTYPE GetUiLayer(IInspectable** value) override {
    return fail_output(value);
  }
  HRESULT STDMETHODCALLTYPE GetApplication(IInspectable** value) override {
    return fail_output(value);
  }
  HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(
      InstanceHandle,
      IInspectable** value) override {
    ++object_queries;
    return fail_output(value);
  }
  HRESULT STDMETHODCALLTYPE GetHandleFromIInspectable(
      IInspectable*,
      InstanceHandle* handle) override {
    ++handle_queries;
    if (handle) {
      *handle = 0;
    }
    return E_FAIL;
  }
  HRESULT STDMETHODCALLTYPE HitTest(RECT,
                                    unsigned int* count,
                                    InstanceHandle** handles) override {
    if (count) {
      *count = 0;
    }
    if (handles) {
      *handles = nullptr;
    }
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE RegisterInstance(IInspectable*,
                                             InstanceHandle* handle) override {
    if (handle) {
      *handle = 0;
    }
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE GetInitializationData(BSTR* data) override {
    if (data) {
      *data = nullptr;
    }
    return E_NOTIMPL;
  }

  HRESULT STDMETHODCALLTYPE AdviseVisualTreeChange(
      IVisualTreeServiceCallback* callback) override {
    ++advise_calls;
    if (on_advise) {
      on_advise();
    }
    auto failures = advise_failures_remaining.load(std::memory_order_relaxed);
    while (failures &&
           !advise_failures_remaining.compare_exchange_weak(
               failures, failures - 1, std::memory_order_relaxed)) {
    }
    if (failures) {
      return E_FAIL;
    }
    if (!callback || callback_) {
      return E_UNEXPECTED;
    }
    callback_ = callback;
    callback_->AddRef();
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE UnadviseVisualTreeChange(
      IVisualTreeServiceCallback* callback) override {
    ++unadvise_calls;
    if (on_unadvise) {
      on_unadvise();
    }
    auto failures =
        unadvise_failures_remaining.load(std::memory_order_relaxed);
    while (failures &&
           !unadvise_failures_remaining.compare_exchange_weak(
               failures, failures - 1, std::memory_order_relaxed)) {
    }
    if (failures) {
      return E_FAIL;
    }
    if (!callback_ || callback != callback_) {
      return E_UNEXPECTED;
    }
    if (on_callback_release) {
      on_callback_release();
    }
    callback_->Release();
    callback_ = nullptr;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetEnums(unsigned int* count,
                                     EnumType** values) override {
    if (count) {
      *count = 0;
    }
    if (values) {
      *values = nullptr;
    }
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE CreateInstance(BSTR,
                                           BSTR,
                                           InstanceHandle* handle) override {
    if (handle) {
      *handle = 0;
    }
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE GetPropertyValuesChain(
      InstanceHandle,
      unsigned int* source_count,
      PropertyChainSource** sources,
      unsigned int* property_count,
      PropertyChainValue** values) override {
    if (source_count) {
      *source_count = 0;
    }
    if (sources) {
      *sources = nullptr;
    }
    if (property_count) {
      *property_count = 0;
    }
    if (values) {
      *values = nullptr;
    }
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE SetProperty(InstanceHandle,
                                        InstanceHandle,
                                        unsigned int) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE ClearProperty(InstanceHandle,
                                          unsigned int) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE GetCollectionCount(InstanceHandle,
                                               unsigned int* count) override {
    if (count) {
      *count = 0;
    }
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE GetCollectionElements(
      InstanceHandle,
      unsigned int,
      unsigned int* count,
      CollectionElementValue** values) override {
    if (count) {
      *count = 0;
    }
    if (values) {
      *values = nullptr;
    }
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE AddChild(InstanceHandle,
                                     InstanceHandle,
                                     unsigned int) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE RemoveChild(InstanceHandle,
                                        unsigned int) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE ClearChildren(InstanceHandle) override {
    return E_NOTIMPL;
  }

  [[nodiscard]] IVisualTreeServiceCallback* retain_callback() noexcept {
    if (!callback_) {
      return nullptr;
    }
    callback_->AddRef();
    return callback_;
  }

  std::atomic<std::size_t> object_queries{0};
  std::atomic<std::size_t> handle_queries{0};
  std::atomic<std::size_t> query_interface_calls{0};
  std::atomic<std::size_t> query_interface_failures_remaining{0};
  std::atomic<std::size_t> advise_calls{0};
  std::atomic<std::size_t> unadvise_calls{0};
  std::atomic<std::size_t> advise_failures_remaining{0};
  std::atomic<std::size_t> unadvise_failures_remaining{0};
  std::function<void()> on_advise;
  std::function<void()> on_unadvise;
  std::function<void()> on_query_interface;
  std::function<void()> on_add_ref;
  std::function<void()> on_release;
  std::function<void()> on_callback_release;

 private:
  static HRESULT fail_output(IInspectable** value) noexcept {
    if (value) {
      *value = nullptr;
    }
    return E_FAIL;
  }

  std::atomic<ULONG> references_{1};
  IVisualTreeServiceCallback* callback_ = nullptr;
};

IUnknown* site_unknown(FakeSite& site) noexcept {
  return static_cast<IXamlDiagnostics*>(&site);
}

cq::bridge::CleanupAuthorizationBinding exact_cleanup_binding() noexcept {
  cq::bridge::CleanupAuthorizationBinding binding{
      .record_key = 77,
      .owner_thread_id = 11,
      .host_identity = 0xA0,
      .xaml_root_identity = 0xA1,
      .content_identity = 0xA2,
      .host_generation = 12,
      .root_generation = 13,
      .diagnostics_epoch = 14,
      .anchor_identity = 0xA3,
      .grid_identity = 0xA4,
  };
  binding.anchor_to_content.identities[0] = binding.anchor_identity;
  binding.anchor_to_content.identities[1] = binding.grid_identity;
  binding.anchor_to_content.identities[2] = binding.content_identity;
  binding.anchor_to_content.count = 3;
  binding.anchor_to_content.complete = true;
  return binding;
}

cq::bridge::CleanupAuthorizationEvidence exact_cleanup_evidence() noexcept {
  const auto binding = exact_cleanup_binding();
  return {
      .owner_thread_id = binding.owner_thread_id,
      .host_identity = binding.host_identity,
      .xaml_root_identity = binding.xaml_root_identity,
      .content_identity = binding.content_identity,
      .host_generation = binding.host_generation,
      .root_generation = binding.root_generation,
      .diagnostics_epoch = binding.diagnostics_epoch,
      .anchor_identity = binding.anchor_identity,
      .grid_identity = binding.grid_identity,
      .anchor_xaml_root_identity = binding.xaml_root_identity,
      .anchor_content_identity = binding.content_identity,
      .grid_xaml_root_identity = binding.xaml_root_identity,
      .grid_content_identity = binding.content_identity,
      .anchor_to_content = binding.anchor_to_content,
      .host_is_still_valid = true,
  };
}

class ExactCleanupEvidenceSource final
    : public cq::bridge::CleanupAuthorizationEvidenceSource {
 public:
  bool resolve(cq::bridge::CapsuleCleanupTrigger,
               cq::bridge::CleanupAuthorizationEvidence& evidence)
      noexcept override {
    evidence = exact_cleanup_evidence();
    return true;
  }
};

class ToggleCleanupEvidenceSource final
    : public cq::bridge::CleanupAuthorizationEvidenceSource {
 public:
  bool resolve(cq::bridge::CapsuleCleanupTrigger,
               cq::bridge::CleanupAuthorizationEvidence& evidence)
      noexcept override {
    if (!available) {
      return false;
    }
    evidence = exact_cleanup_evidence();
    return true;
  }

  bool available = false;
};

class DetachedCleanupAccess final : public cq::bridge::CapsuleCleanupAccess {
 public:
  cq::bridge::CapsuleParentRelationship capsule_parent_relationship()
      noexcept override {
    return cq::bridge::CapsuleParentRelationship::detached;
  }
  bool remove_capsule_from_recorded_parent() noexcept override { return false; }
  bool clear_columns() noexcept override { return true; }
  bool append_column(std::size_t) noexcept override { return true; }
  cq::bridge::OriginalChildState original_child_state(
      std::size_t) noexcept override {
    return cq::bridge::OriginalChildState::present_exact;
  }
  bool restore_original_child_column(std::size_t) noexcept override {
    return true;
  }
};

class ReentrantSetSiteCleanupAccess final
    : public cq::bridge::CapsuleCleanupAccess {
 public:
  ReentrantSetSiteCleanupAccess(cq::bridge::XamlTaskbarProbe& probe,
                                IUnknown* replacement) noexcept
      : probe_(probe), replacement_(replacement) {}

  cq::bridge::CapsuleParentRelationship capsule_parent_relationship()
      noexcept override {
    return cq::bridge::CapsuleParentRelationship::detached;
  }
  bool remove_capsule_from_recorded_parent() noexcept override {
    return false;
  }
  bool clear_columns() noexcept override { return true; }
  bool append_column(std::size_t) noexcept override { return true; }
  cq::bridge::OriginalChildState original_child_state(
      std::size_t) noexcept override {
    return cq::bridge::OriginalChildState::present_exact;
  }
  bool restore_original_child_column(std::size_t) noexcept override {
    replacement_result = probe_.set_site(replacement_);
    return true;
  }

  long replacement_result = E_FAIL;

 private:
  cq::bridge::XamlTaskbarProbe& probe_;
  IUnknown* const replacement_;
};

[[noreturn]] void fail(const char* expression, int line) {
  std::cerr << "line " << line << ": expected " << expression << '\n';
  std::exit(EXIT_FAILURE);
}

#define EXPECT(expression)            \
  do {                                \
    if (!(expression)) {              \
      fail(#expression, __LINE__);    \
    }                                 \
  } while (false)

void accepts_only_the_exact_taskbar_structure() {
  const VisualNodeFacts facts{
      .type_name = L"SystemTray.SystemTrayFrame",
      .parent_is_grid = true,
      .belongs_to_validated_taskbar_window = true,
  };

  EXPECT(evaluate_visual_node(facts) == ProbeDecision::accept);
}

void winrt_pending_status_mapping_never_invents_terminal_proof() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::map_pending_status_for_test(0) ==
         cq::bridge::PendingAsyncStatus::started);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::map_pending_status_for_test(1) ==
         cq::bridge::PendingAsyncStatus::completed);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::map_pending_status_for_test(2) ==
         cq::bridge::PendingAsyncStatus::canceled);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::map_pending_status_for_test(3) ==
         cq::bridge::PendingAsyncStatus::error);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::map_pending_status_for_test(99) ==
         cq::bridge::PendingAsyncStatus::unknown);
}

void capsule_contract_is_fixed_and_noninteractive() {
  static_assert(cq::bridge::kProbeCapsuleWidthDip == 190.0);
  static_assert(cq::bridge::kProbeCapsuleHeightDip == 36.0);
  static_assert(cq::bridge::kProbeCapsuleDisplayMilliseconds == 5000);
  EXPECT(std::wstring{cq::bridge::kProbeCapsuleText} == L"CQ \u00b7 PROBE");
}

void rejects_untrusted_structure_dimensions() {
  const std::vector<VisualNodeFacts> cases{
      {L"SystemTray.SystemTrayFrame", false, true},
      {L"SystemTray.SystemTrayFrame", true, false},
      {L"SystemTray.SystemTrayFrame", false, false},
  };

  for (const auto& facts : cases) {
    EXPECT(evaluate_visual_node(facts) == ProbeDecision::ignore);
  }
}

void rejects_empty_malformed_unicode_control_and_near_match_names() {
  std::wstring embedded_null{L"SystemTray.SystemTrayFrame"};
  embedded_null.push_back(L'\0');
  embedded_null.append(L"suffix");

  const std::vector<std::wstring> rejected{
      L"",
      L"SystemTray.SystemTrayFram",
      L"SystemTray.SystemTrayFrame2",
      L"systemtray.SystemTrayFrame",
      L" SystemTray.SystemTrayFrame",
      L"SystemTray.SystemTrayFrame ",
      L"SystemTray/SystemTrayFrame",
      L"SystemTray.SystemTray\nFrame",
      L"SystemTray.SystemTray\x001fFrame",
      L"SystemTray.SystemTrayFram\u00e9",
      L"SystemTray.SystemTrayFrame\U0001f642",
      L"SystemTray\uff0eSystemTrayFrame",
      std::move(embedded_null),
      std::wstring(4096, L'A'),
  };

  for (const auto& name : rejected) {
    const VisualNodeFacts facts{name, true, true};
    EXPECT(evaluate_visual_node(facts) == ProbeDecision::ignore);
  }
}

void expect_current_site(cq::bridge::XamlTaskbarProbe& probe,
                         IUnknown* expected) {
  void* actual = nullptr;
  EXPECT(probe.get_site(&IID_IUnknown, &actual) == S_OK);
  EXPECT(actual == static_cast<void*>(expected));
  static_cast<IUnknown*>(actual)->Release();
}

bool current_site_is(cq::bridge::XamlTaskbarProbe& probe,
                     IUnknown* expected) noexcept {
  void* actual = nullptr;
  if (probe.get_site(&IID_IUnknown, &actual) != S_OK) {
    return false;
  }
  const bool matches = actual == static_cast<void*>(expected);
  static_cast<IUnknown*>(actual)->Release();
  return matches;
}

bool activate_watched_site(cq::bridge::XamlTaskbarProbe& probe,
                           FakeSite& site,
                           std::uint64_t epoch) noexcept {
  return probe.set_site(site_unknown(site)) == S_OK &&
         cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
             probe, epoch) &&
         probe.advise_watcher();
}

cq::bridge::ProbeSessionAdmissionSnapshot session_admission_snapshot(
    cq::bridge::XamlTaskbarProbe& probe) {
  const auto snapshot =
      cq::bridge::XamlTaskbarProbeTestPeer::session_admission_snapshot(probe);
  EXPECT(snapshot.has_value());
  return *snapshot;
}

void expect_empty_remove_baseline(cq::bridge::XamlTaskbarProbe& probe) {
  const auto snapshot = session_admission_snapshot(probe);
  EXPECT(snapshot.state == cq::bridge::ProbeSessionStateForTest::empty);
  EXPECT(snapshot.pending_open);
  EXPECT(snapshot.root_open);
  EXPECT(!snapshot.callback_open);
  EXPECT(!snapshot.transition_pending);
  EXPECT(!snapshot.transition_driver_active);
}

void expect_all_admissions_open(
    cq::bridge::XamlTaskbarProbe& probe,
    cq::bridge::ProbeSessionStateForTest expected_state) {
  const auto snapshot = session_admission_snapshot(probe);
  EXPECT(snapshot.state == expected_state);
  EXPECT(snapshot.pending_open);
  EXPECT(snapshot.root_open);
  EXPECT(snapshot.callback_open);
  EXPECT(!snapshot.transition_pending);
  EXPECT(!snapshot.transition_driver_active);
}

void expect_retryable_closed_transition(
    cq::bridge::XamlTaskbarProbe& probe) {
  const auto snapshot = session_admission_snapshot(probe);
  EXPECT(!snapshot.pending_open);
  EXPECT(!snapshot.root_open);
  EXPECT(!snapshot.callback_open);
  EXPECT(snapshot.transition_pending);
  EXPECT(!snapshot.transition_driver_active);
}

bool send_valid_add(IVisualTreeServiceCallback* callback) noexcept {
  if (!callback) {
    return false;
  }
  BSTR type = SysAllocString(cq::bridge::kSystemTrayFrameType);
  if (!type) {
    return false;
  }
  const ParentChildRelation relation{.Parent = 1, .Child = 2,
                                     .ChildIndex = 0};
  const VisualElement visual{.Handle = 2, .SrcInfo = {}, .Type = type,
                             .Name = nullptr, .NumChildren = 0};
  const bool succeeded =
      callback->OnVisualTreeChange(relation, visual, Add) == S_OK;
  SysFreeString(type);
  return succeeded;
}

struct WatcherReleaseObservation final {
  cq::bridge::XamlTaskbarProbe* probe = nullptr;
  std::atomic<std::size_t> watcher_releases{0};
  std::atomic<std::size_t> service_releases{0};
  std::atomic<bool> all_unlocked{true};
};

struct UnloadBoundaryObservation final {
  std::atomic<std::size_t> releases{0};
  std::size_t baseline = 0;
  std::atomic<bool> owners_released_before_boundary{false};
};

void observe_unload_boundary(void* value) noexcept {
  auto& observation = *static_cast<UnloadBoundaryObservation*>(value);
  observation.owners_released_before_boundary.store(
      observation.releases.load(std::memory_order_acquire) >
          observation.baseline,
      std::memory_order_release);
}

struct TapPrecommitBarrier final {
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool proceed = false;
};

struct UnloadRollbackBarrier final {
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool proceed = false;
};

struct UnloadRollbackObservation final {
  cq::bridge::XamlTaskbarProbe* probe = nullptr;
  FakeSite* site = nullptr;
  std::atomic<bool> saw_unloading{false};
  std::atomic<bool> active_was_exact{false};
  std::atomic<bool> factory_reopened{false};
  std::atomic<bool> tap_set_site_was_busy{false};
};

struct SuccessfulUnloadObservation final {
  cq::bridge::XamlTaskbarProbe* probe = nullptr;
  std::atomic<bool> impl_was_absent{false};
  std::atomic<bool> unload_mutex_was_available{false};
  std::atomic<bool> reentrant_unload_was_rejected{false};
};

struct ResourceSnapshotBarrier final {
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool proceed = false;
};

struct ManagerOwnerObservation final {
  cq::bridge::ProbeCapsuleManager* manager = nullptr;
  std::atomic<bool> destroyed{false};
  std::atomic<bool> mutex_available{false};
  std::atomic<bool> reentered_count{false};
  std::atomic<std::size_t> observed_count{99};
};

struct ManagerRetainedOwner final {
  explicit ManagerRetainedOwner(ManagerOwnerObservation& value) noexcept
      : observation(value) {}
  ~ManagerRetainedOwner() {
    observation.destroyed.store(true, std::memory_order_release);
    if (observation.manager &&
        cq::bridge::XamlTaskbarProbeTestPeer::manager_mutex_available(
            *observation.manager)) {
      observation.mutex_available.store(true, std::memory_order_release);
      observation.observed_count.store(
          observation.manager->tracked_element_count(),
          std::memory_order_release);
      observation.reentered_count.store(true, std::memory_order_release);
    }
  }
  ManagerOwnerObservation& observation;
};

struct ManagerRemoveSnapshotBarrier final {
  std::mutex mutex;
  std::condition_variable condition;
  std::size_t arrived = 0;
  bool proceed = false;
};

struct ManagerAttachAdmissionBarrier final {
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool proceed = false;
};

struct ManagerFinalReleaseBarrier final {
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool proceed = false;
};

struct BlockingManagerRetainedOwner final {
  explicit BlockingManagerRetainedOwner(
      ManagerFinalReleaseBarrier& value) noexcept
      : barrier(value) {}

  ~BlockingManagerRetainedOwner() {
    std::unique_lock lock(barrier.mutex);
    barrier.entered = true;
    barrier.condition.notify_all();
    barrier.condition.wait(lock, [&] { return barrier.proceed; });
  }

  ManagerFinalReleaseBarrier& barrier;
};

struct HandoffDestructionObservation final {
  std::atomic<std::size_t> destructions{0};
  std::atomic<bool> mutex_available{false};
};

void observe_handoff_destruction(void* value) noexcept {
  auto& observation = *static_cast<HandoffDestructionObservation*>(value);
  observation.destructions.fetch_add(1, std::memory_order_release);
  observation.mutex_available.store(
      cq::bridge::XamlTaskbarProbeTestPeer::handoff_mutex_available(),
      std::memory_order_release);
}

class FakeHandoffWin32Boundary final
    : public cq::bridge::HandoffWin32Boundary {
 public:
  bool acquire_self_module(std::uintptr_t callback_address,
                           std::uintptr_t& module) noexcept override {
    trace.emplace_back("acquire");
    observed_callback = callback_address;
    if (!acquire_succeeds) {
      module = 0;
      return false;
    }
    module = module_handle;
    return true;
  }

  bool publish_retirement_token(
      const cq::bridge::HandoffRetirementTarget& target) noexcept override {
    trace.emplace_back("publish");
    ++publish_calls;
    publish_observed_mutex_available =
        cq::bridge::XamlTaskbarProbeTestPeer::handoff_mutex_available();
    observed_target = target;
    if (!publish_succeeds || published_token != 0 ||
        !target.retirement_token) {
      return false;
    }
    published_token = target.retirement_token;
    return true;
  }

  std::uintptr_t install_callwndproc(
      std::uintptr_t callback_address,
      std::uint32_t thread_id) noexcept override {
    trace.emplace_back("install");
    observed_callback = callback_address;
    observed_thread = thread_id;
    return install_succeeds ? hook_handle : 0;
  }

  bool send_action(const cq::bridge::HandoffRetirementTarget& target,
                   std::uint32_t message,
                   std::uintptr_t cookie) noexcept override {
    trace.emplace_back("send");
    observed_target = target;
    observed_message = message;
    if (!retirement_token_matches(target)) {
      return false;
    }
    if (replace_token_before_dispatch) {
      published_token = target.retirement_token ==
                                (std::numeric_limits<std::uintptr_t>::max)()
                            ? 1
                            : target.retirement_token + 1;
    }
    const bool dispatched =
        cq::bridge::XamlTaskbarProbeTestPeer::dispatch_handoff_for_test(cookie);
    return dispatched && retirement_token_matches(target);
  }

  bool retirement_token_matches(
      const cq::bridge::HandoffRetirementTarget& target) noexcept override {
    return target.retirement_token != 0 &&
           published_token == target.retirement_token;
  }

  bool unhook(std::uintptr_t hook) noexcept override {
    trace.emplace_back("unhook");
    ++unhook_calls;
    observed_hook = hook;
    return unhook_succeeds;
  }

  bool send_barrier(
      const cq::bridge::HandoffRetirementTarget& target) noexcept override {
    trace.emplace_back("barrier");
    ++barrier_calls;
    observed_target = target;
    return barrier_succeeds &&
           published_token == target.retirement_token;
  }

  cq::bridge::HandoffTokenClearResult clear_retirement_token(
      const cq::bridge::HandoffRetirementTarget& target) noexcept override {
    trace.emplace_back("clear");
    ++clear_calls;
    clear_observed_mutex_available =
        cq::bridge::XamlTaskbarProbeTestPeer::handoff_mutex_available();
    observed_target = target;
    if (!clear_succeeds || !target.retirement_token ||
        published_token != target.retirement_token) {
      return cq::bridge::HandoffTokenClearResult::retryable_failure;
    }
    published_token = 0;
    ++successful_clear_calls;
    return cq::bridge::HandoffTokenClearResult::cleared;
  }

  void release_module(std::uintptr_t module) noexcept override {
    trace.emplace_back("release");
    ++release_calls;
    observed_module = module;
    release_observed_mutex_available =
        cq::bridge::XamlTaskbarProbeTestPeer::handoff_mutex_available();
  }

  bool acquire_succeeds = true;
  bool publish_succeeds = true;
  bool install_succeeds = true;
  bool unhook_succeeds = true;
  bool barrier_succeeds = true;
  bool clear_succeeds = true;
  bool replace_token_before_dispatch = false;
  bool publish_observed_mutex_available = false;
  bool clear_observed_mutex_available = false;
  bool release_observed_mutex_available = false;
  std::uintptr_t module_handle = 0x5151;
  std::uintptr_t hook_handle = 0x6161;
  std::uint64_t published_token = 0;
  std::uint64_t host_generation = 0;
  std::uintptr_t observed_callback = 0;
  std::uintptr_t observed_hook = 0;
  std::uintptr_t observed_module = 0;
  std::uint32_t observed_thread = 0;
  std::uint32_t observed_message = 0;
  std::size_t unhook_calls = 0;
  std::size_t barrier_calls = 0;
  std::size_t publish_calls = 0;
  std::size_t clear_calls = 0;
  std::size_t successful_clear_calls = 0;
  std::size_t release_calls = 0;
  cq::bridge::HandoffRetirementTarget observed_target{};
  std::vector<std::string> trace;
};

struct ProbePendingActionObservation final {
  std::atomic<std::size_t> status_calls{0};
  std::atomic<std::size_t> cancel_calls{0};
  std::atomic<std::size_t> destructions{0};
};

class FakeProbePendingAction final : public cq::bridge::PendingAsyncAction {
 public:
  explicit FakeProbePendingAction(
      std::shared_ptr<ProbePendingActionObservation> value) noexcept
      : observation(std::move(value)) {}

  ~FakeProbePendingAction() override {
    observation->destructions.fetch_add(1, std::memory_order_release);
  }

  cq::bridge::PendingAsyncStatus status() override {
    observation->status_calls.fetch_add(1, std::memory_order_release);
    return current_status.load(std::memory_order_acquire);
  }

  void cancel() override {
    observation->cancel_calls.fetch_add(1, std::memory_order_release);
  }

  std::shared_ptr<ProbePendingActionObservation> observation;
  std::atomic<cq::bridge::PendingAsyncStatus> current_status{
      cq::bridge::PendingAsyncStatus::started};
};

class FakeProbePendingBoundary final
    : public cq::bridge::PendingModuleBoundary,
      public cq::bridge::PendingDispatchBoundary {
 public:
  bool acquire_self_module(std::uintptr_t& module) noexcept override {
    ++acquire_calls;
    module = module_handle;
    return true;
  }

  void release_module(std::uintptr_t module) noexcept override {
    ++release_calls;
    observed_module = module;
    if (probe) {
      release_observed_mutex_available =
          cq::bridge::XamlTaskbarProbeTestPeer::pending_mutex_available(
              *probe);
      release_observed_retiring =
          cq::bridge::XamlTaskbarProbeTestPeer::pending_retiring_count(
              *probe);
      release_observed_pending_count =
          probe->resource_counts().pending_actions;
    }
  }

  std::shared_ptr<cq::bridge::PendingAsyncAction> queue(
      std::shared_ptr<cq::bridge::PendingDispatchCallback> callback) override {
    ++queue_calls;
    retained_callback = std::move(callback);
    return std::move(next_action);
  }

  cq::bridge::XamlTaskbarProbe* probe = nullptr;
  std::shared_ptr<cq::bridge::PendingAsyncAction> next_action;
  std::shared_ptr<cq::bridge::PendingDispatchCallback> retained_callback;
  std::uintptr_t module_handle = 0x7171;
  std::uintptr_t observed_module = 0;
  std::size_t acquire_calls = 0;
  std::size_t queue_calls = 0;
  std::size_t release_calls = 0;
  bool release_observed_mutex_available = false;
  std::size_t release_observed_retiring = 0;
  std::size_t release_observed_pending_count = 0;
};

void block_manager_remove_snapshot(void* value) noexcept {
  auto& barrier = *static_cast<ManagerRemoveSnapshotBarrier*>(value);
  std::unique_lock lock(barrier.mutex);
  ++barrier.arrived;
  barrier.condition.notify_all();
  barrier.condition.wait(lock, [&] { return barrier.proceed; });
}

void block_resource_snapshot(void* value) noexcept {
  auto& barrier = *static_cast<ResourceSnapshotBarrier*>(value);
  std::unique_lock lock(barrier.mutex);
  barrier.entered = true;
  barrier.condition.notify_all();
  barrier.condition.wait(lock, [&] { return barrier.proceed; });
}

void block_manager_attach_admission(void* value) noexcept {
  auto& barrier = *static_cast<ManagerAttachAdmissionBarrier*>(value);
  std::unique_lock lock(barrier.mutex);
  barrier.entered = true;
  barrier.condition.notify_all();
  barrier.condition.wait(lock, [&] { return barrier.proceed; });
}

bool reset_release_permit_without_access_violation(
    std::optional<cq::bridge::ProbeCapsuleManager::ReleasePermit>*
        permit) noexcept {
#if defined(_MSC_VER)
  __try {
    permit->reset();
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
#else
  permit->reset();
  return true;
#endif
}

void block_unload_rollback_published(void* value) noexcept {
  auto& barrier = *static_cast<UnloadRollbackBarrier*>(value);
  std::unique_lock lock(barrier.mutex);
  barrier.entered = true;
  barrier.condition.notify_all();
  barrier.condition.wait(lock, [&] { return barrier.proceed; });
}

void block_tap_precommit(void* value) noexcept {
  auto& barrier = *static_cast<TapPrecommitBarrier*>(value);
  std::unique_lock lock(barrier.mutex);
  barrier.entered = true;
  barrier.condition.notify_all();
  barrier.condition.wait(lock, [&] { return barrier.proceed; });
}

constexpr CLSID kTestProbeTapClsid = {
    0x93d08475,
    0xfa45,
    0x461a,
    {0xb8, 0xa5, 0x9a, 0x8d, 0x8a, 0x9c, 0x11, 0x7a}};

long get_test_tap_factory(void** factory) noexcept {
  return cq::bridge::tap_get_class_object(
      &kTestProbeTapClsid, &IID_IClassFactory, factory);
}

void observe_unload_rollback(void* value) noexcept {
  auto& observation = *static_cast<UnloadRollbackObservation*>(value);
  if (!observation.probe || !observation.site) {
    return;
  }
  observation.saw_unloading.store(
      cq::bridge::XamlTaskbarProbeTestPeer::session_is_unloading(
          *observation.probe),
      std::memory_order_release);
  observation.active_was_exact.store(
      cq::bridge::XamlTaskbarProbeTestPeer::active_probe() ==
          observation.probe,
      std::memory_order_release);

  void* raw_factory = nullptr;
  if (get_test_tap_factory(&raw_factory) != S_OK || !raw_factory) {
    return;
  }
  observation.factory_reopened.store(true, std::memory_order_release);
  auto* factory = static_cast<IClassFactory*>(raw_factory);
  void* raw_tap = nullptr;
  if (factory->CreateInstance(nullptr, __uuidof(IObjectWithSite), &raw_tap) ==
          S_OK &&
      raw_tap) {
    auto* tap = static_cast<IObjectWithSite*>(raw_tap);
    const auto queries =
        observation.site->query_interface_calls.load(std::memory_order_acquire);
    observation.tap_set_site_was_busy.store(
        tap->SetSite(site_unknown(*observation.site)) ==
                HRESULT_FROM_WIN32(ERROR_BUSY) &&
            observation.site->query_interface_calls.load(
                std::memory_order_acquire) == queries,
        std::memory_order_release);
    tap->Release();
  }
  factory->Release();
}

void observe_successful_unload(void* value) noexcept {
  auto& observation = *static_cast<SuccessfulUnloadObservation*>(value);
  if (!observation.probe) {
    return;
  }
  observation.impl_was_absent.store(
      !cq::bridge::XamlTaskbarProbeTestPeer::has_impl(*observation.probe),
      std::memory_order_release);
  observation.unload_mutex_was_available.store(
      cq::bridge::XamlTaskbarProbeTestPeer::unload_mutex_available(
          *observation.probe),
      std::memory_order_release);
  observation.reentrant_unload_was_rejected.store(
      !observation.probe->release_for_unload(), std::memory_order_release);
}

void observe_watcher_release(void* value) noexcept {
  auto& observation = *static_cast<WatcherReleaseObservation*>(value);
  ++observation.watcher_releases;
  if (!observation.probe ||
      !cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_mutex_available(
          *observation.probe)) {
    observation.all_unlocked.store(false);
  }
}

void observe_service_release(WatcherReleaseObservation& observation) {
  ++observation.service_releases;
  if (!observation.probe ||
      !cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_mutex_available(
          *observation.probe)) {
    observation.all_unlocked.store(false);
  }
}

void set_site_replacement_waits_for_diagnostics_drain_then_installs() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  EXPECT(probe.set_site(site_unknown(old_site)) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
      probe, 41));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(old_identity.has_value());

  long replacement_result = E_FAIL;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::while_diagnostics_lease(
      probe, [&] {
        replacement_result = probe.set_site(site_unknown(replacement_site));
      }));
  EXPECT(replacement_result == HRESULT_FROM_WIN32(ERROR_BUSY));
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  expect_current_site(probe, site_unknown(replacement_site));
  const auto replacement_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(replacement_identity.has_value());
  EXPECT(*replacement_identity != *old_identity);
}

void set_site_and_advise_claim_before_external_com() {
  {
    FakeSite old_site;
    FakeSite replacement_site;
    FakeSite reentrant_target;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(activate_watched_site(probe, old_site, 45));

    long reentrant_result = S_OK;
    bool armed = true;
    replacement_site.on_query_interface = [&] {
      if (!std::exchange(armed, false)) {
        return;
      }
      reentrant_result = probe.set_site(site_unknown(reentrant_target));
    };

    EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
    EXPECT(reentrant_result == HRESULT_FROM_WIN32(ERROR_BUSY));
    EXPECT(reentrant_target.query_interface_calls.load() == 0);
    expect_current_site(probe, site_unknown(replacement_site));
  }

  {
    FakeSite site;
    FakeSite reentrant_target;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(probe.set_site(site_unknown(site)) == S_OK);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
        probe, 46));

    long reentrant_result = S_OK;
    bool armed = true;
    site.on_add_ref = [&] {
      if (!std::exchange(armed, false)) {
        return;
      }
      reentrant_result = probe.set_site(site_unknown(reentrant_target));
    };

    EXPECT(probe.advise_watcher());
    EXPECT(reentrant_result == HRESULT_FROM_WIN32(ERROR_BUSY));
    EXPECT(reentrant_target.query_interface_calls.load() == 0);
    expect_current_site(probe, site_unknown(site));
  }
}

void site_preparation_keeps_old_callbacks_live_until_commit_or_rollback() {
  for (const bool fail_query : {false, true}) {
    FakeSite old_site;
    FakeSite target_site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(activate_watched_site(probe, old_site,
                                 fail_query ? 153 : 152));
    auto* callback = old_site.retain_callback();
    EXPECT(callback != nullptr);

    std::mutex mutex;
    std::condition_variable condition;
    bool query_entered = false;
    bool allow_query = false;
    FakeSite& queried_site = fail_query ? target_site : old_site;
    if (fail_query) {
      target_site.query_interface_failures_remaining.store(1);
    }
    queried_site.on_query_interface = [&] {
      std::unique_lock lock(mutex);
      query_entered = true;
      condition.notify_all();
      condition.wait(lock, [&] { return allow_query; });
    };

    long result = S_OK;
    std::thread setter([&] {
      result = probe.set_site(site_unknown(queried_site));
    });
    bool entered = false;
    {
      std::unique_lock lock(mutex);
      entered = condition.wait_for(lock, std::chrono::seconds(2),
                                   [&] { return query_entered; });
    }
    const auto queries_before = old_site.object_queries.load();
    bool callback_during_preparation = false;
    if (entered) {
      callback_during_preparation = send_valid_add(callback);
    }
    {
      std::lock_guard lock(mutex);
      allow_query = true;
    }
    condition.notify_all();
    setter.join();
    queried_site.on_query_interface = nullptr;

    EXPECT(entered);
    EXPECT(callback_during_preparation);
    EXPECT(old_site.object_queries.load() == queries_before + 1);
    if (fail_query) {
      EXPECT(FAILED(result));
      EXPECT(send_valid_add(callback));
      EXPECT(old_site.object_queries.load() == queries_before + 2);
      expect_current_site(probe, site_unknown(old_site));
    } else {
      EXPECT(result == S_OK);
      expect_current_site(probe, site_unknown(old_site));
    }
    callback->Release();
    EXPECT(probe.unadvise_watcher().status ==
           cq::bridge::ProbeCleanupStatus::complete);
  }
}

void activation_failures_restore_exact_initial_site_for_retry() {
  for (const auto allocation_stage : {1U, 2U}) {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(probe.set_site(site_unknown(site)) == S_OK);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               fail_next_activation_allocation(probe, allocation_stage));

    EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
                activate_diagnostics_session(probe, 47 + allocation_stage));
    expect_current_site(probe, site_unknown(site));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               activate_diagnostics_session(probe, 57 + allocation_stage));
  }

  FakeSite site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(probe.set_site(site_unknown(site)) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             fail_next_activation_publication(probe));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
      probe, 60));
  expect_current_site(probe, site_unknown(site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
      probe, 61));
}

void remove_publishes_empty_baseline_from_every_admitted_state() {
  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

    EXPECT(probe.set_site(site_unknown(site)) == S_OK);
    EXPECT(probe.set_site(nullptr) == S_OK);
    expect_empty_remove_baseline(probe);
  }

  {
    FakeSite site;
    FakeSite replacement_site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

    EXPECT(probe.set_site(site_unknown(site)) == S_OK);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               activate_diagnostics_session(probe, 201));
    EXPECT(probe.set_site(nullptr) == S_OK);
    expect_empty_remove_baseline(probe);

    EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               activate_diagnostics_session(probe, 211));
    const auto unwatched = session_admission_snapshot(probe);
    EXPECT(unwatched.state ==
           cq::bridge::ProbeSessionStateForTest::current_unwatched);
    EXPECT(unwatched.pending_open);
    EXPECT(unwatched.root_open);
    EXPECT(!unwatched.callback_open);
    EXPECT(!unwatched.transition_pending);
    EXPECT(probe.advise_watcher());
    expect_all_admissions_open(
        probe, cq::bridge::ProbeSessionStateForTest::current);
  }

  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

    EXPECT(activate_watched_site(probe, site, 202));
    EXPECT(probe.set_site(nullptr) == S_OK);
    expect_empty_remove_baseline(probe);
  }
}

void remove_reattach_reopens_every_admission_for_multiple_cycles() {
  FakeSite first_site;
  FakeSite second_site;
  FakeSite third_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  EXPECT(activate_watched_site(probe, first_site, 203));
  expect_all_admissions_open(
      probe, cq::bridge::ProbeSessionStateForTest::current);

  EXPECT(probe.set_site(nullptr) == S_OK);
  expect_empty_remove_baseline(probe);
  EXPECT(activate_watched_site(probe, second_site, 204));
  expect_all_admissions_open(
      probe, cq::bridge::ProbeSessionStateForTest::current);

  EXPECT(probe.set_site(nullptr) == S_OK);
  expect_empty_remove_baseline(probe);
  EXPECT(activate_watched_site(probe, third_site, 205));
  expect_all_admissions_open(
      probe, cq::bridge::ProbeSessionStateForTest::current);
}

void held_pending_and_root_leases_keep_remove_retryable() {
  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(activate_watched_site(probe, site, 206));

    long remove_result = S_OK;
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::while_pending_admission(
        probe, [&] { remove_result = probe.set_site(nullptr); }));
    EXPECT(remove_result == HRESULT_FROM_WIN32(ERROR_BUSY));
    expect_retryable_closed_transition(probe);
    EXPECT(probe.set_site(nullptr) == S_OK);
    expect_empty_remove_baseline(probe);
  }

  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(activate_watched_site(probe, site, 207));

    long remove_result = S_OK;
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::while_root_admission(
        probe, [&] { remove_result = probe.set_site(nullptr); }));
    EXPECT(remove_result == HRESULT_FROM_WIN32(ERROR_BUSY));
    expect_retryable_closed_transition(probe);
    EXPECT(probe.set_site(nullptr) == S_OK);
    expect_empty_remove_baseline(probe);
  }
}

void remove_failures_stay_closed_and_retry_exact_transition() {
  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(activate_watched_site(probe, site, 208));
    site.unadvise_failures_remaining.store(1);

    EXPECT(FAILED(probe.set_site(nullptr)));
    expect_retryable_closed_transition(probe);
    EXPECT(probe.set_site(nullptr) == S_OK);
    expect_empty_remove_baseline(probe);
  }

  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    auto authority = std::make_shared<cq::bridge::CleanupAuthorityGate>();
    EXPECT(activate_watched_site(probe, site, 209));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               attach_synthetic_retained_root(
                   probe, authority, [] { return true; }));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_root_retirement(
        probe));

    EXPECT(FAILED(probe.set_site(nullptr)));
    expect_retryable_closed_transition(probe);
    EXPECT(probe.set_site(nullptr) == S_OK);
    expect_empty_remove_baseline(probe);
  }
}

void partial_remove_baseline_failure_recloses_and_retries() {
  FakeSite site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(probe.set_site(site_unknown(site)) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
      probe, 212));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             fail_next_remove_admission_baseline(probe));

  EXPECT(FAILED(probe.set_site(nullptr)));
  expect_retryable_closed_transition(probe);
  EXPECT(probe.set_site(nullptr) == S_OK);
  expect_empty_remove_baseline(probe);
}

void quiesced_session_keeps_every_admission_closed() {
  FakeSite site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(activate_watched_site(probe, site, 210));
  EXPECT(probe.unadvise_watcher().status ==
         cq::bridge::ProbeCleanupStatus::complete);

  const auto before = session_admission_snapshot(probe);
  EXPECT(before.state == cq::bridge::ProbeSessionStateForTest::quiesced);
  EXPECT(!before.pending_open);
  EXPECT(!before.root_open);
  EXPECT(!before.callback_open);
  EXPECT(!before.transition_pending);
  EXPECT(!before.transition_driver_active);

  EXPECT(probe.set_site(nullptr) == HRESULT_FROM_WIN32(ERROR_BUSY));
  const auto after = session_admission_snapshot(probe);
  EXPECT(after.state == cq::bridge::ProbeSessionStateForTest::quiesced);
  EXPECT(!after.pending_open);
  EXPECT(!after.root_open);
  EXPECT(!after.callback_open);
  EXPECT(!after.transition_pending);
  EXPECT(!after.transition_driver_active);
}

void set_site_replacement_waits_for_cleanup_final_port_then_installs() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  auto authority = std::make_shared<cq::bridge::CleanupAuthorityGate>();

  EXPECT(probe.set_site(site_unknown(old_site)) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
      probe, 51));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(old_identity.has_value());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_cleanup_authority(
      probe, authority));

  cq::bridge::CapsuleCleanupWorkflow workflow(exact_cleanup_binding(),
                                               authority, 0, 1);
  ExactCleanupEvidenceSource evidence;
  ReentrantSetSiteCleanupAccess access(probe,
                                       site_unknown(replacement_site));
  EXPECT(workflow.attempt(cq::bridge::CapsuleCleanupTrigger::shutdown,
                          evidence, access) ==
         cq::bridge::CapsuleTransactionResult::complete);
  EXPECT(access.replacement_result == HRESULT_FROM_WIN32(ERROR_BUSY));
  EXPECT(!authority->accepting());
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  expect_current_site(probe, site_unknown(replacement_site));
  const auto replacement_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(replacement_identity.has_value());
  EXPECT(*replacement_identity != *old_identity);
}

void stale_watcher_cannot_borrow_replacement_diagnostics_adapter() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  EXPECT(probe.set_site(site_unknown(old_site)) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
      probe, 61));
  EXPECT(probe.advise_watcher());
  auto* stale_callback = old_site.retain_callback();
  EXPECT(stale_callback != nullptr);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  BSTR type = SysAllocString(cq::bridge::kSystemTrayFrameType);
  EXPECT(type != nullptr);
  const ParentChildRelation relation{.Parent = 1, .Child = 2,
                                     .ChildIndex = 0};
  const VisualElement visual{.Handle = 2, .SrcInfo = {}, .Type = type,
                             .Name = nullptr, .NumChildren = 0};
  EXPECT(stale_callback->OnVisualTreeChange(relation, visual, Add) == S_OK);
  EXPECT(replacement_site.object_queries.load() == 0);
  EXPECT(replacement_site.handle_queries.load() == 0);
  stale_callback->Release();

  auto* current_callback = replacement_site.retain_callback();
  EXPECT(current_callback != nullptr);
  EXPECT(current_callback->OnVisualTreeChange(relation, visual, Add) == S_OK);
  EXPECT(replacement_site.object_queries.load() == 1);
  current_callback->Release();
  SysFreeString(type);
  EXPECT(probe.unadvise_watcher().status ==
         cq::bridge::ProbeCleanupStatus::complete);
}

void unadvise_waits_for_cleanup_authority_then_enters_cleanup_only() {
  FakeSite site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  auto authority = std::make_shared<cq::bridge::CleanupAuthorityGate>();

  EXPECT(probe.set_site(site_unknown(site)) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
      probe, 71));
  EXPECT(probe.advise_watcher());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_cleanup_authority(
      probe, authority));
  cq::bridge::CapsuleCleanupWorkflow workflow(exact_cleanup_binding(),
                                               authority, 0, 0);
  ExactCleanupEvidenceSource evidence;
  auto authorization =
      workflow.authorize(cq::bridge::CapsuleCleanupTrigger::shutdown,
                         evidence);
  EXPECT(authorization.has_value());

  const auto blocked = probe.unadvise_watcher();
  EXPECT(blocked.status ==
         cq::bridge::ProbeCleanupStatus::unsafe_in_flight);
  EXPECT(blocked.remaining != 0);
  EXPECT(!authority->accepting());
  authorization.reset();

  const auto drained = probe.unadvise_watcher();
  EXPECT(drained.status == cq::bridge::ProbeCleanupStatus::complete);
  EXPECT(drained.remaining == 0);
  EXPECT(authority->mode() ==
         cq::bridge::CleanupAuthorityMode::cleanup_only);
}

void replacement_cleans_retained_root_before_target_publication() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeSite wrong_target;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  auto authority = std::make_shared<cq::bridge::CleanupAuthorityGate>();
  std::size_t cleanup_attempts = 0;
  bool cleanup_succeeds = false;

  EXPECT(activate_watched_site(probe, old_site, 81));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(old_identity.has_value());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_synthetic_retained_root(
      probe, authority, [&] {
        ++cleanup_attempts;
        return cleanup_succeeds;
      }));
  EXPECT(probe.resource_counts().capsules == 1);
  EXPECT(probe.resource_counts().host_bindings == 1);

  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
  EXPECT(cleanup_attempts == 1);
  EXPECT(replacement_site.advise_calls.load() == 0);
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);
  EXPECT(probe.resource_counts().capsules == 1);
  EXPECT(probe.resource_counts().host_bindings == 1);

  cleanup_succeeds = true;
  EXPECT(FAILED(probe.set_site(site_unknown(wrong_target))));
  EXPECT(cleanup_attempts == 1);
  EXPECT(wrong_target.advise_calls.load() == 0);
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(probe.resource_counts().capsules == 1);
  EXPECT(probe.resource_counts().host_bindings == 1);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(cleanup_attempts == 2);
  EXPECT(replacement_site.advise_calls.load() == 1);
  expect_current_site(probe, site_unknown(replacement_site));
  EXPECT(probe.resource_counts().capsules == 0);
  EXPECT(probe.resource_counts().host_bindings == 0);
}

void failed_target_advise_retries_without_publishing_target() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  EXPECT(activate_watched_site(probe, old_site, 91));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  replacement_site.advise_failures_remaining.store(1);

  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
  EXPECT(replacement_site.advise_calls.load() == 1);
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(replacement_site.advise_calls.load() == 2);
  expect_current_site(probe, site_unknown(replacement_site));
  auto* callback = replacement_site.retain_callback();
  EXPECT(callback != nullptr);
  EXPECT(send_valid_add(callback));
  callback->Release();
  EXPECT(replacement_site.object_queries.load() == 1);
}

void post_advise_publication_failure_unadvises_once_before_retry() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  EXPECT(activate_watched_site(probe, old_site, 101));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_target_publication(
      probe));

  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
  EXPECT(replacement_site.advise_calls.load() == 1);
  EXPECT(replacement_site.unadvise_calls.load() == 0);
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(replacement_site.unadvise_calls.load() == 1);
  EXPECT(replacement_site.advise_calls.load() == 2);
  auto* callback = replacement_site.retain_callback();
  EXPECT(callback != nullptr);
  callback->Release();
}

void failed_old_unadvise_retains_exact_transition_for_retry() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeSite wrong_target;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  EXPECT(activate_watched_site(probe, old_site, 106));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  old_site.unadvise_failures_remaining.store(1);

  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
  EXPECT(old_site.unadvise_calls.load() == 1);
  EXPECT(replacement_site.advise_calls.load() == 0);
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);

  EXPECT(FAILED(probe.set_site(site_unknown(wrong_target))));
  EXPECT(old_site.unadvise_calls.load() == 1);
  EXPECT(wrong_target.advise_calls.load() == 0);
  expect_current_site(probe, site_unknown(old_site));

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(old_site.unadvise_calls.load() == 2);
  EXPECT(replacement_site.advise_calls.load() == 1);
  expect_current_site(probe, site_unknown(replacement_site));
}

void failed_advised_target_unadvise_retries_without_double_advice() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  EXPECT(activate_watched_site(probe, old_site, 107));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_target_publication(
      probe));
  replacement_site.unadvise_failures_remaining.store(1);

  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
  EXPECT(replacement_site.advise_calls.load() == 1);
  EXPECT(replacement_site.unadvise_calls.load() == 0);

  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
  EXPECT(replacement_site.unadvise_calls.load() == 1);
  EXPECT(replacement_site.advise_calls.load() == 1);
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(replacement_site.unadvise_calls.load() == 2);
  EXPECT(replacement_site.advise_calls.load() == 2);
  expect_current_site(probe, site_unknown(replacement_site));
}

void post_advise_allocation_failure_retains_exact_watcher_until_unadvised() {
  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

    EXPECT(probe.set_site(site_unknown(site)) == S_OK);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
        probe, 109));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               fail_next_advised_resource_allocation(probe));
    site.unadvise_failures_remaining.store(1);

    EXPECT(!probe.advise_watcher());
    EXPECT(site.advise_calls.load() == 1);
    EXPECT(site.unadvise_calls.load() == 1);
    EXPECT(probe.advise_watcher());
    EXPECT(site.advise_calls.load() == 2);
    EXPECT(site.unadvise_calls.load() == 2);
  }

  {
    FakeSite old_site;
    FakeSite replacement_site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

    EXPECT(activate_watched_site(probe, old_site, 110));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               fail_next_advised_resource_allocation(probe));
    replacement_site.unadvise_failures_remaining.store(1);

    EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
    EXPECT(replacement_site.advise_calls.load() == 1);
    EXPECT(replacement_site.unadvise_calls.load() == 1);
    EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
    EXPECT(replacement_site.advise_calls.load() == 2);
    EXPECT(replacement_site.unadvise_calls.load() == 2);
  }
}

void unadvise_retry_prepares_unadvised_owner_before_external_call() {
  {
    FakeSite site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(probe.set_site(site_unknown(site)) == S_OK);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::activate_diagnostics_session(
        probe, 112));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               fail_next_advised_resource_allocation(probe));
    site.unadvise_failures_remaining.store(1);
    EXPECT(!probe.advise_watcher());
    EXPECT(site.advise_calls.load() == 1);
    EXPECT(site.unadvise_calls.load() == 1);

    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               fail_next_unadvised_resource_allocation(probe));
    EXPECT(!probe.advise_watcher());
    EXPECT(site.advise_calls.load() == 1);
    EXPECT(site.unadvise_calls.load() == 1);
    EXPECT(probe.advise_watcher());
    EXPECT(site.unadvise_calls.load() == 2);
    EXPECT(site.advise_calls.load() == 2);
  }

  {
    FakeSite old_site;
    FakeSite replacement_site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    EXPECT(activate_watched_site(probe, old_site, 113));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_target_publication(
        probe));
    EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
    EXPECT(replacement_site.advise_calls.load() == 1);
    EXPECT(replacement_site.unadvise_calls.load() == 0);

    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               fail_next_unadvised_resource_allocation(probe));
    EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
    EXPECT(replacement_site.advise_calls.load() == 1);
    EXPECT(replacement_site.unadvise_calls.load() == 0);
    EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
    EXPECT(replacement_site.unadvise_calls.load() == 1);
    EXPECT(replacement_site.advise_calls.load() == 2);
  }
}

void failed_root_retirement_retains_old_session_before_target_advice() {
  // A real owner-thread dispatch needs an Explorer-owned taskbar HWND/XamlRoot,
  // which this offline suite deliberately never creates or injects. The
  // production retirement-stage fault below composes with the HandoffRequest
  // claimed/in-flight tests to cover both sides of the same resumable stage.
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  auto authority = std::make_shared<cq::bridge::CleanupAuthorityGate>();

  EXPECT(activate_watched_site(probe, old_site, 108));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_synthetic_retained_root(
      probe, authority, [] { return true; }));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_root_retirement(
      probe));

  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
  EXPECT(replacement_site.advise_calls.load() == 0);
  expect_current_site(probe, site_unknown(old_site));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
         old_identity);
  EXPECT(probe.resource_counts().host_bindings == 1);

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(replacement_site.advise_calls.load() == 1);
  EXPECT(probe.resource_counts().host_bindings == 0);
  expect_current_site(probe, site_unknown(replacement_site));
}

void blocked_target_advise_excludes_unload_without_closing_old_session() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(activate_watched_site(probe, old_site, 111));
  const auto old_identity =
      cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe);

  std::mutex mutex;
  std::condition_variable condition;
  bool advise_entered = false;
  bool allow_advise = false;
  replacement_site.on_advise = [&] {
    std::unique_lock lock(mutex);
    advise_entered = true;
    condition.notify_all();
    condition.wait(lock, [&] { return allow_advise; });
  };

  long replacement_result = E_FAIL;
  std::thread replacement([&] {
    replacement_result = probe.set_site(site_unknown(replacement_site));
  });
  bool entered = false;
  {
    std::unique_lock lock(mutex);
    entered = condition.wait_for(lock, std::chrono::seconds(2),
                                 [&] { return advise_entered; });
  }
  bool unload_result = true;
  bool retained_old_identity = false;
  bool retained_old_site = false;
  if (entered) {
    unload_result = probe.release_for_unload();
    retained_old_identity =
        cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_identity(probe) ==
        old_identity;
    retained_old_site = current_site_is(probe, site_unknown(old_site));
  }
  {
    std::lock_guard lock(mutex);
    allow_advise = true;
  }
  condition.notify_all();
  replacement.join();

  EXPECT(entered);
  EXPECT(!unload_result);
  EXPECT(retained_old_identity);
  EXPECT(retained_old_site);
  EXPECT(replacement_result == S_OK);
  expect_current_site(probe, site_unknown(replacement_site));
}

void synchronous_advise_and_unadvise_reentry_observe_busy_old_site() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeSite reentrant_target;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(activate_watched_site(probe, old_site, 121));

  long unadvise_set_site = E_FAIL;
  long advise_set_site = E_FAIL;
  bool unadvise_saw_old = false;
  bool advise_saw_old = false;
  old_site.on_unadvise = [&] {
    unadvise_set_site = probe.set_site(site_unknown(reentrant_target));
    unadvise_saw_old = current_site_is(probe, site_unknown(old_site));
  };
  replacement_site.on_advise = [&] {
    advise_set_site = probe.set_site(site_unknown(reentrant_target));
    advise_saw_old = current_site_is(probe, site_unknown(old_site));
  };

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(unadvise_set_site == HRESULT_FROM_WIN32(ERROR_BUSY));
  EXPECT(advise_set_site == HRESULT_FROM_WIN32(ERROR_BUSY));
  EXPECT(unadvise_saw_old);
  EXPECT(advise_saw_old);
  expect_current_site(probe, site_unknown(replacement_site));
}

void final_com_releases_observe_diagnostics_mutex_unlocked() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(activate_watched_site(probe, old_site, 131));

  std::atomic<std::size_t> release_observations{0};
  std::atomic<bool> all_unlocked{true};
  const auto observe_release = [&] {
    ++release_observations;
    if (!cq::bridge::XamlTaskbarProbeTestPeer::diagnostics_mutex_available(
            probe)) {
      all_unlocked.store(false);
    }
  };
  old_site.on_release = observe_release;
  old_site.on_callback_release = observe_release;

  EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
  EXPECT(release_observations.load() >= 4);
  EXPECT(all_unlocked.load());
}

void retry_state_releases_observe_diagnostics_mutex_unlocked() {
  {
    FakeSite old_site;
    FakeSite replacement_site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    WatcherReleaseObservation observation;
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    observation.probe = &probe;
    EXPECT(activate_watched_site(probe, old_site, 141));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               set_watcher_destruction_observer(
                   probe, observe_watcher_release, &observation));
    replacement_site.on_release =
        [&] { observe_service_release(observation); };
    replacement_site.advise_failures_remaining.store(1);

    EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
    EXPECT(observation.watcher_releases.load() == 1);
    EXPECT(observation.all_unlocked.load());
    EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
    EXPECT(probe.unadvise_watcher().status ==
           cq::bridge::ProbeCleanupStatus::complete);
    EXPECT(observation.watcher_releases.load() >= 3);
    EXPECT(observation.service_releases.load() != 0);
    EXPECT(observation.all_unlocked.load());
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               set_watcher_destruction_observer(probe, nullptr, nullptr));
  }

  {
    FakeSite old_site;
    FakeSite replacement_site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    WatcherReleaseObservation observation;
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    observation.probe = &probe;
    EXPECT(activate_watched_site(probe, old_site, 142));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               set_watcher_destruction_observer(
                   probe, observe_watcher_release, &observation));
    replacement_site.on_release =
        [&] { observe_service_release(observation); };
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_target_publication(
        probe));

    EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
    EXPECT(observation.watcher_releases.load() == 0);
    EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
    EXPECT(observation.watcher_releases.load() >= 2);
    EXPECT(observation.service_releases.load() != 0);
    EXPECT(observation.all_unlocked.load());
    EXPECT(probe.unadvise_watcher().status ==
           cq::bridge::ProbeCleanupStatus::complete);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               set_watcher_destruction_observer(probe, nullptr, nullptr));
  }

  {
    FakeSite old_site;
    FakeSite replacement_site;
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    WatcherReleaseObservation observation;
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    observation.probe = &probe;
    auto authority = std::make_shared<cq::bridge::CleanupAuthorityGate>();
    EXPECT(activate_watched_site(probe, old_site, 143));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               set_watcher_destruction_observer(
                   probe, observe_watcher_release, &observation));
    replacement_site.on_release =
        [&] { observe_service_release(observation); };
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_synthetic_retained_root(
        probe, authority, [] { return true; }));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_root_retirement(
        probe));

    EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));
    EXPECT(observation.watcher_releases.load() == 0);
    EXPECT(probe.set_site(site_unknown(replacement_site)) == S_OK);
    EXPECT(observation.watcher_releases.load() >= 1);
    EXPECT(observation.service_releases.load() != 0);
    EXPECT(observation.all_unlocked.load());
    EXPECT(probe.unadvise_watcher().status ==
           cq::bridge::ProbeCleanupStatus::complete);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               set_watcher_destruction_observer(probe, nullptr, nullptr));
  }
}

void unload_checks_before_commit_and_releases_owners_before_module_boundary() {
  FakeSite site;
  FakeSite wrong_target;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  UnloadBoundaryObservation observation;
  site.on_release = [&] {
    observation.releases.fetch_add(1, std::memory_order_release);
  };

  EXPECT(activate_watched_site(probe, site, 149));
  EXPECT(probe.unadvise_watcher().status ==
         cq::bridge::ProbeCleanupStatus::complete);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_tap_can_unload(
      probe));
  EXPECT(!probe.release_for_unload());
  expect_current_site(probe, site_unknown(site));
  const auto wrong_queries = wrong_target.query_interface_calls.load();
  EXPECT(probe.set_site(site_unknown(wrong_target)) ==
         HRESULT_FROM_WIN32(ERROR_BUSY));
  EXPECT(wrong_target.query_interface_calls.load() == wrong_queries);

  observation.baseline = observation.releases.load(std::memory_order_acquire);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_unload_boundary_observer(
      probe, observe_unload_boundary, &observation));
  EXPECT(probe.release_for_unload());
  EXPECT(observation.owners_released_before_boundary.load(
      std::memory_order_acquire));
}

void tap_admission_claim_is_exact_monotonic_and_replay_safe() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  constexpr std::uintptr_t first_owner = 0xA11CE;
  constexpr std::uintptr_t wrong_owner = 0xB0B;
  const auto first =
      cq::bridge::XamlTaskbarProbeTestPeer::claim_tap_object_admission(
          first_owner);
  EXPECT(first != 0);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_object_admission_closed());
  EXPECT(cq::bridge::tap_can_unload_now() == S_FALSE);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::claim_tap_object_admission(
             wrong_owner) == 0);
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
             reopen_tap_object_admission(wrong_owner, first));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
             commit_tap_object_admission(first_owner, first + 1));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reopen_tap_object_admission(first_owner, first));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
             reopen_tap_object_admission(first_owner, first));

  const auto second =
      cq::bridge::XamlTaskbarProbeTestPeer::claim_tap_object_admission(
          first_owner);
  EXPECT(second > first);
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
             reopen_tap_object_admission(first_owner, first));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             commit_tap_object_admission(first_owner, second));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
             commit_tap_object_admission(first_owner, second));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
             reopen_tap_object_admission(first_owner, second));
  EXPECT(cq::bridge::tap_can_unload_now() == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void tap_admission_claim_is_bound_to_the_active_probe() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations first_operations;
  FakeBridgeOperations second_operations;
  cq::bridge::BridgeLifecycle first_lifecycle(first_operations);
  cq::bridge::BridgeLifecycle second_lifecycle(second_operations);
  cq::bridge::XamlTaskbarProbe first_probe(nullptr, first_lifecycle);
  cq::bridge::XamlTaskbarProbe second_probe(nullptr, second_lifecycle);
  const auto first_owner = reinterpret_cast<std::uintptr_t>(&first_probe);
  const auto second_owner = reinterpret_cast<std::uintptr_t>(&second_probe);

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_active_probe(&first_probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::claim_tap_object_admission(
             second_owner) == 0);
  const auto rollback_claim =
      cq::bridge::XamlTaskbarProbeTestPeer::claim_tap_object_admission(
          first_owner);
  EXPECT(rollback_claim != 0);
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::set_active_probe(
      &second_probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reopen_tap_object_admission(first_owner, rollback_claim));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::active_probe() == &first_probe);

  const auto commit_claim =
      cq::bridge::XamlTaskbarProbeTestPeer::claim_tap_object_admission(
          first_owner);
  EXPECT(commit_claim > rollback_claim);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             commit_tap_object_admission(first_owner, commit_claim));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::active_probe() == nullptr);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void failed_pre_detach_validation_restores_every_admission() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             fail_next_unload_before_detach(probe));

  EXPECT(!probe.release_for_unload());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::has_impl(probe));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::session_is_unloading(probe));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
              tap_object_admission_closed());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::try_pending_admission(probe));
  bool root_admitted = false;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::while_root_admission(
      probe, [&] { root_admitted = true; }));
  EXPECT(root_admitted);
  EXPECT(probe.release_for_unload());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void failed_tap_reopen_retains_the_exact_rollback_claim() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             fail_next_unload_final_counts(probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_tap_reopen());

  EXPECT(!probe.release_for_unload());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::session_is_unloading(probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             tap_object_admission_closed());
  EXPECT(!probe.release_for_unload());
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::session_is_unloading(probe));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::
              tap_object_admission_closed());
  EXPECT(probe.release_for_unload());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void watcher_callback_can_outlive_the_retired_probe_impl() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  {
    FakeSite site;
    IVisualTreeServiceCallback* retained = nullptr;
    {
      FakeBridgeOperations operations;
      cq::bridge::BridgeLifecycle lifecycle(operations);
      cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
      EXPECT(activate_watched_site(probe, site, 166));
      retained = site.retain_callback();
      EXPECT(retained != nullptr);
    }
    const auto object_queries = site.object_queries.load();
    EXPECT(send_valid_add(retained));
    EXPECT(site.object_queries.load() == object_queries);
    retained->Release();
  }
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void same_probe_rollback_publish_serializes_the_next_unload() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeSite wrong_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  UnloadRollbackObservation rollback_observation{.probe = &probe,
                                                 .site = &wrong_site};
  UnloadRollbackBarrier published_barrier;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_active_probe(&probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_unload_rollback_observer(
      probe, observe_unload_rollback, &rollback_observation));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             set_unload_rollback_published_observer(
                 probe, block_unload_rollback_published,
                 &published_barrier));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             fail_next_unload_final_counts(probe));

  bool first_result = true;
  std::thread first([&] { first_result = probe.release_for_unload(); });
  bool first_published = false;
  {
    std::unique_lock lock(published_barrier.mutex);
    first_published = published_barrier.condition.wait_for(
        lock, std::chrono::seconds(2),
        [&] { return published_barrier.entered; });
  }

  std::mutex second_mutex;
  std::condition_variable second_condition;
  bool second_started = false;
  bool second_done = false;
  bool second_result = false;
  std::thread second([&] {
    {
      std::lock_guard lock(second_mutex);
      second_started = true;
    }
    second_condition.notify_all();
    second_result = probe.release_for_unload();
    {
      std::lock_guard lock(second_mutex);
      second_done = true;
    }
    second_condition.notify_all();
  });
  bool observed_second_start = false;
  bool second_was_serialized = false;
  {
    std::unique_lock lock(second_mutex);
    observed_second_start = second_condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return second_started; });
    if (observed_second_start) {
      (void)second_condition.wait_for(
          lock, std::chrono::milliseconds(50), [&] { return second_done; });
      second_was_serialized = !second_done;
    }
  }
  const bool unload_mutex_was_held =
      !cq::bridge::XamlTaskbarProbeTestPeer::unload_mutex_available(probe);
  {
    std::lock_guard lock(published_barrier.mutex);
    published_barrier.proceed = true;
  }
  published_barrier.condition.notify_all();
  first.join();
  second.join();

  EXPECT(first_published);
  EXPECT(second_was_serialized);
  EXPECT(unload_mutex_was_held);
  EXPECT(!first_result);
  EXPECT(second_result);
  EXPECT(rollback_observation.saw_unloading.load(std::memory_order_acquire));
  EXPECT(rollback_observation.active_was_exact.load(
      std::memory_order_acquire));
  EXPECT(rollback_observation.factory_reopened.load(
      std::memory_order_acquire));
  EXPECT(rollback_observation.tap_set_site_was_busy.load(
      std::memory_order_acquire));
  EXPECT(!cq::bridge::XamlTaskbarProbeTestPeer::has_impl(probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void different_probes_cannot_borrow_or_reopen_a_tap_claim() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations first_operations;
  FakeBridgeOperations second_operations;
  cq::bridge::BridgeLifecycle first_lifecycle(first_operations);
  cq::bridge::BridgeLifecycle second_lifecycle(second_operations);
  cq::bridge::XamlTaskbarProbe first_probe(nullptr, first_lifecycle);
  cq::bridge::XamlTaskbarProbe second_probe(nullptr, second_lifecycle);
  TapPrecommitBarrier barrier;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_active_probe(&first_probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_tap_precommit_observer(
      first_probe, block_tap_precommit, &barrier));
  bool first_result = false;
  std::thread first_unload(
      [&] { first_result = first_probe.release_for_unload(); });
  bool entered = false;
  {
    std::unique_lock lock(barrier.mutex);
    entered = barrier.condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return barrier.entered; });
  }
  const bool loser_result = second_probe.release_for_unload();
  void* factory_during_claim = nullptr;
  const auto factory_result = get_test_tap_factory(&factory_during_claim);
  if (factory_during_claim) {
    static_cast<IUnknown*>(factory_during_claim)->Release();
  }
  {
    std::lock_guard lock(barrier.mutex);
    barrier.proceed = true;
  }
  barrier.condition.notify_all();
  first_unload.join();

  EXPECT(entered);
  EXPECT(!loser_result);
  EXPECT(factory_result == CLASS_E_CLASSNOTAVAILABLE);
  EXPECT(first_result);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::has_impl(second_probe));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  EXPECT(second_probe.release_for_unload());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void successful_unload_clears_impl_and_unlocks_before_reentry() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  SuccessfulUnloadObservation observation{.probe = &probe};
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_unload_boundary_observer(
      probe, observe_successful_unload, &observation));
  EXPECT(probe.release_for_unload());
  EXPECT(observation.impl_was_absent.load(std::memory_order_acquire));
  EXPECT(observation.unload_mutex_was_available.load(
      std::memory_order_acquire));
  EXPECT(observation.reentrant_unload_was_rejected.load(
      std::memory_order_acquire));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void concurrent_public_impl_snapshot_survives_outer_slot_retirement() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  ResourceSnapshotBarrier barrier;
  bool snapshot_result = false;
  std::thread holder([&] {
    snapshot_result =
        cq::bridge::XamlTaskbarProbeTestPeer::while_impl_snapshot(
            probe, [&] {
              std::unique_lock lock(barrier.mutex);
              barrier.entered = true;
              barrier.condition.notify_all();
              barrier.condition.wait(lock, [&] { return barrier.proceed; });
            });
  });
  bool entered = false;
  {
    std::unique_lock lock(barrier.mutex);
    entered = barrier.condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return barrier.entered; });
  }
  const bool unload_result = entered && probe.release_for_unload();
  const bool impl_was_absent =
      !cq::bridge::XamlTaskbarProbeTestPeer::has_impl(probe);
  {
    std::lock_guard lock(barrier.mutex);
    barrier.proceed = true;
  }
  barrier.condition.notify_all();
  holder.join();

  EXPECT(entered);
  EXPECT(unload_result);
  EXPECT(impl_was_absent);
  EXPECT(snapshot_result);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void tap_object_admission_linearizes_factory_creation_with_unload() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());

  {
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    void* retained_factory = nullptr;
    EXPECT(get_test_tap_factory(&retained_factory) == S_OK);
    EXPECT(retained_factory != nullptr);
    EXPECT(!probe.release_for_unload());

    void* reopened_factory = nullptr;
    EXPECT(get_test_tap_factory(&reopened_factory) == S_OK);
    EXPECT(reopened_factory != nullptr);
    static_cast<IUnknown*>(reopened_factory)->Release();
    static_cast<IUnknown*>(retained_factory)->Release();
  }

  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  TapPrecommitBarrier barrier;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_tap_precommit_observer(
      probe, block_tap_precommit, &barrier));
  bool unload_result = false;
  std::thread unload([&] { unload_result = probe.release_for_unload(); });
  bool entered = false;
  {
    std::unique_lock lock(barrier.mutex);
    entered = barrier.condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return barrier.entered; });
  }
  void* racing_factory = nullptr;
  long racing_result = E_FAIL;
  if (entered) {
    racing_result = get_test_tap_factory(&racing_factory);
  }
  if (racing_factory) {
    static_cast<IUnknown*>(racing_factory)->Release();
  }
  {
    std::lock_guard lock(barrier.mutex);
    barrier.proceed = true;
  }
  barrier.condition.notify_all();
  unload.join();

  EXPECT(entered);
  EXPECT(racing_result == CLASS_E_CLASSNOTAVAILABLE);
  EXPECT(racing_factory == nullptr);
  EXPECT(unload_result);
  void* after_unload = nullptr;
  EXPECT(get_test_tap_factory(&after_unload) == CLASS_E_CLASSNOTAVAILABLE);
  EXPECT(after_unload == nullptr);
}

void tap_server_locks_do_not_corrupt_live_object_count() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  void* raw_factory = nullptr;
  EXPECT(get_test_tap_factory(&raw_factory) == S_OK);
  EXPECT(raw_factory != nullptr);
  auto* factory = static_cast<IClassFactory*>(raw_factory);
  EXPECT(cq::bridge::tap_can_unload_now() == S_FALSE);

  EXPECT(factory->LockServer(FALSE) == E_UNEXPECTED);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_object_count() == 1);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_server_lock_count() == 0);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_counter_fault());
  EXPECT(cq::bridge::tap_can_unload_now() == S_FALSE);
  factory->Release();
  EXPECT(cq::bridge::tap_can_unload_now() == S_FALSE);

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  raw_factory = nullptr;
  EXPECT(get_test_tap_factory(&raw_factory) == S_OK);
  factory = static_cast<IClassFactory*>(raw_factory);
  EXPECT(factory->LockServer(TRUE) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_server_lock_count() == 1);
  EXPECT(factory->LockServer(FALSE) == S_OK);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_server_lock_count() == 0);
  EXPECT(cq::bridge::tap_can_unload_now() == S_FALSE);
  factory->Release();
  EXPECT(cq::bridge::tap_can_unload_now() == S_OK);

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  raw_factory = nullptr;
  EXPECT(get_test_tap_factory(&raw_factory) == S_OK);
  factory = static_cast<IClassFactory*>(raw_factory);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_tap_server_lock_count(
      (std::numeric_limits<long>::max)()));
  EXPECT(factory->LockServer(TRUE) == E_UNEXPECTED);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_counter_fault());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::tap_object_count() == 1);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_tap_server_lock_count(0));
  factory->Release();
  EXPECT(cq::bridge::tap_can_unload_now() == S_FALSE);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void unload_gate_close_linearizes_with_pending_admission() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  {
    FakeBridgeOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    bool unload_result = true;
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::while_pending_admission(
        probe, [&] { unload_result = probe.release_for_unload(); }));
    EXPECT(!unload_result);
    EXPECT(!probe.release_for_unload());
    EXPECT(probe.release_for_unload());
  }

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  TapPrecommitBarrier barrier;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_tap_precommit_observer(
      probe, block_tap_precommit, &barrier));
  bool unload_result = false;
  std::thread unload([&] { unload_result = probe.release_for_unload(); });
  bool entered = false;
  {
    std::unique_lock lock(barrier.mutex);
    entered = barrier.condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return barrier.entered; });
  }
  const bool acquired_after_unloading_claim =
      entered &&
      cq::bridge::XamlTaskbarProbeTestPeer::try_pending_admission(probe);
  {
    std::lock_guard lock(barrier.mutex);
    barrier.proceed = true;
  }
  barrier.condition.notify_all();
  unload.join();

  EXPECT(entered);
  EXPECT(!acquired_after_unloading_claim);
  EXPECT(unload_result);
}

void probe_pending_ledger_blocks_unload_until_status_proves_retirement() {
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  FakeProbePendingBoundary boundary;
  boundary.probe = &probe;
  auto observation = std::make_shared<ProbePendingActionObservation>();
  auto action = std::make_shared<FakeProbePendingAction>(observation);
  auto* const action_view = action.get();
  boundary.next_action = action;
  const auto scheduled =
      cq::bridge::XamlTaskbarProbeTestPeer::schedule_pending_for_test(
          probe, boundary, boundary, [] {});
  EXPECT(scheduled.status == cq::bridge::PendingScheduleStatus::queued);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::pending_registry_count(probe) ==
         1);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::pending_stage(
             probe, scheduled.id) ==
         std::optional{cq::bridge::PendingActionStage::queued});
  EXPECT(probe.resource_counts().pending_actions == 1);
  EXPECT(!probe.release_for_unload());

  const auto canceled = probe.cancel_pending_work();
  EXPECT(canceled.status == cq::bridge::ProbeCleanupStatus::unsafe_in_flight);
  EXPECT(canceled.remaining == 1);
  EXPECT(observation->cancel_calls.load(std::memory_order_acquire) == 1);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::pending_stage(
             probe, scheduled.id) ==
         std::optional{cq::bridge::PendingActionStage::cancel_requested});

  action_view->current_status.store(cq::bridge::PendingAsyncStatus::completed,
                                    std::memory_order_release);
  action.reset();
  EXPECT(probe.resource_counts().pending_actions == 0);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::pending_registry_count(probe) ==
         0);
  EXPECT(boundary.release_calls == 1);
  EXPECT(boundary.observed_module == boundary.module_handle);
  EXPECT(boundary.release_observed_mutex_available);
  EXPECT(boundary.release_observed_retiring == 1);
  EXPECT(boundary.release_observed_pending_count == 1);
  EXPECT(observation->destructions.load(std::memory_order_acquire) == 1);

  const auto after_completion = probe.cancel_pending_work();
  EXPECT(after_completion.status == cq::bridge::ProbeCleanupStatus::complete);
  EXPECT(after_completion.remaining == 0);
  EXPECT(observation->cancel_calls.load(std::memory_order_acquire) == 1);
  EXPECT(!probe.release_for_unload());
  EXPECT(probe.release_for_unload());
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             reset_tap_object_admission());
}

void probe_pending_resource_count_uses_registry_or_gate_fail_closed_maximum() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  bool observed = false;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::while_pending_admission(
      probe, [&] {
        EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::pending_registry_count(
                   probe) == 0);
        observed = probe.resource_counts().pending_actions == 1;
      }));
  EXPECT(observed);
  EXPECT(probe.resource_counts().pending_actions == 0);
}

void resource_counts_uses_one_locked_session_resource_snapshot() {
  FakeSite old_site;
  FakeSite replacement_site;
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  EXPECT(activate_watched_site(probe, old_site, 161));
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::fail_next_target_publication(
      probe));
  EXPECT(FAILED(probe.set_site(site_unknown(replacement_site))));

  ResourceSnapshotBarrier barrier;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::set_resource_snapshot_observer(
      probe, block_resource_snapshot, &barrier));
  cq::bridge::ProbeResourceCounts snapshot{};
  std::thread reader([&] { snapshot = probe.resource_counts(); });
  bool entered = false;
  {
    std::unique_lock lock(barrier.mutex);
    entered = barrier.condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return barrier.entered; });
  }
  bool detached = false;
  if (entered) {
    detached =
        cq::bridge::XamlTaskbarProbeTestPeer::detach_transition_resources(
            probe);
  }
  {
    std::lock_guard lock(barrier.mutex);
    barrier.proceed = true;
  }
  barrier.condition.notify_all();
  reader.join();

  EXPECT(entered);
  EXPECT(detached);
  EXPECT(snapshot.watcher_references == 2);
}

void hook_self_reference_precedes_install_and_releases_exactly_once() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  FakeHandoffWin32Boundary boundary;
  HandoffDestructionObservation destruction;
  std::atomic<std::size_t> action_calls{0};
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             set_handoff_destruction_observer(
                 observe_handoff_destruction, &destruction));

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, boundary, [&] { ++action_calls; }) ==
         cq::bridge::HandoffResult::completed);
  EXPECT(action_calls.load(std::memory_order_acquire) == 1);
  EXPECT(boundary.trace == std::vector<std::string>(
                               {"acquire", "publish", "install", "send",
                                "unhook", "barrier", "clear", "release"}));
  EXPECT(boundary.observed_callback != 0);
  EXPECT(boundary.observed_thread != 0);
  EXPECT(boundary.observed_message != 0);
  EXPECT(boundary.observed_target.retirement_token != 0);
  EXPECT(boundary.observed_hook == boundary.hook_handle);
  EXPECT(boundary.observed_module == boundary.module_handle);
  EXPECT(boundary.publish_calls == 1);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.clear_calls == 1);
  EXPECT(boundary.successful_clear_calls == 1);
  EXPECT(boundary.release_calls == 1);
  EXPECT(boundary.published_token == 0);
  EXPECT(boundary.publish_observed_mutex_available);
  EXPECT(boundary.clear_observed_mutex_available);
  EXPECT(boundary.release_observed_mutex_available);
  EXPECT(destruction.destructions.load(std::memory_order_acquire) == 1);
  EXPECT(destruction.mutex_available.load(std::memory_order_acquire));
  EXPECT(probe.resource_counts().handoffs == 0);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             set_handoff_destruction_observer(nullptr, nullptr));
}

void completed_action_remains_counted_until_barrier_proves_retirement() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  FakeHandoffWin32Boundary boundary;
  boundary.barrier_succeeds = false;
  std::atomic<std::size_t> action_calls{0};

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, boundary, [&] { ++action_calls; }) ==
         cq::bridge::HandoffResult::timed_out_in_flight);
  EXPECT(action_calls.load(std::memory_order_acquire) == 1);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.clear_calls == 0);
  EXPECT(boundary.release_calls == 0);
  EXPECT(probe.resource_counts().handoffs == 1);
  EXPECT(!probe.release_for_unload());
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.release_calls == 0);

  boundary.barrier_succeeds = true;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::reap_handoffs_for_test(probe) ==
         0);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.successful_clear_calls == 1);
  EXPECT(boundary.release_calls == 1);
  EXPECT(probe.resource_counts().handoffs == 0);
  EXPECT(probe.release_for_unload());
}

void action_failure_does_not_prevent_proven_hook_retirement() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  FakeHandoffWin32Boundary boundary;

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, boundary, [] { throw 11; }) ==
         cq::bridge::HandoffResult::action_failed);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.successful_clear_calls == 1);
  EXPECT(boundary.release_calls == 1);
  EXPECT(probe.resource_counts().handoffs == 0);
}

void retirement_token_is_independent_from_mutable_host_generation() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  FakeHandoffWin32Boundary boundary;
  boundary.host_generation = 73;

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, boundary, [&] { boundary.host_generation = 0; }) ==
         cq::bridge::HandoffResult::completed);
  EXPECT(boundary.host_generation == 0);
  EXPECT(boundary.observed_target.retirement_token != 0);
  EXPECT(boundary.successful_clear_calls == 1);
  EXPECT(boundary.release_calls == 1);
}

void hook_revalidates_retirement_token_immediately_before_action() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  FakeHandoffWin32Boundary boundary;
  boundary.replace_token_before_dispatch = true;
  std::atomic<std::size_t> action_calls{0};

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, boundary, [&] { ++action_calls; }) ==
         cq::bridge::HandoffResult::timed_out_in_flight);
  EXPECT(action_calls.load(std::memory_order_acquire) == 0);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.clear_calls == 0);
  EXPECT(boundary.release_calls == 0);
  EXPECT(probe.resource_counts().handoffs == 1);

  boundary.replace_token_before_dispatch = false;
  boundary.published_token = boundary.observed_target.retirement_token;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::reap_handoffs_for_test(probe) ==
         0);
  EXPECT(action_calls.load(std::memory_order_acquire) == 0);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.successful_clear_calls == 1);
  EXPECT(boundary.release_calls == 1);
}

void retirement_token_publication_fails_closed() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  FakeHandoffWin32Boundary preoccupied;
  preoccupied.published_token = 91;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, preoccupied, [] {}) ==
         cq::bridge::HandoffResult::action_failed);
  EXPECT(preoccupied.trace ==
         std::vector<std::string>({"acquire", "publish", "release"}));
  EXPECT(preoccupied.published_token == 91);
  EXPECT(preoccupied.release_calls == 1);

  FakeHandoffWin32Boundary publication_failure;
  publication_failure.publish_succeeds = false;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, publication_failure, [] {}) ==
         cq::bridge::HandoffResult::action_failed);
  EXPECT(publication_failure.trace ==
         std::vector<std::string>({"acquire", "publish", "release"}));
  EXPECT(publication_failure.release_calls == 1);
  EXPECT(probe.resource_counts().handoffs == 0);
}

void token_clear_failure_retains_context_and_retries_without_reunhooking() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
  FakeHandoffWin32Boundary boundary;
  boundary.clear_succeeds = false;

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, boundary, [] {}) ==
         cq::bridge::HandoffResult::timed_out_in_flight);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.clear_calls == 1);
  EXPECT(boundary.successful_clear_calls == 0);
  EXPECT(boundary.release_calls == 0);
  EXPECT(probe.resource_counts().handoffs == 1);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.clear_calls == 2);

  boundary.clear_succeeds = true;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::reap_handoffs_for_test(probe) ==
         0);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.clear_calls == 3);
  EXPECT(boundary.successful_clear_calls == 1);
  EXPECT(boundary.release_calls == 1);
  EXPECT(probe.resource_counts().handoffs == 0);
  EXPECT(boundary.clear_calls == 3);
}

void module_or_install_failure_preserves_token_lifecycle() {
  FakeBridgeOperations operations;
  cq::bridge::BridgeLifecycle lifecycle(operations);
  cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);

  FakeHandoffWin32Boundary acquire_failure;
  acquire_failure.acquire_succeeds = false;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, acquire_failure, [] {}) ==
         cq::bridge::HandoffResult::action_failed);
  EXPECT(acquire_failure.trace == std::vector<std::string>({"acquire"}));

  FakeHandoffWin32Boundary install_failure;
  install_failure.install_succeeds = false;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, install_failure, [] {}) ==
         cq::bridge::HandoffResult::action_failed);
  EXPECT(install_failure.trace ==
         std::vector<std::string>(
             {"acquire", "publish", "install", "clear", "release"}));
  EXPECT(install_failure.unhook_calls == 0);
  EXPECT(install_failure.barrier_calls == 0);
  EXPECT(install_failure.successful_clear_calls == 1);
  EXPECT(install_failure.release_calls == 1);
  EXPECT(probe.resource_counts().handoffs == 0);

  FakeHandoffWin32Boundary retained_install_failure;
  retained_install_failure.install_succeeds = false;
  retained_install_failure.clear_succeeds = false;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::run_handoff_for_test(
             probe, retained_install_failure, [] {}) ==
         cq::bridge::HandoffResult::timed_out_in_flight);
  EXPECT(retained_install_failure.unhook_calls == 0);
  EXPECT(retained_install_failure.barrier_calls == 0);
  EXPECT(retained_install_failure.clear_calls == 1);
  EXPECT(retained_install_failure.release_calls == 0);
  EXPECT(probe.resource_counts().handoffs == 1);
  EXPECT(retained_install_failure.clear_calls == 2);

  retained_install_failure.clear_succeeds = true;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::reap_handoffs_for_test(probe) ==
         0);
  EXPECT(retained_install_failure.clear_calls == 3);
  EXPECT(retained_install_failure.successful_clear_calls == 1);
  EXPECT(retained_install_failure.release_calls == 1);
  EXPECT(probe.resource_counts().handoffs == 0);
}

void manager_record_final_release_occurs_after_manager_unlock() {
  cq::bridge::ProbeCapsuleManager manager;
  ManagerOwnerObservation observation;
  observation.manager = &manager;
  auto retained_owner = std::make_shared<ManagerRetainedOwner>(observation);
  std::uint64_t record_id = 0;
  const auto owner_thread = GetCurrentThreadId();
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_manager_record(
      manager, owner_thread,
      [] { return cq::bridge::CapsuleCleanupStatus::complete; },
      retained_owner, record_id));
  retained_owner.reset();

  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::remove_manager_record(
             manager, owner_thread, record_id) ==
         cq::bridge::CapsuleCleanupStatus::complete);
  EXPECT(observation.destroyed.load(std::memory_order_acquire));
  EXPECT(observation.mutex_available.load(std::memory_order_acquire));
  EXPECT(observation.reentered_count.load(std::memory_order_acquire));
  EXPECT(observation.observed_count.load(std::memory_order_acquire) == 0);
}

void manager_release_permit_may_outlive_its_manager() {
  SYSTEM_INFO system_info{};
  GetSystemInfo(&system_info);
  EXPECT(system_info.dwPageSize >= sizeof(cq::bridge::ProbeCapsuleManager));
  void* const page = VirtualAlloc(nullptr, system_info.dwPageSize,
                                  MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  EXPECT(page != nullptr);

  auto* const manager = ::new (page) cq::bridge::ProbeCapsuleManager();
  // Keep the permit off-stack so a caught legacy access violation cannot make
  // normal scope unwinding invoke the same invalid destructor a second time.
  auto* const permit = new (std::nothrow)
      std::optional<cq::bridge::ProbeCapsuleManager::ReleasePermit>(
          manager->try_claim_release());
  EXPECT(permit != nullptr);
  EXPECT(permit->has_value());
  manager->~ProbeCapsuleManager();

  DWORD previous_protection = 0;
  EXPECT(VirtualProtect(page, system_info.dwPageSize, PAGE_NOACCESS,
                        &previous_protection) != FALSE);
  const bool reset_without_manager_access =
      reset_release_permit_without_access_violation(permit);
  DWORD ignored_protection = 0;
  EXPECT(VirtualProtect(page, system_info.dwPageSize, previous_protection,
                        &ignored_protection) != FALSE);
  if (reset_without_manager_access) {
    delete permit;
  }
  EXPECT(VirtualFree(page, 0, MEM_RELEASE) != FALSE);
  EXPECT(reset_without_manager_access);
}

void manager_release_waits_until_last_record_removal_returns() {
  cq::bridge::ProbeCapsuleManager manager;
  ManagerFinalReleaseBarrier barrier;
  auto retained_owner =
      std::make_shared<BlockingManagerRetainedOwner>(barrier);
  std::uint64_t record_id = 0;
  const auto owner_thread = GetCurrentThreadId();
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_manager_record(
      manager, owner_thread,
      [] { return cq::bridge::CapsuleCleanupStatus::complete; },
      retained_owner, record_id));
  retained_owner.reset();

  cq::bridge::CapsuleCleanupStatus removal =
      cq::bridge::CapsuleCleanupStatus::retryable_failure;
  std::thread remove([&] {
    removal = cq::bridge::XamlTaskbarProbeTestPeer::remove_manager_record(
        manager, owner_thread, record_id);
  });
  bool final_release_entered = false;
  {
    std::unique_lock lock(barrier.mutex);
    final_release_entered = barrier.condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return barrier.entered; });
  }
  const auto count_while_remove_is_returning =
      final_release_entered
          ? manager.tracked_element_count()
          : (std::numeric_limits<std::size_t>::max)();
  const auto claim_while_remove_is_returning =
      final_release_entered
          ? cq::bridge::XamlTaskbarProbeTestPeer::claim_manager_release(manager)
          : 0;
  if (claim_while_remove_is_returning) {
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::cancel_manager_release(
        manager, claim_while_remove_is_returning));
  }
  {
    std::lock_guard lock(barrier.mutex);
    barrier.proceed = true;
  }
  barrier.condition.notify_all();
  remove.join();

  EXPECT(final_release_entered);
  EXPECT(count_while_remove_is_returning == 0);
  EXPECT(claim_while_remove_is_returning == 0);
  EXPECT(removal == cq::bridge::CapsuleCleanupStatus::complete);
  const auto drained_claim =
      cq::bridge::XamlTaskbarProbeTestPeer::claim_manager_release(manager);
  EXPECT(drained_claim != 0);
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::commit_manager_release(
      manager, drained_claim));
}

void concurrent_manager_remove_is_exactly_once_and_retains_failures() {
  {
    cq::bridge::ProbeCapsuleManager manager;
    ManagerOwnerObservation observation;
    observation.manager = &manager;
    auto retained_owner = std::make_shared<ManagerRetainedOwner>(observation);
    std::atomic<std::size_t> cleanup_calls{0};
    std::atomic<bool> allow_cleanup{false};
    std::uint64_t record_id = 0;
    const auto owner_thread = GetCurrentThreadId();
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_manager_record(
        manager, owner_thread,
        [&] {
          ++cleanup_calls;
          return allow_cleanup.load(std::memory_order_acquire)
                     ? cq::bridge::CapsuleCleanupStatus::complete
                     : cq::bridge::CapsuleCleanupStatus::retryable_failure;
        },
        retained_owner, record_id));
    retained_owner.reset();
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::remove_manager_record(
               manager, owner_thread, record_id) ==
           cq::bridge::CapsuleCleanupStatus::retryable_failure);
    EXPECT(manager.tracked_element_count() == 1);
    EXPECT(!observation.destroyed.load(std::memory_order_acquire));
    allow_cleanup.store(true, std::memory_order_release);
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::remove_manager_record(
               manager, owner_thread, record_id) ==
           cq::bridge::CapsuleCleanupStatus::complete);
    EXPECT(cleanup_calls.load(std::memory_order_acquire) == 2);
    EXPECT(observation.destroyed.load(std::memory_order_acquire));
  }

  cq::bridge::ProbeCapsuleManager manager;
  ManagerOwnerObservation observation;
  observation.manager = &manager;
  auto retained_owner = std::make_shared<ManagerRetainedOwner>(observation);
  std::atomic<std::size_t> cleanup_calls{0};
  std::uint64_t record_id = 0;
  const auto owner_thread = GetCurrentThreadId();
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_manager_record(
      manager, owner_thread,
      [&] {
        ++cleanup_calls;
        return cq::bridge::CapsuleCleanupStatus::complete;
      },
      retained_owner, record_id));
  retained_owner.reset();
  ManagerRemoveSnapshotBarrier barrier;
  EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
             set_manager_remove_snapshot_observer(
                 manager, block_manager_remove_snapshot, &barrier));
  cq::bridge::CapsuleCleanupStatus first =
      cq::bridge::CapsuleCleanupStatus::retryable_failure;
  cq::bridge::CapsuleCleanupStatus second =
      cq::bridge::CapsuleCleanupStatus::retryable_failure;
  std::thread first_remove([&] {
    first = cq::bridge::XamlTaskbarProbeTestPeer::remove_manager_record(
        manager, owner_thread, record_id);
  });
  std::thread second_remove([&] {
    second = cq::bridge::XamlTaskbarProbeTestPeer::remove_manager_record(
        manager, owner_thread, record_id);
  });
  bool both_snapshotted = false;
  {
    std::unique_lock lock(barrier.mutex);
    both_snapshotted = barrier.condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return barrier.arrived == 2; });
    barrier.proceed = true;
  }
  barrier.condition.notify_all();
  first_remove.join();
  second_remove.join();
  EXPECT(both_snapshotted);
  EXPECT(first == cq::bridge::CapsuleCleanupStatus::complete);
  EXPECT(second == cq::bridge::CapsuleCleanupStatus::complete);
  EXPECT(cleanup_calls.load(std::memory_order_acquire) == 1);
  EXPECT(manager.tracked_element_count() == 0);
  EXPECT(observation.destroyed.load(std::memory_order_acquire));
}

void each_manager_trigger_retention_blocks_lifecycle_and_unload() {
  for (const auto trigger : {
           cq::bridge::CapsuleCleanupTrigger::timer,
           cq::bridge::CapsuleCleanupTrigger::explicit_removal,
           cq::bridge::CapsuleCleanupTrigger::shutdown,
           cq::bridge::CapsuleCleanupTrigger::rollback,
       }) {
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               reset_tap_object_admission());
    FakeSite site;
    ProbeBackedOperations operations;
    cq::bridge::BridgeLifecycle lifecycle(operations);
    EXPECT(lifecycle.initialize());
    cq::bridge::XamlTaskbarProbe probe(nullptr, lifecycle);
    operations.probe = &probe;
    auto authority = std::make_shared<cq::bridge::CleanupAuthorityGate>();
    auto evidence = std::make_shared<ToggleCleanupEvidenceSource>();
    auto access = std::make_shared<DetachedCleanupAccess>();

    EXPECT(activate_watched_site(probe, site, 151));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::attach_cleanup_authority(
        probe, authority));
    EXPECT(cq::bridge::XamlTaskbarProbeTestPeer::
               attach_primitive_capsule_record(
                   probe, exact_cleanup_binding(), authority, evidence,
                   access));
    const auto retained =
        cq::bridge::XamlTaskbarProbeTestPeer::
            trigger_primitive_capsule_record(probe, trigger);
    EXPECT(retained.status ==
           cq::bridge::CapsuleCleanupStatus::retryable_failure);
    EXPECT(retained.remaining == 1);
    EXPECT(probe.resource_counts().capsules == 1);
    EXPECT(probe.resource_counts().timers == 1);

    EXPECT(lifecycle.detach() == cq::bridge::DetachResult::not_quiesced);
    EXPECT(lifecycle.tracked_element_count() == 1);
    EXPECT(!probe.release_for_unload());
    EXPECT(!operations.quiesced_signaled);
    EXPECT(!operations.handles_closed);
    EXPECT(!operations.unload_called);

    evidence->available = true;
    EXPECT(lifecycle.detach() == cq::bridge::DetachResult::quiesced);
    EXPECT(probe.resource_counts().capsules == 0);
    EXPECT(probe.resource_counts().timers == 0);
    EXPECT(probe.release_for_unload());
    operations.probe = nullptr;
  }
}

}  // namespace

int main() {
  static_assert(noexcept(evaluate_visual_node(std::declval<const VisualNodeFacts&>())));
  accepts_only_the_exact_taskbar_structure();
  winrt_pending_status_mapping_never_invents_terminal_proof();
  capsule_contract_is_fixed_and_noninteractive();
  rejects_untrusted_structure_dimensions();
  rejects_empty_malformed_unicode_control_and_near_match_names();
  set_site_replacement_waits_for_diagnostics_drain_then_installs();
  set_site_and_advise_claim_before_external_com();
  site_preparation_keeps_old_callbacks_live_until_commit_or_rollback();
  activation_failures_restore_exact_initial_site_for_retry();
  remove_publishes_empty_baseline_from_every_admitted_state();
  remove_reattach_reopens_every_admission_for_multiple_cycles();
  held_pending_and_root_leases_keep_remove_retryable();
  remove_failures_stay_closed_and_retry_exact_transition();
  partial_remove_baseline_failure_recloses_and_retries();
  quiesced_session_keeps_every_admission_closed();
  set_site_replacement_waits_for_cleanup_final_port_then_installs();
  stale_watcher_cannot_borrow_replacement_diagnostics_adapter();
  unadvise_waits_for_cleanup_authority_then_enters_cleanup_only();
  replacement_cleans_retained_root_before_target_publication();
  failed_target_advise_retries_without_publishing_target();
  post_advise_publication_failure_unadvises_once_before_retry();
  failed_old_unadvise_retains_exact_transition_for_retry();
  failed_advised_target_unadvise_retries_without_double_advice();
  post_advise_allocation_failure_retains_exact_watcher_until_unadvised();
  unadvise_retry_prepares_unadvised_owner_before_external_call();
  failed_root_retirement_retains_old_session_before_target_advice();
  blocked_target_advise_excludes_unload_without_closing_old_session();
  synchronous_advise_and_unadvise_reentry_observe_busy_old_site();
  final_com_releases_observe_diagnostics_mutex_unlocked();
  retry_state_releases_observe_diagnostics_mutex_unlocked();
  unload_checks_before_commit_and_releases_owners_before_module_boundary();
  // New unload transaction tests are invoked below after the legacy coverage.
  tap_object_admission_linearizes_factory_creation_with_unload();
  tap_server_locks_do_not_corrupt_live_object_count();
  unload_gate_close_linearizes_with_pending_admission();
  probe_pending_ledger_blocks_unload_until_status_proves_retirement();
  probe_pending_resource_count_uses_registry_or_gate_fail_closed_maximum();
  resource_counts_uses_one_locked_session_resource_snapshot();
  hook_self_reference_precedes_install_and_releases_exactly_once();
  completed_action_remains_counted_until_barrier_proves_retirement();
  action_failure_does_not_prevent_proven_hook_retirement();
  retirement_token_is_independent_from_mutable_host_generation();
  hook_revalidates_retirement_token_immediately_before_action();
  retirement_token_publication_fails_closed();
  token_clear_failure_retains_context_and_retries_without_reunhooking();
  module_or_install_failure_preserves_token_lifecycle();
  manager_record_final_release_occurs_after_manager_unlock();
  manager_release_waits_until_last_record_removal_returns();
  manager_release_permit_may_outlive_its_manager();
  concurrent_manager_remove_is_exactly_once_and_retains_failures();
  each_manager_trigger_retention_blocks_lifecycle_and_unload();
  tap_admission_claim_is_exact_monotonic_and_replay_safe();
  tap_admission_claim_is_bound_to_the_active_probe();
  failed_pre_detach_validation_restores_every_admission();
  failed_tap_reopen_retains_the_exact_rollback_claim();
  watcher_callback_can_outlive_the_retired_probe_impl();
  same_probe_rollback_publish_serializes_the_next_unload();
  different_probes_cannot_borrow_or_reopen_a_tap_claim();
  successful_unload_clears_impl_and_unlocks_before_reentry();
  concurrent_public_impl_snapshot_survives_outer_slot_retirement();
  return EXIT_SUCCESS;
}
