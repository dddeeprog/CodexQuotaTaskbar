#include "bridge_runtime.h"
#include "bridge_exports.h"

#include "xaml_taskbar_probe.h"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <memory>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <utility>

namespace cq::bridge {

BindingValidation validate_start_request(
    const CQTB_StartProbeRequestV1& request,
    std::uint32_t actual_process_id,
    std::uint64_t actual_creation_time_100ns) noexcept {
  if (request.size != sizeof(CQTB_StartProbeRequestV1) ||
      request.binding.size != sizeof(CQTB_ExplorerInstanceBindingV1)) {
    return BindingValidation::invalid_size;
  }
  if (request.abi_version != CQTB_BRIDGE_ABI_VERSION) {
    return BindingValidation::abi_mismatch;
  }
  if (std::any_of(std::begin(request.reserved), std::end(request.reserved),
                  [](std::uint32_t value) { return value != 0; })) {
    return BindingValidation::reserved_not_zero;
  }
  if (std::all_of(std::begin(request.binding.activation_id),
                  std::end(request.binding.activation_id),
                  [](std::uint8_t value) { return value == 0; })) {
    return BindingValidation::invalid_activation_id;
  }
  if (request.binding.explorer_pid != actual_process_id ||
      request.binding.explorer_creation_time_100ns == 0 ||
      request.binding.explorer_creation_time_100ns != actual_creation_time_100ns) {
    return BindingValidation::wrong_explorer_instance;
  }
  return BindingValidation::valid;
}

BridgeLifecycle::BridgeLifecycle(BridgeOperations& operations) noexcept
    : operations_(operations) {}

bool BridgeLifecycle::initialize() noexcept {
  {
    std::lock_guard lock(state_mutex_);
    if (initialization_attempted_) {
      return initialization_succeeded_;
    }
    initialization_attempted_ = true;
  }

  operations_.report_phase(BridgeLifecyclePhase::InitializeDiagnostics);
  if (!operations_.initialize_xaml_diagnostics()) {
    return false;
  }
  {
    std::lock_guard lock(state_mutex_);
    diagnostics_initialized_ = true;
  }

  operations_.report_phase(BridgeLifecyclePhase::InitializeTaskbarThreads);
  if (!operations_.initialize_xaml_threads()) {
    return false;
  }
  {
    std::lock_guard lock(state_mutex_);
    xaml_threads_initialized_ = true;
  }

  operations_.report_phase(BridgeLifecyclePhase::AdviseWatcher);
  if (!operations_.advise_watcher()) {
    return false;
  }
  {
    std::lock_guard lock(state_mutex_);
    watcher_advised_ = true;
    initialization_succeeded_ = true;
  }
  return true;
}

bool BridgeLifecycle::on_visual_node(const VisualNodeFacts& facts,
                                     CapsuleMutator mutator,
                                     void* context) noexcept {
  if (!mutator) {
    return false;
  }
  auto callback_lease = callback_admission_.try_acquire();
  if (!callback_lease) {
    return false;
  }
  if (shutdown_requested_.load(std::memory_order_acquire)) {
    return false;
  }

  if (evaluate_visual_node(facts) != ProbeDecision::accept) {
    return false;
  }

  auto tracked = tracked_elements_.load(std::memory_order_acquire);
  while (true) {
    if (tracked == (std::numeric_limits<std::size_t>::max)()) {
      return false;
    }
    if (tracked_elements_.compare_exchange_weak(
            tracked, tracked + 1, std::memory_order_acq_rel,
            std::memory_order_acquire)) {
      break;
    }
  }
  if (!mutator(context)) {
    tracked_elements_.fetch_sub(1, std::memory_order_acq_rel);
    return false;
  }
  return true;
}

void BridgeLifecycle::request_shutdown() noexcept {
  shutdown_requested_.store(true, std::memory_order_release);
  callback_admission_.close();
}

DetachResult BridgeLifecycle::detach() noexcept {
  request_shutdown();

  bool watcher_advised = false;
  bool xaml_threads_initialized = false;
  {
    std::lock_guard lock(state_mutex_);
    if (detached_) {
      return DetachResult::quiesced;
    }
    if (cleanup_active_) {
      return DetachResult::not_quiesced;
    }
    cleanup_active_ = true;
    watcher_advised = watcher_advised_;
    xaml_threads_initialized = xaml_threads_initialized_;
  }

  ProbeCleanupResult watcher_cleanup{};
  if (watcher_advised) {
    watcher_cleanup = operations_.unadvise_watcher();
  }
  const auto pending_cleanup = operations_.cancel_delayed_retry();

  ProbeCleanupResult capsule_cleanup{ProbeCleanupStatus::unsafe_in_flight,
                                     tracked_elements_.load(
                                         std::memory_order_acquire)};
  ProbeCleanupResult thread_cleanup{ProbeCleanupStatus::unsafe_in_flight, 1};
  if (callback_admission_.drained() &&
      watcher_cleanup.status == ProbeCleanupStatus::complete &&
      pending_cleanup.status == ProbeCleanupStatus::complete) {
    capsule_cleanup = operations_.remove_all_capsules();
    tracked_elements_.store(capsule_cleanup.remaining,
                            std::memory_order_release);
    if (capsule_cleanup.status == ProbeCleanupStatus::complete &&
        capsule_cleanup.remaining == 0) {
      thread_cleanup = xaml_threads_initialized
                           ? operations_.uninitialize_xaml_threads()
                           : ProbeCleanupResult{};
    }
  }

  const auto counts = operations_.resource_counts();
  tracked_elements_.store(counts.capsules, std::memory_order_release);
  const bool cleanup_counts_empty =
      counts.callbacks == 0 && counts.pending_actions == 0 &&
      counts.handoffs == 0 && counts.capsules == 0 && counts.timers == 0 &&
      counts.watcher_references == 0 && counts.host_bindings == 0;
  const bool complete =
      watcher_cleanup.status == ProbeCleanupStatus::complete &&
      pending_cleanup.status == ProbeCleanupStatus::complete &&
      capsule_cleanup.status == ProbeCleanupStatus::complete &&
      thread_cleanup.status == ProbeCleanupStatus::complete &&
      callback_admission_.drained() && cleanup_counts_empty;

  {
    std::lock_guard lock(state_mutex_);
    cleanup_active_ = false;
    if (complete) {
      watcher_advised_ = false;
      diagnostics_initialized_ = false;
      xaml_threads_initialized_ = false;
      detached_ = true;
    }
  }
  return complete ? DetachResult::quiesced : DetachResult::not_quiesced;
}

std::size_t BridgeLifecycle::tracked_element_count() const noexcept {
  return tracked_elements_.load(std::memory_order_acquire);
}

std::size_t BridgeLifecycle::live_callback_count() const noexcept {
  return callback_admission_.live_count();
}

bool BridgeLifecycle::shutdown_requested() const noexcept {
  return shutdown_requested_.load(std::memory_order_acquire);
}

BridgeWorker::BridgeWorker(BridgeOperations& operations) noexcept
    : operations_(operations), lifecycle_(operations) {}

int BridgeWorker::run() noexcept {
  int result = 1;
  bool shutdown_event_acquired = false;
  bool quiesced_event_acquired = false;
  bool ready_event_acquired = false;

  shutdown_event_acquired = operations_.acquire_shutdown_event();
  if (shutdown_event_acquired) {
    quiesced_event_acquired = operations_.acquire_quiesced_event();
  }
  if (quiesced_event_acquired) {
    ready_event_acquired = operations_.acquire_ready_event();
  }

  if (shutdown_event_acquired && quiesced_event_acquired &&
      ready_event_acquired) {
    operations_.report_phase(BridgeLifecyclePhase::ControlEvents);
  }
  if (shutdown_event_acquired && quiesced_event_acquired && ready_event_acquired &&
      lifecycle_.initialize()) {
    result = 0;
    operations_.report_phase(BridgeLifecyclePhase::Ready);
    operations_.signal_ready();
    while (!lifecycle_.shutdown_requested()) {
      switch (operations_.wait_for_work()) {
        case WorkSignal::shutdown:
          lifecycle_.request_shutdown();
          break;
        case WorkSignal::delayed_retry:
          if (!lifecycle_.shutdown_requested() &&
              !operations_.retry_initialize_xaml_threads()) {
            result = 1;
            lifecycle_.request_shutdown();
          }
          break;
        case WorkSignal::failure:
          result = 1;
          lifecycle_.request_shutdown();
          break;
      }
    }
  }

  const auto detach_result = lifecycle_.detach();
  if (detach_result != DetachResult::quiesced ||
      !operations_.prepare_for_unload()) {
    return kBridgeWorkerUnsafeRetained;
  }
  if (quiesced_event_acquired) {
    operations_.signal_quiesced();
  }
  operations_.close_control_handles();
  operations_.self_unload();
  return result;
}

void BridgeWorker::request_shutdown() noexcept {
  lifecycle_.request_shutdown();
}

BridgeLifecycle& BridgeWorker::lifecycle() noexcept {
  return lifecycle_;
}

namespace {

std::atomic<HMODULE> g_bridge_module{nullptr};
std::mutex g_start_mutex;
bool g_worker_started = false;
CQTB_StartProbeRequestV1 g_active_request{};

[[nodiscard]] std::uint64_t current_process_creation_time() noexcept {
  FILETIME creation{};
  FILETIME exit{};
  FILETIME kernel{};
  FILETIME user{};
  if (!GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user)) {
    return 0;
  }
  ULARGE_INTEGER value{};
  value.LowPart = creation.dwLowDateTime;
  value.HighPart = creation.dwHighDateTime;
  return value.QuadPart;
}

[[nodiscard]] std::wstring activation_hex(
    const CQTB_ExplorerInstanceBindingV1& binding) {
  constexpr wchar_t digits[] = L"0123456789abcdef";
  std::wstring value;
  value.reserve(CQTB_ACTIVATION_ID_BYTES * 2);
  for (const auto byte : binding.activation_id) {
    value.push_back(digits[(byte >> 4) & 0x0f]);
    value.push_back(digits[byte & 0x0f]);
  }
  return value;
}

[[nodiscard]] std::wstring event_name(
    const CQTB_ExplorerInstanceBindingV1& binding,
    const wchar_t* suffix) {
  wchar_t instance[64]{};
  swprintf_s(instance, L"%08x.%016llx", binding.explorer_pid,
             static_cast<unsigned long long>(
                 binding.explorer_creation_time_100ns));
  return std::wstring{L"Local\\CQTB.Probe.v1."} + instance + L"." +
         activation_hex(binding) + L"." + suffix;
}

[[nodiscard]] const wchar_t* phase_event_suffix(
    BridgeLifecyclePhase phase) noexcept {
  switch (phase) {
    case BridgeLifecyclePhase::ControlEvents:
      return L"Phase.ControlEvents";
    case BridgeLifecyclePhase::InitializeDiagnostics:
      return L"Phase.InitializeDiagnostics";
    case BridgeLifecyclePhase::InitializeTaskbarThreads:
      return L"Phase.InitializeTaskbarThreads";
    case BridgeLifecyclePhase::AdviseWatcher:
      return L"Phase.AdviseWatcher";
    case BridgeLifecyclePhase::Ready:
      return L"Phase.Ready";
  }
  return nullptr;
}

struct PhaseEventHandles final {
  PhaseEventHandles() = default;
  PhaseEventHandles(const PhaseEventHandles&) = delete;
  PhaseEventHandles& operator=(const PhaseEventHandles&) = delete;

