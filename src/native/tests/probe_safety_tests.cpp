#include "probe_safety.h"
#include "probe_capsule.h"
#include "xaml_taskbar_probe.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <thread>
#include <utility>
#include <vector>

namespace {

using cq::bridge::AdmissionGate;
using cq::bridge::CapsuleCleanupAccess;
using cq::bridge::CapsuleCleanupRecord;
using cq::bridge::CapsuleCleanupTrigger;
using cq::bridge::CapsuleCleanupWorkflow;
using cq::bridge::CapsuleParentRelationship;
using cq::bridge::CapsuleTransactionResult;
using cq::bridge::CleanupAuthorityGate;
using cq::bridge::CleanupAuthorityMode;
using cq::bridge::CleanupAuthorizationBinding;
using cq::bridge::CleanupAuthorizationEvidence;
using cq::bridge::CleanupAuthorizationEvidenceSource;
using cq::bridge::CleanupIdentityPath;
using cq::bridge::CleanupAncestryRetention;
using cq::bridge::CleanupRetainedIdentity;
using cq::bridge::DiagnosticsQueryAdapter;
using cq::bridge::DiagnosticsCallbackAddAdapter;
using cq::bridge::DiagnosticsCallbackPipeline;
using cq::bridge::DiagnosticsSessionGate;
using cq::bridge::DiagnosticsSessionIdentity;
using cq::bridge::OriginalChildState;
using cq::bridge::HandoffRequest;
using cq::bridge::HandoffResult;
using cq::bridge::HandoffModuleRelease;
using cq::bridge::HandoffRetirement;
using cq::bridge::HandoffRetirementAttempt;
using cq::bridge::HandoffRetirementBoundary;
using cq::bridge::HandoffRetirementStage;
using cq::bridge::HandoffRetirementTarget;
using cq::bridge::HandoffTokenClearResult;
using cq::bridge::PendingActionLedger;
using cq::bridge::PendingActionStage;
using cq::bridge::PendingAsyncAction;
using cq::bridge::PendingAsyncStatus;
using cq::bridge::PendingDispatchBoundary;
using cq::bridge::PendingDispatchCallback;
using cq::bridge::PendingLedgerOperationResult;
using cq::bridge::PendingModuleBoundary;
using cq::bridge::PendingScheduleResult;
using cq::bridge::PendingScheduleStatus;
using cq::bridge::HostRootBinding;
using cq::bridge::HostRootBindingRegistry;
using cq::bridge::RootMatchResult;
using cq::bridge::SessionCloseResult;
using cq::bridge::VisualRootEvidence;

template <typename T>
concept HasWeakCleanupContextCheck = requires(T& value) {
  value.cleanup_context_is_current();
};

static_assert(!HasWeakCleanupContextCheck<CapsuleCleanupAccess>);

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

void exact_root_binding_never_uses_same_thread_as_membership() {
  HostRootBindingRegistry bindings;
  EXPECT(bindings.bind({.host_identity = 0x100,
                        .thread_id = 41,
                        .xaml_root_identity = 0xA0,
                        .content_identity = 0xA1,
                        .host_generation = 7,
                        .root_generation = 11,
                        .diagnostics_epoch = 13}));

  EXPECT(bindings.match({.host_identity = 0x100,
                         .thread_id = 41,
                         .xaml_root_identity = 0xA0,
                         .content_identity = 0xA1,
                         .host_generation = 7,
                         .root_generation = 11,
                         .diagnostics_epoch = 13,
                         .host_is_still_valid = true}) ==
         RootMatchResult::exact);
  const auto unrelated = bindings.match({.host_identity = 0x100,
                                          .thread_id = 41,
                                          .xaml_root_identity = 0xB0,
                                          .content_identity = 0xB1,
                                          .host_generation = 7,
                                          .root_generation = 11,
                                          .diagnostics_epoch = 13,
                                          .host_is_still_valid = true});
  EXPECT(unrelated == RootMatchResult::wrong_xaml_root);
  EXPECT(cq::bridge::evaluate_visual_node(
             {L"SystemTray.SystemTrayFrame", true,
              unrelated == RootMatchResult::exact}) ==
         cq::bridge::ProbeDecision::ignore);
}

void root_binding_fails_closed_for_missing_ambiguous_reused_or_destroyed_roots() {
  HostRootBindingRegistry bindings;
  const HostRootBinding primary{0x100, 41, 0xA0, 0xA1, 7, 11, 13};
  EXPECT(bindings.bind(primary));
  EXPECT(!bindings.bind(primary));
  EXPECT(!bindings.bind({0x200, 41, 0xA0, 0xB1, 8, 12, 13}));
  EXPECT(!bindings.bind({0x200, 41, 0xB0, 0xA1, 8, 12, 13}));

  EXPECT(bindings.match({0x999, 41, 0xA0, 0xA1, 7, 11, 13, true}) ==
         RootMatchResult::missing_binding);
  EXPECT(bindings.match({0x100, 41, 0xA0, 0xA1, 8, 11, 13, true}) ==
         RootMatchResult::stale_host_generation);
  EXPECT(bindings.match({0x100, 41, 0xA0, 0xA1, 7, 12, 13, true}) ==
         RootMatchResult::stale_root_generation);
  EXPECT(bindings.match({0x100, 41, 0xA0, 0xA1, 7, 11, 14, true}) ==
         RootMatchResult::stale_diagnostics_epoch);
  EXPECT(bindings.match({0x100, 41, 0xA0, 0xB1, 7, 11, 13, true}) ==
         RootMatchResult::wrong_content);
  EXPECT(bindings.match({0x100, 41, 0xA0, 0xA1, 7, 11, 13, false}) ==
         RootMatchResult::destroyed_host);
  EXPECT(bindings.invalidate(0x100, 7, 11, 13));
  EXPECT(bindings.match({0x100, 41, 0xA0, 0xA1, 7, 11, 13, true}) ==
         RootMatchResult::missing_binding);

  EXPECT(bindings.bind(primary));
  bindings.invalidate_all();
  EXPECT(bindings.match({0x100, 41, 0xA0, 0xA1, 7, 11, 13, true}) ==
         RootMatchResult::missing_binding);
}

void replacement_xaml_root_with_same_content_and_hwnd_generation_is_rejected() {
  HostRootBindingRegistry bindings;
  EXPECT(bindings.bind({0x100, 41, 0xA0, 0xC0, 7, 11, 13}));
  const auto replacement =
      bindings.match({0x100, 41, 0xB0, 0xC0, 7, 11, 13, true});
  EXPECT(replacement == RootMatchResult::wrong_xaml_root);
  EXPECT(cq::bridge::evaluate_visual_node(
             {L"SystemTray.SystemTrayFrame", true,
              replacement == RootMatchResult::exact}) ==
         cq::bridge::ProbeDecision::ignore);
}

void handoff_outcomes_are_bounded_and_late_completion_is_safe() {
  auto unclaimed = std::make_shared<HandoffRequest>([] {});
  EXPECT(unclaimed->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::timed_out_unclaimed);
  EXPECT(!unclaimed->try_claim());

  std::atomic<int> calls = 0;
  auto delayed = std::make_shared<HandoffRequest>([&calls] { ++calls; });
  EXPECT(delayed->try_claim());
  EXPECT(delayed->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::timed_out_in_flight);
  delayed->run_claimed_action();
  EXPECT(delayed->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::completed);
  EXPECT(calls.load() == 1);

  auto throwing = std::make_shared<HandoffRequest>([] { throw 7; });
  EXPECT(throwing->try_claim());
  throwing->run_claimed_action();
  EXPECT(throwing->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::action_failed);
}

class FakeHandoffRetirementBoundary final
    : public HandoffRetirementBoundary {
 public:
  bool unhook(std::uintptr_t hook) noexcept override {
    ++unhook_calls;
    observed_hook = hook;
    return unhook_succeeds;
  }

  bool send_barrier(const HandoffRetirementTarget& target) noexcept override {
    ++barrier_calls;
    observed_target = target;
    if (during_barrier) {
      during_barrier();
    }
    return barrier_succeeds;
  }

  HandoffTokenClearResult clear_retirement_token(
      const HandoffRetirementTarget& target) noexcept override {
    ++clear_calls;
    observed_target = target;
    if (during_clear) {
      during_clear();
    }
    return clear_result;
  }

  void release_module(std::uintptr_t module) noexcept override {
    ++release_calls;
    observed_module = module;
  }

  bool unhook_succeeds = true;
  bool barrier_succeeds = true;
  HandoffTokenClearResult clear_result = HandoffTokenClearResult::cleared;
  std::function<void()> during_barrier;
  std::function<void()> during_clear;
  std::size_t unhook_calls = 0;
  std::size_t barrier_calls = 0;
  std::size_t clear_calls = 0;
  std::size_t release_calls = 0;
  std::uintptr_t observed_hook = 0;
  std::uintptr_t observed_module = 0;
  HandoffRetirementTarget observed_target{};
};

class FakePendingAsyncAction final : public PendingAsyncAction {
 public:
  ~FakePendingAsyncAction() override {
    if (on_destroy) {
      on_destroy();
    }
  }

  PendingAsyncStatus status() override {
    ++status_calls;
    if (on_status) {
      on_status();
    }
    if (status_throws) {
      throw std::runtime_error("status");
    }
    return current_status.load(std::memory_order_acquire);
  }

  void cancel() override {
    ++cancel_calls;
    if (on_cancel) {
      on_cancel();
    }
    if (cancel_throws) {
      throw std::runtime_error("cancel");
    }
  }

