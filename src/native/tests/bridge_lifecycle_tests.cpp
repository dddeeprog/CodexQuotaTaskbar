#include "bridge_runtime.h"
#include "bridge_exports.h"

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <condition_variable>
#include <cstdlib>
#include <functional>
#include <iostream>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {

using cq::bridge::BridgeLifecycle;
using cq::bridge::BridgeOperations;
using cq::bridge::BridgeWorker;
using cq::bridge::DetachResult;
using cq::bridge::ProbeCleanupResult;
using cq::bridge::ProbeCleanupStatus;
using cq::bridge::ProbeResourceCounts;
using cq::bridge::VisualNodeFacts;
using cq::bridge::WorkSignal;
using cq::bridge::BindingValidation;
using cq::bridge::validate_start_request;

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

enum class FailurePoint {
  none,
  shutdown_event,
  quiesced_event,
  ready_event,
  xaml_threads,
  diagnostics,
  watcher,
  cancel,
  unadvise,
  remove,
  unhook,
  prepare,
};

class FakeOperations final : public BridgeOperations {
 public:
  FailurePoint failure_point = FailurePoint::none;
  std::vector<WorkSignal> work{WorkSignal::shutdown};
  std::function<void()> on_wait;
  std::vector<std::string> trace;
  int retries = 0;
  int removes = 0;
  int unadvises = 0;
  int unhooks = 0;
  ProbeResourceCounts counts{};

  bool acquire_shutdown_event() noexcept override {
    trace.emplace_back("acquire-shutdown");
    return failure_point != FailurePoint::shutdown_event;
  }

  bool acquire_quiesced_event() noexcept override {
    trace.emplace_back("acquire-quiesced");
    return failure_point != FailurePoint::quiesced_event;
  }

  bool acquire_ready_event() noexcept override {
    trace.emplace_back("acquire-ready");
    return failure_point != FailurePoint::ready_event;
  }

  bool initialize_xaml_threads() noexcept override {
    trace.emplace_back("initialize-threads");
    if (failure_point == FailurePoint::xaml_threads) {
      return false;
    }
    counts.host_bindings = 1;
    return true;
  }

  bool initialize_xaml_diagnostics() noexcept override {
    trace.emplace_back("initialize-diagnostics");
    if (failure_point == FailurePoint::diagnostics) {
      return false;
    }
    counts.com_objects = 1;
    return true;
  }

  bool advise_watcher() noexcept override {
    trace.emplace_back("advise-watcher");
    if (failure_point == FailurePoint::watcher) {
      return false;
    }
    counts.watcher_references = 1;
    return true;
  }

  WorkSignal wait_for_work() noexcept override {
    if (on_wait) {
      on_wait();
    }
    if (work.empty()) {
      return WorkSignal::shutdown;
    }
    const auto next = work.front();
    work.erase(work.begin());
    return next;
  }

  bool retry_initialize_xaml_threads() noexcept override {
    ++retries;
    trace.emplace_back("retry");
    return true;
  }

  ProbeCleanupResult cancel_delayed_retry() noexcept override {
    trace.emplace_back("cancel-retry");
    if (failure_point == FailurePoint::cancel) {
      counts.pending_actions = 1;
      return {ProbeCleanupStatus::retryable_failure, 1};
    }
    counts.pending_actions = 0;
    return {};
  }

  ProbeCleanupResult unadvise_watcher() noexcept override {
    ++unadvises;
    trace.emplace_back("unadvise");
    if (failure_point == FailurePoint::unadvise) {
      return {ProbeCleanupStatus::retryable_failure, 1};
    }
    counts.watcher_references = 0;
    return {};
  }

  ProbeCleanupResult remove_all_capsules() noexcept override {
    ++removes;
    trace.emplace_back("remove");
    if (failure_point == FailurePoint::remove) {
      counts.capsules = 1;
      counts.timers = 1;
      return {ProbeCleanupStatus::retryable_failure, 1};
    }
    counts.capsules = 0;
    counts.timers = 0;
    return {};
  }

  ProbeCleanupResult uninitialize_xaml_threads() noexcept override {
    ++unhooks;
    trace.emplace_back("unhook");
    if (failure_point == FailurePoint::unhook) {
      return {ProbeCleanupStatus::retryable_failure, 1};
    }
    counts.host_bindings = 0;
    return {};
  }