  ~PhaseEventHandles() { close_all(); }

  [[nodiscard]] HANDLE take(BridgeLifecyclePhase phase) noexcept {
    switch (phase) {
      case BridgeLifecyclePhase::ControlEvents:
        return std::exchange(control_events, nullptr);
      case BridgeLifecyclePhase::InitializeDiagnostics:
        return std::exchange(initialize_diagnostics, nullptr);
      case BridgeLifecyclePhase::InitializeTaskbarThreads:
        return std::exchange(initialize_taskbar_threads, nullptr);
      case BridgeLifecyclePhase::AdviseWatcher:
        return std::exchange(advise_watcher, nullptr);
      case BridgeLifecyclePhase::Ready:
        return std::exchange(ready, nullptr);
    }
    return nullptr;
  }

  HANDLE control_events = nullptr;
  HANDLE initialize_diagnostics = nullptr;
  HANDLE initialize_taskbar_threads = nullptr;
  HANDLE advise_watcher = nullptr;
  HANDLE ready = nullptr;

 private:
  static void close(HANDLE& handle) noexcept {
    if (handle) {
      CloseHandle(std::exchange(handle, nullptr));
    }
  }

  void close_all() noexcept {
    close(ready);
    close(advise_watcher);
    close(initialize_taskbar_threads);
    close(initialize_diagnostics);
    close(control_events);
  }
};

[[nodiscard]] HANDLE create_diagnostic_phase_event(
    const CQTB_ExplorerInstanceBindingV1& binding,
    BridgeLifecyclePhase phase) noexcept {
  const auto suffix = phase_event_suffix(phase);
  if (!suffix) {
    return nullptr;
  }

  try {
    const auto name = event_name(binding, suffix);
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
    if (!event) {
      return nullptr;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
      CloseHandle(event);
      return nullptr;
    }
    return event;
  } catch (...) {
    return nullptr;
  }
}

void precreate_diagnostic_phase_events(
    const CQTB_ExplorerInstanceBindingV1& binding,
    PhaseEventHandles& events) noexcept {
  events.control_events = create_diagnostic_phase_event(
      binding, BridgeLifecyclePhase::ControlEvents);
  events.initialize_diagnostics = create_diagnostic_phase_event(
      binding, BridgeLifecyclePhase::InitializeDiagnostics);
  events.initialize_taskbar_threads = create_diagnostic_phase_event(
      binding, BridgeLifecyclePhase::InitializeTaskbarThreads);
  events.advise_watcher = create_diagnostic_phase_event(
      binding, BridgeLifecyclePhase::AdviseWatcher);
  events.ready = create_diagnostic_phase_event(
      binding, BridgeLifecyclePhase::Ready);
}

class Win32BridgeOperations final : public BridgeOperations {
 public:
  Win32BridgeOperations(const CQTB_ExplorerInstanceBindingV1& binding,
                        HMODULE initial_module,
                        HMODULE worker_module,
                        PhaseEventHandles& phase_events) noexcept
      : binding_(binding),
        initial_module_(initial_module),
        worker_module_(worker_module),
        phase_control_events_event_(
            phase_events.take(BridgeLifecyclePhase::ControlEvents)),
        phase_initialize_diagnostics_event_(
            phase_events.take(BridgeLifecyclePhase::InitializeDiagnostics)),
        phase_initialize_taskbar_threads_event_(
            phase_events.take(BridgeLifecyclePhase::InitializeTaskbarThreads)),
        phase_advise_watcher_event_(
            phase_events.take(BridgeLifecyclePhase::AdviseWatcher)),
        phase_ready_event_(phase_events.take(BridgeLifecyclePhase::Ready)) {}