  std::atomic<PendingAsyncStatus> current_status{PendingAsyncStatus::started};
  bool status_throws = false;
  bool cancel_throws = false;
  std::size_t status_calls = 0;
  std::size_t cancel_calls = 0;
  std::function<void()> on_status;
  std::function<void()> on_cancel;
  std::function<void()> on_destroy;
};

class FakePendingBoundary final : public PendingModuleBoundary,
                                  public PendingDispatchBoundary {
 public:
  enum class QueueMode : std::uint8_t {
    queued,
    rejected,
    throw_after_retaining_callback,
  };

  bool acquire_self_module(std::uintptr_t& module) noexcept override {
    ++acquire_calls;
    module = acquire_succeeds || module_on_failed_acquire ? module_handle : 0;
    return acquire_succeeds;
  }

  void release_module(std::uintptr_t module) noexcept override {
    ++release_calls;
    observed_module = module;
    if (on_release_module) {
      on_release_module();
    }
  }

  std::shared_ptr<PendingAsyncAction> queue(
      std::shared_ptr<PendingDispatchCallback> callback) override {
    ++queue_calls;
    if (on_queue) {
      on_queue();
    }
    if (queue_mode == QueueMode::throw_after_retaining_callback) {
      retained_callback = std::move(callback);
      throw std::runtime_error("unknown queue outcome");
    }
    if (queue_mode == QueueMode::rejected) {
      return {};
    }
    retained_callback = callback;
    if (invoke_callback_before_return && callback) {
      (*callback)();
    }
    return std::move(next_action);
  }

  bool acquire_succeeds = true;
  bool module_on_failed_acquire = false;
  bool invoke_callback_before_return = false;
  QueueMode queue_mode = QueueMode::queued;
  std::uintptr_t module_handle = 0x4141;
  std::uintptr_t observed_module = 0;
  std::size_t acquire_calls = 0;
  std::size_t queue_calls = 0;
  std::size_t release_calls = 0;
  std::function<void()> on_queue;
  std::function<void()> on_release_module;
  std::shared_ptr<PendingDispatchCallback> retained_callback;
  std::shared_ptr<PendingAsyncAction> next_action;
};

struct PendingOwnerObservation final {
  PendingActionLedger* ledger = nullptr;
  std::atomic<std::size_t> destructions{0};
  std::atomic<bool> mutex_available{false};
  std::atomic<std::size_t> observed_outstanding{0};
};

struct PendingOwner final {
  explicit PendingOwner(PendingOwnerObservation& value) noexcept
      : observation(value) {}
  ~PendingOwner() {
    observation.destructions.fetch_add(1, std::memory_order_release);
    if (observation.ledger) {
      observation.mutex_available.store(observation.ledger->mutex_available(),
                                        std::memory_order_release);
      observation.observed_outstanding.store(
          observation.ledger->outstanding_count(), std::memory_order_release);
    }
  }
  PendingOwnerObservation& observation;
};

constexpr HandoffRetirementTarget kRetirementTarget{
    .window = 0x101,
    .process_id = 17,
    .thread_id = 23,
    .retirement_token = 29,
};

void failed_unhook_is_permanently_retained_and_never_repeated() {
  FakeHandoffRetirementBoundary boundary;
  boundary.unhook_succeeds = false;
  HandoffRetirement retirement(0x202, kRetirementTarget);
  EXPECT(retirement.mark_token_published());
  EXPECT(retirement.attach_hook(0x303));

  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::permanently_unsafe);
  EXPECT(retirement.stage() == HandoffRetirementStage::unhook_uncertain);
  EXPECT(retirement.outstanding());
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 0);
  EXPECT(!retirement.detach_ready().has_value());

  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::permanently_unsafe);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.release_calls == 0);
}

void barrier_timeout_is_retryable_without_repeating_unhook() {
  FakeHandoffRetirementBoundary boundary;
  boundary.barrier_succeeds = false;
  HandoffRetirement retirement(0x212, kRetirementTarget);
  EXPECT(retirement.mark_token_published());
  EXPECT(retirement.attach_hook(0x313));

  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::retryable_unsafe);
  EXPECT(retirement.stage() ==
         HandoffRetirementStage::unhooked_waiting_for_barrier);
  EXPECT(retirement.outstanding());
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);

  boundary.barrier_succeeds = true;
  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::ready_to_detach);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 2);
  auto release = retirement.detach_ready();
  EXPECT(release.has_value());
  EXPECT(!retirement.outstanding());
  EXPECT(release->release(boundary));
  EXPECT(!release->release(boundary));
  EXPECT(boundary.release_calls == 1);
  EXPECT(boundary.observed_module == 0x212);
}

void barrier_cannot_retire_an_active_or_nested_hook_frame() {
  FakeHandoffRetirementBoundary boundary;
  HandoffRetirement retirement(0x222, kRetirementTarget);
  EXPECT(retirement.mark_token_published());
  EXPECT(retirement.attach_hook(0x323));
  EXPECT(retirement.enter_hook_frame());

  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::retryable_unsafe);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 0);
  retirement.exit_hook_frame();

  boundary.during_barrier = [&] {
    EXPECT(retirement.enter_hook_frame());
    retirement.exit_hook_frame();
  };
  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::retryable_unsafe);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(retirement.outstanding());

  boundary.during_barrier = {};
  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::ready_to_detach);
  auto release = retirement.detach_ready();
  EXPECT(release.has_value());
  EXPECT(release->release(boundary));
}

void request_result_is_independent_from_proven_hook_retirement() {
  auto failed_request = std::make_shared<HandoffRequest>([] { throw 9; });
  EXPECT(failed_request->try_claim());
  failed_request->run_claimed_action();
  EXPECT(failed_request->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::action_failed);

  FakeHandoffRetirementBoundary boundary;
  HandoffRetirement retirement(0x232, kRetirementTarget);
  EXPECT(retirement.mark_token_published());
  EXPECT(retirement.attach_hook(0x333));
  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::ready_to_detach);
  auto release = retirement.detach_ready();
  EXPECT(release.has_value());
  EXPECT(release->release(boundary));
  EXPECT(!retirement.outstanding());
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  EXPECT(boundary.release_calls == 1);
}

void synchronous_barrier_reentry_is_typed_unsafe_without_blocking() {
  FakeHandoffRetirementBoundary boundary;
  HandoffRetirement retirement(0x242, kRetirementTarget);
  EXPECT(retirement.mark_token_published());
  EXPECT(retirement.attach_hook(0x343));
  auto nested = HandoffRetirementAttempt::permanently_unsafe;
  boundary.during_barrier = [&] { nested = retirement.attempt(boundary); };

  EXPECT(retirement.attempt(boundary) ==
         HandoffRetirementAttempt::ready_to_detach);
  EXPECT(nested == HandoffRetirementAttempt::retryable_unsafe);
  EXPECT(boundary.unhook_calls == 1);
  EXPECT(boundary.barrier_calls == 1);
  auto release = retirement.detach_ready();
  EXPECT(release.has_value());
  EXPECT(release->release(boundary));
}

void pending_registry_precedes_queue_and_definite_rejection_rolls_back() {
  AdmissionGate gate(4);
  PendingActionLedger ledger(4);
  FakePendingBoundary boundary;
  boundary.queue_mode = FakePendingBoundary::QueueMode::rejected;
  PendingOwnerObservation observation;
  observation.ledger = &ledger;
  auto owner = std::make_shared<PendingOwner>(observation);
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  boundary.on_queue = [&] {
    EXPECT(ledger.registry_count() == 1);
    EXPECT(ledger.outstanding_count() == 1);
    EXPECT(gate.live_count() == 1);
  };
  boundary.on_release_module = [&] { EXPECT(ledger.mutex_available()); };

  const auto result = ledger.schedule(
      boundary, boundary, std::move(owner), std::move(*lease), [] {});

  EXPECT(result.status == PendingScheduleStatus::rejected);
  EXPECT(result.id != 0);
  EXPECT(ledger.outstanding_count() == 0);
  EXPECT(gate.live_count() == 0);
  EXPECT(boundary.acquire_calls == 1);
  EXPECT(boundary.queue_calls == 1);
  EXPECT(boundary.release_calls == 1);
  EXPECT(boundary.observed_module == boundary.module_handle);
  EXPECT(observation.destructions.load(std::memory_order_acquire) == 1);
  EXPECT(observation.mutex_available.load(std::memory_order_acquire));
  EXPECT(observation.observed_outstanding.load(std::memory_order_acquire) == 1);
}

void unknown_queue_or_publication_outcome_is_permanently_retained() {
  {
    AdmissionGate gate(4);
    PendingActionLedger ledger(4);
    FakePendingBoundary boundary;
    boundary.queue_mode =
        FakePendingBoundary::QueueMode::throw_after_retaining_callback;
    auto lease = gate.try_acquire();
    EXPECT(lease.has_value());
    std::atomic<std::size_t> callback_calls{0};

    const auto result = ledger.schedule(
        boundary, boundary, std::make_shared<int>(7), std::move(*lease),
        [&] { ++callback_calls; });

    EXPECT(result.status == PendingScheduleStatus::retained_unknown);
    EXPECT(ledger.stage(result.id) ==
           std::optional{PendingActionStage::retained_unknown});
    EXPECT(ledger.outstanding_count() == 1);
    EXPECT(gate.live_count() == 1);
    EXPECT(boundary.release_calls == 0);
    EXPECT(boundary.retained_callback != nullptr);
    (*boundary.retained_callback)();
    EXPECT(callback_calls.load(std::memory_order_acquire) == 1);
    EXPECT(ledger.reap_all().remaining == 1);
    EXPECT(ledger.cancel_all().remaining == 1);
  }

  AdmissionGate gate(4);
  PendingActionLedger ledger(4);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  const auto result = ledger.schedule(
      boundary, boundary, std::make_shared<int>(8), std::move(*lease), [] {},
      true);
  EXPECT(result.status == PendingScheduleStatus::retained_unknown);
  EXPECT(ledger.outstanding_count() == 1);
  EXPECT(gate.live_count() == 1);
  EXPECT(action->status_calls == 0);
  EXPECT(action->cancel_calls == 0);
  EXPECT(boundary.release_calls == 0);
}

void terminal_status_without_callback_is_the_only_retirement_proof() {
  AdmissionGate gate(4);
  PendingActionLedger ledger(4);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  std::atomic<std::size_t> callback_calls{0};
  const auto scheduled = ledger.schedule(
      boundary, boundary, std::make_shared<int>(9), std::move(*lease),
      [&] { ++callback_calls; });
  EXPECT(scheduled.status == PendingScheduleStatus::queued);

  const auto started = ledger.reap_all();
  EXPECT(!started.operation_failed);
  EXPECT(started.retired == 0);
  EXPECT(started.remaining == 1);
  EXPECT(callback_calls.load(std::memory_order_acquire) == 0);
  EXPECT(boundary.release_calls == 0);

  action->current_status.store(PendingAsyncStatus::completed,
                               std::memory_order_release);
  const auto completed = ledger.reap_all();
  EXPECT(!completed.operation_failed);
  EXPECT(completed.retired == 1);
  EXPECT(completed.remaining == 0);
  EXPECT(callback_calls.load(std::memory_order_acquire) == 0);
  EXPECT(gate.live_count() == 0);
  EXPECT(boundary.release_calls == 1);
}

void unknown_async_status_is_not_terminal_proof() {
  AdmissionGate gate(1);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  action->current_status.store(PendingAsyncStatus::unknown,
                               std::memory_order_release);
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  const auto scheduled = ledger.schedule(
      boundary, boundary, std::make_shared<int>(22), std::move(*lease), [] {});
  EXPECT(scheduled.status == PendingScheduleStatus::queued);
  action.reset();

  const auto reaped = ledger.reap_all();
  EXPECT(reaped.operation_failed);
  EXPECT(reaped.retired == 0);
  EXPECT(reaped.remaining == 1);
  EXPECT(ledger.stage(scheduled.id) ==
         std::optional{PendingActionStage::queued});
  EXPECT(gate.live_count() == 1);
  EXPECT(boundary.release_calls == 0);
}