  ProbeResourceCounts resource_counts() const noexcept override {
    return counts;
  }

  bool prepare_for_unload() noexcept override {
    trace.emplace_back("prepare");
    if (failure_point == FailurePoint::prepare) {
      return false;
    }
    counts.com_objects = 0;
    return true;
  }

  void signal_quiesced() noexcept override {
    trace.emplace_back("quiesced");
  }

  void signal_ready() noexcept override {
    trace.emplace_back("ready");
  }

  void close_control_handles() noexcept override {
    trace.emplace_back("close");
  }

  void self_unload() noexcept override {
    trace.emplace_back("unload");
  }
};

bool successful_mutator(void* context) noexcept {
  ++*static_cast<int*>(context);
  return true;
}

bool failing_mutator(void* context) noexcept {
  ++*static_cast<int*>(context);
  return false;
}

struct BlockingMutator {
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false;
  bool release = false;
};

bool blocking_mutator(void* context) noexcept {
  auto& state = *static_cast<BlockingMutator*>(context);
  std::unique_lock lock(state.mutex);
  state.entered = true;
  state.condition.notify_all();
  state.condition.wait(lock, [&state] { return state.release; });
  return true;
}

VisualNodeFacts accepted_facts() {
  return {L"SystemTray.SystemTrayFrame", true, true};
}

CQTB_StartProbeRequestV1 valid_start_request() {
  CQTB_StartProbeRequestV1 request{};
  request.size = sizeof(request);
  request.abi_version = CQTB_BRIDGE_ABI_VERSION;
  request.binding.size = sizeof(request.binding);
  request.binding.explorer_pid = 4242;
  request.binding.explorer_creation_time_100ns = 123456789;
  request.binding.activation_id[0] = 0xA5;
  return request;
}

void start_abi_is_bounded_and_bound_to_the_same_explorer_instance() {
  static_assert(sizeof(CQTB_ExplorerInstanceBindingV1) == 32);
  static_assert(sizeof(CQTB_StartProbeRequestV1) == 56);
  static_assert(offsetof(CQTB_StartProbeRequestV1, binding) == 8);

  auto request = valid_start_request();
  EXPECT(validate_start_request(request, 4242, 123456789) == BindingValidation::valid);

  request.binding.explorer_pid = 7;
  EXPECT(validate_start_request(request, 4242, 123456789) ==
         BindingValidation::wrong_explorer_instance);
  request = valid_start_request();
  request.binding.explorer_creation_time_100ns = 0;
  EXPECT(validate_start_request(request, 4242, 123456789) ==
         BindingValidation::wrong_explorer_instance);
  request = valid_start_request();
  request.binding.activation_id[0] = 0;
  EXPECT(validate_start_request(request, 4242, 123456789) ==
         BindingValidation::invalid_activation_id);
  request = valid_start_request();
  request.reserved[2] = 1;
  EXPECT(validate_start_request(request, 4242, 123456789) ==
         BindingValidation::reserved_not_zero);
  request = valid_start_request();
  request.abi_version = CQTB_BRIDGE_ABI_VERSION + 1;
  EXPECT(validate_start_request(request, 4242, 123456789) ==
         BindingValidation::abi_mismatch);
}

void initialize_insert_detach_detach_is_idempotent() {
  FakeOperations operations;
  BridgeLifecycle lifecycle(operations);
  int insertions = 0;

  EXPECT(lifecycle.initialize());
  EXPECT(lifecycle.on_visual_node(accepted_facts(), successful_mutator, &insertions));
  EXPECT(insertions == 1);
  EXPECT(lifecycle.tracked_element_count() == 1);

  EXPECT(lifecycle.detach() == DetachResult::quiesced);
  EXPECT(lifecycle.detach() == DetachResult::quiesced);

  EXPECT(operations.unadvises == 1);
  EXPECT(operations.removes == 1);
  EXPECT(operations.unhooks == 1);
  EXPECT(lifecycle.tracked_element_count() == 0);
  EXPECT(lifecycle.live_callback_count() == 0);
}