  void attach_lifecycle(BridgeLifecycle& lifecycle) noexcept {
    probe_.reset(new (std::nothrow)
                     XamlTaskbarProbe(worker_module_, lifecycle));
  }

  bool acquire_shutdown_event() noexcept override {
    try {
      return create_unique_event(event_name(binding_, L"Shutdown"),
                                 shutdown_event_);
    } catch (...) {
      return false;
    }
  }

  bool acquire_quiesced_event() noexcept override {
    try {
      return create_unique_event(event_name(binding_, L"Quiesced"),
                                 quiesced_event_);
    } catch (...) {
      return false;
    }
  }

  bool acquire_ready_event() noexcept override {
    try {
      return create_unique_event(event_name(binding_, L"Ready"), ready_event_);
    } catch (...) {
      return false;
    }
  }

  void report_phase(BridgeLifecyclePhase phase) noexcept override {
    auto* const phase_event = phase_event_handle(phase);
    if (!phase_event || !*phase_event) {
      return;
    }
    // Phase evidence is diagnostics-only and must not alter probe behavior.
    SetEvent(*phase_event);
  }

  bool initialize_xaml_threads() noexcept override {
    return probe_ && probe_->initialize_existing_taskbar_threads();
  }

  bool initialize_xaml_diagnostics() noexcept override {
    return probe_ && probe_->initialize_xaml_diagnostics();
  }