void cancel_and_status_failures_retain_until_terminal_status_is_observed() {
  AdmissionGate gate(4);
  PendingActionLedger ledger(4);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  const auto scheduled = ledger.schedule(
      boundary, boundary, std::make_shared<int>(10), std::move(*lease), [] {});
  EXPECT(scheduled.status == PendingScheduleStatus::queued);

  const auto canceled = ledger.cancel_all();
  EXPECT(!canceled.operation_failed);
  EXPECT(canceled.affected == 1);
  EXPECT(canceled.remaining == 1);
  EXPECT(action->cancel_calls == 1);
  EXPECT(ledger.stage(scheduled.id) ==
         std::optional{PendingActionStage::cancel_requested});
  EXPECT(ledger.reap_all().remaining == 1);

  action->status_throws = true;
  const auto failed_status = ledger.reap_all();
  EXPECT(failed_status.operation_failed);
  EXPECT(failed_status.remaining == 1);
  action->status_throws = false;
  action->current_status.store(PendingAsyncStatus::canceled,
                               std::memory_order_release);
  EXPECT(ledger.reap_all().retired == 1);
  EXPECT(ledger.outstanding_count() == 0);
  EXPECT(gate.live_count() == 0);
}

struct GateOwningPendingOwner final {
  explicit GateOwningPendingOwner(std::atomic<bool>& observed) noexcept
      : drained_before_destruction(observed) {}
  ~GateOwningPendingOwner() {
    drained_before_destruction.store(gate.live_count() == 0,
                                     std::memory_order_release);
  }

  AdmissionGate gate{1};
  std::atomic<bool>& drained_before_destruction;
};

void pending_lease_is_released_before_its_last_gate_owner() {
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  boundary.next_action = action;
  std::atomic<bool> drained_before_destruction{false};
  auto owner =
      std::make_shared<GateOwningPendingOwner>(drained_before_destruction);
  auto lease = owner->gate.try_acquire();
  EXPECT(lease.has_value());

  const auto scheduled = ledger.schedule(
      boundary, boundary, owner, std::move(*lease), [] {});
  EXPECT(scheduled.status == PendingScheduleStatus::queued);
  owner.reset();
  action->current_status.store(PendingAsyncStatus::completed,
                               std::memory_order_release);
  action.reset();

  EXPECT(ledger.reap_all().retired == 1);
  EXPECT(drained_before_destruction.load(std::memory_order_acquire));
}

void shutdown_poll_does_not_cancel_an_already_terminal_action() {
  AdmissionGate gate(1);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  EXPECT(ledger.schedule(boundary, boundary, std::make_shared<int>(11),
                         std::move(*lease), [] {})
             .status == PendingScheduleStatus::queued);
  action->current_status.store(PendingAsyncStatus::completed,
                               std::memory_order_release);

  const auto canceled = ledger.cancel_all();
  EXPECT(!canceled.operation_failed);
  EXPECT(canceled.retired == 1);
  EXPECT(canceled.remaining == 0);
  EXPECT(action->cancel_calls == 0);
}

void pending_record_teardown_keeps_gate_owner_alive_through_lease_release() {
  std::atomic<bool> drained_before_destruction{false};
  {
    PendingActionLedger ledger(1);
    FakePendingBoundary boundary;
    boundary.next_action = std::make_shared<FakePendingAsyncAction>();
    auto owner =
        std::make_shared<GateOwningPendingOwner>(drained_before_destruction);
    auto lease = owner->gate.try_acquire();
    EXPECT(lease.has_value());
    EXPECT(ledger.schedule(boundary, boundary, owner, std::move(*lease), [] {})
               .status == PendingScheduleStatus::queued);
    owner.reset();
  }
  EXPECT(drained_before_destruction.load(std::memory_order_acquire));
}

void pending_setup_failure_releases_lease_and_owner_before_module_pin() {
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  boundary.acquire_succeeds = false;
  boundary.module_on_failed_acquire = true;
  boundary.on_release_module = [&] { EXPECT(ledger.mutex_available()); };
  std::atomic<bool> drained_before_destruction{false};
  auto owner =
      std::make_shared<GateOwningPendingOwner>(drained_before_destruction);
  auto lease = owner->gate.try_acquire();
  EXPECT(lease.has_value());

  const auto failed = ledger.schedule(boundary, boundary, std::move(owner),
                                      std::move(*lease), [] {});
  EXPECT(failed.status == PendingScheduleStatus::resource_failure);
  EXPECT(drained_before_destruction.load(std::memory_order_acquire));
  EXPECT(boundary.queue_calls == 0);
  EXPECT(boundary.release_calls == 1);
  EXPECT(ledger.outstanding_count() == 0);
}

void pending_capacity_rejection_destroys_unregistered_resources_lock_free() {
  AdmissionGate gate(2);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  auto first_action = std::make_shared<FakePendingAsyncAction>();
  auto* const first_view = first_action.get();
  boundary.next_action = first_action;
  auto first_lease = gate.try_acquire();
  EXPECT(first_lease.has_value());
  EXPECT(ledger.schedule(boundary, boundary, std::make_shared<int>(21),
                         std::move(*first_lease), [] {})
             .status == PendingScheduleStatus::queued);
  first_action.reset();

  PendingOwnerObservation observation;
  observation.ledger = &ledger;
  auto second_owner = std::make_shared<PendingOwner>(observation);
  auto second_lease = gate.try_acquire();
  EXPECT(second_lease.has_value());
  const auto rejected = ledger.schedule(
      boundary, boundary, std::move(second_owner), std::move(*second_lease),
      [] {});
  EXPECT(rejected.status == PendingScheduleStatus::resource_failure);
  EXPECT(rejected.id == 0);
  EXPECT(boundary.queue_calls == 1);
  EXPECT(boundary.release_calls == 1);
  EXPECT(observation.destructions.load(std::memory_order_acquire) == 1);
  EXPECT(observation.mutex_available.load(std::memory_order_acquire));
  EXPECT(observation.observed_outstanding.load(std::memory_order_acquire) ==
         1);
  EXPECT(gate.live_count() == 1);

  first_view->current_status.store(PendingAsyncStatus::completed,
                                   std::memory_order_release);
  EXPECT(ledger.reap_all().retired == 1);
}

void cancel_completion_race_reaps_terminal_action_without_false_failure() {
  AdmissionGate gate(1);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  EXPECT(ledger.schedule(boundary, boundary, std::make_shared<int>(12),
                         std::move(*lease), [] {})
             .status == PendingScheduleStatus::queued);
  action->on_cancel = [&] {
    action->current_status.store(PendingAsyncStatus::completed,
                                 std::memory_order_release);
  };
  action->cancel_throws = true;

  const auto canceled = ledger.cancel_all();
  EXPECT(!canceled.operation_failed);
  EXPECT(canceled.retired == 1);
  EXPECT(canceled.remaining == 0);
  EXPECT(gate.live_count() == 0);
}

void cancel_failure_is_retryable_and_never_retires_a_started_action() {
  AdmissionGate gate(1);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  auto* const action_view = action.get();
  action->cancel_throws = true;
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  const auto scheduled = ledger.schedule(
      boundary, boundary, std::make_shared<int>(13), std::move(*lease), [] {});
  EXPECT(scheduled.status == PendingScheduleStatus::queued);
  action.reset();

  const auto failed = ledger.cancel_all();
  EXPECT(failed.operation_failed);
  EXPECT(failed.remaining == 1);
  EXPECT(action_view->cancel_calls == 1);
  EXPECT(ledger.stage(scheduled.id) ==
         std::optional{PendingActionStage::queued});

  action_view->cancel_throws = false;
  const auto retried = ledger.cancel_all();
  EXPECT(!retried.operation_failed);
  EXPECT(retried.remaining == 1);
  EXPECT(action_view->cancel_calls == 2);
  EXPECT(ledger.stage(scheduled.id) ==
         std::optional{PendingActionStage::cancel_requested});
  action_view->current_status.store(PendingAsyncStatus::canceled,
                                    std::memory_order_release);
  EXPECT(ledger.reap_all().retired == 1);
}

void double_reaper_claim_and_stale_snapshot_cannot_remove_a_new_record() {
  AdmissionGate gate(3);
  PendingActionLedger ledger(3);
  FakePendingBoundary boundary;
  std::mutex pause_mutex;
  std::condition_variable pause_condition;
  bool status_entered = false;
  bool release_status = false;

  auto first_action = std::make_shared<FakePendingAsyncAction>();
  auto* const first_view = first_action.get();
  first_action->current_status.store(PendingAsyncStatus::completed,
                                     std::memory_order_release);
  first_action->on_status = [&] {
    std::unique_lock lock(pause_mutex);
    status_entered = true;
    pause_condition.notify_all();
    pause_condition.wait(lock, [&] { return release_status; });
  };
  boundary.next_action = first_action;
  auto first_lease = gate.try_acquire();
  EXPECT(first_lease.has_value());
  const auto first = ledger.schedule(boundary, boundary,
                                     std::make_shared<int>(14),
                                     std::move(*first_lease), [] {});
  EXPECT(first.status == PendingScheduleStatus::queued);
  first_action.reset();

  PendingLedgerOperationResult first_reap;
  std::thread blocked_reaper([&] { first_reap = ledger.reap_all(); });
  {
    std::unique_lock lock(pause_mutex);
    const bool entered = pause_condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return status_entered; });
    EXPECT(entered);
  }

  const auto competing = ledger.reap_all();
  EXPECT(competing.affected == 0);
  EXPECT(competing.retired == 0);
  EXPECT(competing.remaining == 1);
  EXPECT(first_view->status_calls == 1);

  auto second_action = std::make_shared<FakePendingAsyncAction>();
  second_action->current_status.store(PendingAsyncStatus::completed,
                                      std::memory_order_release);
  boundary.next_action = second_action;
  auto second_lease = gate.try_acquire();
  EXPECT(second_lease.has_value());
  const auto second = ledger.schedule(boundary, boundary,
                                      std::make_shared<int>(15),
                                      std::move(*second_lease), [] {});
  EXPECT(second.status == PendingScheduleStatus::queued);
  second_action.reset();
  EXPECT(ledger.reap_all().retired == 1);

  auto replacement_action = std::make_shared<FakePendingAsyncAction>();
  boundary.next_action = replacement_action;
  auto replacement_lease = gate.try_acquire();
  EXPECT(replacement_lease.has_value());
  const auto replacement = ledger.schedule(
      boundary, boundary, std::make_shared<int>(16),
      std::move(*replacement_lease), [] {});
  EXPECT(replacement.status == PendingScheduleStatus::queued);
  EXPECT(replacement.id > second.id);
  replacement_action.reset();

  {
    std::lock_guard lock(pause_mutex);
    release_status = true;
  }
  pause_condition.notify_all();
  blocked_reaper.join();

  EXPECT(first_reap.retired == 1);
  EXPECT(ledger.registry_count() == 1);
  EXPECT(ledger.stage(replacement.id) ==
         std::optional{PendingActionStage::queued});
  EXPECT(gate.live_count() == 1);
}

