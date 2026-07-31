#include "probe_safety.h"

#include <algorithm>
#include <limits>
#include <utility>

namespace cq::bridge {
namespace {

std::atomic<std::uint64_t> g_next_cleanup_issuer{1};

[[nodiscard]] bool valid_diagnostics_identity(
    DiagnosticsSessionIdentity identity) noexcept {
  return identity.epoch && identity.site_identity &&
         identity.diagnostics_identity;
}

[[nodiscard]] bool valid_cleanup_trigger(
    CapsuleCleanupTrigger trigger) noexcept {
  switch (trigger) {
    case CapsuleCleanupTrigger::timer:
    case CapsuleCleanupTrigger::explicit_removal:
    case CapsuleCleanupTrigger::shutdown:
    case CapsuleCleanupTrigger::rollback:
      return true;
  }
  return false;
}

[[nodiscard]] bool valid_cleanup_path(const CleanupIdentityPath& path,
                                      std::uintptr_t anchor_identity,
                                      std::uintptr_t grid_identity,
                                      std::uintptr_t content_identity) noexcept {
  if (!path.complete || path.count < 3 ||
      path.count > path.identities.size() || !anchor_identity ||
      !grid_identity || !content_identity ||
      path.identities[0] != anchor_identity ||
      path.identities[path.count - 1] != content_identity) {
    return false;
  }

  std::size_t grid_count = 0;
  for (std::size_t index = 0; index < path.count; ++index) {
    const auto identity = path.identities[index];
    if (!identity) {
      return false;
    }
    if (identity == grid_identity) {
      ++grid_count;
      if (index == 0 || index + 1 == path.count) {
        return false;
      }
    }
    for (std::size_t prior = 0; prior < index; ++prior) {
      if (path.identities[prior] == identity) {
        return false;
      }
    }
  }
  return grid_count == 1;
}

[[nodiscard]] bool valid_retained_cleanup_path(
    const CleanupIdentityPath& path,
    const std::vector<CleanupRetainedIdentity>& owners) noexcept {
  if (!path.complete || path.count < 3 ||
      path.count > path.identities.size() || owners.size() != path.count) {
    return false;
  }
  for (std::size_t index = 0; index < path.count; ++index) {
    if (!path.identities[index] || !owners[index].owner ||
        owners[index].identity != path.identities[index]) {
      return false;
    }
    for (std::size_t previous = 0; previous < index; ++previous) {
      if (path.identities[previous] == path.identities[index]) {
        return false;
      }
    }
  }
  return true;
}

[[nodiscard]] bool valid_cleanup_binding(
    const CleanupAuthorizationBinding& binding) noexcept {
  return binding.record_key && binding.owner_thread_id &&
         binding.host_identity && binding.xaml_root_identity &&
         binding.content_identity && binding.host_generation &&
         binding.root_generation && binding.diagnostics_epoch &&
         binding.anchor_identity && binding.grid_identity &&
         valid_cleanup_path(binding.anchor_to_content,
                            binding.anchor_identity,
                            binding.grid_identity,
                            binding.content_identity);
}

[[nodiscard]] bool cleanup_evidence_matches(
    const CleanupAuthorizationBinding& binding,
    const CleanupAuthorizationEvidence& evidence) noexcept {
  if (!valid_cleanup_binding(binding) || !evidence.host_is_still_valid ||
      evidence.owner_thread_id != binding.owner_thread_id ||
      evidence.host_identity != binding.host_identity ||
      evidence.xaml_root_identity != binding.xaml_root_identity ||
      evidence.content_identity != binding.content_identity ||
      evidence.host_generation != binding.host_generation ||
      evidence.root_generation != binding.root_generation ||
      evidence.diagnostics_epoch != binding.diagnostics_epoch ||
      evidence.anchor_identity != binding.anchor_identity ||
      evidence.grid_identity != binding.grid_identity ||
      evidence.anchor_xaml_root_identity != binding.xaml_root_identity ||
      evidence.anchor_content_identity != binding.content_identity ||
      evidence.grid_xaml_root_identity != binding.xaml_root_identity ||
      evidence.grid_content_identity != binding.content_identity ||
      !valid_cleanup_path(evidence.anchor_to_content,
                          evidence.anchor_identity,
                          evidence.grid_identity,
                          evidence.content_identity) ||
      evidence.anchor_to_content.count != binding.anchor_to_content.count) {
    return false;
  }
  return std::equal(
      binding.anchor_to_content.identities.begin(),
      binding.anchor_to_content.identities.begin() +
          static_cast<std::ptrdiff_t>(binding.anchor_to_content.count),
      evidence.anchor_to_content.identities.begin());
}

[[nodiscard]] std::uint64_t allocate_cleanup_issuer() noexcept {
  auto issuer = g_next_cleanup_issuer.load(std::memory_order_relaxed);
  while (issuer && issuer != (std::numeric_limits<std::uint64_t>::max)()) {
    if (g_next_cleanup_issuer.compare_exchange_weak(
            issuer, issuer + 1, std::memory_order_relaxed,
            std::memory_order_relaxed)) {
      return issuer;
    }
  }
  return 0;
}

}  // namespace

bool CleanupAncestryRetention::initialize(
    CleanupIdentityPath path,
    std::vector<CleanupRetainedIdentity> owners) noexcept {
  if (initialized_ || !valid_retained_cleanup_path(path, owners)) {
    return false;
  }
  path_ = path;
  owners_ = std::move(owners);
  initialized_ = true;
  return true;
}

bool CleanupAncestryRetention::matches(
    const CleanupIdentityPath& path) const noexcept {
  if (!initialized_ || !valid_retained_cleanup_path(path_, owners_) ||
      path.complete != path_.complete || path.count != path_.count) {
    return false;
  }
  return std::equal(path.identities.begin(),
                    path.identities.begin() + path.count,
                    path_.identities.begin());
}

std::size_t CleanupAncestryRetention::size() const noexcept {
  return initialized_ ? owners_.size() : 0;
}

bool cleanup_binding_is_well_formed(
    const CleanupAuthorizationBinding& binding) noexcept {
  return valid_cleanup_binding(binding);
}

bool HostRootBindingRegistry::bind(const HostRootBinding& binding) noexcept {
  if (!binding.host_identity || !binding.thread_id ||
      !binding.xaml_root_identity || !binding.content_identity ||
      !binding.host_generation || !binding.root_generation ||
      !binding.diagnostics_epoch) {
    return false;
  }
  try {
    std::lock_guard lock(mutex_);
    const auto conflict = std::find_if(
        bindings_.begin(), bindings_.end(), [&binding](const auto& existing) {
          return existing.host_identity == binding.host_identity ||
                 existing.xaml_root_identity == binding.xaml_root_identity ||
                 existing.content_identity == binding.content_identity;
        });
    if (conflict != bindings_.end()) {
      return false;
    }
    bindings_.push_back(binding);
    return true;
  } catch (...) {
    return false;
  }
}

bool HostRootBindingRegistry::invalidate(std::uintptr_t host_identity,
                                         std::uint64_t host_generation,
                                         std::uint64_t root_generation,
                                         std::uint64_t diagnostics_epoch) noexcept {
  std::lock_guard lock(mutex_);
  const auto found = std::find_if(
      bindings_.begin(), bindings_.end(),
      [host_identity, host_generation, root_generation,
       diagnostics_epoch](const auto& binding) {
        return binding.host_identity == host_identity &&
               binding.host_generation == host_generation &&
               binding.root_generation == root_generation &&
               binding.diagnostics_epoch == diagnostics_epoch;
      });
  if (found == bindings_.end()) {
    return false;
  }
  bindings_.erase(found);
  return true;
}

void HostRootBindingRegistry::invalidate_all() noexcept {
  std::lock_guard lock(mutex_);
  bindings_.clear();
}

RootMatchResult HostRootBindingRegistry::match(
    const VisualRootEvidence& evidence) const noexcept {
  std::lock_guard lock(mutex_);
  const auto found = std::find_if(
      bindings_.begin(), bindings_.end(), [&evidence](const auto& binding) {
        return binding.host_identity == evidence.host_identity;
      });
  if (found == bindings_.end()) {
    return RootMatchResult::missing_binding;
  }
  if (!evidence.host_is_still_valid) {
    return RootMatchResult::destroyed_host;
  }
  if (found->host_generation != evidence.host_generation) {
    return RootMatchResult::stale_host_generation;
  }
  if (found->root_generation != evidence.root_generation) {
    return RootMatchResult::stale_root_generation;
  }
  if (found->diagnostics_epoch != evidence.diagnostics_epoch) {
    return RootMatchResult::stale_diagnostics_epoch;
  }
  if (found->thread_id != evidence.thread_id) {
    return RootMatchResult::wrong_thread;
  }
  if (found->xaml_root_identity != evidence.xaml_root_identity) {
    return RootMatchResult::wrong_xaml_root;
  }
  if (found->content_identity != evidence.content_identity) {
    return RootMatchResult::wrong_content;
  }
  return RootMatchResult::exact;
}

std::size_t HostRootBindingRegistry::size() const noexcept {
  std::lock_guard lock(mutex_);
  return bindings_.size();
}

HandoffRequest::HandoffRequest(Action action) : action_(std::move(action)) {}

bool HandoffRequest::try_claim() noexcept {
  std::lock_guard lock(mutex_);
  if (state_ != State::waiting || cancel_requested_) {
    return false;
  }
  state_ = State::claimed;
  return true;
}

void HandoffRequest::run_claimed_action() noexcept {
  {
    std::lock_guard lock(mutex_);
    if (state_ != State::claimed) {
      return;
    }
    if (cancel_requested_) {
      state_ = State::cancelled;
      condition_.notify_all();
      return;
    }
  }

  bool succeeded = false;
  try {
    if (action_) {
      action_();
      succeeded = true;
    }
  } catch (...) {
  }

  {
    std::lock_guard lock(mutex_);
    state_ = succeeded ? State::completed : State::action_failed;
  }
  condition_.notify_all();
}

void HandoffRequest::request_cancel() noexcept {
  std::lock_guard lock(mutex_);
  cancel_requested_ = true;
  if (state_ == State::waiting) {
    state_ = State::cancelled;
    condition_.notify_all();
  }
}

HandoffResult HandoffRequest::wait_for(
    std::chrono::milliseconds timeout) noexcept {
  std::unique_lock lock(mutex_);
  if (!terminal(state_)) {
    (void)condition_.wait_for(lock, timeout,
                              [this] { return terminal(state_); });
  }
  if (terminal(state_)) {
    return result_for(state_);
  }
  if (state_ == State::waiting) {
    cancel_requested_ = true;
    state_ = State::timed_out_unclaimed;
    return HandoffResult::timed_out_unclaimed;
  }
  return HandoffResult::timed_out_in_flight;
}

HandoffResult HandoffRequest::result_for(State state) noexcept {
  switch (state) {
    case State::completed:
      return HandoffResult::completed;
    case State::action_failed:
      return HandoffResult::action_failed;
    case State::cancelled:
      return HandoffResult::cancelled;
    case State::timed_out_unclaimed:
      return HandoffResult::timed_out_unclaimed;
    case State::waiting:
    case State::claimed:
      return HandoffResult::timed_out_in_flight;
  }
  return HandoffResult::action_failed;
}

bool HandoffRequest::terminal(State state) noexcept {
  return state == State::completed || state == State::action_failed ||
         state == State::cancelled || state == State::timed_out_unclaimed;
}

HandoffModuleRelease::HandoffModuleRelease(std::uintptr_t module) noexcept
    : module_(module) {}

HandoffModuleRelease::HandoffModuleRelease(
    HandoffModuleRelease&& other) noexcept
    : module_(std::exchange(other.module_, 0)) {}

HandoffModuleRelease& HandoffModuleRelease::operator=(
    HandoffModuleRelease&& other) noexcept {
  if (this != &other && module_ == 0) {
    module_ = std::exchange(other.module_, 0);
  }
  return *this;
}

bool HandoffModuleRelease::release(
    HandoffRetirementBoundary& boundary) noexcept {
  const auto module = std::exchange(module_, 0);
  if (!module) {
    return false;
  }
  boundary.release_module(module);
  return true;
}

bool HandoffModuleRelease::pending() const noexcept { return module_ != 0; }

HandoffRetirement::HandoffRetirement(
    std::uintptr_t module,
    HandoffRetirementTarget target) noexcept
    : module_(module), target_(target) {}

bool HandoffRetirement::mark_token_published() noexcept {
  std::lock_guard lock(retirement_mutex_);
  if (!module_ || !target_.retirement_token || hook_ ||
      stage_ != HandoffRetirementStage::waiting_for_token_publication) {
    return false;
  }
  stage_ = HandoffRetirementStage::token_published_waiting_for_hook;
  return true;
}

bool HandoffRetirement::attach_hook(std::uintptr_t hook) noexcept {
  std::lock_guard lock(retirement_mutex_);
  if (!module_ || !hook ||
      stage_ != HandoffRetirementStage::token_published_waiting_for_hook) {
    return false;
  }
  hook_ = hook;
  stage_ = HandoffRetirementStage::hook_installed;
  return true;
}

bool HandoffRetirement::mark_hook_install_failed() noexcept {
  std::lock_guard lock(retirement_mutex_);
  if (!module_ || hook_ ||
      stage_ != HandoffRetirementStage::token_published_waiting_for_hook ||
      frame_counter_fault_.load(std::memory_order_acquire) ||
      active_frames_.load(std::memory_order_acquire) != 0) {
    return false;
  }
  verified_entry_sequence_ = entry_sequence_.load(std::memory_order_acquire);
  stage_ = HandoffRetirementStage::no_hook_waiting_for_token_clear;
  return true;
}

bool HandoffRetirement::enter_hook_frame() noexcept {
  auto active = active_frames_.load(std::memory_order_acquire);
  for (;;) {
    if (active == (std::numeric_limits<std::size_t>::max)()) {
      frame_counter_fault_.store(true, std::memory_order_release);
      return false;
    }
    if (active_frames_.compare_exchange_weak(
            active, active + 1, std::memory_order_acq_rel,
            std::memory_order_acquire)) {
      break;
    }
  }

  auto sequence = entry_sequence_.load(std::memory_order_acquire);
  for (;;) {
    if (sequence == (std::numeric_limits<std::uint64_t>::max)()) {
      frame_counter_fault_.store(true, std::memory_order_release);
      return true;
    }
    if (entry_sequence_.compare_exchange_weak(
            sequence, sequence + 1, std::memory_order_acq_rel,
            std::memory_order_acquire)) {
      return true;
    }
  }
}

void HandoffRetirement::exit_hook_frame() noexcept {
  auto active = active_frames_.load(std::memory_order_acquire);
  for (;;) {
    if (active == 0) {
      frame_counter_fault_.store(true, std::memory_order_release);
      return;
    }
    if (active_frames_.compare_exchange_weak(
            active, active - 1, std::memory_order_acq_rel,
            std::memory_order_acquire)) {
      return;
    }
  }
}

HandoffRetirementAttempt HandoffRetirement::attempt(
    HandoffRetirementBoundary& boundary) noexcept {
  std::uintptr_t hook = 0;
  std::uint64_t operation_token = 0;
  {
    std::unique_lock lock(retirement_mutex_, std::try_to_lock);
    if (!lock.owns_lock() ||
        stage_ == HandoffRetirementStage::unhook_in_progress ||
        stage_ == HandoffRetirementStage::barrier_in_progress ||
        stage_ == HandoffRetirementStage::token_clear_in_progress) {
      return HandoffRetirementAttempt::retryable_unsafe;
    }
    if (stage_ == HandoffRetirementStage::unhook_uncertain ||
        stage_ == HandoffRetirementStage::token_clear_uncertain ||
        stage_ == HandoffRetirementStage::waiting_for_token_publication ||
        stage_ ==
            HandoffRetirementStage::token_published_waiting_for_hook) {
      return HandoffRetirementAttempt::permanently_unsafe;
    }
    if (stage_ ==
            HandoffRetirementStage::token_cleared_waiting_for_detach ||
        stage_ == HandoffRetirementStage::detached) {
      return HandoffRetirementAttempt::ready_to_detach;
    }
    if (stage_ == HandoffRetirementStage::hook_installed) {
      if (attempt_token_ == (std::numeric_limits<std::uint64_t>::max)()) {
        stage_ = HandoffRetirementStage::unhook_uncertain;
        return HandoffRetirementAttempt::permanently_unsafe;
      }
      operation_token = ++attempt_token_;
      stage_ = HandoffRetirementStage::unhook_in_progress;
      hook = hook_;
    }
  }

  if (hook) {
    const bool unhooked = boundary.unhook(hook);
    std::lock_guard lock(retirement_mutex_);
    if (stage_ != HandoffRetirementStage::unhook_in_progress ||
        attempt_token_ != operation_token || !unhooked) {
      stage_ = HandoffRetirementStage::unhook_uncertain;
      return HandoffRetirementAttempt::permanently_unsafe;
    }
    stage_ = HandoffRetirementStage::unhooked_waiting_for_barrier;
  }

  std::uint64_t sequence = 0;
  bool run_barrier = false;
  {
    std::unique_lock lock(retirement_mutex_, std::try_to_lock);
    if (!lock.owns_lock() ||
        stage_ == HandoffRetirementStage::unhook_in_progress ||
        stage_ == HandoffRetirementStage::barrier_in_progress ||
        stage_ == HandoffRetirementStage::token_clear_in_progress) {
      return HandoffRetirementAttempt::retryable_unsafe;
    }
    if (stage_ == HandoffRetirementStage::unhook_uncertain ||
        stage_ == HandoffRetirementStage::token_clear_uncertain ||
        stage_ == HandoffRetirementStage::waiting_for_token_publication ||
        stage_ ==
            HandoffRetirementStage::token_published_waiting_for_hook) {
      return HandoffRetirementAttempt::permanently_unsafe;
    }
    if (stage_ ==
            HandoffRetirementStage::token_cleared_waiting_for_detach ||
        stage_ == HandoffRetirementStage::detached) {
      return HandoffRetirementAttempt::ready_to_detach;
    }
    if (stage_ == HandoffRetirementStage::no_hook_waiting_for_token_clear ||
        stage_ == HandoffRetirementStage::
                      retirement_ready_waiting_for_token_clear) {
      // The next phase clears the exact retirement token.
    } else if (stage_ !=
                   HandoffRetirementStage::unhooked_waiting_for_barrier ||
               frame_counter_fault_.load(std::memory_order_acquire) ||
               active_frames_.load(std::memory_order_acquire) != 0) {
      return HandoffRetirementAttempt::retryable_unsafe;
    } else if (attempt_token_ ==
               (std::numeric_limits<std::uint64_t>::max)()) {
      frame_counter_fault_.store(true, std::memory_order_release);
      return HandoffRetirementAttempt::retryable_unsafe;
    } else {
      sequence = entry_sequence_.load(std::memory_order_acquire);
      operation_token = ++attempt_token_;
      stage_ = HandoffRetirementStage::barrier_in_progress;
      run_barrier = true;
    }
  }

  if (run_barrier) {
    const bool barrier_completed = boundary.send_barrier(target_);
    std::lock_guard lock(retirement_mutex_);
    if (stage_ != HandoffRetirementStage::barrier_in_progress ||
        attempt_token_ != operation_token) {
      frame_counter_fault_.store(true, std::memory_order_release);
      return HandoffRetirementAttempt::retryable_unsafe;
    }
    if (!barrier_completed ||
        frame_counter_fault_.load(std::memory_order_acquire) ||
        active_frames_.load(std::memory_order_acquire) != 0 ||
        entry_sequence_.load(std::memory_order_acquire) != sequence) {
      stage_ = HandoffRetirementStage::unhooked_waiting_for_barrier;
      return HandoffRetirementAttempt::retryable_unsafe;
    }
    verified_entry_sequence_ = sequence;
    stage_ = HandoffRetirementStage::
        retirement_ready_waiting_for_token_clear;
  }

  HandoffRetirementStage retry_stage =
      HandoffRetirementStage::retirement_ready_waiting_for_token_clear;
  {
    std::unique_lock lock(retirement_mutex_, std::try_to_lock);
    if (!lock.owns_lock() ||
        stage_ == HandoffRetirementStage::token_clear_in_progress) {
      return HandoffRetirementAttempt::retryable_unsafe;
    }
    if (stage_ == HandoffRetirementStage::token_clear_uncertain ||
        stage_ == HandoffRetirementStage::unhook_uncertain) {
      return HandoffRetirementAttempt::permanently_unsafe;
    }
    if (stage_ ==
            HandoffRetirementStage::token_cleared_waiting_for_detach ||
        stage_ == HandoffRetirementStage::detached) {
      return HandoffRetirementAttempt::ready_to_detach;
    }
    if (stage_ != HandoffRetirementStage::no_hook_waiting_for_token_clear &&
        stage_ != HandoffRetirementStage::
                      retirement_ready_waiting_for_token_clear) {
      return HandoffRetirementAttempt::retryable_unsafe;
    }
    if (frame_counter_fault_.load(std::memory_order_acquire) ||
        active_frames_.load(std::memory_order_acquire) != 0 ||
        entry_sequence_.load(std::memory_order_acquire) !=
            verified_entry_sequence_) {
      if (stage_ == HandoffRetirementStage::
                        retirement_ready_waiting_for_token_clear) {
        stage_ = HandoffRetirementStage::unhooked_waiting_for_barrier;
        return HandoffRetirementAttempt::retryable_unsafe;
      }
      stage_ = HandoffRetirementStage::token_clear_uncertain;
      return HandoffRetirementAttempt::permanently_unsafe;
    }
    if (attempt_token_ == (std::numeric_limits<std::uint64_t>::max)()) {
      stage_ = HandoffRetirementStage::token_clear_uncertain;
      return HandoffRetirementAttempt::permanently_unsafe;
    }
    retry_stage = stage_;
    operation_token = ++attempt_token_;
    stage_ = HandoffRetirementStage::token_clear_in_progress;
  }

  const auto clear_result = boundary.clear_retirement_token(target_);
  std::lock_guard lock(retirement_mutex_);
  if (stage_ != HandoffRetirementStage::token_clear_in_progress ||
      attempt_token_ != operation_token) {
    stage_ = HandoffRetirementStage::token_clear_uncertain;
    return HandoffRetirementAttempt::permanently_unsafe;
  }
  if (clear_result == HandoffTokenClearResult::retryable_failure) {
    stage_ = retry_stage;
    return HandoffRetirementAttempt::retryable_unsafe;
  }
  if (clear_result == HandoffTokenClearResult::permanently_unsafe ||
      frame_counter_fault_.load(std::memory_order_acquire) ||
      active_frames_.load(std::memory_order_acquire) != 0 ||
      entry_sequence_.load(std::memory_order_acquire) !=
          verified_entry_sequence_) {
    stage_ = HandoffRetirementStage::token_clear_uncertain;
    return HandoffRetirementAttempt::permanently_unsafe;
  }
  stage_ = HandoffRetirementStage::token_cleared_waiting_for_detach;
  return HandoffRetirementAttempt::ready_to_detach;
}

std::optional<HandoffModuleRelease>
HandoffRetirement::detach_ready() noexcept {
  std::unique_lock lock(retirement_mutex_, std::try_to_lock);
  if (!lock.owns_lock()) {
    return std::nullopt;
  }
  if (stage_ !=
          HandoffRetirementStage::token_cleared_waiting_for_detach ||
      frame_counter_fault_.load(std::memory_order_acquire) ||
      active_frames_.load(std::memory_order_acquire) != 0 ||
      entry_sequence_.load(std::memory_order_acquire) !=
          verified_entry_sequence_ ||
      !module_) {
    if (stage_ ==
        HandoffRetirementStage::token_cleared_waiting_for_detach) {
      stage_ = HandoffRetirementStage::token_clear_uncertain;
    }
    return std::nullopt;
  }
  stage_ = HandoffRetirementStage::detached;
  return HandoffModuleRelease(std::exchange(module_, 0));
}

std::optional<HandoffModuleRelease>
HandoffRetirement::detach_without_published_token() noexcept {
  std::lock_guard lock(retirement_mutex_);
  if (stage_ != HandoffRetirementStage::waiting_for_token_publication ||
      !module_ || hook_ ||
      active_frames_.load(std::memory_order_acquire) != 0) {
    return std::nullopt;
  }
  stage_ = HandoffRetirementStage::detached;
  return HandoffModuleRelease(std::exchange(module_, 0));
}

HandoffRetirementStage HandoffRetirement::stage() const noexcept {
  std::lock_guard lock(retirement_mutex_);
  return stage_;
}

bool HandoffRetirement::outstanding() const noexcept {
  return stage() != HandoffRetirementStage::detached;
}

std::size_t HandoffRetirement::active_frames() const noexcept {
  return active_frames_.load(std::memory_order_acquire);
}

std::uint64_t HandoffRetirement::entry_sequence() const noexcept {
  return entry_sequence_.load(std::memory_order_acquire);
}

AdmissionGate::Lease::Lease(AdmissionGate& owner) noexcept : owner_(&owner) {}

AdmissionGate::Lease::Lease(Lease&& other) noexcept
    : owner_(std::exchange(other.owner_, nullptr)) {}

AdmissionGate::Lease& AdmissionGate::Lease::operator=(Lease&& other) noexcept {
  if (this != &other) {
    release();
    owner_ = std::exchange(other.owner_, nullptr);
  }
  return *this;
}

AdmissionGate::Lease::~Lease() { release(); }

void AdmissionGate::Lease::release() noexcept {
  if (owner_) {
    std::exchange(owner_, nullptr)->release();
  }
}

AdmissionGate::AdmissionGate(std::size_t maximum_live) noexcept
    : maximum_live_(maximum_live) {}

std::optional<AdmissionGate::Lease> AdmissionGate::try_acquire() noexcept {
  std::lock_guard lock(mutex_);
  if (live_ == (std::numeric_limits<std::size_t>::max)() ||
      live_ >= maximum_live_) {
    if (counter_faults_ != (std::numeric_limits<std::size_t>::max)()) {
      ++counter_faults_;
    }
    return std::nullopt;
  }

  // Increment first, then recheck admission while close() is excluded by the
  // same lock. A rejected entrant releases its count before returning.
  ++live_;
  if (!accepting_) {
    --live_;
    return std::nullopt;
  }
  return Lease{*this};
}

void AdmissionGate::close() noexcept {
  std::lock_guard lock(mutex_);
  accepting_ = false;
}

bool AdmissionGate::reopen_if_drained() noexcept {
  std::lock_guard lock(mutex_);
  if (accepting_ || live_) {
    return false;
  }
  accepting_ = true;
  return true;
}

bool AdmissionGate::accepting() const noexcept {
  std::lock_guard lock(mutex_);
  return accepting_;
}

bool AdmissionGate::drained() const noexcept {
  std::lock_guard lock(mutex_);
  return live_ == 0;
}

std::size_t AdmissionGate::live_count() const noexcept {
  std::lock_guard lock(mutex_);
  return live_;
}

std::size_t AdmissionGate::counter_faults() const noexcept {
  std::lock_guard lock(mutex_);
  return counter_faults_;
}

void AdmissionGate::release() noexcept {
  std::lock_guard lock(mutex_);
  if (!live_) {
    if (counter_faults_ != (std::numeric_limits<std::size_t>::max)()) {
      ++counter_faults_;
    }
    return;
  }
  --live_;
}

struct PendingActionLedger::Record final {
  Record(std::uint64_t record_id,
         AdmissionGate::Lease record_lease,
         std::shared_ptr<void> record_owner,
         std::shared_ptr<PendingDispatchCallback> record_callback,
         PendingModuleBoundary& record_module_boundary,
         std::uintptr_t record_module) noexcept
      : id(record_id),
        owner(std::move(record_owner)),
        lease(std::move(record_lease)),
        callback(std::move(record_callback)),
        module_boundary(&record_module_boundary),
        module(record_module) {}