  bool advise_watcher() noexcept override {
    return probe_ && probe_->advise_watcher();
  }

  WorkSignal wait_for_work() noexcept override {
    if (!shutdown_event_) {
      return WorkSignal::failure;
    }
    switch (WaitForSingleObject(shutdown_event_, 2000)) {
      case WAIT_OBJECT_0:
        return WorkSignal::shutdown;
      case WAIT_TIMEOUT:
        return WorkSignal::delayed_retry;
      default:
        return WorkSignal::failure;
    }
  }

  bool retry_initialize_xaml_threads() noexcept override {
    return probe_ && probe_->initialize_existing_taskbar_threads();
  }

  ProbeCleanupResult cancel_delayed_retry() noexcept override {
    return probe_ ? probe_->cancel_pending_work() : ProbeCleanupResult{};
  }

  ProbeCleanupResult unadvise_watcher() noexcept override {
    return probe_ ? probe_->unadvise_watcher() : ProbeCleanupResult{};
  }

  ProbeCleanupResult remove_all_capsules() noexcept override {
    return probe_ ? probe_->remove_all_capsules() : ProbeCleanupResult{};
  }

  ProbeCleanupResult uninitialize_xaml_threads() noexcept override {
    return probe_ ? probe_->uninitialize_taskbar_threads()
                  : ProbeCleanupResult{};
  }