void pending_status_cancel_and_final_releases_are_reentrant_and_lock_free() {
  AdmissionGate gate(1);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  auto action = std::make_shared<FakePendingAsyncAction>();
  auto* const action_view = action.get();
  boundary.next_action = action;
  std::atomic<bool> status_safe{false};
  std::atomic<bool> cancel_safe{false};
  std::atomic<bool> action_release_safe{false};
  std::atomic<bool> module_release_safe{false};
  std::uint64_t id = 0;
  action->on_status = [&] {
    status_safe.store(ledger.mutex_available() &&
                          ledger.outstanding_count() == 1,
                      std::memory_order_release);
  };
  action->on_cancel = [&] {
    cancel_safe.store(ledger.mutex_available() &&
                          ledger.outstanding_count() == 1,
                      std::memory_order_release);
  };
  action->on_destroy = [&] {
    action_release_safe.store(
        ledger.mutex_available() && ledger.outstanding_count() == 1 &&
            ledger.retiring_count() == 1 &&
            ledger.stage(id) == std::optional{PendingActionStage::retiring},
        std::memory_order_release);
  };
  boundary.on_release_module = [&] {
    module_release_safe.store(
        ledger.mutex_available() && ledger.outstanding_count() == 1 &&
            ledger.retiring_count() == 1,
        std::memory_order_release);
  };
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  const auto scheduled = ledger.schedule(
      boundary, boundary, std::make_shared<int>(17), std::move(*lease), [] {});
  id = scheduled.id;
  EXPECT(scheduled.status == PendingScheduleStatus::queued);
  action.reset();

  EXPECT(ledger.cancel_all().remaining == 1);
  EXPECT(status_safe.load(std::memory_order_acquire));
  EXPECT(cancel_safe.load(std::memory_order_acquire));
  action_view->current_status.store(PendingAsyncStatus::completed,
                                    std::memory_order_release);
  EXPECT(ledger.reap_all().retired == 1);
  EXPECT(action_release_safe.load(std::memory_order_acquire));
  EXPECT(module_release_safe.load(std::memory_order_acquire));
}

void final_action_release_keeps_retiring_record_and_counts_visible() {
  AdmissionGate gate(1);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  std::mutex pause_mutex;
  std::condition_variable pause_condition;
  bool release_entered = false;
  bool allow_release = false;
  auto action = std::make_shared<FakePendingAsyncAction>();
  action->current_status.store(PendingAsyncStatus::completed,
                               std::memory_order_release);
  action->on_destroy = [&] {
    std::unique_lock lock(pause_mutex);
    release_entered = true;
    pause_condition.notify_all();
    pause_condition.wait(lock, [&] { return allow_release; });
  };
  boundary.next_action = action;
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  const auto scheduled = ledger.schedule(
      boundary, boundary, std::make_shared<int>(18), std::move(*lease), [] {});
  EXPECT(scheduled.status == PendingScheduleStatus::queued);
  action.reset();

  PendingLedgerOperationResult reaped;
  std::thread reaper([&] { reaped = ledger.reap_all(); });
  {
    std::unique_lock lock(pause_mutex);
    const bool entered = pause_condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return release_entered; });
    EXPECT(entered);
  }
  EXPECT(ledger.mutex_available());
  EXPECT(ledger.registry_count() == 1);
  EXPECT(ledger.retiring_count() == 1);
  EXPECT(ledger.outstanding_count() == 1);
  EXPECT(ledger.stage(scheduled.id) ==
         std::optional{PendingActionStage::retiring});
  EXPECT(gate.live_count() == 1);

  {
    std::lock_guard lock(pause_mutex);
    allow_release = true;
  }
  pause_condition.notify_all();
  reaper.join();
  EXPECT(reaped.retired == 1);
  EXPECT(reaped.remaining == 0);
  EXPECT(gate.live_count() == 0);
}

void sequential_pending_completions_are_bounded_and_ids_never_reuse() {
  constexpr std::size_t kMaximum = 4096;
  constexpr std::size_t kIterations = 2 * kMaximum + 5;
  AdmissionGate gate(kMaximum);
  PendingActionLedger ledger(kMaximum);
  FakePendingBoundary boundary;
  std::uint64_t previous_id = 0;

  for (std::size_t index = 0; index < kIterations; ++index) {
    auto action = std::make_shared<FakePendingAsyncAction>();
    action->current_status.store(PendingAsyncStatus::completed,
                                 std::memory_order_release);
    boundary.next_action = action;
    auto lease = gate.try_acquire();
    EXPECT(lease.has_value());
    const auto scheduled = ledger.schedule(
        boundary, boundary, std::make_shared<std::size_t>(index),
        std::move(*lease), [] {});
    EXPECT(scheduled.status == PendingScheduleStatus::queued);
    EXPECT(scheduled.id > previous_id);
    previous_id = scheduled.id;
    action.reset();
    EXPECT(ledger.registry_count() == 1);
    EXPECT(ledger.reap_all().retired == 1);
    EXPECT(ledger.registry_count() == 0);
    EXPECT(ledger.outstanding_count() == 0);
    EXPECT(gate.live_count() == 0);
  }
  EXPECT(boundary.release_calls == kIterations);
}

void callback_before_queue_return_runs_once_while_record_is_preparing() {
  AdmissionGate gate(1);
  PendingActionLedger ledger(1);
  FakePendingBoundary boundary;
  boundary.invoke_callback_before_return = true;
  auto action = std::make_shared<FakePendingAsyncAction>();
  action->current_status.store(PendingAsyncStatus::completed,
                               std::memory_order_release);
  boundary.next_action = action;
  std::atomic<std::size_t> callback_calls{0};
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());

  const auto scheduled = ledger.schedule(
      boundary, boundary, std::make_shared<int>(19), std::move(*lease), [&] {
        ++callback_calls;
        EXPECT(ledger.mutex_available());
        EXPECT(ledger.stage(1) ==
               std::optional{PendingActionStage::preparing});
        const auto nested_cancel = ledger.cancel_all();
        EXPECT(nested_cancel.affected == 0);
        EXPECT(nested_cancel.remaining == 1);
        throw std::runtime_error("delegate outcome is contained");
      });

  EXPECT(scheduled.status == PendingScheduleStatus::queued);
  EXPECT(callback_calls.load(std::memory_order_acquire) == 1);
  action.reset();
  EXPECT(ledger.reap_all().retired == 1);
  EXPECT(boundary.release_calls == 1);
}

void close_and_schedule_have_one_gate_winner_while_preparing_is_counted() {
  AdmissionGate gate(2);
  PendingActionLedger ledger(2);
  FakePendingBoundary boundary;
  std::mutex pause_mutex;
  std::condition_variable pause_condition;
  bool queue_entered = false;
  bool allow_queue_return = false;
  auto action = std::make_shared<FakePendingAsyncAction>();
  auto* const action_view = action.get();
  boundary.next_action = action;
  boundary.on_queue = [&] {
    std::unique_lock lock(pause_mutex);
    queue_entered = true;
    pause_condition.notify_all();
    pause_condition.wait(lock, [&] { return allow_queue_return; });
  };
  auto lease = gate.try_acquire();
  EXPECT(lease.has_value());
  PendingScheduleResult scheduled;
  std::thread scheduler([&] {
    scheduled = ledger.schedule(boundary, boundary, std::make_shared<int>(20),
                                std::move(*lease), [] {});
  });
  {
    std::unique_lock lock(pause_mutex);
    const bool entered = pause_condition.wait_for(
        lock, std::chrono::seconds(2), [&] { return queue_entered; });
    EXPECT(entered);
  }

  EXPECT(ledger.registry_count() == 1);
  EXPECT(ledger.outstanding_count() == 1);
  EXPECT(ledger.stage(1) ==
         std::optional{PendingActionStage::preparing});
  EXPECT(gate.live_count() == 1);
  gate.close();
  EXPECT(!gate.try_acquire().has_value());
  const auto preparing_cancel = ledger.cancel_all();
  EXPECT(preparing_cancel.affected == 0);
  EXPECT(preparing_cancel.remaining == 1);

  {
    std::lock_guard lock(pause_mutex);
    allow_queue_return = true;
  }
  pause_condition.notify_all();
  scheduler.join();
  EXPECT(scheduled.status == PendingScheduleStatus::queued);
  EXPECT(ledger.stage(scheduled.id) ==
         std::optional{PendingActionStage::queued});
  action.reset();
  action_view->current_status.store(PendingAsyncStatus::completed,
                                    std::memory_order_release);
  EXPECT(ledger.reap_all().retired == 1);
  EXPECT(gate.live_count() == 0);
  EXPECT(!gate.accepting());

  FakePendingBoundary never_queued;
  EXPECT(!gate.try_acquire().has_value());
  EXPECT(never_queued.queue_calls == 0);
}

void shutdown_during_claimed_handoff_cancels_before_action() {
  std::atomic<int> calls = 0;
  auto request = std::make_shared<HandoffRequest>([&calls] { ++calls; });
  EXPECT(request->try_claim());
  request->request_cancel();
  request->run_claimed_action();
  EXPECT(request->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::cancelled);
  EXPECT(calls.load() == 0);
}

void shutdown_during_executing_handoff_reports_unsafe_until_late_completion() {
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool release = false;
  auto request = std::make_shared<HandoffRequest>([&] {
    std::unique_lock lock(mutex);
    entered = true;
    condition.notify_all();
    condition.wait(lock, [&] { return release; });
  });
  EXPECT(request->try_claim());
  std::thread runner([request] { request->run_claimed_action(); });
  {
    std::unique_lock lock(mutex);
    condition.wait(lock, [&] { return entered; });
  }
  request->request_cancel();
  EXPECT(request->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::timed_out_in_flight);
  {
    std::lock_guard lock(mutex);
    release = true;
  }
  condition.notify_all();
  runner.join();
  EXPECT(request->wait_for(std::chrono::milliseconds{0}) ==
         HandoffResult::completed);
}

void admission_lease_makes_shutdown_and_counts_truthful() {
  AdmissionGate gate(2);
  auto admitted = gate.try_acquire();
  EXPECT(admitted.has_value());
  EXPECT(gate.live_count() == 1);

  gate.close();
  EXPECT(!gate.drained());
  auto rejected = gate.try_acquire();
  EXPECT(!rejected.has_value());
  EXPECT(gate.live_count() == 1);

  admitted.reset();
  EXPECT(gate.drained());
  EXPECT(gate.live_count() == 0);
  EXPECT(gate.reopen_if_drained());
  auto replacement = gate.try_acquire();
  EXPECT(replacement.has_value());
  replacement.reset();
}