  std::uint64_t id = 0;
  // Declared before the non-owning lease so reverse destruction releases the
  // lease first even on exceptional/test-only record teardown.
  std::shared_ptr<void> owner;
  std::optional<AdmissionGate::Lease> lease;
  std::shared_ptr<PendingDispatchCallback> callback;
  std::shared_ptr<PendingAsyncAction> action;
  PendingModuleBoundary* module_boundary = nullptr;
  std::uintptr_t module = 0;
  PendingActionStage stage = PendingActionStage::preparing;
  bool claimed = false;
};

namespace {

struct PendingRetirementBundle final {
  std::optional<AdmissionGate::Lease> lease;
  std::shared_ptr<void> owner;
  std::shared_ptr<PendingDispatchCallback> callback;
  std::shared_ptr<PendingAsyncAction> action;
  PendingModuleBoundary* module_boundary = nullptr;
  std::uintptr_t module = 0;
};

void release_pending_bundle(PendingRetirementBundle& bundle) noexcept {
  // Each reset can invoke foreign code.  This helper is therefore called only
  // after the ledger lock has been released, while the retiring record still
  // keeps resource accounting non-zero.
  bundle.action.reset();
  bundle.callback.reset();
  // A lease contains a non-owning pointer to its gate.  The strong owner may
  // itself own that gate, so it must remain alive through lease release.
  bundle.lease.reset();
  bundle.owner.reset();
  const auto module = std::exchange(bundle.module, 0);
  auto* const boundary = std::exchange(bundle.module_boundary, nullptr);
  if (module && boundary) {
    boundary->release_module(module);
  }
}

[[nodiscard]] bool terminal_pending_status(
    PendingAsyncStatus status) noexcept {
  return status == PendingAsyncStatus::completed ||
         status == PendingAsyncStatus::canceled ||
         status == PendingAsyncStatus::error;
}

}  // namespace

PendingActionLedger::PendingActionLedger(std::size_t maximum_live) noexcept
    : maximum_live_(maximum_live) {}

PendingActionLedger::~PendingActionLedger() {
  // Destruction is not retirement proof.  In production an outstanding record
  // owns the probe and prevents this destructor.  Swapping here merely keeps
  // test-only teardown destructors outside the mutex; module pins are
  // intentionally never released without a proven terminal action.
  std::vector<std::shared_ptr<Record>> abandoned;
  {
    std::lock_guard lock(mutex_);
    abandoned.swap(records_);
    retiring_ = 0;
  }
}

PendingScheduleResult PendingActionLedger::schedule(
    PendingModuleBoundary& module_boundary,
    PendingDispatchBoundary& dispatch_boundary,
    std::shared_ptr<void> owner,
    AdmissionGate::Lease lease,
    PendingDispatchCallback callback,
    bool fail_publication_for_test) noexcept {
  PendingScheduleResult result;
  if (!owner || !maximum_live_) {
    callback = {};
    lease = AdmissionGate::Lease{};
    owner.reset();
    return result;
  }

  std::uintptr_t module = 0;
  if (!module_boundary.acquire_self_module(module) || !module) {
    callback = {};
    lease = AdmissionGate::Lease{};
    owner.reset();
    if (module) {
      module_boundary.release_module(module);
    }
    return result;
  }

  std::shared_ptr<PendingDispatchCallback> dispatch_callback;
  std::shared_ptr<Record> record;
  try {
    dispatch_callback = std::make_shared<PendingDispatchCallback>(
        [callback = std::move(callback)]() noexcept {
          try {
            if (callback) {
              callback();
            }
          } catch (...) {
          }
        });

    record = std::make_shared<Record>(0, std::move(lease), std::move(owner),
                                      dispatch_callback, module_boundary,
                                      module);
    bool inserted = false;
    {
      std::lock_guard lock(mutex_);
      if (records_.size() < maximum_live_ && next_id_) {
        record->id = next_id_;
        records_.push_back(record);
        next_id_ = next_id_ ==
                               (std::numeric_limits<std::uint64_t>::max)()
                           ? 0
                           : next_id_ + 1;
        inserted = true;
      }
    }
    if (!inserted) {
      // Record destruction can release a lease and arbitrary owners/callbacks.
      // Keep it strictly outside the registry mutex.
      record.reset();
      dispatch_callback.reset();
      callback = {};
      lease = AdmissionGate::Lease{};
      owner.reset();
      module_boundary.release_module(module);
      return result;
    }
    result.id = record->id;
  } catch (...) {
    // No registry entry exists if construction failed.  If insertion itself
    // failed, the local record is still the sole lifetime owner.
    record.reset();
    dispatch_callback.reset();
    callback = {};
    lease = AdmissionGate::Lease{};
    owner.reset();
    module_boundary.release_module(module);
    return result;
  }

  std::shared_ptr<PendingAsyncAction> action;
  try {
    action = dispatch_boundary.queue(dispatch_callback);
  } catch (...) {
    std::lock_guard lock(mutex_);
    const auto found = std::find_if(
        records_.begin(), records_.end(), [&](const auto& candidate) {
          return candidate.get() == record.get() &&
                 candidate->id == result.id;
        });
    if (found != records_.end() &&
        (*found)->stage == PendingActionStage::preparing) {
      (*found)->stage = PendingActionStage::retained_unknown;
    } else {
      consistency_fault_ = true;
    }
    result.status = PendingScheduleStatus::retained_unknown;
    return result;
  }

  if (action) {
    static_assert(noexcept(std::declval<std::shared_ptr<PendingAsyncAction>&>() =
                           std::declval<
                               std::shared_ptr<PendingAsyncAction>&&>()));
    {
      std::lock_guard lock(mutex_);
      const auto found = std::find_if(
          records_.begin(), records_.end(), [&](const auto& candidate) {
            return candidate.get() == record.get() &&
                   candidate->id == result.id;
          });
      if (found == records_.end() ||
          (*found)->stage != PendingActionStage::preparing) {
        consistency_fault_ = true;
        result.status = PendingScheduleStatus::retained_unknown;
      } else if (fail_publication_for_test) {
        (*found)->stage = PendingActionStage::retained_unknown;
        result.status = PendingScheduleStatus::retained_unknown;
      } else {
        (*found)->action = std::move(action);
        (*found)->stage = PendingActionStage::queued;
        result.status = PendingScheduleStatus::queued;
      }
    }
    // In the injected publication-failure case this is the final action
    // release.  It is deliberately outside the ledger mutex; the unknown
    // record retains every other lifetime resource permanently.
    action.reset();
    return result;
  }

  PendingRetirementBundle retired;
  bool claimed = false;
  {
    std::lock_guard lock(mutex_);
    const auto found = std::find_if(
        records_.begin(), records_.end(), [&](const auto& candidate) {
          return candidate.get() == record.get() &&
                 candidate->id == result.id;
        });
    if (found != records_.end() &&
        (*found)->stage == PendingActionStage::preparing &&
        retiring_ != (std::numeric_limits<std::size_t>::max)()) {
      auto& entry = **found;
      entry.stage = PendingActionStage::retiring;
      ++retiring_;
      retired.lease = std::move(entry.lease);
      retired.owner = std::move(entry.owner);
      retired.callback = std::move(entry.callback);
      retired.action = std::move(entry.action);
      retired.module_boundary = std::exchange(entry.module_boundary, nullptr);
      retired.module = std::exchange(entry.module, 0);
      claimed = true;
    } else {
      consistency_fault_ = true;
    }
  }

  if (!claimed) {
    result.status = PendingScheduleStatus::retained_unknown;
    return result;
  }

  dispatch_callback.reset();
  release_pending_bundle(retired);
  {
    std::lock_guard lock(mutex_);
    const auto found = std::find_if(
        records_.begin(), records_.end(), [&](const auto& candidate) {
          return candidate.get() == record.get() &&
                 candidate->id == result.id;
        });
    if (found != records_.end() &&
        (*found)->stage == PendingActionStage::retiring && retiring_) {
      records_.erase(found);
      --retiring_;
    } else {
      consistency_fault_ = true;
    }
  }
  result.status = PendingScheduleStatus::rejected;
  return result;
}

PendingLedgerOperationResult PendingActionLedger::reap_all() noexcept {
  struct Snapshot final {
    std::uint64_t id = 0;
    std::shared_ptr<Record> record;
    std::shared_ptr<PendingAsyncAction> action;
  };

  PendingLedgerOperationResult result;
  std::vector<Snapshot> snapshots;
  try {
    std::lock_guard lock(mutex_);
    snapshots.reserve(records_.size());
    for (const auto& record : records_) {
      if (!record->claimed && record->action &&
          (record->stage == PendingActionStage::queued ||
           record->stage == PendingActionStage::cancel_requested)) {
        record->claimed = true;
        snapshots.push_back({record->id, record, record->action});
      }
    }
  } catch (...) {
    result.operation_failed = true;
    result.remaining = outstanding_count();
    return result;
  }

  for (auto& snapshot : snapshots) {
    ++result.affected;
    PendingAsyncStatus status = PendingAsyncStatus::started;
    bool status_valid = false;
    try {
      status = snapshot.action->status();
      status_valid = status == PendingAsyncStatus::started ||
                     terminal_pending_status(status);
    } catch (...) {
      result.operation_failed = true;
    }

    if (!status_valid || status == PendingAsyncStatus::started) {
      if (!status_valid) {
        result.operation_failed = true;
      }
      std::lock_guard lock(mutex_);
      const auto found = std::find_if(
          records_.begin(), records_.end(), [&](const auto& candidate) {
            return candidate.get() == snapshot.record.get() &&
                   candidate->id == snapshot.id;
          });
      if (found != records_.end() &&
          (*found)->action.get() == snapshot.action.get() &&
          ((*found)->stage == PendingActionStage::queued ||
           (*found)->stage == PendingActionStage::cancel_requested)) {
        (*found)->claimed = false;
      } else {
        consistency_fault_ = true;
        result.operation_failed = true;
      }
      continue;
    }

    PendingRetirementBundle retired;
    bool claimed = false;
    {
      std::lock_guard lock(mutex_);
      const auto found = std::find_if(
          records_.begin(), records_.end(), [&](const auto& candidate) {
            return candidate.get() == snapshot.record.get() &&
                   candidate->id == snapshot.id;
          });
      if (found != records_.end() && (*found)->claimed &&
          (*found)->action.get() == snapshot.action.get() &&
          ((*found)->stage == PendingActionStage::queued ||
           (*found)->stage == PendingActionStage::cancel_requested) &&
          retiring_ != (std::numeric_limits<std::size_t>::max)()) {
        auto& entry = **found;
        entry.stage = PendingActionStage::retiring;
        ++retiring_;
        retired.lease = std::move(entry.lease);
        retired.owner = std::move(entry.owner);
        retired.callback = std::move(entry.callback);
        retired.action = std::move(entry.action);
        retired.module_boundary =
            std::exchange(entry.module_boundary, nullptr);
        retired.module = std::exchange(entry.module, 0);
        claimed = true;
      } else {
        consistency_fault_ = true;
        result.operation_failed = true;
      }
    }
    if (!claimed) {
      continue;
    }

    snapshot.action.reset();
    release_pending_bundle(retired);
    {
      std::lock_guard lock(mutex_);
      const auto found = std::find_if(
          records_.begin(), records_.end(), [&](const auto& candidate) {
            return candidate.get() == snapshot.record.get() &&
                   candidate->id == snapshot.id;
          });
      if (found != records_.end() &&
          (*found)->stage == PendingActionStage::retiring && retiring_) {
        records_.erase(found);
        --retiring_;
        ++result.retired;
      } else {
        consistency_fault_ = true;
        result.operation_failed = true;
      }
    }
  }

  result.remaining = outstanding_count();
  return result;
}

PendingLedgerOperationResult PendingActionLedger::cancel_all() noexcept {
  struct Snapshot final {
    std::uint64_t id = 0;
    std::shared_ptr<Record> record;
    std::shared_ptr<PendingAsyncAction> action;
    bool cancel_failed = false;
  };

  // Avoid issuing Cancel against an action that was already terminal when
  // shutdown began.  Only the status poll proves delegate return.
  const auto initial_reap = reap_all();
  PendingLedgerOperationResult result;
  result.operation_failed = initial_reap.operation_failed;
  result.retired = initial_reap.retired;
  std::vector<Snapshot> snapshots;
  try {
    std::lock_guard lock(mutex_);
    snapshots.reserve(records_.size());
    for (const auto& record : records_) {
      if (!record->claimed && record->action &&
          record->stage == PendingActionStage::queued) {
        record->claimed = true;
        snapshots.push_back({record->id, record, record->action, false});
      }
    }
  } catch (...) {
    result.operation_failed = true;
    result.remaining = outstanding_count();
    return result;
  }

  for (auto& snapshot : snapshots) {
    ++result.affected;
    bool canceled = false;
    try {
      snapshot.action->cancel();
      canceled = true;
    } catch (...) {
      snapshot.cancel_failed = true;
    }

    {
      std::lock_guard lock(mutex_);
      const auto found = std::find_if(
          records_.begin(), records_.end(), [&](const auto& candidate) {
            return candidate.get() == snapshot.record.get() &&
                   candidate->id == snapshot.id;
          });
      if (found != records_.end() && (*found)->claimed &&
          (*found)->action.get() == snapshot.action.get() &&
          (*found)->stage == PendingActionStage::queued) {
        (*found)->claimed = false;
        if (canceled) {
          (*found)->stage = PendingActionStage::cancel_requested;
        }
      } else {
        consistency_fault_ = true;
        result.operation_failed = true;
      }
    }
  }

  bool cancel_failed = false;
  for (auto& snapshot : snapshots) {
    cancel_failed = cancel_failed || snapshot.cancel_failed;
    // A subsequent status poll must be able to perform the final action
    // release while its retiring count is still visible.
    snapshot.action.reset();
    snapshot.record.reset();
  }

  if (cancel_failed) {
    // Cancel commonly races normal completion.  Re-query status: a terminal
    // action is a successful retirement, while a still-live action preserves
    // the cancellation failure for retry.
    const auto raced_reap = reap_all();
    if (result.retired <=
        (std::numeric_limits<std::size_t>::max)() - raced_reap.retired) {
      result.retired += raced_reap.retired;
    } else {
      result.operation_failed = true;
    }
    result.operation_failed =
        result.operation_failed || raced_reap.operation_failed;
    std::lock_guard lock(mutex_);
    for (const auto& snapshot : snapshots) {
      if (!snapshot.cancel_failed) {
        continue;
      }
      const auto found = std::find_if(
          records_.begin(), records_.end(), [&](const auto& candidate) {
            return candidate->id == snapshot.id;
          });
      if (found != records_.end()) {
        result.operation_failed = true;
      }
    }
    result.operation_failed = result.operation_failed || consistency_fault_;
  }

  result.remaining = outstanding_count();
  return result;
}

std::size_t PendingActionLedger::outstanding_count() const noexcept {
  std::lock_guard lock(mutex_);
  auto count = (std::max)(records_.size(), retiring_);
  if (consistency_fault_ && !count) {
    count = 1;
  }
  return count;
}

std::size_t PendingActionLedger::registry_count() const noexcept {
  std::lock_guard lock(mutex_);
  return records_.size();
}

std::size_t PendingActionLedger::retiring_count() const noexcept {
  std::lock_guard lock(mutex_);
  return retiring_;
}

bool PendingActionLedger::mutex_available() const noexcept {
  std::unique_lock lock(mutex_, std::try_to_lock);
  return lock.owns_lock();
}

std::optional<PendingActionStage> PendingActionLedger::stage(
    std::uint64_t id) const noexcept {
  std::lock_guard lock(mutex_);
  const auto found = std::find_if(
      records_.begin(), records_.end(), [id](const auto& candidate) {
        return candidate->id == id;
      });
  if (found == records_.end()) {
    return std::nullopt;
  }
  return (*found)->stage;
}

struct DiagnosticsSessionGate::State final {
  std::mutex mutex;
  DiagnosticsSessionIdentity identity{};
  std::shared_ptr<DiagnosticsQueryAdapter> adapter;
  bool accepting = false;
  std::size_t live = 0;
};

DiagnosticsSessionGate::Lease::Lease(
    std::shared_ptr<State> state,
    std::shared_ptr<DiagnosticsQueryAdapter> adapter,
    DiagnosticsSessionIdentity identity) noexcept
    : state_(std::move(state)),
      adapter_(std::move(adapter)),
      identity_(identity) {}

DiagnosticsSessionGate::Lease::Lease(Lease&& other) noexcept
    : state_(std::move(other.state_)),
      adapter_(std::move(other.adapter_)),
      identity_(std::exchange(other.identity_, {})) {}

DiagnosticsSessionGate::Lease& DiagnosticsSessionGate::Lease::operator=(
    Lease&& other) noexcept {
  if (this != &other) {
    release();
    state_ = std::move(other.state_);
    adapter_ = std::move(other.adapter_);
    identity_ = std::exchange(other.identity_, {});
  }
  return *this;
}

DiagnosticsSessionGate::Lease::~Lease() { release(); }

bool DiagnosticsSessionGate::Lease::get_inspectable(
    std::uint64_t handle,
    void** object) noexcept {
  return adapter_ && object && adapter_->get_inspectable(handle, object);
}

bool DiagnosticsSessionGate::Lease::get_handle(
    void* object,
    std::uint64_t* handle) noexcept {
  return adapter_ && object && handle && adapter_->get_handle(object, handle);
}

bool DiagnosticsSessionGate::Lease::inspect_xaml(void* object) noexcept {
  return adapter_ && object && adapter_->inspect_xaml(object);
}

DiagnosticsSessionIdentity DiagnosticsSessionGate::Lease::identity()
    const noexcept {
  return identity_;
}

void DiagnosticsSessionGate::Lease::release() noexcept {
  adapter_.reset();
  identity_ = {};
  auto state = std::move(state_);
  if (!state) {
    return;
  }
  std::lock_guard lock(state->mutex);
  if (state->live) {
    --state->live;
  }
}

DiagnosticsSessionGate::DiagnosticsSessionGate() noexcept {
  try {
    state_ = std::make_shared<State>();
  } catch (...) {
  }
}

bool DiagnosticsSessionGate::install(
    DiagnosticsSessionIdentity identity,
    std::shared_ptr<DiagnosticsQueryAdapter> adapter) noexcept {
  if (!state_ || !valid_diagnostics_identity(identity) || !adapter) {
    return false;
  }
  std::lock_guard lock(state_->mutex);
  if (state_->accepting || state_->live || state_->adapter) {
    return false;
  }
  state_->identity = identity;
  state_->adapter = std::move(adapter);
  state_->accepting = true;
  return true;
}

std::optional<DiagnosticsSessionGate::Lease>
DiagnosticsSessionGate::try_acquire(
    DiagnosticsSessionIdentity expected) noexcept {
  if (!state_ || !valid_diagnostics_identity(expected)) {
    return std::nullopt;
  }
  std::lock_guard lock(state_->mutex);
  if (!state_->accepting || !state_->adapter || !state_->identity.epoch ||
      !state_->identity.site_identity ||
      !state_->identity.diagnostics_identity ||
      state_->identity != expected ||
      state_->live == (std::numeric_limits<std::size_t>::max)()) {
    return std::nullopt;
  }
  ++state_->live;
  return Lease{state_, state_->adapter, state_->identity};
}

std::optional<DiagnosticsSessionIdentity> DiagnosticsSessionGate::identity()
    const noexcept {
  if (!state_) {
    return std::nullopt;
  }
  std::lock_guard lock(state_->mutex);
  if (!state_->adapter || !valid_diagnostics_identity(state_->identity)) {
    return std::nullopt;
  }
  return state_->identity;
}

SessionCloseResult DiagnosticsSessionGate::close() noexcept {
  if (!state_) {
    return SessionCloseResult::drained;
  }
  std::lock_guard lock(state_->mutex);
  state_->accepting = false;
  return state_->live ? SessionCloseResult::in_flight
                       : SessionCloseResult::drained;
}

std::optional<DiagnosticsSessionGate::DetachedSession>
DiagnosticsSessionGate::detach_closed(
    DiagnosticsSessionIdentity expected) noexcept {
  if (!state_ || !valid_diagnostics_identity(expected)) {
    return std::nullopt;
  }
  DetachedSession detached;
  {
    std::lock_guard lock(state_->mutex);
    if (state_->accepting || state_->live || !state_->adapter ||
        state_->identity != expected) {
      return std::nullopt;
    }
    detached.identity = std::exchange(state_->identity, {});
    detached.adapter = std::move(state_->adapter);
  }
  return std::optional<DetachedSession>{std::move(detached)};
}

std::optional<DiagnosticsSessionGate::DetachedSession>
DiagnosticsSessionGate::replace_closed(
    DiagnosticsSessionIdentity expected,
    DiagnosticsSessionIdentity replacement_identity,
    std::shared_ptr<DiagnosticsQueryAdapter> replacement_adapter) noexcept {
  if (!state_ || !valid_diagnostics_identity(expected) ||
      !valid_diagnostics_identity(replacement_identity) ||
      replacement_identity.epoch == expected.epoch || !replacement_adapter) {
    return std::nullopt;
  }
  DetachedSession detached;
  {
    std::lock_guard lock(state_->mutex);
    if (state_->accepting || state_->live || !state_->adapter ||
        state_->identity != expected) {
      return std::nullopt;
    }
    detached.identity = state_->identity;
    detached.adapter = std::move(state_->adapter);
    state_->identity = replacement_identity;
    state_->adapter = std::move(replacement_adapter);
    state_->accepting = true;
  }
  return std::optional<DetachedSession>{std::move(detached)};
}

bool DiagnosticsSessionGate::reset_if_drained() noexcept {
  if (!state_) {
    return true;
  }
  std::shared_ptr<DiagnosticsQueryAdapter> detached;
  {
    std::lock_guard lock(state_->mutex);
    if (state_->accepting || state_->live) {
      return false;
    }
    state_->identity = {};
    detached = std::move(state_->adapter);
  }
  return true;
}

std::size_t DiagnosticsSessionGate::live_count() const noexcept {
  if (!state_) {
    return 0;
  }
  std::lock_guard lock(state_->mutex);
  return state_->live;
}

DiagnosticsCallbackPipeline::DiagnosticsCallbackPipeline(
    DiagnosticsSessionGate& session) noexcept
    : session_(session) {}

bool DiagnosticsCallbackPipeline::process_add(
    DiagnosticsSessionIdentity expected,
    DiagnosticsCallbackAddAdapter& adapter) noexcept {
  auto session = session_.try_acquire(expected);
  return session && adapter.process_add(*session);
}

struct CleanupAuthorityGate::State final {
  std::mutex mutex;
  CleanupAuthorityMode mode = CleanupAuthorityMode::forward;
  std::uint64_t transition_token = 0;
  std::size_t live = 0;
};

CleanupAuthorityGate::TransitionPermit::TransitionPermit(
    std::shared_ptr<State> state,
    std::uint64_t transition_token) noexcept
    : state_(std::move(state)), transition_token_(transition_token) {}

CleanupAuthorityGate::TransitionPermit::TransitionPermit(
    TransitionPermit&& other) noexcept
    : state_(std::move(other.state_)),
      transition_token_(std::exchange(other.transition_token_, 0)) {}

CleanupAuthorityGate::TransitionPermit&
CleanupAuthorityGate::TransitionPermit::operator=(
    TransitionPermit&& other) noexcept {
  if (this != &other) {
    state_ = std::move(other.state_);
    transition_token_ = std::exchange(other.transition_token_, 0);
  }
  return *this;
}

bool CleanupAuthorityGate::TransitionPermit::valid_for(
    const CleanupAuthorityGate& gate) const noexcept {
  return transition_token_ && state_ && state_ == gate.state_;
}

CleanupAuthorityGate::Lease::Lease(std::shared_ptr<State> state) noexcept
    : state_(std::move(state)) {}

CleanupAuthorityGate::Lease::Lease(Lease&& other) noexcept
    : state_(std::move(other.state_)) {}

CleanupAuthorityGate::Lease& CleanupAuthorityGate::Lease::operator=(
    Lease&& other) noexcept {
  if (this != &other) {
    release();
    state_ = std::move(other.state_);
  }
  return *this;
}

CleanupAuthorityGate::Lease::~Lease() { release(); }

void CleanupAuthorityGate::Lease::release() noexcept {
  auto state = std::move(state_);
  if (!state) {
    return;
  }
  std::lock_guard lock(state->mutex);
  if (state->live) {
    --state->live;
  }
}

bool CleanupAuthorityGate::Lease::valid() const noexcept {
  return static_cast<bool>(state_);
}

CleanupAuthorityGate::MutationLease::MutationLease(
    std::shared_ptr<State> state) noexcept
    : state_(std::move(state)) {}

CleanupAuthorityGate::MutationLease::MutationLease(
    MutationLease&& other) noexcept
    : state_(std::move(other.state_)) {}

CleanupAuthorityGate::MutationLease&
CleanupAuthorityGate::MutationLease::operator=(MutationLease&& other) noexcept {
  if (this != &other) {
    release();
    state_ = std::move(other.state_);
  }
  return *this;
}

CleanupAuthorityGate::MutationLease::~MutationLease() { release(); }

void CleanupAuthorityGate::MutationLease::release() noexcept {
  auto state = std::move(state_);
  if (!state) {
    return;
  }
  std::lock_guard lock(state->mutex);
  if (state->live) {
    --state->live;
  }
}

bool CleanupAuthorityGate::MutationLease::valid_for(
    const CleanupAuthorityGate& gate) const noexcept {
  return state_ && state_ == gate.state_;
}

CleanupAuthorityGate::CleanupAuthorityGate() noexcept {
  try {
    state_ = std::make_shared<State>();
  } catch (...) {
  }
}

std::optional<CleanupAuthorityGate::Lease>
CleanupAuthorityGate::try_acquire() noexcept {
  if (!state_) {
    return std::nullopt;
  }
  std::lock_guard lock(state_->mutex);
  if (state_->mode != CleanupAuthorityMode::forward ||
      state_->live == (std::numeric_limits<std::size_t>::max)()) {
    return std::nullopt;
  }
  ++state_->live;
  Lease lease{state_};
  return std::optional<Lease>{std::move(lease)};
}

SessionCloseResult CleanupAuthorityGate::close() noexcept {
  if (!state_) {
    return SessionCloseResult::drained;
  }
  std::lock_guard lock(state_->mutex);
  if (state_->mode != CleanupAuthorityMode::revoked) {
    state_->mode = CleanupAuthorityMode::closed;
  }
  return state_->live ? SessionCloseResult::in_flight
                       : SessionCloseResult::drained;
}

bool CleanupAuthorityGate::reopen_if_drained() noexcept {
  if (!state_) {
    return false;
  }
  std::lock_guard lock(state_->mutex);
  if (state_->mode != CleanupAuthorityMode::closed ||
      state_->transition_token || state_->live) {
    return false;
  }
  state_->mode = CleanupAuthorityMode::forward;
  return true;
}

std::optional<CleanupAuthorityGate::TransitionPermit>
CleanupAuthorityGate::begin_transition(
    std::uint64_t transition_token,
    SessionCloseResult& close_result) noexcept {
  close_result = SessionCloseResult::drained;
  if (!state_ || !transition_token) {
    return std::nullopt;
  }
  std::lock_guard lock(state_->mutex);
  close_result = state_->live ? SessionCloseResult::in_flight
                              : SessionCloseResult::drained;
  if (state_->mode == CleanupAuthorityMode::revoked ||
      (state_->transition_token &&
       state_->transition_token != transition_token) ||
      (!state_->transition_token &&
       state_->mode != CleanupAuthorityMode::forward)) {
    return std::nullopt;
  }
  if (!state_->transition_token) {
    state_->transition_token = transition_token;
    state_->mode = CleanupAuthorityMode::closed;
  }
  TransitionPermit permit{state_, transition_token};
  return std::optional<TransitionPermit>{std::move(permit)};
}

bool CleanupAuthorityGate::enter_cleanup_only(
    std::uint64_t transition_token,
    const TransitionPermit& permit) noexcept {
  if (!state_ || !transition_token || !permit.valid_for(*this)) {
    return false;
  }
  std::lock_guard lock(state_->mutex);
  if (permit.transition_token_ != transition_token ||
      state_->transition_token != transition_token || state_->live ||
      (state_->mode != CleanupAuthorityMode::closed &&
       state_->mode != CleanupAuthorityMode::cleanup_only)) {
    return false;
  }
  state_->mode = CleanupAuthorityMode::cleanup_only;
  return true;
}

std::optional<SessionCloseResult> CleanupAuthorityGate::close_cleanup_only(
    std::uint64_t transition_token,
    const TransitionPermit& permit) noexcept {
  if (!state_ || !transition_token || !permit.valid_for(*this)) {
    return std::nullopt;
  }
  std::lock_guard lock(state_->mutex);
  if (permit.transition_token_ != transition_token ||
      state_->transition_token != transition_token ||
      (state_->mode != CleanupAuthorityMode::cleanup_only &&
       state_->mode != CleanupAuthorityMode::closed)) {
    return std::nullopt;
  }
  state_->mode = CleanupAuthorityMode::closed;
  return state_->live ? SessionCloseResult::in_flight
                      : SessionCloseResult::drained;
}

SessionCloseResult CleanupAuthorityGate::revoke_permanently() noexcept {
  if (!state_) {
    return SessionCloseResult::drained;
  }
  std::lock_guard lock(state_->mutex);
  state_->mode = CleanupAuthorityMode::revoked;
  state_->transition_token = 0;
  return state_->live ? SessionCloseResult::in_flight
                      : SessionCloseResult::drained;
}

std::optional<CleanupAuthorityGate::MutationLease>
CleanupAuthorityGate::try_acquire_mutation() noexcept {
  if (!state_) {
    return std::nullopt;
  }
  std::lock_guard lock(state_->mutex);
  if (state_->mode != CleanupAuthorityMode::forward ||
      state_->live == (std::numeric_limits<std::size_t>::max)()) {
    return std::nullopt;
  }
  ++state_->live;
  MutationLease lease{state_};
  return std::optional<MutationLease>{std::move(lease)};
}

std::optional<CleanupAuthorityGate::Lease>
CleanupAuthorityGate::try_acquire_transition(
    const TransitionPermit& permit) noexcept {
  if (!state_ || !permit.valid_for(*this)) {
    return std::nullopt;
  }
  std::lock_guard lock(state_->mutex);
  if (state_->mode != CleanupAuthorityMode::cleanup_only ||
      state_->transition_token != permit.transition_token_ ||
      state_->live == (std::numeric_limits<std::size_t>::max)()) {
    return std::nullopt;
  }
  ++state_->live;
  Lease lease{state_};
  return std::optional<Lease>{std::move(lease)};
}

bool CleanupAuthorityGate::available() const noexcept {
  return static_cast<bool>(state_);
}

bool CleanupAuthorityGate::accepting() const noexcept {
  if (!state_) {
    return false;
  }
  std::lock_guard lock(state_->mutex);
  return state_->mode == CleanupAuthorityMode::forward;
}

CleanupAuthorityMode CleanupAuthorityGate::mode() const noexcept {
  if (!state_) {
    return CleanupAuthorityMode::revoked;
  }
  std::lock_guard lock(state_->mutex);
  return state_->mode;
}

std::size_t CleanupAuthorityGate::live_count() const noexcept {
  if (!state_) {
    return 0;
  }
  std::lock_guard lock(state_->mutex);
  return state_->live;
}

CleanupAuthorization::CleanupAuthorization(
    CleanupAuthorityGate::Lease&& authority,
    std::uint64_t issuer_identity,
    const CleanupAuthorizationBinding& binding,
    CapsuleCleanupTrigger trigger,
    std::uint64_t attempt_nonce) noexcept
    : authority_(std::move(authority)),
      issuer_identity_(issuer_identity),
      record_key_(binding.record_key),
      trigger_(trigger),
      host_identity_(binding.host_identity),
      xaml_root_identity_(binding.xaml_root_identity),
      content_identity_(binding.content_identity),
      host_generation_(binding.host_generation),
      root_generation_(binding.root_generation),
      diagnostics_epoch_(binding.diagnostics_epoch),
      anchor_identity_(binding.anchor_identity),
      grid_identity_(binding.grid_identity),
      attempt_nonce_(attempt_nonce),
      consumed_(false) {}

CleanupAuthorization::CleanupAuthorization(
    CleanupAuthorization&& other) noexcept
    : authority_(std::move(other.authority_)),
      issuer_identity_(std::exchange(other.issuer_identity_, 0)),
      record_key_(std::exchange(other.record_key_, 0)),
      trigger_(std::exchange(other.trigger_, CapsuleCleanupTrigger::timer)),
      host_identity_(std::exchange(other.host_identity_, 0)),
      xaml_root_identity_(std::exchange(other.xaml_root_identity_, 0)),
      content_identity_(std::exchange(other.content_identity_, 0)),
      host_generation_(std::exchange(other.host_generation_, 0)),
      root_generation_(std::exchange(other.root_generation_, 0)),
      diagnostics_epoch_(std::exchange(other.diagnostics_epoch_, 0)),
      anchor_identity_(std::exchange(other.anchor_identity_, 0)),
      grid_identity_(std::exchange(other.grid_identity_, 0)),
      attempt_nonce_(std::exchange(other.attempt_nonce_, 0)),
      consumed_(std::exchange(other.consumed_, true)) {}

CleanupAuthorization& CleanupAuthorization::operator=(
    CleanupAuthorization&& other) noexcept {
  if (this != &other) {
    authority_ = std::move(other.authority_);
    issuer_identity_ = std::exchange(other.issuer_identity_, 0);
    record_key_ = std::exchange(other.record_key_, 0);
    trigger_ =
        std::exchange(other.trigger_, CapsuleCleanupTrigger::timer);
    host_identity_ = std::exchange(other.host_identity_, 0);
    xaml_root_identity_ = std::exchange(other.xaml_root_identity_, 0);
    content_identity_ = std::exchange(other.content_identity_, 0);
    host_generation_ = std::exchange(other.host_generation_, 0);
    root_generation_ = std::exchange(other.root_generation_, 0);
    diagnostics_epoch_ = std::exchange(other.diagnostics_epoch_, 0);
    anchor_identity_ = std::exchange(other.anchor_identity_, 0);
    grid_identity_ = std::exchange(other.grid_identity_, 0);
    attempt_nonce_ = std::exchange(other.attempt_nonce_, 0);
    consumed_ = std::exchange(other.consumed_, true);
  }
  return *this;
}

CapsuleCleanupTransaction::CapsuleCleanupTransaction(
    std::size_t column_count,
    std::size_t child_count) noexcept
    : column_count_(column_count), child_count_(child_count) {}

CapsuleTransactionResult CapsuleCleanupTransaction::cleanup_authorized(
    CleanupAuthorization& authorization,
    std::uint64_t issuer_identity,
    const CleanupAuthorizationBinding& binding,
    CapsuleCleanupTrigger trigger,
    CapsuleCleanupAccess& access) noexcept {
  std::lock_guard lock(mutex_);
  if (authorization.consumed_ || !authorization.authority_.valid() ||
      !authorization.issuer_identity_ ||
      authorization.issuer_identity_ != issuer_identity ||
      authorization.record_key_ != binding.record_key ||
      authorization.trigger_ != trigger ||
      authorization.host_identity_ != binding.host_identity ||
      authorization.xaml_root_identity_ != binding.xaml_root_identity ||
      authorization.content_identity_ != binding.content_identity ||
      authorization.host_generation_ != binding.host_generation ||
      authorization.root_generation_ != binding.root_generation ||
      authorization.diagnostics_epoch_ != binding.diagnostics_epoch ||
      authorization.anchor_identity_ != binding.anchor_identity ||
      authorization.grid_identity_ != binding.grid_identity ||
      !authorization.attempt_nonce_) {
    return CapsuleTransactionResult::retryable_failure;
  }
  authorization.consumed_ = true;
  return cleanup_locked(access);
}

CapsuleTransactionResult CapsuleCleanupTransaction::cleanup_locked(
    CapsuleCleanupAccess& access) noexcept {
  if (complete_) {
    return CapsuleTransactionResult::already_complete;
  }

  const auto relationship = access.capsule_parent_relationship();
  if (relationship == CapsuleParentRelationship::different_parent ||
      relationship == CapsuleParentRelationship::access_failure) {
    return CapsuleTransactionResult::retryable_failure;
  }
  if (relationship == CapsuleParentRelationship::recorded_parent) {
    if (!access.remove_capsule_from_recorded_parent() ||
        access.capsule_parent_relationship() !=
            CapsuleParentRelationship::detached) {
      return CapsuleTransactionResult::retryable_failure;
    }
  }
  if (!access.clear_columns()) {
    return CapsuleTransactionResult::retryable_failure;
  }
  for (std::size_t index = 0; index < column_count_; ++index) {
    if (!access.append_column(index)) {
      return CapsuleTransactionResult::retryable_failure;
    }
  }
  for (std::size_t index = 0; index < child_count_; ++index) {
    if (access.original_child_state(index) !=
            OriginalChildState::present_exact ||
        !access.restore_original_child_column(index)) {
      return CapsuleTransactionResult::retryable_failure;
    }
  }
  complete_ = true;
  return CapsuleTransactionResult::complete;
}

bool CapsuleCleanupTransaction::complete() const noexcept {
  std::lock_guard lock(mutex_);
  return complete_;
}

CapsuleCleanupWorkflow::CapsuleCleanupWorkflow(
    CleanupAuthorizationBinding binding,
    std::shared_ptr<CleanupAuthorityGate> authority,
    std::size_t column_count,
    std::size_t child_count) noexcept
    : binding_(std::move(binding)),
      authority_(std::move(authority)),
      issuer_identity_(allocate_cleanup_issuer()),
      transaction_(column_count, child_count) {}

std::optional<CleanupAuthorization> CapsuleCleanupWorkflow::authorize(
    CapsuleCleanupTrigger trigger,
    CleanupAuthorizationEvidenceSource& evidence_source) noexcept {
  if (!issuer_identity_ || !authority_ || !valid_cleanup_trigger(trigger) ||
      !valid_cleanup_binding(binding_)) {
    return std::nullopt;
  }
  auto authority = authority_->try_acquire();
  if (!authority) {
    return std::nullopt;
  }

  CleanupAuthorizationEvidence evidence{};
  if (!evidence_source.resolve(trigger, evidence) ||
      !cleanup_evidence_matches(binding_, evidence)) {
    return std::nullopt;
  }
  const auto attempt_nonce = next_attempt_nonce();
  if (!attempt_nonce) {
    return std::nullopt;
  }
  CleanupAuthorization authorization{std::move(*authority), issuer_identity_,
                                     binding_, trigger, attempt_nonce};
  return std::optional<CleanupAuthorization>{std::move(authorization)};
}

CapsuleTransactionResult CapsuleCleanupWorkflow::consume(
    CapsuleCleanupTrigger trigger,
    CleanupAuthorization&& authorization,
    CapsuleCleanupAccess& access) noexcept {
  CleanupAuthorization retained{std::move(authorization)};
  return transaction_.cleanup_authorized(retained, issuer_identity_, binding_,
                                         trigger, access);
}

CapsuleTransactionResult CapsuleCleanupWorkflow::attempt(
    CapsuleCleanupTrigger trigger,
    CleanupAuthorizationEvidenceSource& evidence_source,
    CapsuleCleanupAccess& access) noexcept {
  auto authorization = authorize(trigger, evidence_source);
  if (!authorization) {
    return CapsuleTransactionResult::retryable_failure;
  }
  return consume(trigger, std::move(*authorization), access);
}

CapsuleTransactionResult CapsuleCleanupWorkflow::attempt_transition(
    CapsuleCleanupTrigger trigger,
    const CleanupAuthorityGate::TransitionPermit& permit,
    CleanupAuthorizationEvidenceSource& evidence_source,
    CapsuleCleanupAccess& access) noexcept {
  if (!issuer_identity_ || !authority_ || !valid_cleanup_trigger(trigger) ||
      !valid_cleanup_binding(binding_)) {
    return CapsuleTransactionResult::retryable_failure;
  }
  auto authority = authority_->try_acquire_transition(permit);
  if (!authority) {
    return CapsuleTransactionResult::retryable_failure;
  }
  CleanupAuthorizationEvidence evidence{};
  if (!evidence_source.resolve(trigger, evidence) ||
      !cleanup_evidence_matches(binding_, evidence)) {
    return CapsuleTransactionResult::retryable_failure;
  }
  const auto attempt_nonce = next_attempt_nonce();
  if (!attempt_nonce) {
    return CapsuleTransactionResult::retryable_failure;
  }
  CleanupAuthorization authorization{std::move(*authority), issuer_identity_,
                                     binding_, trigger, attempt_nonce};
  return consume(trigger, std::move(authorization), access);
}

bool CapsuleCleanupWorkflow::complete() const noexcept {
  return transaction_.complete();
}

bool CapsuleCleanupWorkflow::valid() const noexcept {
  return issuer_identity_ && authority_ && authority_->available() &&
         valid_cleanup_binding(binding_);
}

std::uint64_t CapsuleCleanupWorkflow::next_attempt_nonce() noexcept {
  auto nonce = next_attempt_nonce_.load(std::memory_order_relaxed);
  while (nonce && nonce != (std::numeric_limits<std::uint64_t>::max)()) {
    if (next_attempt_nonce_.compare_exchange_weak(
            nonce, nonce + 1, std::memory_order_relaxed,
            std::memory_order_relaxed)) {
      return nonce;
    }
  }
  return 0;
}

CapsuleCleanupRecord::CapsuleCleanupRecord(
    CleanupAuthorizationBinding binding,
    std::shared_ptr<CleanupAuthorityGate> authority,
    std::size_t column_count,
    std::size_t child_count) noexcept
    : workflow_(std::move(binding), std::move(authority), column_count,
                child_count) {}

CapsuleTransactionResult CapsuleCleanupRecord::on_timer(
    CleanupAuthorizationEvidenceSource& evidence_source,
    CapsuleCleanupAccess& access) noexcept {
  return workflow_.attempt(CapsuleCleanupTrigger::timer, evidence_source,
                           access);
}

CapsuleTransactionResult CapsuleCleanupRecord::on_explicit_removal(
    CleanupAuthorizationEvidenceSource& evidence_source,
    CapsuleCleanupAccess& access) noexcept {
  return workflow_.attempt(CapsuleCleanupTrigger::explicit_removal,
                           evidence_source, access);
}

CapsuleTransactionResult CapsuleCleanupRecord::on_shutdown(
    CleanupAuthorizationEvidenceSource& evidence_source,
    CapsuleCleanupAccess& access) noexcept {
  return workflow_.attempt(CapsuleCleanupTrigger::shutdown, evidence_source,
                           access);
}

CapsuleTransactionResult CapsuleCleanupRecord::on_transition_shutdown(
    const CleanupAuthorityGate::TransitionPermit& permit,
    CleanupAuthorizationEvidenceSource& evidence_source,
    CapsuleCleanupAccess& access) noexcept {
  return workflow_.attempt_transition(CapsuleCleanupTrigger::shutdown, permit,
                                      evidence_source, access);
}

CapsuleTransactionResult CapsuleCleanupRecord::on_rollback(
    CleanupAuthorizationEvidenceSource& evidence_source,
    CapsuleCleanupAccess& access) noexcept {
  return workflow_.attempt(CapsuleCleanupTrigger::rollback, evidence_source,
                           access);
}

bool CapsuleCleanupRecord::complete() const noexcept {
  return workflow_.complete();
}

bool CapsuleCleanupRecord::valid() const noexcept { return workflow_.valid(); }

}  // namespace cq::bridge
