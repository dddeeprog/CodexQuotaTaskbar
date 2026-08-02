#pragma once

#include "bridge_exports.h"
#include "probe_safety.h"
#include "xaml_taskbar_probe.h"

#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <mutex>

namespace cq::bridge {

enum class WorkSignal {
  shutdown,
  delayed_retry,
  failure,
};

enum class BridgeLifecyclePhase : std::uint8_t {
  ControlEvents,
  InitializeDiagnostics,
  InitializeTaskbarThreads,
  AdviseWatcher,
  Ready,
};

enum class DetachResult : std::uint8_t {
  quiesced,
  not_quiesced,
};

inline constexpr int kBridgeWorkerSuccess = 0;
inline constexpr int kBridgeWorkerFailure = 1;
inline constexpr int kBridgeWorkerUnsafeRetained = 2;

class BridgeOperations {
 public:
  virtual ~BridgeOperations() = default;

  [[nodiscard]] virtual bool acquire_shutdown_event() noexcept = 0;
  [[nodiscard]] virtual bool acquire_quiesced_event() noexcept = 0;
  [[nodiscard]] virtual bool acquire_ready_event() noexcept = 0;
  virtual void report_phase(BridgeLifecyclePhase) noexcept {}
  [[nodiscard]] virtual bool initialize_xaml_threads() noexcept = 0;
  [[nodiscard]] virtual bool initialize_xaml_diagnostics() noexcept = 0;
  [[nodiscard]] virtual bool advise_watcher() noexcept = 0;
  [[nodiscard]] virtual WorkSignal wait_for_work() noexcept = 0;
  [[nodiscard]] virtual bool retry_initialize_xaml_threads() noexcept = 0;

  [[nodiscard]] virtual ProbeCleanupResult cancel_delayed_retry() noexcept = 0;
  [[nodiscard]] virtual ProbeCleanupResult unadvise_watcher() noexcept = 0;
  [[nodiscard]] virtual ProbeCleanupResult remove_all_capsules() noexcept = 0;
  [[nodiscard]] virtual ProbeCleanupResult uninitialize_xaml_threads() noexcept = 0;
  [[nodiscard]] virtual ProbeResourceCounts resource_counts() const noexcept = 0;
  [[nodiscard]] virtual bool prepare_for_unload() noexcept = 0;
  virtual void signal_quiesced() noexcept = 0;
  virtual void signal_ready() noexcept = 0;
  virtual void close_control_handles() noexcept = 0;
  virtual void self_unload() noexcept = 0;
};

using CapsuleMutator = bool (*)(void* context) noexcept;

class BridgeLifecycle final {
 public:
  explicit BridgeLifecycle(BridgeOperations& operations) noexcept;
  BridgeLifecycle(const BridgeLifecycle&) = delete;
  BridgeLifecycle& operator=(const BridgeLifecycle&) = delete;

  [[nodiscard]] bool initialize() noexcept;
  [[nodiscard]] bool on_visual_node(const VisualNodeFacts& facts,
                                    CapsuleMutator mutator,
                                    void* context) noexcept;
  void request_shutdown() noexcept;
  [[nodiscard]] DetachResult detach() noexcept;

  [[nodiscard]] std::size_t tracked_element_count() const noexcept;
  [[nodiscard]] std::size_t live_callback_count() const noexcept;
 [[nodiscard]] bool shutdown_requested() const noexcept;

 private:
  BridgeOperations& operations_;
  std::atomic<bool> shutdown_requested_{false};
  std::atomic<std::size_t> tracked_elements_{0};
  AdmissionGate callback_admission_{4096};

  mutable std::mutex state_mutex_;
  bool xaml_threads_initialized_ = false;
  bool diagnostics_initialized_ = false;
  bool watcher_advised_ = false;
  bool initialization_attempted_ = false;
  bool initialization_succeeded_ = false;
  bool detached_ = false;
  bool cleanup_active_ = false;
};

class BridgeWorker final {
 public:
  explicit BridgeWorker(BridgeOperations& operations) noexcept;
  BridgeWorker(const BridgeWorker&) = delete;
  BridgeWorker& operator=(const BridgeWorker&) = delete;

  [[nodiscard]] int run() noexcept;
  void request_shutdown() noexcept;
  [[nodiscard]] BridgeLifecycle& lifecycle() noexcept;

 private:
  BridgeOperations& operations_;
  BridgeLifecycle lifecycle_;
};

void cache_bridge_module(void* module) noexcept;
[[nodiscard]] std::int32_t start_probe_worker(
    const CQTB_StartProbeRequestV1& request) noexcept;

}  // namespace cq::bridge