void admission_overflow_and_moved_lease_fail_closed_without_underflow() {
  AdmissionGate gate(1);
  auto first = gate.try_acquire();
  EXPECT(first.has_value());
  EXPECT(!gate.try_acquire().has_value());
  auto moved = std::move(first);
  first.reset();
  EXPECT(gate.live_count() == 1);
  moved.reset();
  EXPECT(gate.live_count() == 0);
  EXPECT(gate.counter_faults() == 1);
}

void pending_lease_releases_once_when_dispatch_setup_throws() {
  AdmissionGate gate(4);
  try {
    auto lease = gate.try_acquire();
    EXPECT(lease.has_value());
    EXPECT(gate.live_count() == 1);
    throw 9;
  } catch (...) {
  }
  EXPECT(gate.live_count() == 0);
  EXPECT(gate.counter_faults() == 0);
}

class FakeDiagnosticsQueryAdapter final : public DiagnosticsQueryAdapter {
 public:
  std::atomic<std::size_t> object_queries = 0;
  std::atomic<std::size_t> handle_queries = 0;
  std::atomic<std::size_t> xaml_queries = 0;

  bool get_inspectable(std::uint64_t, void** object) noexcept override {
    ++object_queries;
    if (object) {
      *object = reinterpret_cast<void*>(0x1000);
    }
    return object != nullptr;
  }

  bool get_handle(void*, std::uint64_t* handle) noexcept override {
    ++handle_queries;
    if (handle) {
      *handle = 0x2000;
    }
    return handle != nullptr;
  }

  bool inspect_xaml(void*) noexcept override {
    ++xaml_queries;
    return true;
  }

  [[nodiscard]] std::size_t total_queries() const noexcept {
    return object_queries.load() + handle_queries.load() +
           xaml_queries.load();
  }
};

struct AdapterDestructionObserver final {
  DiagnosticsSessionGate* gate = nullptr;
  std::atomic<bool> inside_gate_call{false};
  bool destroyed = false;
  bool destroyed_during_gate_call = false;
  bool reentered_gate = false;
  std::optional<DiagnosticsSessionIdentity> identity_seen_during_destruction;
};

class DestructionObservingDiagnosticsAdapter final
    : public DiagnosticsQueryAdapter {
 public:
  explicit DestructionObservingDiagnosticsAdapter(
      AdapterDestructionObserver& observer) noexcept
      : observer_(observer) {}

  ~DestructionObservingDiagnosticsAdapter() override {
    observer_.destroyed = true;
    if (observer_.inside_gate_call.load(std::memory_order_acquire)) {
      observer_.destroyed_during_gate_call = true;
      return;
    }
    observer_.reentered_gate = true;
    observer_.identity_seen_during_destruction = observer_.gate->identity();
  }

  bool get_inspectable(std::uint64_t, void**) noexcept override {
    return false;
  }
  bool get_handle(void*, std::uint64_t*) noexcept override { return false; }
  bool inspect_xaml(void*) noexcept override { return false; }

 private:
  AdapterDestructionObserver& observer_;
};

constexpr DiagnosticsSessionIdentity valid_diagnostics_session() noexcept {
  return {.epoch = 13, .site_identity = 0xA0,
          .diagnostics_identity = 0xB0};
}

class FakeDiagnosticsAddAdapter final
    : public DiagnosticsCallbackAddAdapter {
 public:
  std::size_t invocations = 0;
  std::mutex* barrier_mutex = nullptr;
  std::condition_variable* barrier_condition = nullptr;
  bool* entered = nullptr;
  bool* release = nullptr;
  DiagnosticsSessionGate* reentrant_close_gate = nullptr;
  std::optional<SessionCloseResult> reentrant_close_result;

  bool process_add(DiagnosticsSessionGate::Lease& session) noexcept override {
    ++invocations;
    if (reentrant_close_gate) {
      reentrant_close_result = reentrant_close_gate->close();
    }
    if (barrier_mutex && barrier_condition && entered && release) {
      std::unique_lock lock(*barrier_mutex);
      *entered = true;
      barrier_condition->notify_all();
      barrier_condition->wait(lock, [&] { return *release; });
    }
    void* object = nullptr;
    std::uint64_t handle = 0;
    return session.get_inspectable(7, &object) &&
           session.inspect_xaml(object) &&
           session.get_handle(object, &handle) && handle == 0x2000;
  }
};

void diagnostics_pipeline_invalidation_before_add_is_zero_query() {
  DiagnosticsSessionGate gate;
  auto queries = std::make_shared<FakeDiagnosticsQueryAdapter>();
  EXPECT(gate.install(valid_diagnostics_session(), queries));
  DiagnosticsCallbackPipeline pipeline(gate);
  FakeDiagnosticsAddAdapter add;

  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(!pipeline.process_add(valid_diagnostics_session(), add));
  EXPECT(add.invocations == 0);
  EXPECT(queries->total_queries() == 0);
}

void diagnostics_pipeline_acquisition_wins_and_closes_new_admission() {
  DiagnosticsSessionGate gate;
  auto queries = std::make_shared<FakeDiagnosticsQueryAdapter>();
  EXPECT(gate.install(valid_diagnostics_session(), queries));
  DiagnosticsCallbackPipeline pipeline(gate);
  FakeDiagnosticsAddAdapter add;
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool release = false;
  add.barrier_mutex = &mutex;
  add.barrier_condition = &condition;
  add.entered = &entered;
  add.release = &release;
  bool processed = false;
  std::thread callback([&] {
    processed = pipeline.process_add(valid_diagnostics_session(), add);
  });
  {
    std::unique_lock lock(mutex);
    condition.wait(lock, [&] { return entered; });
  }

  EXPECT(gate.close() == SessionCloseResult::in_flight);
  FakeDiagnosticsAddAdapter rejected;
  EXPECT(!pipeline.process_add(valid_diagnostics_session(), rejected));
  EXPECT(rejected.invocations == 0);
  EXPECT(!gate.reset_if_drained());
  {
    std::lock_guard lock(mutex);
    release = true;
  }
  condition.notify_all();
  callback.join();
  EXPECT(processed);
  EXPECT(queries->total_queries() == 3);
  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
}

void diagnostics_pipeline_reentrant_close_is_nonblocking_and_retained() {
  DiagnosticsSessionGate gate;
  auto queries = std::make_shared<FakeDiagnosticsQueryAdapter>();
  EXPECT(gate.install(valid_diagnostics_session(), queries));
  DiagnosticsCallbackPipeline pipeline(gate);
  FakeDiagnosticsAddAdapter add;
  add.reentrant_close_gate = &gate;

  EXPECT(pipeline.process_add(valid_diagnostics_session(), add));
  EXPECT(add.reentrant_close_result == SessionCloseResult::in_flight);
  EXPECT(queries->total_queries() == 3);
  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
}

void diagnostics_session_close_wins_before_any_query() {
  DiagnosticsSessionGate gate;
  auto adapter = std::make_shared<FakeDiagnosticsQueryAdapter>();
  EXPECT(gate.install(valid_diagnostics_session(), adapter));
  EXPECT(gate.close() == SessionCloseResult::drained);

  auto lease = gate.try_acquire(valid_diagnostics_session());
  EXPECT(!lease.has_value());
  EXPECT(adapter->total_queries() == 0);
  EXPECT(gate.reset_if_drained());
}

void diagnostics_session_acquisition_wins_and_retains_exact_adapter() {
  DiagnosticsSessionGate gate;
  auto adapter = std::make_shared<FakeDiagnosticsQueryAdapter>();
  std::weak_ptr<FakeDiagnosticsQueryAdapter> retained = adapter;
  EXPECT(gate.install(valid_diagnostics_session(), adapter));
  adapter.reset();

  std::mutex mutex;
  std::condition_variable condition;
  bool acquired = false;
  bool release = false;
  std::thread callback([&] {
    auto lease = gate.try_acquire(valid_diagnostics_session());
    {
      std::lock_guard lock(mutex);
      acquired = lease.has_value();
    }
    condition.notify_all();
    {
      std::unique_lock lock(mutex);
      condition.wait(lock, [&] { return release; });
    }
    if (lease) {
      void* object = nullptr;
      (void)lease->get_inspectable(7, &object);
      (void)lease->inspect_xaml(object);
    }
  });

  {
    std::unique_lock lock(mutex);
    condition.wait(lock, [&] { return acquired; });
  }
  EXPECT(gate.close() == SessionCloseResult::in_flight);
  EXPECT(!gate.try_acquire(valid_diagnostics_session()).has_value());
  auto replacement = std::make_shared<FakeDiagnosticsQueryAdapter>();
  EXPECT(!gate.install({.epoch = 14,
                        .site_identity = 0xA1,
                        .diagnostics_identity = 0xB1},
                       replacement));
  EXPECT(!retained.expired());
  EXPECT(!gate.reset_if_drained());
  {
    std::lock_guard lock(mutex);
    release = true;
  }
  condition.notify_all();
  callback.join();

  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
  EXPECT(retained.expired());
  EXPECT(gate.install({.epoch = 14,
                       .site_identity = 0xA1,
                       .diagnostics_identity = 0xB1},
                      replacement));
  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
}

void diagnostics_session_reentrant_close_never_waits_on_its_own_lease() {
  DiagnosticsSessionGate gate;
  auto adapter = std::make_shared<FakeDiagnosticsQueryAdapter>();
  EXPECT(gate.install(valid_diagnostics_session(), adapter));
  auto lease = gate.try_acquire(valid_diagnostics_session());
  EXPECT(lease.has_value());

  EXPECT(gate.close() == SessionCloseResult::in_flight);
  EXPECT(adapter->total_queries() == 0);
  lease.reset();
  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
}

void diagnostics_session_rejects_zero_mismatch_and_stale_replay() {
  DiagnosticsSessionGate gate;
  auto old_adapter = std::make_shared<FakeDiagnosticsQueryAdapter>();
  const auto old_identity = valid_diagnostics_session();
  const DiagnosticsSessionIdentity replacement_identity{
      .epoch = 14,
      .site_identity = 0xA1,
      .diagnostics_identity = 0xB1,
  };
  EXPECT(gate.install(old_identity, old_adapter));
  DiagnosticsCallbackPipeline pipeline(gate);

  FakeDiagnosticsAddAdapter zero;
  EXPECT(!pipeline.process_add({}, zero));
  EXPECT(zero.invocations == 0);
  EXPECT(old_adapter->total_queries() == 0);

  FakeDiagnosticsAddAdapter mismatch;
  EXPECT(!pipeline.process_add(replacement_identity, mismatch));
  EXPECT(mismatch.invocations == 0);
  EXPECT(old_adapter->total_queries() == 0);

  FakeDiagnosticsAddAdapter current;
  EXPECT(pipeline.process_add(old_identity, current));
  EXPECT(current.invocations == 1);
  EXPECT(old_adapter->total_queries() == 3);

  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
  auto replacement_adapter =
      std::make_shared<FakeDiagnosticsQueryAdapter>();
  EXPECT(gate.install(replacement_identity, replacement_adapter));

  FakeDiagnosticsAddAdapter stale_replay;
  EXPECT(!pipeline.process_add(old_identity, stale_replay));
  EXPECT(stale_replay.invocations == 0);
  EXPECT(replacement_adapter->total_queries() == 0);

  FakeDiagnosticsAddAdapter replacement;
  EXPECT(pipeline.process_add(replacement_identity, replacement));
  EXPECT(replacement.invocations == 1);
  EXPECT(replacement_adapter->total_queries() == 3);
  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
}