  ProbeResourceCounts resource_counts() const noexcept override {
    return probe_ ? probe_->resource_counts() : ProbeResourceCounts{};
  }

  bool prepare_for_unload() noexcept override {
    if (probe_ && !probe_->release_for_unload()) {
      return false;
    }
    probe_.reset();
    if (initial_module_) {
      FreeLibrary(std::exchange(initial_module_, nullptr));
    }
    return !probe_;
  }

  void signal_quiesced() noexcept override {
    if (quiesced_event_) {
      SetEvent(quiesced_event_);
    }
  }

  void signal_ready() noexcept override {
    if (ready_event_) {
      SetEvent(ready_event_);
    }
  }

  void close_control_handles() noexcept override {
    close_handle(phase_ready_event_);
    close_handle(phase_advise_watcher_event_);
    close_handle(phase_initialize_taskbar_threads_event_);
    close_handle(phase_initialize_diagnostics_event_);
    close_handle(phase_control_events_event_);
    close_handle(ready_event_);
    close_handle(quiesced_event_);
    close_handle(shutdown_event_);
  }

  void self_unload() noexcept override {
    if (probe_) {
      return;
    }
    const HMODULE module = std::exchange(worker_module_, nullptr);
    if (module) {
      FreeLibraryAndExitThread(module, 0);
    }
  }

 private:
  HANDLE* phase_event_handle(BridgeLifecyclePhase phase) noexcept {
    switch (phase) {
      case BridgeLifecyclePhase::ControlEvents:
        return &phase_control_events_event_;
      case BridgeLifecyclePhase::InitializeDiagnostics:
        return &phase_initialize_diagnostics_event_;
      case BridgeLifecyclePhase::InitializeTaskbarThreads:
        return &phase_initialize_taskbar_threads_event_;
      case BridgeLifecyclePhase::AdviseWatcher:
        return &phase_advise_watcher_event_;
      case BridgeLifecyclePhase::Ready:
        return &phase_ready_event_;
    }
    return nullptr;
  }

  static bool create_unique_event(const std::wstring& name,
                                  HANDLE& destination) noexcept {
    destination = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
    if (!destination) {
      return false;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
      CloseHandle(std::exchange(destination, nullptr));
      return false;
    }
    return true;
  }

  static void close_handle(HANDLE& handle) noexcept {
    if (handle) {
      CloseHandle(std::exchange(handle, nullptr));
    }
  }

  CQTB_ExplorerInstanceBindingV1 binding_{};
  HMODULE initial_module_ = nullptr;
  HMODULE worker_module_ = nullptr;
  HANDLE shutdown_event_ = nullptr;
  HANDLE quiesced_event_ = nullptr;
  HANDLE ready_event_ = nullptr;
  HANDLE phase_control_events_event_ = nullptr;
  HANDLE phase_initialize_diagnostics_event_ = nullptr;
  HANDLE phase_initialize_taskbar_threads_event_ = nullptr;
  HANDLE phase_advise_watcher_event_ = nullptr;
  HANDLE phase_ready_event_ = nullptr;
  std::unique_ptr<XamlTaskbarProbe> probe_;
};

struct BootstrapContext final {
  CQTB_ExplorerInstanceBindingV1 binding{};
  HMODULE initial_module = nullptr;
  HMODULE worker_module = nullptr;
  HANDLE start_released_event = nullptr;
  PhaseEventHandles phase_events;
};

struct BridgeRuntimeContext final {
  BridgeRuntimeContext(const CQTB_ExplorerInstanceBindingV1& binding,
                       HMODULE initial_module,
                       HMODULE worker_module,
                       PhaseEventHandles& phase_events) noexcept
      : operations(binding, initial_module, worker_module, phase_events),
        worker(operations) {
    operations.attach_lifecycle(worker.lifecycle());
  }