void diagnostics_session_precedes_root_binding_and_watcher_advice() {
  FakeOperations operations;
  BridgeLifecycle lifecycle(operations);

  EXPECT(lifecycle.initialize());
  const std::vector<std::string> expected{
      "initialize-diagnostics", "initialize-threads", "advise-watcher"};
  EXPECT(operations.trace == expected);
  EXPECT(lifecycle.detach() == DetachResult::quiesced);
}

void every_acquisition_failure_quiesces_without_leaks() {
  for (const auto failure : {FailurePoint::shutdown_event,
                             FailurePoint::quiesced_event,
                             FailurePoint::ready_event,
                             FailurePoint::xaml_threads,
                             FailurePoint::diagnostics,
                             FailurePoint::watcher}) {
    FakeOperations operations;
    operations.failure_point = failure;
    BridgeWorker worker(operations);

    EXPECT(worker.run() != 0);
    EXPECT(worker.lifecycle().tracked_element_count() == 0);
    EXPECT(worker.lifecycle().live_callback_count() == 0);
    EXPECT(!operations.trace.empty());
    EXPECT(operations.trace.back() == "unload");
  }
}

void wrong_parent_never_invokes_the_mutator() {
  FakeOperations operations;
  BridgeLifecycle lifecycle(operations);
  int insertions = 0;

  EXPECT(lifecycle.initialize());
  EXPECT(!lifecycle.on_visual_node(
      {L"SystemTray.SystemTrayFrame", false, true}, successful_mutator, &insertions));
  EXPECT(insertions == 0);
  EXPECT(lifecycle.detach() == DetachResult::quiesced);
  EXPECT(lifecycle.tracked_element_count() == 0);
  EXPECT(lifecycle.live_callback_count() == 0);
}

void malformed_names_and_failed_insertion_never_track_an_element() {
  FakeOperations operations;
  BridgeLifecycle lifecycle(operations);
  int insertions = 0;

  EXPECT(lifecycle.initialize());
  for (const auto& type_name : {L"", L"SystemTray.SystemTrayFrame2",
                                L"SystemTray.SystemTray\nFrame",
                                L"SystemTray\uff0eSystemTrayFrame"}) {
    EXPECT(!lifecycle.on_visual_node(
        {type_name, true, true}, successful_mutator, &insertions));
  }
  EXPECT(insertions == 0);
  EXPECT(!lifecycle.on_visual_node(
      accepted_facts(), failing_mutator, &insertions));
  EXPECT(insertions == 1);
  EXPECT(lifecycle.tracked_element_count() == 0);
  EXPECT(lifecycle.detach() == DetachResult::quiesced);
  EXPECT(lifecycle.tracked_element_count() == 0);
  EXPECT(lifecycle.live_callback_count() == 0);
}

void shutdown_during_delayed_retry_prevents_the_retry() {
  FakeOperations operations;
  operations.work = {WorkSignal::delayed_retry};
  BridgeWorker worker(operations);
  operations.on_wait = [&worker] { worker.request_shutdown(); };

  EXPECT(worker.run() == 0);
  EXPECT(operations.retries == 0);
  EXPECT(worker.lifecycle().tracked_element_count() == 0);
  EXPECT(worker.lifecycle().live_callback_count() == 0);
}

void detach_reports_not_quiesced_while_callback_is_live_then_retry_succeeds() {
  FakeOperations operations;
  BridgeLifecycle lifecycle(operations);
  BlockingMutator mutator;

  EXPECT(lifecycle.initialize());
  std::thread callback([&] {
    EXPECT(lifecycle.on_visual_node(accepted_facts(), blocking_mutator, &mutator));
  });

  {
    std::unique_lock lock(mutator.mutex);
    mutator.condition.wait(lock, [&mutator] { return mutator.entered; });
  }

  EXPECT(lifecycle.detach() == DetachResult::not_quiesced);
  {
    std::lock_guard lock(mutator.mutex);
    mutator.release = true;
  }
  mutator.condition.notify_all();

  callback.join();
  EXPECT(lifecycle.detach() == DetachResult::quiesced);
  EXPECT(lifecycle.tracked_element_count() == 0);
  EXPECT(lifecycle.live_callback_count() == 0);
}