void diagnostics_session_detach_moves_final_adapter_destruction_outside_gate() {
  DiagnosticsSessionGate gate;
  AdapterDestructionObserver observer{.gate = &gate};
  auto adapter =
      std::make_shared<DestructionObservingDiagnosticsAdapter>(observer);
  const auto expected = valid_diagnostics_session();
  EXPECT(gate.install(expected, adapter));
  adapter.reset();

  auto lease = gate.try_acquire(expected);
  EXPECT(lease.has_value());
  EXPECT(gate.close() == SessionCloseResult::in_flight);
  EXPECT(!gate.detach_closed(expected).has_value());
  EXPECT(!observer.destroyed);
  lease.reset();

  observer.inside_gate_call.store(true, std::memory_order_release);
  auto detached = gate.detach_closed(expected);
  observer.inside_gate_call.store(false, std::memory_order_release);
  EXPECT(detached.has_value());
  EXPECT(detached->identity == expected);
  EXPECT(detached->adapter != nullptr);
  EXPECT(!gate.identity().has_value());
  EXPECT(!observer.destroyed);
  EXPECT(!observer.destroyed_during_gate_call);

  detached.reset();
  EXPECT(observer.destroyed);
  EXPECT(observer.reentered_gate);
  EXPECT(!observer.destroyed_during_gate_call);
  EXPECT(!observer.identity_seen_during_destruction.has_value());
}

void diagnostics_session_replace_returns_old_adapter_for_lock_free_release() {
  DiagnosticsSessionGate gate;
  AdapterDestructionObserver observer{.gate = &gate};
  auto old_adapter =
      std::make_shared<DestructionObservingDiagnosticsAdapter>(observer);
  auto replacement_adapter =
      std::make_shared<FakeDiagnosticsQueryAdapter>();
  const auto old_identity = valid_diagnostics_session();
  const DiagnosticsSessionIdentity replacement_identity{
      .epoch = 21,
      .site_identity = 0xA2,
      .diagnostics_identity = 0xB2,
  };
  EXPECT(gate.install(old_identity, old_adapter));
  old_adapter.reset();
  EXPECT(gate.close() == SessionCloseResult::drained);

  const DiagnosticsSessionIdentity wrong_expected{
      .epoch = 99,
      .site_identity = old_identity.site_identity,
      .diagnostics_identity = old_identity.diagnostics_identity,
  };
  EXPECT(!gate.replace_closed(wrong_expected, replacement_identity,
                              replacement_adapter)
              .has_value());
  EXPECT(gate.identity() == old_identity);

  observer.inside_gate_call.store(true, std::memory_order_release);
  auto retired = gate.replace_closed(old_identity, replacement_identity,
                                     replacement_adapter);
  observer.inside_gate_call.store(false, std::memory_order_release);
  EXPECT(retired.has_value());
  EXPECT(retired->identity == old_identity);
  EXPECT(retired->adapter != nullptr);
  EXPECT(gate.identity() == replacement_identity);
  EXPECT(!gate.try_acquire(old_identity).has_value());
  EXPECT(gate.try_acquire(replacement_identity).has_value());
  EXPECT(!observer.destroyed);
  EXPECT(!observer.destroyed_during_gate_call);

  retired.reset();
  EXPECT(observer.destroyed);
  EXPECT(observer.reentered_gate);
  EXPECT(!observer.destroyed_during_gate_call);
  EXPECT(observer.identity_seen_during_destruction == replacement_identity);
  EXPECT(gate.close() == SessionCloseResult::drained);
  EXPECT(gate.reset_if_drained());
}

class FakeXamlObjectAccess final : public CapsuleCleanupAccess {
 public:
  CapsuleParentRelationship capsule_parent =
      CapsuleParentRelationship::recorded_parent;
  std::vector<OriginalChildState> child_states;
  std::size_t fail_on_mutation = 0;
  std::size_t mutations = 0;
  std::size_t parent_queries = 0;
  std::size_t child_queries = 0;
  bool capsule_remove_called = false;
  std::vector<std::size_t> appended;
  std::vector<std::size_t> restored_children;
  std::mutex* final_access_mutex = nullptr;
  std::condition_variable* final_access_condition = nullptr;
  bool* final_access_entered = nullptr;
  bool* release_final_access = nullptr;

  CapsuleParentRelationship capsule_parent_relationship() noexcept override {
    ++parent_queries;
    return capsule_parent;
  }

  bool remove_capsule_from_recorded_parent() noexcept override {
    ++mutations;
    if (mutations == fail_on_mutation) {
      return false;
    }
    capsule_remove_called = true;
    capsule_parent = CapsuleParentRelationship::detached;
    return true;
  }

  bool clear_columns() noexcept override {
    ++mutations;
    if (mutations == fail_on_mutation) {
      return false;
    }
    appended.clear();
    restored_children.clear();
    return true;
  }

  bool append_column(std::size_t index) noexcept override {
    ++mutations;
    if (mutations == fail_on_mutation) {
      return false;
    }
    appended.push_back(index);
    return true;
  }

  OriginalChildState original_child_state(
      std::size_t index) noexcept override {
    ++child_queries;
    return index < child_states.size() ? child_states[index]
                                       : OriginalChildState::access_failure;
  }

  bool restore_original_child_column(std::size_t index) noexcept override {
    ++mutations;
    if (mutations == fail_on_mutation) {
      return false;
    }
    if (final_access_mutex && final_access_condition && final_access_entered &&
        release_final_access && index + 1 == child_states.size()) {
      std::unique_lock lock(*final_access_mutex);
      *final_access_entered = true;
      final_access_condition->notify_all();
      final_access_condition->wait(lock,
                                   [&] { return *release_final_access; });
    }
    restored_children.push_back(index);
    return true;
  }

  [[nodiscard]] std::size_t total_access_calls() const noexcept {
    return parent_queries + child_queries + mutations;
  }
};

FakeXamlObjectAccess complete_access() {
  FakeXamlObjectAccess access;
  access.child_states.assign(3, OriginalChildState::present_exact);
  return access;
}

CleanupIdentityPath exact_cleanup_path() noexcept {
  CleanupIdentityPath path;
  path.identities[0] = 0xD0;
  path.identities[1] = 0xD1;
  path.identities[2] = 0xD2;
  path.identities[3] = 0xC0;
  path.count = 4;
  path.complete = true;
  return path;
}

CleanupAuthorizationBinding exact_cleanup_binding(
    std::uint64_t record_key = 23) noexcept {
  return {.record_key = record_key,
          .owner_thread_id = 41,
          .host_identity = 0x100,
          .xaml_root_identity = 0xB0,
          .content_identity = 0xC0,
          .host_generation = 7,
          .root_generation = 11,
          .diagnostics_epoch = 13,
          .anchor_identity = 0xD0,
          .grid_identity = 0xD1,
          .anchor_to_content = exact_cleanup_path()};
}

CleanupAuthorizationEvidence exact_cleanup_evidence() noexcept {
  return {.owner_thread_id = 41,
          .host_identity = 0x100,
          .xaml_root_identity = 0xB0,
          .content_identity = 0xC0,
          .host_generation = 7,
          .root_generation = 11,
          .diagnostics_epoch = 13,
          .anchor_identity = 0xD0,
          .grid_identity = 0xD1,
          .anchor_xaml_root_identity = 0xB0,
          .anchor_content_identity = 0xC0,
          .grid_xaml_root_identity = 0xB0,
          .grid_content_identity = 0xC0,
          .anchor_to_content = exact_cleanup_path(),
          .host_is_still_valid = true};
}

class FakeCleanupEvidenceSource final
    : public CleanupAuthorizationEvidenceSource {
 public:
  CleanupAuthorizationEvidence evidence = exact_cleanup_evidence();
  bool succeeds = true;
  std::size_t resolutions = 0;
  std::optional<CapsuleCleanupTrigger> observed_trigger;

  bool resolve(CapsuleCleanupTrigger trigger,
               CleanupAuthorizationEvidence& output) noexcept override {
    ++resolutions;
    observed_trigger = trigger;
    if (!succeeds) {
      return false;
    }
    output = evidence;
    return true;
  }
};

CapsuleTransactionResult invoke_cleanup_trigger(
    CapsuleCleanupRecord& record,
    CapsuleCleanupTrigger trigger,
    CleanupAuthorizationEvidenceSource& evidence,
    CapsuleCleanupAccess& access) noexcept {
  switch (trigger) {
    case CapsuleCleanupTrigger::timer:
      return record.on_timer(evidence, access);
    case CapsuleCleanupTrigger::explicit_removal:
      return record.on_explicit_removal(evidence, access);
    case CapsuleCleanupTrigger::shutdown:
      return record.on_shutdown(evidence, access);
    case CapsuleCleanupTrigger::rollback:
      return record.on_rollback(evidence, access);
  }
  return CapsuleTransactionResult::retryable_failure;
}