  Win32BridgeOperations operations;
  BridgeWorker worker;
};

DWORD WINAPI bootstrap_worker(void* parameter) noexcept {
  std::unique_ptr<BootstrapContext> context{
      static_cast<BootstrapContext*>(parameter)};
  if (WaitForSingleObject(context->start_released_event, INFINITE) !=
      WAIT_OBJECT_0) {
    return 1;
  }
  CloseHandle(std::exchange(context->start_released_event, nullptr));
  std::unique_ptr<BridgeRuntimeContext> runtime{
      new (std::nothrow) BridgeRuntimeContext(
          context->binding, context->initial_module, context->worker_module,
          context->phase_events)};
  if (!runtime) {
    return static_cast<DWORD>(kBridgeWorkerUnsafeRetained);
  }
  const int result = runtime->worker.run();
  if (result == kBridgeWorkerUnsafeRetained) {
    // The worker has a late handoff or an unreverted UI transaction. Keep the
    // operations, COM references, handles, and module references alive until
    // Task 5 records/reconciles the unsafe Explorer instance.
    runtime.release();
  }
  return static_cast<DWORD>(result);
}

}  // namespace

void cache_bridge_module(void* module) noexcept {
  g_bridge_module.store(static_cast<HMODULE>(module), std::memory_order_release);
}

std::int32_t start_probe_worker(
    const CQTB_StartProbeRequestV1& request) noexcept {
  const auto process_id = GetCurrentProcessId();
  const auto creation_time = current_process_creation_time();
  if (!g_bridge_module.load(std::memory_order_acquire)) {
    return E_UNEXPECTED;
  }
  if (validate_start_request(request, process_id, creation_time) !=
      BindingValidation::valid) {
    return E_INVALIDARG;
  }

  std::lock_guard lock(g_start_mutex);
  if (g_worker_started) {
    return std::memcmp(&g_active_request, &request, sizeof(request)) == 0
               ? S_FALSE
               : E_ACCESSDENIED;
  }

  std::wstring start_released_name;
  try {
    start_released_name = event_name(request.binding, L"StartReleased");
  } catch (...) {
    return E_OUTOFMEMORY;
  }

  HMODULE worker_module = nullptr;
  if (!GetModuleHandleExW(
          GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
          reinterpret_cast<LPCWSTR>(&start_probe_worker), &worker_module)) {
    return HRESULT_FROM_WIN32(GetLastError());
  }
  HANDLE start_released_event = CreateEventW(
      nullptr, TRUE, FALSE, start_released_name.c_str());
  if (!start_released_event || GetLastError() == ERROR_ALREADY_EXISTS) {
    const HRESULT result = start_released_event
                               ? HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)
                               : HRESULT_FROM_WIN32(GetLastError());
    if (start_released_event) {
      CloseHandle(start_released_event);
    }
    FreeLibrary(worker_module);
    return result;
  }
  std::unique_ptr<BootstrapContext> context{
      new (std::nothrow) BootstrapContext()};
  if (!context) {
    CloseHandle(start_released_event);
    FreeLibrary(worker_module);
    return E_OUTOFMEMORY;
  }
  context->binding = request.binding;
  context->initial_module = g_bridge_module.load(std::memory_order_acquire);
  context->worker_module = worker_module;
  context->start_released_event = start_released_event;
  precreate_diagnostic_phase_events(request.binding, context->phase_events);

  HANDLE worker = CreateThread(nullptr, 0, bootstrap_worker, context.get(), 0,
                               nullptr);
  if (!worker) {
    const HRESULT result = HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(start_released_event);
    FreeLibrary(worker_module);
    return result;
  }
  CloseHandle(worker);
  context.release();
  g_active_request = request;
  g_worker_started = true;
  return S_OK;
}

}  // namespace cq::bridge