void worker_orders_detach_quiescence_close_and_self_unload() {
  FakeOperations operations;
  BridgeWorker worker(operations);
  int insertions = 0;

  EXPECT(worker.lifecycle().initialize());
  EXPECT(worker.lifecycle().on_visual_node(
      accepted_facts(), successful_mutator, &insertions));
  operations.trace.clear();
  EXPECT(worker.run() == 0);

  const std::vector<std::string> suffix{
      "unadvise", "cancel-retry", "remove", "unhook", "prepare", "quiesced",
      "close", "unload"};
  EXPECT(operations.trace.size() >= suffix.size());
  EXPECT(std::equal(suffix.begin(), suffix.end(),
                    operations.trace.end() - static_cast<std::ptrdiff_t>(suffix.size())));
  EXPECT(operations.trace.back() == "unload");
  EXPECT(worker.lifecycle().tracked_element_count() == 0);
  EXPECT(worker.lifecycle().live_callback_count() == 0);
}

void every_cleanup_failure_is_retained_until_a_successful_retry() {
  for (const auto failure : {FailurePoint::cancel, FailurePoint::unadvise,
                             FailurePoint::remove, FailurePoint::unhook}) {
    FakeOperations operations;
    BridgeLifecycle lifecycle(operations);
    EXPECT(lifecycle.initialize());
    operations.failure_point = failure;

    EXPECT(lifecycle.detach() == DetachResult::not_quiesced);
    EXPECT(!operations.counts.empty());
    operations.failure_point = FailurePoint::none;
    EXPECT(lifecycle.detach() == DetachResult::quiesced);
    EXPECT(operations.counts.callbacks == 0);
    EXPECT(operations.counts.pending_actions == 0);
    EXPECT(operations.counts.capsules == 0);
    EXPECT(operations.counts.timers == 0);
    EXPECT(operations.counts.watcher_references == 0);
    EXPECT(operations.counts.host_bindings == 0);
  }
}

void persistent_cleanup_or_prepare_failure_never_claims_quiescence_or_unload() {
  for (const auto failure : {FailurePoint::remove, FailurePoint::prepare}) {
    FakeOperations operations;
    operations.failure_point = failure;
    BridgeWorker worker(operations);

    EXPECT(worker.run() == cq::bridge::kBridgeWorkerUnsafeRetained);
    if (failure == FailurePoint::remove) {
      EXPECT(operations.counts.capsules == 1);
      EXPECT(operations.counts.timers == 1);
    }
    EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                     "quiesced") == operations.trace.end());
    EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                     "close") == operations.trace.end());
    EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                     "unload") == operations.trace.end());
  }
}

void truthful_remaining_count_blocks_quiescence_even_when_steps_report_success() {
  FakeOperations operations;
  BridgeWorker worker(operations);
  operations.on_wait = [&operations] { operations.counts.handoffs = 1; };

  EXPECT(worker.run() == cq::bridge::kBridgeWorkerUnsafeRetained);
  EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                   "quiesced") == operations.trace.end());
  EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                   "unload") == operations.trace.end());
}

void retained_diagnostics_session_lease_blocks_quiescence_and_unload() {
  FakeOperations operations;
  BridgeWorker worker(operations);
  operations.on_wait = [&operations] { operations.counts.callbacks = 1; };

  EXPECT(worker.run() == cq::bridge::kBridgeWorkerUnsafeRetained);
  EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                   "quiesced") == operations.trace.end());
  EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                   "close") == operations.trace.end());
  EXPECT(std::find(operations.trace.begin(), operations.trace.end(),
                   "unload") == operations.trace.end());
}

}  // namespace

int main() {
  start_abi_is_bounded_and_bound_to_the_same_explorer_instance();
  initialize_insert_detach_detach_is_idempotent();
  diagnostics_session_precedes_root_binding_and_watcher_advice();
  every_acquisition_failure_quiesces_without_leaks();
  wrong_parent_never_invokes_the_mutator();
  malformed_names_and_failed_insertion_never_track_an_element();
  shutdown_during_delayed_retry_prevents_the_retry();
  detach_reports_not_quiesced_while_callback_is_live_then_retry_succeeds();
  every_cleanup_failure_is_retained_until_a_successful_retry();
  persistent_cleanup_or_prepare_failure_never_claims_quiescence_or_unload();
  truthful_remaining_count_blocks_quiescence_even_when_steps_report_success();
  retained_diagnostics_session_lease_blocks_quiescence_and_unload();
  worker_orders_detach_quiescence_close_and_self_unload();
  return EXIT_SUCCESS;
}