void every_cleanup_entry_rejects_whole_subtree_moved_to_another_root() {
  for (const auto trigger : {CapsuleCleanupTrigger::timer,
                             CapsuleCleanupTrigger::explicit_removal,
                             CapsuleCleanupTrigger::shutdown,
                             CapsuleCleanupTrigger::rollback}) {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupRecord record(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    evidence.evidence.xaml_root_identity = 0xE0;
    evidence.evidence.content_identity = 0xE1;
    evidence.evidence.anchor_xaml_root_identity = 0xE0;
    evidence.evidence.anchor_content_identity = 0xE1;
    evidence.evidence.grid_xaml_root_identity = 0xE0;
    evidence.evidence.grid_content_identity = 0xE1;
    evidence.evidence.anchor_to_content.identities[3] = 0xE1;
    auto access = complete_access();

    EXPECT(invoke_cleanup_trigger(record, trigger, evidence, access) ==
           CapsuleTransactionResult::retryable_failure);
    EXPECT(evidence.resolutions == 1);
    EXPECT(evidence.observed_trigger == trigger);
    EXPECT(access.total_access_calls() == 0);
    EXPECT(!record.complete());
  }
}

void cleanup_authorization_rejects_inexact_or_malformed_evidence() {
  std::vector<CleanupAuthorizationEvidence> invalid;
  auto wrong_thread = exact_cleanup_evidence();
  wrong_thread.owner_thread_id = 42;
  invalid.push_back(wrong_thread);
  auto wrong_host = exact_cleanup_evidence();
  wrong_host.host_generation = 8;
  invalid.push_back(wrong_host);
  auto wrong_root = exact_cleanup_evidence();
  wrong_root.root_generation = 12;
  invalid.push_back(wrong_root);
  auto wrong_session = exact_cleanup_evidence();
  wrong_session.diagnostics_epoch = 14;
  invalid.push_back(wrong_session);
  auto wrong_anchor = exact_cleanup_evidence();
  wrong_anchor.anchor_identity = 0xD9;
  invalid.push_back(wrong_anchor);
  auto zero_path = exact_cleanup_evidence();
  zero_path.anchor_to_content.identities[2] = 0;
  invalid.push_back(zero_path);
  auto truncated_path = exact_cleanup_evidence();
  truncated_path.anchor_to_content.complete = false;
  invalid.push_back(truncated_path);
  auto cyclic_path = exact_cleanup_evidence();
  cyclic_path.anchor_to_content.identities[2] = 0xD0;
  invalid.push_back(cyclic_path);
  auto ambiguous_grid = exact_cleanup_evidence();
  ambiguous_grid.anchor_to_content.identities[2] = 0xD1;
  invalid.push_back(ambiguous_grid);
  auto changed_path = exact_cleanup_evidence();
  changed_path.anchor_to_content.identities[2] = 0xD8;
  invalid.push_back(changed_path);

  for (const auto& candidate : invalid) {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    evidence.evidence = candidate;
    auto access = complete_access();
    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::retryable_failure);
    EXPECT(evidence.resolutions == 1);
    EXPECT(access.total_access_calls() == 0);
    EXPECT(!workflow.complete());
  }
}

void cleanup_capability_is_exactly_scoped_and_one_shot() {
  {
    auto gate_a = std::make_shared<CleanupAuthorityGate>();
    auto gate_b = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow issuer(exact_cleanup_binding(23), gate_a, 2, 3);
    CapsuleCleanupWorkflow other_record(exact_cleanup_binding(24), gate_b, 2,
                                        3);
    FakeCleanupEvidenceSource evidence;
    auto authorization =
        issuer.authorize(CapsuleCleanupTrigger::timer, evidence);
    EXPECT(authorization.has_value());
    auto access = complete_access();
    EXPECT(other_record.consume(CapsuleCleanupTrigger::timer,
                                std::move(*authorization), access) ==
           CapsuleTransactionResult::retryable_failure);
    EXPECT(access.total_access_calls() == 0);
  }
  {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    auto authorization =
        workflow.authorize(CapsuleCleanupTrigger::timer, evidence);
    EXPECT(authorization.has_value());
    auto access = complete_access();
    EXPECT(workflow.consume(CapsuleCleanupTrigger::shutdown,
                            std::move(*authorization), access) ==
           CapsuleTransactionResult::retryable_failure);
    EXPECT(access.total_access_calls() == 0);
  }
  {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    auto authorization =
        workflow.authorize(CapsuleCleanupTrigger::timer, evidence);
    EXPECT(authorization.has_value());
    auto access = complete_access();
    EXPECT(workflow.consume(CapsuleCleanupTrigger::timer,
                            std::move(*authorization), access) ==
           CapsuleTransactionResult::complete);
    const auto calls_after_success = access.total_access_calls();
    EXPECT(workflow.consume(CapsuleCleanupTrigger::timer,
                            std::move(*authorization), access) ==
           CapsuleTransactionResult::retryable_failure);
    EXPECT(access.total_access_calls() == calls_after_success);
  }
}

void cleanup_authority_close_wins_before_evidence_or_port_query() {
  auto gate = std::make_shared<CleanupAuthorityGate>();
  CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
  FakeCleanupEvidenceSource evidence;
  auto access = complete_access();
  EXPECT(gate->close() == SessionCloseResult::drained);
  EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
         CapsuleTransactionResult::retryable_failure);
  EXPECT(evidence.resolutions == 0);
  EXPECT(access.total_access_calls() == 0);
}

void cleanup_mutation_lease_linearizes_close_before_any_mutation() {
  {
    CleanupAuthorityGate gate;
    std::size_t mutations = 0;
    EXPECT(gate.available());
    EXPECT(gate.accepting());
    EXPECT(gate.close() == SessionCloseResult::drained);
    EXPECT(gate.available());
    EXPECT(!gate.accepting());
    auto lease = gate.try_acquire_mutation();
    if (lease) {
      ++mutations;
    }
    EXPECT(!lease.has_value());
    EXPECT(mutations == 0);
  }
  {
    CleanupAuthorityGate gate;
    auto lease = gate.try_acquire_mutation();
    EXPECT(lease.has_value());
    EXPECT(gate.close() == SessionCloseResult::in_flight);
    EXPECT(!gate.try_acquire_mutation().has_value());
    lease.reset();
    EXPECT(gate.close() == SessionCloseResult::drained);
    EXPECT(gate.reopen_if_drained());
    EXPECT(gate.accepting());
  }
}

struct InsertionBarrierContext final {
  std::mutex mutex;
  std::condition_variable condition;
  bool registration_entered = false;
  bool allow_registration = false;
  std::size_t final_validations = 0;
  std::size_t xaml_mutations = 0;
  std::size_t record_registrations = 0;
};

bool run_insertion_barrier_mutator(
    void* value,
    CleanupAuthorityGate::MutationLease mutation_authority) noexcept {
  auto& context = *static_cast<InsertionBarrierContext*>(value);
  (void)mutation_authority;
  ++context.final_validations;
  ++context.xaml_mutations;
  std::unique_lock lock(context.mutex);
  ++context.record_registrations;
  context.registration_entered = true;
  context.condition.notify_all();
  context.condition.wait(lock,
                         [&context] { return context.allow_registration; });
  return true;
}

void production_insertion_pipeline_holds_lease_through_registration() {
  {
    auto authority = std::make_shared<CleanupAuthorityGate>();
    EXPECT(authority->close() == SessionCloseResult::drained);
    InsertionBarrierContext context;
    context.allow_registration = true;
    EXPECT(!cq::bridge::run_authorized_capsule_insertion(
        authority, run_insertion_barrier_mutator, &context));
    EXPECT(context.final_validations == 0);
    EXPECT(context.xaml_mutations == 0);
    EXPECT(context.record_registrations == 0);
  }

  auto authority = std::make_shared<CleanupAuthorityGate>();
  InsertionBarrierContext context;
  bool inserted = false;
  std::thread insertion([&] {
    inserted = cq::bridge::run_authorized_capsule_insertion(
        authority, run_insertion_barrier_mutator, &context);
  });
  {
    std::unique_lock lock(context.mutex);
    EXPECT(context.condition.wait_for(
        lock, std::chrono::seconds(2),
        [&context] { return context.registration_entered; }));
  }
  EXPECT(authority->close() == SessionCloseResult::in_flight);
  EXPECT(!authority->try_acquire_mutation().has_value());
  {
    std::lock_guard lock(context.mutex);
    context.allow_registration = true;
  }
  context.condition.notify_all();
  insertion.join();

  EXPECT(inserted);
  EXPECT(context.final_validations == 1);
  EXPECT(context.xaml_mutations == 1);
  EXPECT(context.record_registrations == 1);
  EXPECT(authority->live_count() == 0);
}

void retained_cleanup_ancestry_prevents_identity_address_reuse() {
  std::weak_ptr<int> weak_middle;
  std::uintptr_t middle_identity = 0;
  {
    auto anchor = std::make_shared<int>(1);
    auto middle = std::make_shared<int>(2);
    auto content = std::make_shared<int>(3);
    weak_middle = middle;
    middle_identity = reinterpret_cast<std::uintptr_t>(middle.get());

    CleanupIdentityPath path{};
    path.identities[0] = reinterpret_cast<std::uintptr_t>(anchor.get());
    path.identities[1] = middle_identity;
    path.identities[2] = reinterpret_cast<std::uintptr_t>(content.get());
    path.count = 3;
    path.complete = true;
    std::vector<CleanupRetainedIdentity> owners{
        {path.identities[0], anchor},
        {path.identities[1], middle},
        {path.identities[2], content},
    };

    CleanupAncestryRetention retained;
    EXPECT(retained.initialize(path, std::move(owners)));
    EXPECT(retained.matches(path));
    anchor.reset();
    middle.reset();
    content.reset();
    EXPECT(!weak_middle.expired());

    auto replacement = std::make_shared<int>(4);
    EXPECT(reinterpret_cast<std::uintptr_t>(replacement.get()) !=
           middle_identity);
    auto replaced_path = path;
    replaced_path.identities[1] =
        reinterpret_cast<std::uintptr_t>(replacement.get());
    EXPECT(!retained.matches(replaced_path));
  }
  EXPECT(weak_middle.expired());
}

void cleanup_authority_acquisition_wins_through_final_port_access() {
  auto gate = std::make_shared<CleanupAuthorityGate>();
  CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
  FakeCleanupEvidenceSource evidence;
  auto authorization =
      workflow.authorize(CapsuleCleanupTrigger::timer, evidence);
  EXPECT(authorization.has_value());
  EXPECT(gate->close() == SessionCloseResult::in_flight);

  auto access = complete_access();
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool release = false;
  access.final_access_mutex = &mutex;
  access.final_access_condition = &condition;
  access.final_access_entered = &entered;
  access.release_final_access = &release;
  CapsuleTransactionResult result = CapsuleTransactionResult::retryable_failure;
  std::thread cleanup([&workflow, &access, &result,
                       authorization = std::move(*authorization)]() mutable {
    result = workflow.consume(CapsuleCleanupTrigger::timer,
                              std::move(authorization), access);
  });
  {
    std::unique_lock lock(mutex);
    condition.wait(lock, [&] { return entered; });
  }
  EXPECT(gate->close() == SessionCloseResult::in_flight);
  {
    std::lock_guard lock(mutex);
    release = true;
  }
  condition.notify_all();
  cleanup.join();
  EXPECT(result == CapsuleTransactionResult::complete);
  EXPECT(gate->close() == SessionCloseResult::drained);
}

void transition_cleanup_permit_is_exact_and_revocation_is_permanent() {
  constexpr std::uint64_t transition_token = 0xCAFE;
  auto gate = std::make_shared<CleanupAuthorityGate>();
  auto other_gate = std::make_shared<CleanupAuthorityGate>();
  CapsuleCleanupWorkflow workflow(exact_cleanup_binding(31), gate, 2, 3);
  CapsuleCleanupWorkflow other_workflow(exact_cleanup_binding(32),
                                        other_gate, 2, 3);
  FakeCleanupEvidenceSource evidence;
  EXPECT(gate->mode() == CleanupAuthorityMode::forward);

  auto normal_cleanup =
      workflow.authorize(CapsuleCleanupTrigger::shutdown, evidence);
  auto mutation = gate->try_acquire_mutation();
  EXPECT(normal_cleanup.has_value());
  EXPECT(mutation.has_value());

  SessionCloseResult close_result = SessionCloseResult::drained;
  auto permit = gate->begin_transition(transition_token, close_result);
  EXPECT(permit.has_value());
  EXPECT(close_result == SessionCloseResult::in_flight);
  EXPECT(gate->mode() == CleanupAuthorityMode::closed);
  EXPECT(!gate->try_acquire_mutation().has_value());

  FakeCleanupEvidenceSource closed_evidence;
  auto closed_access = complete_access();
  EXPECT(workflow.attempt(CapsuleCleanupTrigger::shutdown, closed_evidence,
                          closed_access) ==
         CapsuleTransactionResult::retryable_failure);
  EXPECT(closed_evidence.resolutions == 0);
  EXPECT(closed_access.total_access_calls() == 0);

  EXPECT(!other_gate->enter_cleanup_only(transition_token, *permit));
  EXPECT(!gate->enter_cleanup_only(transition_token + 1, *permit));
  mutation.reset();
  normal_cleanup.reset();
  EXPECT(gate->enter_cleanup_only(transition_token, *permit));
  EXPECT(gate->mode() == CleanupAuthorityMode::cleanup_only);
  EXPECT(!gate->try_acquire_mutation().has_value());
  EXPECT(!workflow.authorize(CapsuleCleanupTrigger::shutdown, evidence)
              .has_value());

  FakeCleanupEvidenceSource wrong_gate_evidence;
  auto wrong_gate_access = complete_access();
  EXPECT(other_workflow.attempt_transition(
             CapsuleCleanupTrigger::shutdown, *permit, wrong_gate_evidence,
             wrong_gate_access) ==
         CapsuleTransactionResult::retryable_failure);
  EXPECT(wrong_gate_evidence.resolutions == 0);
  EXPECT(wrong_gate_access.total_access_calls() == 0);

  FakeCleanupEvidenceSource exact_evidence;
  auto exact_access = complete_access();
  EXPECT(workflow.attempt_transition(CapsuleCleanupTrigger::shutdown, *permit,
                                     exact_evidence, exact_access) ==
         CapsuleTransactionResult::complete);
  EXPECT(exact_access.total_access_calls() != 0);
  EXPECT(gate->close_cleanup_only(transition_token, *permit));
  EXPECT(gate->mode() == CleanupAuthorityMode::closed);
  EXPECT(gate->revoke_permanently() == SessionCloseResult::drained);
  EXPECT(gate->mode() == CleanupAuthorityMode::revoked);
  EXPECT(!gate->reopen_if_drained());
  EXPECT(!gate->enter_cleanup_only(transition_token, *permit));
  EXPECT(!gate->try_acquire_mutation().has_value());
}

void production_capsule_transaction_rejects_reparented_capsule() {
  auto gate = std::make_shared<CleanupAuthorityGate>();
  CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
  FakeCleanupEvidenceSource evidence;
  auto access = complete_access();
  access.capsule_parent = CapsuleParentRelationship::different_parent;

  EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
         CapsuleTransactionResult::retryable_failure);
  EXPECT(!workflow.complete());
  EXPECT(!access.capsule_remove_called);
  EXPECT(access.appended.empty());

  access.capsule_parent = CapsuleParentRelationship::recorded_parent;
  EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
         CapsuleTransactionResult::complete);
  EXPECT(workflow.complete());
  EXPECT(access.capsule_remove_called);
}

void production_capsule_transaction_rejects_stale_root_context() {
  auto gate = std::make_shared<CleanupAuthorityGate>();
  CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
  FakeCleanupEvidenceSource evidence;
  auto access = complete_access();
  evidence.evidence.host_is_still_valid = false;

  EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
         CapsuleTransactionResult::retryable_failure);
  EXPECT(!workflow.complete());
  EXPECT(access.mutations == 0);
  EXPECT(access.parent_queries == 0);

  evidence.evidence.host_is_still_valid = true;
  EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
         CapsuleTransactionResult::complete);
  EXPECT(workflow.complete());
}

void production_capsule_transaction_revalidates_parent_after_partial_cleanup() {
  auto gate = std::make_shared<CleanupAuthorityGate>();
  CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
  FakeCleanupEvidenceSource evidence;
  auto access = complete_access();
  access.fail_on_mutation = 2;
  EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
         CapsuleTransactionResult::retryable_failure);
  EXPECT(access.capsule_parent == CapsuleParentRelationship::detached);

  access.fail_on_mutation = 0;
  access.capsule_parent = CapsuleParentRelationship::different_parent;
  EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
         CapsuleTransactionResult::retryable_failure);
  EXPECT(!workflow.complete());
}

void production_capsule_transaction_handles_recorded_and_detached_capsules() {
  {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    auto access = complete_access();
    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::complete);
    EXPECT(access.capsule_remove_called);
    EXPECT(access.parent_queries >= 2);
  }
  {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    auto access = complete_access();
    access.capsule_parent = CapsuleParentRelationship::detached;
    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::complete);
    EXPECT(!access.capsule_remove_called);
  }
}

void production_capsule_transaction_retains_missing_or_changed_original_child() {
  for (const auto unavailable : {OriginalChildState::missing,
                                 OriginalChildState::identity_mismatch}) {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    auto access = complete_access();
    access.child_states[1] = unavailable;

    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::retryable_failure);
    EXPECT(!workflow.complete());
    EXPECT((access.restored_children == std::vector<std::size_t>{0}));

    access.child_states[1] = OriginalChildState::present_exact;
    access.mutations = 0;
    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::complete);
    EXPECT(workflow.complete());
    EXPECT((access.appended == std::vector<std::size_t>{0, 1}));
    EXPECT((access.restored_children == std::vector<std::size_t>{0, 1, 2}));
  }
}

void production_capsule_transaction_retries_every_mutating_step() {
  for (std::size_t failed_mutation = 1; failed_mutation <= 7;
       ++failed_mutation) {
    auto gate = std::make_shared<CleanupAuthorityGate>();
    CapsuleCleanupWorkflow workflow(exact_cleanup_binding(), gate, 2, 3);
    FakeCleanupEvidenceSource evidence;
    auto access = complete_access();
    access.fail_on_mutation = failed_mutation;
    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::retryable_failure);
    EXPECT(!workflow.complete());

    access.fail_on_mutation = 0;
    access.mutations = 0;
    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::complete);
    const auto successful_mutations = access.mutations;
    EXPECT(workflow.attempt(CapsuleCleanupTrigger::timer, evidence, access) ==
           CapsuleTransactionResult::already_complete);
    EXPECT(access.mutations == successful_mutations);
  }
}

}  // namespace

int main() {
  exact_root_binding_never_uses_same_thread_as_membership();
  root_binding_fails_closed_for_missing_ambiguous_reused_or_destroyed_roots();
  replacement_xaml_root_with_same_content_and_hwnd_generation_is_rejected();
  handoff_outcomes_are_bounded_and_late_completion_is_safe();
  failed_unhook_is_permanently_retained_and_never_repeated();
  barrier_timeout_is_retryable_without_repeating_unhook();
  barrier_cannot_retire_an_active_or_nested_hook_frame();
  request_result_is_independent_from_proven_hook_retirement();
  synchronous_barrier_reentry_is_typed_unsafe_without_blocking();
  pending_registry_precedes_queue_and_definite_rejection_rolls_back();
  unknown_queue_or_publication_outcome_is_permanently_retained();
  terminal_status_without_callback_is_the_only_retirement_proof();
  unknown_async_status_is_not_terminal_proof();
  cancel_and_status_failures_retain_until_terminal_status_is_observed();
  pending_lease_is_released_before_its_last_gate_owner();
  shutdown_poll_does_not_cancel_an_already_terminal_action();
  pending_record_teardown_keeps_gate_owner_alive_through_lease_release();
  pending_setup_failure_releases_lease_and_owner_before_module_pin();
  pending_capacity_rejection_destroys_unregistered_resources_lock_free();
  cancel_completion_race_reaps_terminal_action_without_false_failure();
  cancel_failure_is_retryable_and_never_retires_a_started_action();
  double_reaper_claim_and_stale_snapshot_cannot_remove_a_new_record();
  pending_status_cancel_and_final_releases_are_reentrant_and_lock_free();
  final_action_release_keeps_retiring_record_and_counts_visible();
  sequential_pending_completions_are_bounded_and_ids_never_reuse();
  callback_before_queue_return_runs_once_while_record_is_preparing();
  close_and_schedule_have_one_gate_winner_while_preparing_is_counted();
  shutdown_during_claimed_handoff_cancels_before_action();
  shutdown_during_executing_handoff_reports_unsafe_until_late_completion();
  admission_lease_makes_shutdown_and_counts_truthful();
  admission_overflow_and_moved_lease_fail_closed_without_underflow();
  pending_lease_releases_once_when_dispatch_setup_throws();
  diagnostics_session_close_wins_before_any_query();
  diagnostics_session_acquisition_wins_and_retains_exact_adapter();
  diagnostics_session_reentrant_close_never_waits_on_its_own_lease();
  diagnostics_session_rejects_zero_mismatch_and_stale_replay();
  diagnostics_session_detach_moves_final_adapter_destruction_outside_gate();
  diagnostics_session_replace_returns_old_adapter_for_lock_free_release();
  diagnostics_pipeline_invalidation_before_add_is_zero_query();
  diagnostics_pipeline_acquisition_wins_and_closes_new_admission();
  diagnostics_pipeline_reentrant_close_is_nonblocking_and_retained();
  every_cleanup_entry_rejects_whole_subtree_moved_to_another_root();
  cleanup_authorization_rejects_inexact_or_malformed_evidence();
  cleanup_capability_is_exactly_scoped_and_one_shot();
  cleanup_authority_close_wins_before_evidence_or_port_query();
  cleanup_mutation_lease_linearizes_close_before_any_mutation();
  production_insertion_pipeline_holds_lease_through_registration();
  retained_cleanup_ancestry_prevents_identity_address_reuse();
  cleanup_authority_acquisition_wins_through_final_port_access();
  transition_cleanup_permit_is_exact_and_revocation_is_permanent();
  production_capsule_transaction_rejects_reparented_capsule();
  production_capsule_transaction_rejects_stale_root_context();
  production_capsule_transaction_revalidates_parent_after_partial_cleanup();
  production_capsule_transaction_handles_recorded_and_detached_capsules();
  production_capsule_transaction_retains_missing_or_changed_original_child();
  production_capsule_transaction_retries_every_mutating_step();
  return EXIT_SUCCESS;
}
