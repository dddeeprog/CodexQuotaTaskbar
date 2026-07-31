#include "xaml_taskbar_probe.h"
#include "xaml_taskbar_probe_test_peer.h"

#include "bridge_runtime.h"
#include "com_ptr.h"
#include "probe_capsule.h"
#include "probe_safety.h"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <ocidl.h>
#include <CoreWindow.h>
#include <xamlOM.h>
#ifdef GetCurrentTime
#undef GetCurrentTime
#endif
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>

#include <array>
#include <atomic>
#include <chrono>
#include <cwchar>
#include <functional>
#include <iterator>
#include <memory>
#include <limits>
#include <mutex>
#include <new>
#include <optional>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

namespace cq::bridge {

ProbeDecision evaluate_visual_node(const VisualNodeFacts& facts) noexcept {
  if (!facts.parent_is_grid ||
      !facts.belongs_to_validated_taskbar_window ||
      facts.type_name.size() > kMaximumVisualTypeNameLength) {
    return ProbeDecision::ignore;
  }

  return std::wstring_view{facts.type_name} == kSystemTrayFrameType
             ? ProbeDecision::accept
             : ProbeDecision::ignore;
}

bool ProbeResourceCounts::empty() const noexcept {
  return callbacks == 0 && pending_actions == 0 && handoffs == 0 &&
         capsules == 0 && timers == 0 && watcher_references == 0 &&
         host_bindings == 0 && com_objects == 0;
}

namespace {

using cq::common::ComPtr;
namespace wf = winrt::Windows::Foundation;
namespace wuc = winrt::Windows::UI::Core;
namespace wux = winrt::Windows::UI::Xaml;
namespace wuxc = winrt::Windows::UI::Xaml::Controls;
namespace wuxm = winrt::Windows::UI::Xaml::Media;

constexpr std::size_t kMaximumTaskbarHosts = 16;
constexpr std::size_t kMaximumCallbacks = 4096;
constexpr std::size_t kMaximumPendingActions = 4096;
constexpr DWORD kThreadHandoffTimeoutMilliseconds = 2000;
constexpr auto kClaimedHandoffDrainTimeout = std::chrono::milliseconds{100};
constexpr wchar_t kHostGenerationProperty[] =
    L"CodexQuotaTaskbar.Probe.HostGeneration.v1";
constexpr wchar_t kHandoffRetirementTokenProperty[] =
    L"CodexQuotaTaskbar.Probe.HandoffRetirementToken.v1";

// Project-local TAP CLSID: {93D08475-FA45-461A-B8A5-9A8D8A9C117A}.
constexpr CLSID kProbeTapClsid = {
    0x93d08475,
    0xfa45,
    0x461a,
    {0xb8, 0xa5, 0x9a, 0x8d, 0x8a, 0x9c, 0x11, 0x7a}};

std::atomic<XamlTaskbarProbe*> g_active_probe{nullptr};
std::recursive_mutex g_tap_object_admission_mutex;
long g_com_object_count = 0;
long g_server_lock_count = 0;
bool g_com_counter_fault = false;
enum class TapAdmissionPhase : std::uint8_t {
  open,
  claimed,
  committed_closed,
};

struct TapAdmissionClaim final {
  std::uintptr_t owner = 0;
  std::uint64_t generation = 0;

  [[nodiscard]] explicit operator bool() const noexcept {
    return owner != 0 && generation != 0;
  }
};

TapAdmissionPhase g_tap_object_admission_phase = TapAdmissionPhase::open;
std::uintptr_t g_tap_claim_owner = 0;
std::uint64_t g_tap_claim_generation = 0;
bool g_tap_claim_had_active_owner = false;
std::uint64_t g_next_tap_claim_generation = 1;
bool g_tap_initialization_in_flight = false;
std::atomic<bool> g_fail_next_tap_reopen_for_test{false};

[[nodiscard]] bool begin_tap_initialization(
    XamlTaskbarProbe* owner) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (!owner || g_tap_object_admission_phase != TapAdmissionPhase::open ||
      g_tap_initialization_in_flight ||
      g_active_probe.load(std::memory_order_acquire) != nullptr) {
    return false;
  }
  g_active_probe.store(owner, std::memory_order_release);
  g_tap_initialization_in_flight = true;
  return true;
}

[[nodiscard]] bool finish_tap_initialization(
    XamlTaskbarProbe* owner,
    bool succeeded) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (!owner || !g_tap_initialization_in_flight ||
      g_tap_object_admission_phase != TapAdmissionPhase::open ||
      g_active_probe.load(std::memory_order_acquire) != owner) {
    return false;
  }
  g_tap_initialization_in_flight = false;
  if (!succeeded) {
    g_active_probe.store(nullptr, std::memory_order_release);
  }
  return true;
}

[[nodiscard]] TapAdmissionClaim claim_tap_object_admission_for_unload(
    std::uintptr_t owner) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  auto* const active = g_active_probe.load(std::memory_order_acquire);
  auto* const requested = reinterpret_cast<XamlTaskbarProbe*>(owner);
  if (!owner || g_tap_object_admission_phase != TapAdmissionPhase::open ||
      g_tap_initialization_in_flight || (active && active != requested) ||
      g_com_counter_fault || g_com_object_count != 0 ||
      g_server_lock_count != 0 || !g_next_tap_claim_generation ||
      g_next_tap_claim_generation ==
          (std::numeric_limits<std::uint64_t>::max)()) {
    return {};
  }
  const auto generation = g_next_tap_claim_generation++;
  g_tap_object_admission_phase = TapAdmissionPhase::claimed;
  g_tap_claim_owner = owner;
  g_tap_claim_generation = generation;
  g_tap_claim_had_active_owner = active == requested;
  return {owner, generation};
}

[[nodiscard]] bool tap_admission_claim_matches(
    const TapAdmissionClaim& claim) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (!claim || g_tap_object_admission_phase != TapAdmissionPhase::claimed ||
      g_tap_claim_owner != claim.owner ||
      g_tap_claim_generation != claim.generation) {
    return false;
  }
  auto* const owner = reinterpret_cast<XamlTaskbarProbe*>(claim.owner);
  auto* const active = g_active_probe.load(std::memory_order_acquire);
  return g_tap_claim_had_active_owner ? active == owner : active == nullptr;
}

[[nodiscard]] bool reopen_tap_object_admission(
    TapAdmissionClaim& claim) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (!claim || g_tap_object_admission_phase != TapAdmissionPhase::claimed ||
      g_tap_claim_owner != claim.owner ||
      g_tap_claim_generation != claim.generation) {
    return false;
  }
  if (g_fail_next_tap_reopen_for_test.exchange(
          false, std::memory_order_acq_rel)) {
    return false;
  }
  g_tap_object_admission_phase = TapAdmissionPhase::open;
  g_tap_claim_owner = 0;
  g_tap_claim_generation = 0;
  g_tap_claim_had_active_owner = false;
  claim = {};
  return true;
}

[[nodiscard]] bool commit_tap_object_admission(
    TapAdmissionClaim& claim) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (!claim || g_tap_object_admission_phase != TapAdmissionPhase::claimed ||
      g_tap_claim_owner != claim.owner ||
      g_tap_claim_generation != claim.generation) {
    return false;
  }
  auto* const owner = reinterpret_cast<XamlTaskbarProbe*>(claim.owner);
  auto* const active = g_active_probe.load(std::memory_order_acquire);
  if ((g_tap_claim_had_active_owner && active != owner) ||
      (!g_tap_claim_had_active_owner && active != nullptr)) {
    return false;
  }
  if (g_tap_claim_had_active_owner) {
    g_active_probe.store(nullptr, std::memory_order_release);
  }
  g_tap_object_admission_phase = TapAdmissionPhase::committed_closed;
  g_tap_claim_owner = 0;
  g_tap_claim_generation = 0;
  g_tap_claim_had_active_owner = false;
  claim = {};
  return true;
}

void commit_tap_object_admission_prevalidated(
    TapAdmissionClaim& claim) noexcept {
  if (commit_tap_object_admission(claim)) {
    return;
  }
  std::lock_guard lock(g_tap_object_admission_mutex);
  g_com_counter_fault = true;
  claim = {};
}

[[nodiscard]] bool add_com_object() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (g_tap_object_admission_phase != TapAdmissionPhase::open ||
      g_com_counter_fault || g_com_object_count < 0 ||
      g_com_object_count == (std::numeric_limits<long>::max)()) {
    g_com_counter_fault = true;
    return false;
  }
  ++g_com_object_count;
  return true;
}

[[nodiscard]] bool remove_com_object() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (g_com_object_count <= 0) {
    g_com_counter_fault = true;
    return false;
  }
  --g_com_object_count;
  return true;
}

[[nodiscard]] bool update_server_lock_count(bool lock_server) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (lock_server) {
    if (g_tap_object_admission_phase != TapAdmissionPhase::open ||
        g_server_lock_count < 0 ||
        g_server_lock_count == (std::numeric_limits<long>::max)()) {
      if (g_server_lock_count < 0 ||
          g_server_lock_count == (std::numeric_limits<long>::max)()) {
        g_com_counter_fault = true;
      }
      return false;
    }
    ++g_server_lock_count;
    return true;
  }
  if (g_server_lock_count <= 0) {
    g_com_counter_fault = true;
    return false;
  }
  --g_server_lock_count;
  return true;
}

[[nodiscard]] long com_object_count_snapshot() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  return g_com_object_count;
}

class ComObjectLifetime {
 public:
  ComObjectLifetime() noexcept : counted_(add_com_object()) {}
  ComObjectLifetime(const ComObjectLifetime&) = delete;
  ComObjectLifetime& operator=(const ComObjectLifetime&) = delete;
  ~ComObjectLifetime() {
    if (counted_) {
      (void)remove_com_object();
    }
  }

  [[nodiscard]] bool valid() const noexcept { return counted_; }

 private:
  bool counted_ = false;
};

struct TaskbarHost final {
  HWND tray_window = nullptr;
  HWND xaml_window = nullptr;
  DWORD process_id = 0;
  DWORD thread_id = 0;
  std::uint64_t generation = 0;
};

[[nodiscard]] bool class_name_is(HWND window, const wchar_t* expected) noexcept {
  std::array<wchar_t, 128> class_name{};
  return GetClassNameW(window, class_name.data(), static_cast<int>(class_name.size())) > 0 &&
         std::wcscmp(class_name.data(), expected) == 0;
}

[[nodiscard]] bool is_taskbar_window(HWND window) noexcept {
  return class_name_is(window, L"Shell_TrayWnd") ||
         class_name_is(window, L"Shell_SecondaryTrayWnd");
}

[[nodiscard]] bool validate_host(const TaskbarHost& host) noexcept {
  if (!IsWindow(host.tray_window) || !IsWindow(host.xaml_window) ||
      !is_taskbar_window(host.tray_window) ||
      !class_name_is(host.xaml_window,
                     L"Windows.UI.Composition.DesktopWindowContentBridge") ||
      !IsChild(host.tray_window, host.xaml_window)) {
    return false;
  }

  DWORD tray_process = 0;
  DWORD xaml_process = 0;
  const DWORD xaml_thread =
      GetWindowThreadProcessId(host.xaml_window, &xaml_process);
  (void)GetWindowThreadProcessId(host.tray_window, &tray_process);
  const bool generation_matches =
      host.generation == 0 ||
      reinterpret_cast<std::uintptr_t>(
          GetPropW(host.xaml_window, kHostGenerationProperty)) ==
          host.generation;
  return generation_matches && tray_process == GetCurrentProcessId() &&
         xaml_process == GetCurrentProcessId() &&
         host.process_id == xaml_process && host.thread_id == xaml_thread;
}

struct ChildInventory final {
  HWND tray = nullptr;
  std::vector<HWND> content_bridges;
  bool overflow = false;
};

BOOL CALLBACK enumerate_content_bridges(HWND window, LPARAM parameter) noexcept {
  auto& inventory = *reinterpret_cast<ChildInventory*>(parameter);
  if (!class_name_is(window,
                     L"Windows.UI.Composition.DesktopWindowContentBridge")) {
    return TRUE;
  }
  DWORD process_id = 0;
  if (!GetWindowThreadProcessId(window, &process_id) ||
      process_id != GetCurrentProcessId() || !IsChild(inventory.tray, window)) {
    return TRUE;
  }
  if (inventory.content_bridges.size() >= kMaximumTaskbarHosts) {
    inventory.overflow = true;
    return FALSE;
  }
  try {
    inventory.content_bridges.push_back(window);
  } catch (...) {
    inventory.overflow = true;
    return FALSE;
  }
  return TRUE;
}

struct HostInventory final {
  std::vector<TaskbarHost> hosts;
  bool invalid = false;
};

BOOL CALLBACK enumerate_taskbars(HWND window, LPARAM parameter) noexcept {
  auto& inventory = *reinterpret_cast<HostInventory*>(parameter);
  if (!is_taskbar_window(window)) {
    return TRUE;
  }
  DWORD tray_process = 0;
  if (!GetWindowThreadProcessId(window, &tray_process) ||
      tray_process != GetCurrentProcessId()) {
    return TRUE;
  }

  ChildInventory children{.tray = window};
  EnumChildWindows(window, enumerate_content_bridges,
                   reinterpret_cast<LPARAM>(&children));
  if (children.overflow || children.content_bridges.empty()) {
    inventory.invalid = true;
    return FALSE;
  }

  for (HWND content_bridge : children.content_bridges) {
    if (inventory.hosts.size() >= kMaximumTaskbarHosts) {
      inventory.invalid = true;
      return FALSE;
    }
    DWORD process_id = 0;
    const DWORD thread_id =
        GetWindowThreadProcessId(content_bridge, &process_id);
    TaskbarHost host{window, content_bridge, process_id, thread_id, 0};
    if (!thread_id || !validate_host(host)) {
      inventory.invalid = true;
      return FALSE;
    }
    const auto duplicate = std::find_if(
        inventory.hosts.begin(), inventory.hosts.end(),
        [content_bridge](const TaskbarHost& candidate) {
          return candidate.xaml_window == content_bridge;
        });
    if (duplicate == inventory.hosts.end()) {
      try {
        inventory.hosts.push_back(host);
      } catch (...) {
        inventory.invalid = true;
        return FALSE;
      }
    }
  }
  return TRUE;
}

[[nodiscard]] std::vector<TaskbarHost> discover_hosts() noexcept {
  HostInventory inventory;
  if (!EnumWindows(enumerate_taskbars, reinterpret_cast<LPARAM>(&inventory)) ||
      inventory.invalid || inventory.hosts.empty()) {
    return {};
  }
  return inventory.hosts;
}

using ThreadAction = HandoffRequest::Action;

LRESULT CALLBACK handoff_hook(int code,
                              WPARAM hook_wparam,
                              LPARAM parameter) noexcept;

struct HandoffContext final {
  HandoffContext(std::uintptr_t value_cookie,
                 TaskbarHost value_host,
                 HandoffRetirementTarget value_target,
                 std::shared_ptr<void> value_owner,
                 ThreadAction action,
                 std::uintptr_t module,
                 HandoffWin32Boundary& value_boundary,
                 bool value_synthetic)
      : cookie(value_cookie),
        host(value_host),
        target(value_target),
        owner(std::move(value_owner)),
        request(std::make_unique<HandoffRequest>(std::move(action))),
        retirement(module, target),
        boundary(&value_boundary),
        synthetic_for_test(value_synthetic) {}

  ~HandoffContext();

  const std::uintptr_t cookie;
  const TaskbarHost host;
  const HandoffRetirementTarget target;
  const std::shared_ptr<void> owner;
  const std::unique_ptr<HandoffRequest> request;
  HandoffRetirement retirement;
  HandoffWin32Boundary* const boundary;
  const bool synthetic_for_test;
  std::uintptr_t native_hook = 0;
  std::atomic<bool> retirement_driver_claimed{true};
};

std::mutex g_handoff_mutex;
std::vector<std::unique_ptr<HandoffContext>> g_handoffs;
std::atomic<std::uintptr_t> g_next_handoff_cookie{1};
std::atomic<HandoffDestructionObserver> g_handoff_destruction_observer{
    nullptr};
std::atomic<void*> g_handoff_destruction_context{nullptr};
std::atomic<std::uint64_t> g_next_host_generation{1};
std::atomic<std::uint64_t> g_next_root_generation{1};
std::atomic<std::uint64_t> g_next_diagnostics_epoch{1};
std::atomic<std::uint64_t> g_next_session_transition{1};
std::atomic<std::uint64_t> g_next_unload_transition{1};

[[nodiscard]] UINT handoff_message() noexcept {
  static const UINT message =
      RegisterWindowMessageW(L"CodexQuotaTaskbar.Probe.ThreadHandoff.v1");
  return message;
}

HandoffContext::~HandoffContext() {
  const auto observer =
      g_handoff_destruction_observer.load(std::memory_order_acquire);
  if (observer) {
    observer(g_handoff_destruction_context.load(std::memory_order_acquire));
  }
}

[[nodiscard]] std::uintptr_t next_handoff_cookie() noexcept {
  auto current = g_next_handoff_cookie.load(std::memory_order_acquire);
  for (;;) {
    if (!current || current ==
                        (std::numeric_limits<std::uintptr_t>::max)()) {
      return 0;
    }
    if (g_next_handoff_cookie.compare_exchange_weak(
            current, current + 1, std::memory_order_acq_rel,
            std::memory_order_acquire)) {
      return current;
    }
  }
}

[[nodiscard]] bool retirement_window_identity_matches(
    const HandoffRetirementTarget& target) noexcept {
  const auto window = reinterpret_cast<HWND>(target.window);
  if (!window || !IsWindow(window)) {
    return false;
  }
  DWORD process_id = 0;
  const DWORD thread_id = GetWindowThreadProcessId(window, &process_id);
  return process_id == target.process_id && thread_id == target.thread_id;
}

[[nodiscard]] bool retirement_target_matches(
    const HandoffRetirementTarget& target) noexcept {
  if (!target.retirement_token || !retirement_window_identity_matches(target)) {
    return false;
  }
  return reinterpret_cast<std::uintptr_t>(GetPropW(
             reinterpret_cast<HWND>(target.window),
             kHandoffRetirementTokenProperty)) == target.retirement_token;
}

class ProductionHandoffWin32Boundary final : public HandoffWin32Boundary {
 public:
  bool acquire_self_module(std::uintptr_t callback_address,
                           std::uintptr_t& module) noexcept override {
    module = 0;
    HMODULE self = nullptr;
    if (!callback_address ||
        !GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(callback_address), &self)) {
      return false;
    }
    module = reinterpret_cast<std::uintptr_t>(self);
    return true;
  }

  bool publish_retirement_token(
      const HandoffRetirementTarget& target) noexcept override {
    const auto window = reinterpret_cast<HWND>(target.window);
    if (!target.retirement_token ||
        !retirement_window_identity_matches(target) ||
        GetPropW(window, kHandoffRetirementTokenProperty) != nullptr) {
      return false;
    }
    return SetPropW(window, kHandoffRetirementTokenProperty,
                    reinterpret_cast<HANDLE>(target.retirement_token)) !=
           FALSE;
  }

  bool retirement_token_matches(
      const HandoffRetirementTarget& target) noexcept override {
    return retirement_target_matches(target);
  }

  std::uintptr_t install_callwndproc(
      std::uintptr_t callback_address,
      std::uint32_t thread_id) noexcept override {
    if (!callback_address || !thread_id) {
      return 0;
    }
    const auto callback = reinterpret_cast<HOOKPROC>(callback_address);
    return reinterpret_cast<std::uintptr_t>(
        SetWindowsHookExW(WH_CALLWNDPROC, callback, nullptr, thread_id));
  }

  bool send_action(const HandoffRetirementTarget& target,
                   std::uint32_t message,
                   std::uintptr_t cookie) noexcept override {
    if (!message || !cookie || !retirement_token_matches(target)) {
      return false;
    }
    DWORD_PTR ignored = 0;
    const LRESULT sent = SendMessageTimeoutW(
        reinterpret_cast<HWND>(target.window), message,
        static_cast<WPARAM>(cookie), 0,
        SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT,
        kThreadHandoffTimeoutMilliseconds, &ignored);
    return sent != 0 && retirement_token_matches(target);
  }

  bool unhook(std::uintptr_t hook) noexcept override {
    return hook &&
           UnhookWindowsHookEx(reinterpret_cast<HHOOK>(hook)) != FALSE;
  }

  bool send_barrier(
      const HandoffRetirementTarget& target) noexcept override {
    if (!retirement_token_matches(target)) {
      return false;
    }
    DWORD_PTR ignored = 0;
    const LRESULT sent = SendMessageTimeoutW(
        reinterpret_cast<HWND>(target.window), WM_NULL, 0, 0,
        SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT,
        kThreadHandoffTimeoutMilliseconds, &ignored);
    return sent != 0 && retirement_token_matches(target);
  }

  HandoffTokenClearResult clear_retirement_token(
      const HandoffRetirementTarget& target) noexcept override {
    const auto window = reinterpret_cast<HWND>(target.window);
    const auto expected = reinterpret_cast<HANDLE>(target.retirement_token);
    if (!target.retirement_token ||
        !retirement_window_identity_matches(target) ||
        GetPropW(window, kHandoffRetirementTokenProperty) != expected) {
      return HandoffTokenClearResult::retryable_failure;
    }
    const auto removed =
        RemovePropW(window, kHandoffRetirementTokenProperty);
    if (removed == expected) {
      return HandoffTokenClearResult::cleared;
    }
    return removed == nullptr
               ? HandoffTokenClearResult::retryable_failure
               : HandoffTokenClearResult::permanently_unsafe;
  }

  void release_module(std::uintptr_t module) noexcept override {
    if (module) {
      (void)FreeLibrary(reinterpret_cast<HMODULE>(module));
    }
  }
};

[[nodiscard]] HandoffWin32Boundary& production_handoff_boundary() noexcept {
  static ProductionHandoffWin32Boundary boundary;
  return boundary;
}

void pending_action_module_anchor() noexcept {}

class ProductionPendingModuleBoundary final : public PendingModuleBoundary {
 public:
  bool acquire_self_module(std::uintptr_t& module) noexcept override {
    module = 0;
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(&pending_action_module_anchor),
            &self)) {
      return false;
    }
    module = reinterpret_cast<std::uintptr_t>(self);
    return true;
  }

  void release_module(std::uintptr_t module) noexcept override {
    if (module) {
      (void)FreeLibrary(reinterpret_cast<HMODULE>(module));
    }
  }
};

[[nodiscard]] PendingModuleBoundary& production_pending_module_boundary()
    noexcept {
  static ProductionPendingModuleBoundary boundary;
  return boundary;
}

[[nodiscard]] PendingAsyncStatus map_winrt_pending_status(
    wf::AsyncStatus status) noexcept {
  switch (status) {
    case wf::AsyncStatus::Started:
      return PendingAsyncStatus::started;
    case wf::AsyncStatus::Completed:
      return PendingAsyncStatus::completed;
    case wf::AsyncStatus::Canceled:
      return PendingAsyncStatus::canceled;
    case wf::AsyncStatus::Error:
      return PendingAsyncStatus::error;
  }
  return PendingAsyncStatus::unknown;
}

class WinrtPendingAsyncAction final : public PendingAsyncAction {
 public:
  explicit WinrtPendingAsyncAction(wf::IAsyncAction action) noexcept
      : action_(std::move(action)) {}

  PendingAsyncStatus status() override {
    return map_winrt_pending_status(action_.Status());
  }

  void cancel() override { action_.Cancel(); }

 private:
  wf::IAsyncAction action_{nullptr};
};

class CoreDispatcherPendingBoundary final : public PendingDispatchBoundary {
 public:
  explicit CoreDispatcherPendingBoundary(
      wuc::CoreDispatcher dispatcher) noexcept
      : dispatcher_(std::move(dispatcher)) {}

  std::shared_ptr<PendingAsyncAction> queue(
      std::shared_ptr<PendingDispatchCallback> callback) override {
    auto action = dispatcher_.RunAsync(
        wuc::CoreDispatcherPriority::Low,
        wuc::DispatchedHandler([callback = std::move(callback)]() noexcept {
          try {
            if (callback) {
              (*callback)();
            }
          } catch (...) {
          }
        }));
    if (!action) {
      return {};
    }
    // If allocation fails after RunAsync retained the delegate, queue() throws
    // and the ledger deliberately records an unknown outcome forever.
    return std::make_shared<WinrtPendingAsyncAction>(std::move(action));
  }

 private:
  wuc::CoreDispatcher dispatcher_{nullptr};
};

struct EnteredHandoffFrame final {
  HandoffContext* context = nullptr;
  bool counted = false;
};

[[nodiscard]] EnteredHandoffFrame enter_handoff_frame(
    DWORD thread_id) noexcept {
  std::lock_guard lock(g_handoff_mutex);
  const auto found = std::find_if(
      g_handoffs.begin(), g_handoffs.end(),
      [thread_id](const auto& candidate) {
        return candidate && candidate->target.thread_id == thread_id;
      });
  if (found == g_handoffs.end()) {
    return {};
  }
  auto* context = found->get();
  return {context, context->retirement.enter_hook_frame()};
}

void run_handoff_action(HandoffContext& context) noexcept {
  if (context.request && context.request->try_claim()) {
    context.request->run_claimed_action();
  }
}

LRESULT CALLBACK handoff_hook(int code,
                              WPARAM hook_wparam,
                              LPARAM parameter) noexcept {
  const DWORD current_thread = GetCurrentThreadId();
  const DWORD current_process = GetCurrentProcessId();
  const auto frame = enter_handoff_frame(current_thread);
  if (code == HC_ACTION && parameter) {
    const auto* message = reinterpret_cast<const CWPSTRUCT*>(parameter);
    if (frame.context && message->message == handoff_message()) {
      const auto cookie = static_cast<std::uintptr_t>(message->wParam);
      if (frame.context->cookie == cookie && frame.context->request &&
          frame.context->host.xaml_window == message->hwnd &&
          frame.context->host.process_id == current_process &&
          frame.context->host.thread_id == current_thread &&
          validate_host(frame.context->host) && frame.context->boundary &&
          frame.context->boundary->retirement_token_matches(
              frame.context->target)) {
        run_handoff_action(*frame.context);
      }
    }
  }
  const LRESULT result =
      CallNextHookEx(nullptr, code, hook_wparam, parameter);
  if (frame.context && frame.counted) {
    frame.context->retirement.exit_hook_frame();
  }
  return result;
}

enum class RegisteredRetirementResult : std::uint8_t {
  retired,
  retryable_unsafe,
  permanently_unsafe,
  busy,
  missing,
};

[[nodiscard]] RegisteredRetirementResult drive_handoff_retirement(
    std::uintptr_t cookie) noexcept {
  HandoffContext* context = nullptr;
  {
    std::lock_guard lock(g_handoff_mutex);
    const auto found = std::find_if(
        g_handoffs.begin(), g_handoffs.end(),
        [cookie](const auto& candidate) {
          return candidate && candidate->cookie == cookie;
        });
    if (found == g_handoffs.end()) {
      return RegisteredRetirementResult::missing;
    }
    context = found->get();
  }

  const auto attempt = context->retirement.attempt(*context->boundary);
  if (attempt != HandoffRetirementAttempt::ready_to_detach) {
    context->retirement_driver_claimed.store(false,
                                             std::memory_order_release);
    return attempt == HandoffRetirementAttempt::permanently_unsafe
               ? RegisteredRetirementResult::permanently_unsafe
               : RegisteredRetirementResult::retryable_unsafe;
  }

  std::optional<HandoffModuleRelease> module_release;
  std::unique_ptr<HandoffContext> removed;
  HandoffWin32Boundary* boundary = nullptr;
  {
    std::lock_guard lock(g_handoff_mutex);
    const auto found = std::find_if(
        g_handoffs.begin(), g_handoffs.end(),
        [cookie, context](const auto& candidate) {
          return candidate && candidate.get() == context &&
                 candidate->cookie == cookie;
        });
    if (found != g_handoffs.end()) {
      module_release = context->retirement.detach_ready();
      if (module_release) {
        boundary = context->boundary;
        removed = std::move(*found);
        g_handoffs.erase(found);
      }
    }
  }
  if (!module_release || !removed || !boundary) {
    context->retirement_driver_claimed.store(false,
                                             std::memory_order_release);
    return RegisteredRetirementResult::retryable_unsafe;
  }
  removed.reset();
  (void)module_release->release(*boundary);
  return RegisteredRetirementResult::retired;
}

[[nodiscard]] bool reap_one_handoff(void* owner) noexcept {
  std::uintptr_t cookie = 0;
  {
    std::lock_guard lock(g_handoff_mutex);
    const auto found = std::find_if(
        g_handoffs.begin(), g_handoffs.end(),
        [owner](const auto& candidate) {
          if (!candidate || candidate->owner.get() != owner) {
            return false;
          }
          bool expected = false;
          return candidate->retirement_driver_claimed.compare_exchange_strong(
              expected, true, std::memory_order_acq_rel,
              std::memory_order_acquire);
        });
    if (found == g_handoffs.end()) {
      return false;
    }
    cookie = (*found)->cookie;
  }
  (void)drive_handoff_retirement(cookie);
  return true;
}

[[nodiscard]] bool detach_handoff_without_published_token(
    std::uintptr_t cookie) noexcept {
  std::optional<HandoffModuleRelease> module_release;
  std::unique_ptr<HandoffContext> removed;
  HandoffWin32Boundary* boundary = nullptr;
  {
    std::lock_guard lock(g_handoff_mutex);
    const auto found = std::find_if(
        g_handoffs.begin(), g_handoffs.end(),
        [cookie](const auto& candidate) {
          return candidate && candidate->cookie == cookie;
        });
    if (found == g_handoffs.end()) {
      return false;
    }
    module_release =
        (*found)->retirement.detach_without_published_token();
    if (!module_release) {
      return false;
    }
    boundary = (*found)->boundary;
    removed = std::move(*found);
    g_handoffs.erase(found);
  }
  removed.reset();
  (void)module_release->release(*boundary);
  return true;
}

[[nodiscard]] HandoffResult run_prevalidated_handoff(
    const TaskbarHost& host,
    HandoffRetirementTarget target,
    std::shared_ptr<void> owner,
    const ThreadAction& action,
    HandoffWin32Boundary& boundary,
    bool synthetic_for_test) noexcept {
  const UINT message = handoff_message();
  const auto callback_address =
      reinterpret_cast<std::uintptr_t>(&handoff_hook);
  if (!action || !owner || !message || !callback_address || !target.window ||
      !target.process_id || !target.thread_id) {
    return HandoffResult::action_failed;
  }

  std::uintptr_t module = 0;
  if (!boundary.acquire_self_module(callback_address, module) || !module) {
    return HandoffResult::action_failed;
  }

  const auto cookie = next_handoff_cookie();
  std::unique_ptr<HandoffContext> pending;
  try {
    if (!cookie) {
      throw std::bad_alloc{};
    }
    target.retirement_token = cookie;
    pending = std::make_unique<HandoffContext>(
        cookie, host, target, std::move(owner), action, module, boundary,
        synthetic_for_test);
  } catch (...) {
    pending.reset();
    boundary.release_module(module);
    return HandoffResult::action_failed;
  }

  HandoffContext* context = nullptr;
  bool thread_busy = false;
  try {
    std::lock_guard lock(g_handoff_mutex);
    thread_busy = std::any_of(
        g_handoffs.begin(), g_handoffs.end(),
        [&target](const auto& candidate) {
          return candidate && candidate->target.thread_id == target.thread_id;
        });
    if (!thread_busy) {
      g_handoffs.reserve(g_handoffs.size() + 1);
      context = pending.get();
      g_handoffs.push_back(std::move(pending));
    }
  } catch (...) {
  }
  if (!context) {
    pending.reset();
    boundary.release_module(module);
    return thread_busy ? HandoffResult::timed_out_in_flight
                       : HandoffResult::action_failed;
  }

  if (!boundary.publish_retirement_token(target)) {
    if (detach_handoff_without_published_token(cookie)) {
      return HandoffResult::action_failed;
    }
    context->retirement_driver_claimed.store(false,
                                             std::memory_order_release);
    return HandoffResult::timed_out_in_flight;
  }
  if (!context->retirement.mark_token_published()) {
    context->retirement_driver_claimed.store(false,
                                             std::memory_order_release);
    return HandoffResult::timed_out_in_flight;
  }

  const auto hook =
      boundary.install_callwndproc(callback_address, target.thread_id);
  if (!hook) {
    if (!context->retirement.mark_hook_install_failed()) {
      context->retirement_driver_claimed.store(false,
                                               std::memory_order_release);
      return HandoffResult::timed_out_in_flight;
    }
    return drive_handoff_retirement(cookie) ==
                   RegisteredRetirementResult::retired
               ? HandoffResult::action_failed
               : HandoffResult::timed_out_in_flight;
  }
  {
    std::lock_guard lock(g_handoff_mutex);
    context->native_hook = hook;
    if (!context->retirement.attach_hook(hook)) {
      context->retirement_driver_claimed.store(false,
                                               std::memory_order_release);
      return HandoffResult::timed_out_in_flight;
    }
  }

  const bool sent = boundary.send_action(target, message, cookie);
  const auto action_result = context->request->wait_for(
      sent ? kClaimedHandoffDrainTimeout : std::chrono::milliseconds{0});
  const auto retirement_result = drive_handoff_retirement(cookie);
  if (retirement_result != RegisteredRetirementResult::retired) {
    return HandoffResult::timed_out_in_flight;
  }
  return action_result;
}

[[nodiscard]] HandoffResult run_on_host_thread(const TaskbarHost& host,
                                               std::shared_ptr<void> owner,
                                               const ThreadAction& action) noexcept {
  if (!action || !validate_host(host)) {
    return HandoffResult::action_failed;
  }
  if (host.thread_id == GetCurrentThreadId()) {
    try {
      action();
      return HandoffResult::completed;
    } catch (...) {
      return HandoffResult::action_failed;
    }
  }

  const HandoffRetirementTarget target{
      .window = reinterpret_cast<std::uintptr_t>(host.xaml_window),
      .process_id = host.process_id,
      .thread_id = host.thread_id,
      .retirement_token = 0,
  };
  return run_prevalidated_handoff(host, target, std::move(owner), action,
                                  production_handoff_boundary(), false);
}

[[nodiscard]] std::size_t outstanding_handoffs(void* owner) noexcept {
  (void)reap_one_handoff(owner);
  std::lock_guard lock(g_handoff_mutex);
  return static_cast<std::size_t>(std::count_if(
      g_handoffs.begin(), g_handoffs.end(),
      [owner](const auto& item) {
        return item && item->owner.get() == owner;
      }));
}

}  // namespace

struct XamlTaskbarProbe::Impl final
    : public std::enable_shared_from_this<XamlTaskbarProbe::Impl> {
  class XamlDiagnosticsQueryAdapter final : public DiagnosticsQueryAdapter {
   public:
    explicit XamlDiagnosticsQueryAdapter(
        ComPtr<IXamlDiagnostics> diagnostics) noexcept
        : diagnostics_(std::move(diagnostics)) {}

    bool get_inspectable(std::uint64_t handle,
                         void** object) noexcept override {
      if (!object) {
        return false;
      }
      *object = nullptr;
      return diagnostics_ &&
             SUCCEEDED(diagnostics_->GetIInspectableFromHandle(
                 handle, reinterpret_cast<IInspectable**>(object)));
    }

    bool get_handle(void* object, std::uint64_t* handle) noexcept override {
      if (!object || !handle) {
        return false;
      }
      *handle = 0;
      return diagnostics_ &&
             SUCCEEDED(diagnostics_->GetHandleFromIInspectable(
                 static_cast<IInspectable*>(object), handle));
    }

    bool inspect_xaml(void* object) noexcept override {
      return object != nullptr;
    }

   private:
    const ComPtr<IXamlDiagnostics> diagnostics_;
  };

  class Watcher final : public IVisualTreeServiceCallback2 {
   public:
    Watcher(std::weak_ptr<Impl> owner,
            DiagnosticsSessionIdentity session_identity) noexcept
        : owner_(std::move(owner)), session_identity_(session_identity) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interface_id,
                                             void** object) noexcept override {
      if (!object) {
        return E_POINTER;
      }
      *object = nullptr;
      if (interface_id == __uuidof(IUnknown) ||
          interface_id == __uuidof(IVisualTreeServiceCallback) ||
          interface_id == __uuidof(IVisualTreeServiceCallback2)) {
        *object = static_cast<IVisualTreeServiceCallback2*>(this);
        AddRef();
        return S_OK;
      }
      return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override {
      return references_.fetch_add(1) + 1;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override {
      const ULONG references = references_.fetch_sub(1) - 1;
      if (!references) {
        delete this;
      }
      return references;
    }

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation relation,
        VisualElement element,
        VisualMutationType mutation_type) noexcept override {
      if (const auto owner = owner_.lock()) {
        owner->on_visual_tree_change(session_identity_, relation, element,
                                     mutation_type);
      }
      return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle,
        VisualElementState,
        LPCWSTR) noexcept override {
      return S_OK;
    }

   private:
    ~Watcher() {
      if (const auto owner = owner_.lock()) {
        owner->notify_watcher_destruction_for_test();
      }
    }
    ComObjectLifetime lifetime_;
    std::atomic<ULONG> references_{1};
    std::weak_ptr<Impl> owner_;
    const DiagnosticsSessionIdentity session_identity_;
  };

  class CallbackAddOperation final : public DiagnosticsCallbackAddAdapter {
   public:
    CallbackAddOperation(Impl& owner,
                         ParentChildRelation relation,
                         VisualElement visual) noexcept
        : owner_(owner), relation_(relation), visual_(visual) {}

    bool process_add(DiagnosticsSessionGate::Lease& session) noexcept override {
      return owner_.process_add_with_session(session, relation_, visual_);
    }

   private:
    Impl& owner_;
    const ParentChildRelation relation_;
    const VisualElement visual_;
  };

  struct BoundRoot final {
    TaskbarHost host;
    wux::XamlRoot xaml_root{nullptr};
    wux::UIElement content{nullptr};
    std::uintptr_t xaml_root_identity = 0;
    std::uintptr_t content_identity = 0;
    std::uint64_t root_generation = 0;
    std::uint64_t diagnostics_epoch = 0;
    std::shared_ptr<CleanupAuthorityGate> cleanup_authority;
  };

  struct HostToken final {
    TaskbarHost host;
    wux::XamlRoot xaml_root{nullptr};
    wux::UIElement content{nullptr};
    std::uintptr_t xaml_root_identity = 0;
    std::uintptr_t content_identity = 0;
    std::uint64_t root_generation = 0;
    std::uint64_t diagnostics_epoch = 0;
    std::shared_ptr<CleanupAuthorityGate> cleanup_authority;
  };

  enum class SessionState : std::uint8_t {
    empty,
    initial_site,
    current_unwatched,
    current,
    closing,
    prepared,
    watcher_needed,
    advise_in_progress,
    advised_not_published,
    quiesced,
    unloading,
    unsafe_retained,
  };

  enum class TransitionKind : std::uint8_t {
    replace,
    remove,
    initial_advise,
    unadvise,
    unload,
  };

  enum class TransitionStage : std::uint8_t {
    close_admissions,
    cleanup_capsules,
    retire_roots,
    unadvise_old,
    prepare_target,
    advise_target,
    publish_target,
    complete,
  };

  struct SessionResources final {
    ComPtr<IUnknown> site;
    ComPtr<IXamlDiagnostics> diagnostics;
    ComPtr<IVisualTreeService> visual_service;
    std::shared_ptr<XamlDiagnosticsQueryAdapter> adapter;
    DiagnosticsSessionIdentity identity{};
    ComPtr<IVisualTreeService> advised_visual_service;
    ComPtr<IVisualTreeServiceCallback2> watcher;

    [[nodiscard]] bool same_target(
        std::uintptr_t site_identity,
        std::uintptr_t diagnostics_identity) const noexcept {
      return identity.site_identity == site_identity &&
             identity.diagnostics_identity == diagnostics_identity;
    }
  };

  struct RootTransitionEntry final {
    std::shared_ptr<CleanupAuthorityGate> authority;
    std::optional<CleanupAuthorityGate::TransitionPermit> permit;
    std::vector<TaskbarHost> hosts;
    std::size_t retired_host_count = 0;
    bool includes_synthetic_root = false;
    bool cleanup_lane_closed = false;
    bool retired = false;
  };

  enum class TransitionWorkResult : std::uint8_t {
    complete,
    busy,
    failed,
  };

  struct SessionTransition final {
    std::uint64_t token = 0;
    TransitionKind kind = TransitionKind::replace;
    TransitionStage stage = TransitionStage::close_admissions;
    std::shared_ptr<SessionResources> old_resources;
    std::shared_ptr<SessionResources> target_resources;
    std::vector<RootTransitionEntry> roots;
    IUnknown* requested_site_argument = nullptr;
    bool watcher_required = false;
    bool old_watcher_unadvised = false;
    bool target_watcher_advised = false;
    bool old_session_detached = false;
    bool root_transition_started = false;
    bool driver_active = false;
  };

  struct SitePreparation final {
    std::uint64_t token = 0;
    IUnknown* requested_site_argument = nullptr;
    SessionState prior_state = SessionState::empty;
    std::shared_ptr<SessionResources> base_resources;
  };

  struct UnloadTransition final {
    std::uint64_t token = 0;
    SessionState prior_state = SessionState::empty;
    std::shared_ptr<SessionResources> session;
    std::shared_ptr<SessionTransition> quiesced;
    bool callback_was_accepting = false;
    bool pending_was_accepting = false;
    bool root_was_accepting = false;
    TapAdmissionClaim tap_claim{};
    bool driver_active = false;
  };

  struct SyntheticRetainedRoot final {
    std::shared_ptr<CleanupAuthorityGate> authority;
    std::function<bool()> cleanup;
    bool cleaned = false;
  };

  Impl(void* module_value, BridgeLifecycle& lifecycle_value) noexcept
      : module(static_cast<HMODULE>(module_value)),
        lifecycle(lifecycle_value),
        diagnostics_pipeline(diagnostics_session) {
    callback_admission.close();
  }

  ~Impl() = default;

  template <typename T>
  [[nodiscard]] static std::uintptr_t object_identity(
      const T& object) noexcept {
    if (!object) {
      return 0;
    }
    ComPtr<IUnknown> identity;
    auto* inspectable =
        reinterpret_cast<IInspectable*>(winrt::get_abi(object));
    if (!inspectable ||
        FAILED(inspectable->QueryInterface(__uuidof(IUnknown),
                                           identity.put_void()))) {
      return 0;
    }
    return reinterpret_cast<std::uintptr_t>(identity.get());
  }

  [[nodiscard]] static std::uintptr_t com_identity(
      IUnknown* object) noexcept {
    if (!object) {
      return 0;
    }
    ComPtr<IUnknown> identity;
    if (FAILED(object->QueryInterface(__uuidof(IUnknown),
                                      identity.put_void()))) {
      return 0;
    }
    return reinterpret_cast<std::uintptr_t>(identity.get());
  }

  [[nodiscard]] bool activate_diagnostics_session(
      std::uint64_t epoch) noexcept {
    if (!epoch) {
      return false;
    }
    std::shared_ptr<SessionResources> base;
    std::shared_ptr<SessionResources> activated;
    std::uint64_t preparation_token = 0;
    bool gate_installed = false;
    const auto rollback = [&]() noexcept {
      std::optional<DiagnosticsSessionGate::DetachedSession> detached;
      if (gate_installed && activated) {
        (void)diagnostics_session.close();
        detached = diagnostics_session.detach_closed(activated->identity);
      }
      std::lock_guard lock(diagnostics_mutex);
      if (activation_preparation_token == preparation_token &&
          session_state == SessionState::prepared && !site_preparation &&
          !session_transition && current_session == base) {
        activation_preparation_token = 0;
        session_state = SessionState::initial_site;
      }
    };
    try {
      {
        std::lock_guard lock(diagnostics_mutex);
        if (session_state != SessionState::initial_site ||
            site_preparation || session_transition || !current_session ||
            !current_session->site || !current_session->diagnostics) {
          return false;
        }
        preparation_token =
            g_next_session_transition.fetch_add(1, std::memory_order_relaxed);
        if (!preparation_token) {
          return false;
        }
        activation_preparation_token = preparation_token;
        session_state = SessionState::prepared;
        base = current_session;
      }

      unsigned int expected_allocation_stage = 1;
      if (fail_next_activation_allocation_for_test.compare_exchange_strong(
              expected_allocation_stage, 0, std::memory_order_acq_rel)) {
        throw std::bad_alloc{};
      }
      activated = std::make_shared<SessionResources>(*base);
      activated->identity.epoch = epoch;
      if (!activated->identity.site_identity ||
          !activated->identity.diagnostics_identity) {
        rollback();
        return false;
      }
      if (!activated->adapter) {
        activated->adapter =
            std::make_shared<XamlDiagnosticsQueryAdapter>(
                activated->diagnostics);
      }
      expected_allocation_stage = 2;
      if (fail_next_activation_allocation_for_test.compare_exchange_strong(
              expected_allocation_stage, 0, std::memory_order_acq_rel)) {
        throw std::bad_alloc{};
      }
      if (!diagnostics_session.reset_if_drained() ||
          !diagnostics_session.install(activated->identity,
                                       activated->adapter)) {
        rollback();
        return false;
      }
      gate_installed = true;

      if (fail_next_activation_publication_for_test.exchange(
              false, std::memory_order_acq_rel)) {
        throw std::bad_alloc{};
      }

      std::shared_ptr<SessionResources> retired;
      bool publication_failed = false;
      {
        std::lock_guard lock(diagnostics_mutex);
        if (activation_preparation_token != preparation_token ||
            session_state != SessionState::prepared || site_preparation ||
            session_transition || current_session != base) {
          publication_failed = true;
        } else {
          retired = std::move(current_session);
          current_session = activated;
          activation_preparation_token = 0;
          session_state = SessionState::current_unwatched;
        }
      }
      if (publication_failed) {
        rollback();
        return false;
      }
      diagnostics_epoch.store(epoch, std::memory_order_release);
      diagnostics_initialized.store(true, std::memory_order_release);
      return true;
    } catch (...) {
      rollback();
      return false;
    }
  }

  [[nodiscard]] static bool is_descendant_of(
      const wux::DependencyObject& element,
      const wux::UIElement& expected_root) noexcept {
    try {
      const auto expected_identity = object_identity(expected_root);
      if (!expected_identity) {
        return false;
      }
      wux::DependencyObject current = element;
      for (std::size_t depth = 0; current && depth < 512; ++depth) {
        const auto current_identity = object_identity(current);
        if (!current_identity) {
          return false;
        }
        if (current_identity == expected_identity) {
          return true;
        }
        current = wuxm::VisualTreeHelper::GetParent(current);
      }
    } catch (...) {
    }
    return false;
  }

  [[nodiscard]] static bool capture_identity_path(
      const wux::DependencyObject& element,
      const wux::UIElement& expected_content,
      CleanupIdentityPath& output,
      std::vector<wux::DependencyObject>* retained = nullptr) noexcept {
    output = {};
    if (retained) {
      retained->clear();
    }
    try {
      const auto content_identity = object_identity(expected_content);
      if (!element || !expected_content || !content_identity) {
        return false;
      }
      wux::DependencyObject current = element;
      while (current && output.count < output.identities.size()) {
        const auto identity = object_identity(current);
        if (!identity) {
          return false;
        }
        for (std::size_t index = 0; index < output.count; ++index) {
          if (output.identities[index] == identity) {
            return false;
          }
        }
        if (retained) {
          retained->push_back(current);
        }
        output.identities[output.count++] = identity;
        if (identity == content_identity) {
          output.complete = true;
          return true;
        }
        current = wuxm::VisualTreeHelper::GetParent(current);
      }
    } catch (...) {
    }
    output.complete = false;
    return false;
  }

  [[nodiscard]] static bool same_host_token(const HostToken& left,
                                            const HostToken& right) noexcept {
    return left.host.xaml_window == right.host.xaml_window &&
           left.host.thread_id == right.host.thread_id &&
           left.host.generation == right.host.generation &&
           left.xaml_root_identity == right.xaml_root_identity &&
           left.content_identity == right.content_identity &&
           left.root_generation == right.root_generation &&
           left.diagnostics_epoch == right.diagnostics_epoch &&
           left.cleanup_authority &&
           left.cleanup_authority == right.cleanup_authority;
  }

  class CapsuleEvidenceSource final
      : public CleanupAuthorizationEvidenceSource {
   public:
    CapsuleEvidenceSource(Impl& owner,
                          HostToken token,
                          wux::FrameworkElement anchor,
                          wuxc::Grid grid,
                          CleanupIdentityPath retained_identity_path,
                          std::vector<wux::DependencyObject> retained_path)
        noexcept
        : owner_(owner),
          token_(std::move(token)),
          anchor_(std::move(anchor)),
          grid_(std::move(grid)),
          retained_identity_path_(retained_identity_path) {
      try {
        std::vector<CleanupRetainedIdentity> owners;
        owners.reserve(retained_path.size());
        for (auto& element : retained_path) {
          const auto identity = object_identity(element);
          auto retained =
              std::make_shared<wux::DependencyObject>(std::move(element));
          owners.push_back({identity, std::move(retained)});
        }
        (void)retained_path_.initialize(retained_identity_path_,
                                        std::move(owners));
      } catch (...) {
      }
    }

    bool resolve(CapsuleCleanupTrigger trigger,
                 CleanupAuthorizationEvidence& evidence) noexcept override {
      if (!retained_path_.matches(retained_identity_path_)) {
        return false;
      }
      return owner_.resolve_cleanup_evidence(trigger, token_, anchor_, grid_,
                                             evidence);
    }

   private:
    Impl& owner_;
    const HostToken token_;
    const wux::FrameworkElement anchor_;
    const wuxc::Grid grid_;
    const CleanupIdentityPath retained_identity_path_;
    CleanupAncestryRetention retained_path_;
  };

  [[nodiscard]] bool session_accepts_forward_work(
      std::uint64_t epoch) const noexcept {
    std::lock_guard lock(diagnostics_mutex);
    return (session_state == SessionState::current ||
            session_state == SessionState::current_unwatched) &&
           current_session && current_session->identity.epoch == epoch;
  }

  [[nodiscard]] bool bind_prevalidated_host_root(
      const TaskbarHost& host) noexcept {
    auto binding_admission = root_binding_admission.try_acquire();
    const auto session_epoch =
        diagnostics_epoch.load(std::memory_order_acquire);
    if (!binding_admission ||
        !diagnostics_initialized.load(std::memory_order_acquire) ||
        !session_epoch || GetCurrentThreadId() != host.thread_id ||
        !session_accepts_forward_work(session_epoch) ||
        host.generation == 0 ||
        GetPropW(host.xaml_window, kHostGenerationProperty) != nullptr ||
        !SetPropW(host.xaml_window, kHostGenerationProperty,
                  reinterpret_cast<HANDLE>(
                      static_cast<std::uintptr_t>(host.generation))) ||
        !validate_host(host)) {
      return false;
    }

    try {
      const auto window = wux::Window::Current();
      if (!window) {
        RemovePropW(host.xaml_window, kHostGenerationProperty);
        return false;
      }
      const auto core_window = window.CoreWindow();
      ComPtr<ICoreWindowInterop> interop;
      HWND core_window_handle = nullptr;
      if (!core_window ||
          FAILED(reinterpret_cast<IInspectable*>(winrt::get_abi(core_window))
                     ->QueryInterface(__uuidof(ICoreWindowInterop),
                                      interop.put_void())) ||
          FAILED(interop->get_WindowHandle(&core_window_handle)) ||
          core_window_handle != host.xaml_window) {
        RemovePropW(host.xaml_window, kHostGenerationProperty);
        return false;
      }

      const auto content = window.Content();
      const auto xaml_root = content ? content.XamlRoot() : nullptr;
      const auto root_content = xaml_root ? xaml_root.Content() : nullptr;
      const auto xaml_root_identity = object_identity(xaml_root);
      const auto content_identity = object_identity(root_content);
      const auto window_content_identity = object_identity(content);
      const auto root_generation =
          g_next_root_generation.fetch_add(1, std::memory_order_relaxed);
      if (!root_content || !xaml_root_identity || !content_identity ||
          content_identity != window_content_identity || !root_generation ||
          !is_descendant_of(content, root_content)) {
        RemovePropW(host.xaml_window, kHostGenerationProperty);
        return false;
      }

      auto cleanup_authority = std::make_shared<CleanupAuthorityGate>();
      if (!cleanup_authority->available()) {
        RemovePropW(host.xaml_window, kHostGenerationProperty);
        return false;
      }

      try {
        if (!session_accepts_forward_work(session_epoch)) {
          RemovePropW(host.xaml_window, kHostGenerationProperty);
          return false;
        }
        std::lock_guard lock(host_mutex);
        if (!root_bindings.bind(
                {reinterpret_cast<std::uintptr_t>(host.xaml_window),
                 host.thread_id, xaml_root_identity, content_identity,
                 host.generation, root_generation, session_epoch})) {
          RemovePropW(host.xaml_window, kHostGenerationProperty);
          return false;
        }
        bound_roots.push_back({host, xaml_root, root_content,
                               xaml_root_identity, content_identity,
                               root_generation, session_epoch,
                               std::move(cleanup_authority)});
      } catch (...) {
        (void)root_bindings.invalidate(
            reinterpret_cast<std::uintptr_t>(host.xaml_window),
            host.generation, root_generation, session_epoch);
        RemovePropW(host.xaml_window, kHostGenerationProperty);
        return false;
      }
      return validate_host(host);
    } catch (...) {
      RemovePropW(host.xaml_window, kHostGenerationProperty);
      return false;
    }
  }

  [[nodiscard]] std::optional<HostToken> exact_host_for(
      const wux::DependencyObject& element) noexcept {
    try {
      std::vector<BoundRoot> owner_bindings;
      {
        std::lock_guard lock(host_mutex);
        for (const auto& binding : bound_roots) {
          if (binding.host.thread_id == GetCurrentThreadId()) {
            owner_bindings.push_back(binding);
          }
        }
      }
      const auto ui_element = element.try_as<wux::UIElement>();
      const auto xaml_root = ui_element ? ui_element.XamlRoot() : nullptr;
      const auto callback_root = xaml_root ? xaml_root.Content() : nullptr;
      const auto xaml_root_identity = object_identity(xaml_root);
      const auto content_identity = object_identity(callback_root);
      if (!callback_root || !xaml_root_identity || !content_identity ||
          !is_descendant_of(element, callback_root)) {
        for (const auto& binding : owner_bindings) {
          (void)binding_is_current(binding);
        }
        return std::nullopt;
      }

      std::optional<BoundRoot> candidate;
      for (const auto& binding : owner_bindings) {
        if (binding.xaml_root_identity != xaml_root_identity ||
            binding.content_identity != content_identity) {
          continue;
        }
        if (candidate) {
          return std::nullopt;
        }
        candidate = binding;
      }
      if (!candidate) {
        for (const auto& binding : owner_bindings) {
          (void)binding_is_current(binding);
        }
        return std::nullopt;
      }
      if (!binding_is_current(*candidate) ||
          root_bindings.match(
              {reinterpret_cast<std::uintptr_t>(candidate->host.xaml_window),
               GetCurrentThreadId(), xaml_root_identity, content_identity,
               candidate->host.generation, candidate->root_generation,
               candidate->diagnostics_epoch,
               validate_host(candidate->host)}) != RootMatchResult::exact ||
          !is_descendant_of(element, candidate->content)) {
        return std::nullopt;
      }
      return HostToken{candidate->host, candidate->xaml_root,
                        candidate->content, xaml_root_identity,
                        content_identity, candidate->root_generation,
                        candidate->diagnostics_epoch,
                        candidate->cleanup_authority};
    } catch (...) {
      return std::nullopt;
    }
  }

  [[nodiscard]] bool build_cleanup_binding(
      const HostToken& token,
      const wux::FrameworkElement& anchor,
      const wuxc::Grid& grid,
      CleanupAuthorizationBinding& binding,
      std::vector<wux::DependencyObject>& retained_path) noexcept {
    binding = {};
    retained_path.clear();
    try {
      if (GetCurrentThreadId() != token.host.thread_id ||
          !token.cleanup_authority || !token_is_current(token, anchor) ||
          !token_is_current(token, grid)) {
        return false;
      }
      const auto xaml_root = anchor.XamlRoot();
      const auto content = xaml_root ? xaml_root.Content() : nullptr;
      CleanupIdentityPath path;
      if (!content ||
          !capture_identity_path(anchor, content, path, &retained_path) ||
          retained_path.size() != path.count ||
          object_identity(token.xaml_root) != token.xaml_root_identity ||
          object_identity(token.content) != token.content_identity) {
        return false;
      }
      binding = {
          .record_key = 0,
          .owner_thread_id = token.host.thread_id,
          .host_identity =
              reinterpret_cast<std::uintptr_t>(token.host.xaml_window),
          .xaml_root_identity = token.xaml_root_identity,
          .content_identity = token.content_identity,
          .host_generation = token.host.generation,
          .root_generation = token.root_generation,
          .diagnostics_epoch = token.diagnostics_epoch,
          .anchor_identity = object_identity(anchor),
          .grid_identity = object_identity(grid),
          .anchor_to_content = path,
      };
      return binding.anchor_identity && binding.grid_identity &&
             path.identities[0] == binding.anchor_identity;
    } catch (...) {
      return false;
    }
  }

  [[nodiscard]] bool resolve_cleanup_evidence(
      CapsuleCleanupTrigger trigger,
      const HostToken& token,
      const wux::FrameworkElement& anchor,
      const wuxc::Grid& grid,
      CleanupAuthorizationEvidence& evidence) noexcept {
    (void)trigger;
    evidence = {};
    try {
      if (GetCurrentThreadId() != token.host.thread_id ||
          !token.cleanup_authority) {
        return false;
      }
      const auto anchor_token = exact_host_for(anchor);
      const auto grid_token = exact_host_for(grid);
      if (!anchor_token || !grid_token ||
          !same_host_token(token, *anchor_token) ||
          !same_host_token(token, *grid_token)) {
        return false;
      }

      const auto anchor_xaml_root = anchor.XamlRoot();
      const auto anchor_content =
          anchor_xaml_root ? anchor_xaml_root.Content() : nullptr;
      const auto grid_xaml_root = grid.XamlRoot();
      const auto grid_content =
          grid_xaml_root ? grid_xaml_root.Content() : nullptr;
      CleanupIdentityPath path;
      if (!anchor_content || !grid_content ||
          !capture_identity_path(anchor, anchor_content, path)) {
        return false;
      }

      evidence = {
          .owner_thread_id = GetCurrentThreadId(),
          .host_identity = reinterpret_cast<std::uintptr_t>(
              anchor_token->host.xaml_window),
          .xaml_root_identity = anchor_token->xaml_root_identity,
          .content_identity = anchor_token->content_identity,
          .host_generation = anchor_token->host.generation,
          .root_generation = anchor_token->root_generation,
          .diagnostics_epoch = anchor_token->diagnostics_epoch,
          .anchor_identity = object_identity(anchor),
          .grid_identity = object_identity(grid),
          .anchor_xaml_root_identity = object_identity(anchor_xaml_root),
          .anchor_content_identity = object_identity(anchor_content),
          .grid_xaml_root_identity = object_identity(grid_xaml_root),
          .grid_content_identity = object_identity(grid_content),
          .anchor_to_content = path,
          .host_is_still_valid = validate_host(anchor_token->host),
      };
      return evidence.anchor_identity && evidence.grid_identity &&
             evidence.anchor_xaml_root_identity &&
             evidence.anchor_content_identity &&
             evidence.grid_xaml_root_identity &&
             evidence.grid_content_identity;
    } catch (...) {
      evidence = {};
      return false;
    }
  }

  [[nodiscard]] bool token_is_current(
      const HostToken& token,
      const wux::DependencyObject& element) noexcept {
    const auto current = exact_host_for(element);
    return current && same_host_token(*current, token);
  }

  [[nodiscard]] bool token_host_is_current(const HostToken& token) noexcept {
    try {
      std::optional<BoundRoot> candidate;
      {
        std::lock_guard lock(host_mutex);
        const auto found = std::find_if(
            bound_roots.begin(), bound_roots.end(), [&token](const auto& item) {
              return item.host.xaml_window == token.host.xaml_window &&
                     item.host.generation == token.host.generation &&
                     item.xaml_root_identity == token.xaml_root_identity &&
                     item.content_identity == token.content_identity &&
                     item.root_generation == token.root_generation &&
                     item.diagnostics_epoch == token.diagnostics_epoch &&
                     item.cleanup_authority == token.cleanup_authority;
            });
        if (found != bound_roots.end()) {
          candidate = *found;
        }
      }
      return candidate && binding_is_current(*candidate);
    } catch (...) {
      return false;
    }
  }

  [[nodiscard]] TransitionWorkResult begin_root_transition(
      SessionTransition& transition) noexcept {
    if (!transition.root_transition_started) {
      std::vector<RootTransitionEntry> roots;
      try {
        std::lock_guard lock(host_mutex);
        roots.reserve(bound_roots.size() + synthetic_retained_roots.size());
        const auto entry_for = [&roots](
                                   const std::shared_ptr<CleanupAuthorityGate>&
                                       authority) {
          return std::find_if(
              roots.begin(), roots.end(),
              [&authority](const RootTransitionEntry& entry) {
                return entry.authority == authority;
              });
        };
        for (const auto& binding : bound_roots) {
          if (!binding.cleanup_authority) {
            return TransitionWorkResult::failed;
          }
          auto found = entry_for(binding.cleanup_authority);
          if (found == roots.end()) {
            RootTransitionEntry entry{};
            entry.authority = binding.cleanup_authority;
            roots.push_back(std::move(entry));
            found = std::prev(roots.end());
          }
          if (binding.host.xaml_window) {
            const auto duplicate = std::find_if(
                found->hosts.begin(), found->hosts.end(),
                [&binding](const TaskbarHost& host) {
                  return host.xaml_window == binding.host.xaml_window &&
                         host.generation == binding.host.generation;
                });
            if (duplicate == found->hosts.end()) {
              found->hosts.push_back(binding.host);
            }
          }
        }
        for (const auto& synthetic : synthetic_retained_roots) {
          if (!synthetic.authority) {
            return TransitionWorkResult::failed;
          }
          auto found = entry_for(synthetic.authority);
          if (found == roots.end()) {
            RootTransitionEntry entry{};
            entry.authority = synthetic.authority;
            entry.includes_synthetic_root = true;
            roots.push_back(std::move(entry));
          } else {
            found->includes_synthetic_root = true;
          }
        }
        transition.roots = std::move(roots);
        transition.root_transition_started = true;
      } catch (...) {
        return TransitionWorkResult::failed;
      }
    }

    bool in_flight = false;
    for (auto& entry : transition.roots) {
      if (!entry.authority) {
        return TransitionWorkResult::failed;
      }
      if (!entry.permit) {
        SessionCloseResult close_result = SessionCloseResult::drained;
        auto permit = entry.authority->begin_transition(transition.token,
                                                        close_result);
        if (!permit) {
          return TransitionWorkResult::failed;
        }
        entry.permit.emplace(std::move(*permit));
        if (close_result == SessionCloseResult::in_flight) {
          in_flight = true;
        }
      } else if (entry.authority->live_count()) {
        in_flight = true;
      }
    }
    return in_flight ? TransitionWorkResult::busy
                     : TransitionWorkResult::complete;
  }

  void pause_transition(const std::shared_ptr<SessionTransition>& transition,
                        SessionState state) noexcept {
    std::lock_guard lock(diagnostics_mutex);
    if (session_transition == transition) {
      session_state = state;
      transition->driver_active = false;
    }
  }

  [[nodiscard]] bool reopen_remove_admissions_if_drained() noexcept {
    // The caller holds diagnostics_mutex. Drained callback admission excludes
    // the only production pending-work source, while root admission rechecks
    // the session state under this same mutex before mutating any binding.
    callback_admission.close();
    const bool pending_open = pending_admission.reopen_if_drained();
    const bool inject_failure =
        fail_next_remove_admission_baseline_for_test.exchange(
            false, std::memory_order_acq_rel);
    const bool root_open =
        pending_open && !inject_failure &&
        root_binding_admission.reopen_if_drained();
    if (pending_open && root_open) {
      return true;
    }
    callback_admission.close();
    pending_admission.close();
    root_binding_admission.close();
    return false;
  }

  [[nodiscard]] TransitionWorkResult enter_transition_cleanup_only(
      SessionTransition& transition) noexcept {
    if (callback_admission.live_count() || pending_admission.live_count() ||
        root_binding_admission.live_count() ||
        diagnostics_session.live_count() || outstanding_handoffs(this)) {
      return TransitionWorkResult::busy;
    }
    for (auto& entry : transition.roots) {
      if (!entry.authority || !entry.permit) {
        return TransitionWorkResult::failed;
      }
      if (entry.cleanup_lane_closed) {
        continue;
      }
      if (entry.authority->live_count()) {
        return TransitionWorkResult::busy;
      }
      if (!entry.authority->enter_cleanup_only(transition.token,
                                               *entry.permit)) {
        return TransitionWorkResult::failed;
      }
    }
    return TransitionWorkResult::complete;
  }

  [[nodiscard]] std::size_t synthetic_capsule_count() const noexcept {
    std::lock_guard lock(host_mutex);
    return static_cast<std::size_t>(std::count_if(
        synthetic_retained_roots.begin(), synthetic_retained_roots.end(),
        [](const SyntheticRetainedRoot& root) { return !root.cleaned; }));
  }

  [[nodiscard]] TransitionWorkResult cleanup_transition_capsules(
      SessionTransition& transition) noexcept {
    if (outstanding_handoffs(this)) {
      return TransitionWorkResult::busy;
    }
    bool retryable_failure = false;
    for (auto& entry : transition.roots) {
      if (!entry.authority || !entry.permit) {
        return TransitionWorkResult::failed;
      }
      if (entry.hosts.empty()) {
        const auto cleanup_result =
            capsules.cleanup_current_thread_for_transition(*entry.permit);
        if (cleanup_result.status != CapsuleCleanupStatus::complete) {
          retryable_failure = true;
        }
      }
      for (const auto& host : entry.hosts) {
        std::shared_ptr<CapsuleCleanupResult> cleanup_result;
        try {
          cleanup_result = std::make_shared<CapsuleCleanupResult>(
              CapsuleCleanupResult{CapsuleCleanupStatus::retryable_failure,
                                   capsules.tracked_element_count()});
        } catch (...) {
          return TransitionWorkResult::failed;
        }
        const auto cleanup = [this, &entry, cleanup_result]() noexcept {
          *cleanup_result =
              capsules.cleanup_current_thread_for_transition(*entry.permit);
        };
        TaskbarHost dispatch_host = host;
        dispatch_host.generation = 0;
        const auto handoff =
            run_on_host_thread(dispatch_host, shared_from_this(), cleanup);
        if (handoff == HandoffResult::timed_out_in_flight) {
          return TransitionWorkResult::busy;
        }
        if (handoff != HandoffResult::completed ||
            cleanup_result->status != CapsuleCleanupStatus::complete) {
          retryable_failure = true;
        }
      }

      if (entry.includes_synthetic_root) {
        std::vector<std::size_t> pending;
        std::vector<std::function<bool()>> cleanup_actions;
        try {
          std::lock_guard lock(host_mutex);
          pending.reserve(synthetic_retained_roots.size());
          cleanup_actions.reserve(synthetic_retained_roots.size());
          for (std::size_t index = 0;
               index < synthetic_retained_roots.size(); ++index) {
            const auto& root = synthetic_retained_roots[index];
            if (root.authority == entry.authority && !root.cleaned) {
              pending.push_back(index);
              cleanup_actions.push_back(root.cleanup);
            }
          }
        } catch (...) {
          return TransitionWorkResult::failed;
        }
        for (std::size_t index = 0; index < pending.size(); ++index) {
          bool cleaned = false;
          try {
            cleaned = cleanup_actions[index] && cleanup_actions[index]();
          } catch (...) {
            cleaned = false;
          }
          if (!cleaned) {
            retryable_failure = true;
            continue;
          }
          std::lock_guard lock(host_mutex);
          const auto root_index = pending[index];
          if (root_index >= synthetic_retained_roots.size() ||
              synthetic_retained_roots[root_index].authority !=
                  entry.authority) {
            retryable_failure = true;
          } else {
            synthetic_retained_roots[root_index].cleaned = true;
          }
        }
      }
    }
    if (capsules.tracked_element_count() || synthetic_capsule_count()) {
      retryable_failure = true;
    }
    if (!capsules.tracked_element_count()) {
      std::lock_guard lock(anchor_binding_mutex);
      anchor_bindings.clear();
    }
    return retryable_failure ? TransitionWorkResult::failed
                             : TransitionWorkResult::complete;
  }

  [[nodiscard]] bool retire_transition_host(
      const std::shared_ptr<CleanupAuthorityGate>& authority,
      const TaskbarHost& host) noexcept {
    if (!authority || !host.xaml_window ||
        GetCurrentThreadId() != host.thread_id || !validate_host(host)) {
      return false;
    }
    std::vector<BoundRoot> retained;
    std::vector<BoundRoot> retired;
    try {
      std::lock_guard lock(host_mutex);
      const auto matching = static_cast<std::size_t>(std::count_if(
          bound_roots.begin(), bound_roots.end(),
          [&authority, &host](const BoundRoot& binding) {
            return binding.cleanup_authority == authority &&
                   binding.host.xaml_window == host.xaml_window &&
                   binding.host.generation == host.generation;
          }));
      if (!matching ||
          reinterpret_cast<std::uintptr_t>(GetPropW(
              host.xaml_window, kHostGenerationProperty)) != host.generation) {
        return false;
      }
      retained.reserve(bound_roots.size() - matching);
      retired.reserve(matching);
      const auto removed_generation = reinterpret_cast<std::uintptr_t>(
          RemovePropW(host.xaml_window, kHostGenerationProperty));
      if (removed_generation != host.generation) {
        return false;
      }
      for (const auto& binding : bound_roots) {
        if (binding.cleanup_authority == authority &&
            binding.host.xaml_window == host.xaml_window &&
            binding.host.generation == host.generation &&
            !root_bindings.invalidate(
                reinterpret_cast<std::uintptr_t>(binding.host.xaml_window),
                binding.host.generation, binding.root_generation,
                binding.diagnostics_epoch)) {
          (void)SetPropW(host.xaml_window, kHostGenerationProperty,
                         reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(
                             host.generation)));
          return false;
        }
      }
      for (auto& binding : bound_roots) {
        if (binding.cleanup_authority == authority &&
            binding.host.xaml_window == host.xaml_window &&
            binding.host.generation == host.generation) {
          retired.push_back(std::move(binding));
        } else {
          retained.push_back(std::move(binding));
        }
      }
      bound_roots.swap(retained);
      hosts.erase(std::remove_if(hosts.begin(), hosts.end(),
                                 [&host](const TaskbarHost& candidate) {
                                   return candidate.xaml_window ==
                                              host.xaml_window &&
                                          candidate.generation ==
                                              host.generation;
                                 }),
                  hosts.end());
    } catch (...) {
      return false;
    }
    return true;
  }

  [[nodiscard]] bool retire_local_transition_roots(
      const std::shared_ptr<CleanupAuthorityGate>& authority) noexcept {
    if (!authority) {
      return false;
    }
    {
      std::lock_guard lock(diagnostics_mutex);
      if (fail_next_root_retirement_for_test) {
        fail_next_root_retirement_for_test = false;
        return false;
      }
    }
    std::vector<BoundRoot> retained_bindings;
    std::vector<BoundRoot> retired_bindings;
    std::vector<SyntheticRetainedRoot> retained_synthetic;
    std::vector<SyntheticRetainedRoot> retired_synthetic;
    try {
      std::lock_guard lock(host_mutex);
      for (const auto& root : synthetic_retained_roots) {
        if (root.authority == authority && !root.cleaned) {
          return false;
        }
      }
      retained_bindings.reserve(bound_roots.size());
      retired_bindings.reserve(bound_roots.size());
      retained_synthetic.reserve(synthetic_retained_roots.size());
      retired_synthetic.reserve(synthetic_retained_roots.size());
      for (auto& binding : bound_roots) {
        if (binding.cleanup_authority == authority &&
            !binding.host.xaml_window) {
          retired_bindings.push_back(std::move(binding));
        } else {
          retained_bindings.push_back(std::move(binding));
        }
      }
      for (auto& root : synthetic_retained_roots) {
        if (root.authority == authority) {
          retired_synthetic.push_back(std::move(root));
        } else {
          retained_synthetic.push_back(std::move(root));
        }
      }
      bound_roots.swap(retained_bindings);
      synthetic_retained_roots.swap(retained_synthetic);
    } catch (...) {
      return false;
    }
    return true;
  }

  [[nodiscard]] TransitionWorkResult retire_transition_roots(
      SessionTransition& transition) noexcept {
    if (outstanding_handoffs(this)) {
      return TransitionWorkResult::busy;
    }
    for (auto& entry : transition.roots) {
      if (!entry.authority || !entry.permit) {
        return TransitionWorkResult::failed;
      }
      if (!entry.cleanup_lane_closed) {
        const auto close_result = entry.authority->close_cleanup_only(
            transition.token, *entry.permit);
        if (!close_result) {
          return TransitionWorkResult::failed;
        }
        if (*close_result == SessionCloseResult::in_flight) {
          return TransitionWorkResult::busy;
        }
        if (entry.authority->revoke_permanently() ==
            SessionCloseResult::in_flight) {
          return TransitionWorkResult::busy;
        }
        entry.cleanup_lane_closed = true;
      }
      while (entry.retired_host_count < entry.hosts.size()) {
        const auto& host = entry.hosts[entry.retired_host_count];
        std::shared_ptr<std::atomic<bool>> retired;
        try {
          retired = std::make_shared<std::atomic<bool>>(false);
        } catch (...) {
          return TransitionWorkResult::failed;
        }
        const auto retire = [this, authority = entry.authority, host,
                             retired]() noexcept {
          retired->store(retire_transition_host(authority, host),
                         std::memory_order_release);
        };
        TaskbarHost dispatch_host = host;
        dispatch_host.generation = 0;
        const auto handoff =
            run_on_host_thread(dispatch_host, shared_from_this(), retire);
        if (handoff == HandoffResult::timed_out_in_flight) {
          return TransitionWorkResult::busy;
        }
        if (handoff != HandoffResult::completed ||
            !retired->load(std::memory_order_acquire)) {
          return TransitionWorkResult::failed;
        }
        ++entry.retired_host_count;
      }
      if (!entry.retired &&
          !retire_local_transition_roots(entry.authority)) {
        return TransitionWorkResult::failed;
      }
      entry.retired = true;
    }
    {
      std::lock_guard lock(host_mutex);
      if (!bound_roots.empty() || !synthetic_retained_roots.empty()) {
        return TransitionWorkResult::failed;
      }
      hosts.clear();
    }
    root_bindings.invalidate_all();
    return TransitionWorkResult::complete;
  }

  SessionCloseResult invalidate_binding(const BoundRoot& binding) noexcept {
    if (!binding.cleanup_authority ||
        binding.cleanup_authority->revoke_permanently() ==
            SessionCloseResult::in_flight) {
      return SessionCloseResult::in_flight;
    }
    (void)root_bindings.invalidate(
        reinterpret_cast<std::uintptr_t>(binding.host.xaml_window),
        binding.host.generation, binding.root_generation,
        binding.diagnostics_epoch);
    return SessionCloseResult::drained;
  }

  SessionCloseResult close_all_binding_authorities() noexcept {
    auto close_result = SessionCloseResult::drained;
    std::lock_guard lock(host_mutex);
    for (const auto& binding : bound_roots) {
      if (!binding.cleanup_authority ||
          binding.cleanup_authority->close() ==
              SessionCloseResult::in_flight) {
        close_result = SessionCloseResult::in_flight;
      }
    }
    return close_result;
  }

  bool reopen_all_binding_authorities() noexcept {
    std::lock_guard lock(host_mutex);
    for (const auto& binding : bound_roots) {
      if (!binding.cleanup_authority ||
          binding.cleanup_authority->accepting() ||
          binding.cleanup_authority->live_count()) {
        return false;
      }
    }
    for (const auto& binding : bound_roots) {
      if (!binding.cleanup_authority->reopen_if_drained()) {
        return false;
      }
    }
    return true;
  }

  std::size_t binding_authority_live_count() const noexcept {
    std::lock_guard lock(host_mutex);
    std::size_t total = 0;
    for (const auto& binding : bound_roots) {
      const auto live = binding.cleanup_authority
                            ? binding.cleanup_authority->live_count()
                            : 1U;
      if (live > (std::numeric_limits<std::size_t>::max)() - total) {
        return (std::numeric_limits<std::size_t>::max)();
      }
      total += live;
    }
    return total;
  }

  SessionCloseResult invalidate_all_bindings() noexcept {
    const auto close_result = close_all_binding_authorities();
    if (close_result == SessionCloseResult::drained) {
      root_bindings.invalidate_all();
    }
    return close_result;
  }

  [[nodiscard]] bool binding_is_current(const BoundRoot& binding) noexcept {
    if (GetCurrentThreadId() != binding.host.thread_id ||
        !validate_host(binding.host) ||
        !diagnostics_initialized.load(std::memory_order_acquire) ||
        diagnostics_epoch.load(std::memory_order_acquire) !=
            binding.diagnostics_epoch) {
      (void)invalidate_binding(binding);
      return false;
    }
    try {
      const auto window = wux::Window::Current();
      const auto core_window = window ? window.CoreWindow() : nullptr;
      ComPtr<ICoreWindowInterop> interop;
      HWND core_window_handle = nullptr;
      if (!core_window ||
          FAILED(reinterpret_cast<IInspectable*>(winrt::get_abi(core_window))
                     ->QueryInterface(__uuidof(ICoreWindowInterop),
                                      interop.put_void())) ||
          FAILED(interop->get_WindowHandle(&core_window_handle)) ||
          core_window_handle != binding.host.xaml_window) {
        (void)invalidate_binding(binding);
        return false;
      }
      const auto content = window.Content();
      const auto xaml_root = content ? content.XamlRoot() : nullptr;
      const auto current_root = xaml_root ? xaml_root.Content() : nullptr;
      const auto xaml_root_identity = object_identity(xaml_root);
      const auto content_identity = object_identity(current_root);
      const auto current =
          current_root && xaml_root_identity == binding.xaml_root_identity &&
          content_identity == binding.content_identity &&
          content_identity == object_identity(content) &&
          is_descendant_of(content, current_root) &&
          root_bindings.match(
              {reinterpret_cast<std::uintptr_t>(binding.host.xaml_window),
               binding.host.thread_id, xaml_root_identity, content_identity,
               binding.host.generation, binding.root_generation,
               binding.diagnostics_epoch, true}) == RootMatchResult::exact;
      if (!current) {
        invalidate_binding(binding);
      }
      return current;
    } catch (...) {
      (void)invalidate_binding(binding);
      return false;
    }
  }

  [[nodiscard]] bool unbind_prevalidated_host_root(
      const TaskbarHost& host) noexcept {
    if (GetCurrentThreadId() != host.thread_id || !validate_host(host)) {
      return false;
    }
    std::lock_guard lock(host_mutex);
    const auto found = std::find_if(
        bound_roots.begin(), bound_roots.end(), [&host](const auto& binding) {
          return binding.host.xaml_window == host.xaml_window &&
                 binding.host.generation == host.generation;
        });
    if (found == bound_roots.end() || !binding_is_current(*found)) {
      return false;
    }
    if (!found->cleanup_authority ||
        found->cleanup_authority->revoke_permanently() ==
            SessionCloseResult::in_flight) {
      return false;
    }
    const auto removed_generation = reinterpret_cast<std::uintptr_t>(
        RemovePropW(host.xaml_window, kHostGenerationProperty));
    if (removed_generation != host.generation) {
      return false;
    }
    if (!root_bindings.invalidate(
            reinterpret_cast<std::uintptr_t>(host.xaml_window),
            host.generation, found->root_generation,
            found->diagnostics_epoch)) {
      (void)SetPropW(host.xaml_window, kHostGenerationProperty,
                     reinterpret_cast<HANDLE>(
                         static_cast<std::uintptr_t>(host.generation)));
      return false;
    }
    bound_roots.erase(found);
    return true;
  }

  static bool insert_capsule_authorized(
      void* context,
      CleanupAuthorityGate::MutationLease mutation_authority) noexcept {
    auto& insertion = *static_cast<InsertionContext*>(context);
    try {
      if (!insertion.owner || !insertion.host_token.cleanup_authority) {
        return false;
      }
      CleanupAuthorizationBinding binding;
      std::vector<wux::DependencyObject> retained_path;
      if (!insertion.owner->build_cleanup_binding(
              insertion.host_token, insertion.anchor, insertion.parent,
              binding, retained_path)) {
        return false;
      }
      auto evidence_source = std::make_shared<CapsuleEvidenceSource>(
          *insertion.owner, insertion.host_token, insertion.anchor,
          insertion.parent, binding.anchor_to_content,
          std::move(retained_path));
      return insertion.owner->capsules.insert(
          insertion.anchor_handle,
          reinterpret_cast<IInspectable*>(winrt::get_abi(insertion.anchor)),
          reinterpret_cast<IInspectable*>(winrt::get_abi(insertion.parent)),
           std::move(binding), insertion.host_token.cleanup_authority,
           std::move(evidence_source), std::move(mutation_authority));
    } catch (...) {
      return false;
    }
  }

  static bool insert_capsule_mutator(void* context) noexcept {
    auto& insertion = *static_cast<InsertionContext*>(context);
    if (!insertion.owner || !insertion.host_token.cleanup_authority) {
      return false;
    }
    return run_authorized_capsule_insertion(
        insertion.host_token.cleanup_authority, insert_capsule_authorized,
        context);
  }

  struct InsertionContext final {
    Impl* owner = nullptr;
    std::uint64_t anchor_handle = 0;
    wux::FrameworkElement anchor{nullptr};
    wuxc::Grid parent{nullptr};
    HostToken host_token;
  };

  void schedule_insertion(std::uint64_t anchor_handle,
                          const VisualNodeFacts& facts,
                          const HostToken& host_token,
                          const wux::FrameworkElement& anchor,
                          const wuxc::Grid& parent) noexcept {
    if (evaluate_visual_node(facts) != ProbeDecision::accept) {
      return;
    }
    try {
      auto dispatcher = anchor.Dispatcher();
      if (!dispatcher) {
        return;
      }
      auto pending_lease = pending_admission.try_acquire();
      if (!pending_lease) {
        return;
      }
      auto owner = shared_from_this();
      CoreDispatcherPendingBoundary dispatch_boundary{std::move(dispatcher)};
      PendingDispatchCallback callback =
          [owner, anchor_handle, facts, host_token, anchor,
           parent]() noexcept {
            if (!owner->token_is_current(host_token, anchor)) {
              return;
            }
            try {
              auto current_parent = anchor.Parent().try_as<wuxc::Grid>();
              if (!current_parent ||
                  winrt::get_abi(current_parent) != winrt::get_abi(parent)) {
                return;
              }
              InsertionContext insertion{owner.get(), anchor_handle, anchor,
                                         parent, host_token};
              (void)owner->lifecycle.on_visual_node(
                  facts, insert_capsule_mutator, &insertion);
            } catch (...) {
            }
          };
      (void)pending_actions.schedule(
          production_pending_module_boundary(), dispatch_boundary, owner,
          std::move(*pending_lease), std::move(callback));
    } catch (...) {
    }
  }

  [[nodiscard]] bool process_remove_with_session(
      DiagnosticsSessionGate::Lease& session,
      const VisualElement& visual) noexcept {
    (void)session;
    try {
      HostToken token;
      {
        std::lock_guard lock(anchor_binding_mutex);
        const auto found = anchor_bindings.find(visual.Handle);
        if (found == anchor_bindings.end()) {
          return false;
        }
        token = found->second;
      }
      if (!token_host_is_current(token)) {
        return false;
      }
      const auto cleanup =
          capsules.remove_anchor_on_current_thread(visual.Handle);
      if (cleanup.status == CapsuleCleanupStatus::complete) {
        std::lock_guard lock(anchor_binding_mutex);
        anchor_bindings.erase(visual.Handle);
      }
      return cleanup.status == CapsuleCleanupStatus::complete;
    } catch (...) {
      return false;
    }
  }

  [[nodiscard]] bool process_add_with_session(
      DiagnosticsSessionGate::Lease& session,
      ParentChildRelation relation,
      const VisualElement& visual) noexcept {
    try {
      if (!visual.Type) {
        return false;
      }
      const auto type_length = SysStringLen(visual.Type);
      if (type_length > kMaximumVisualTypeNameLength) {
        return false;
      }

      wf::IInspectable inspectable{nullptr};
      if (!session.get_inspectable(
              visual.Handle,
              reinterpret_cast<void**>(winrt::put_abi(inspectable))) ||
          !session.inspect_xaml(
              reinterpret_cast<void*>(winrt::get_abi(inspectable)))) {
        return false;
      }
      auto anchor = inspectable.try_as<wux::FrameworkElement>();
      if (!anchor) {
        return false;
      }
      const auto host_token = exact_host_for(anchor);
      if (!host_token) {
        return false;
      }
      auto parent = anchor.Parent().try_as<wuxc::Grid>();
      std::uint64_t parent_handle = 0;
      if (!parent || !session.get_handle(
                         reinterpret_cast<void*>(winrt::get_abi(parent)),
                         &parent_handle) ||
          parent_handle != relation.Parent) {
        return false;
      }
      VisualNodeFacts facts{
          std::wstring(visual.Type, type_length),
          static_cast<bool>(parent),
          true,
      };
      {
        std::lock_guard lock(anchor_binding_mutex);
        const auto [item, inserted] =
            anchor_bindings.emplace(visual.Handle, *host_token);
        if (!inserted && !same_host_token(item->second, *host_token)) {
          return false;
        }
      }
      schedule_insertion(visual.Handle, facts, *host_token, anchor, parent);
      return true;
    } catch (...) {
      return false;
    }
  }

  void on_visual_tree_change(DiagnosticsSessionIdentity expected_session,
                             ParentChildRelation relation,
                             const VisualElement& visual,
                             VisualMutationType mutation_type) noexcept {
    auto callback_lease = callback_admission.try_acquire();
    if (!callback_lease || relation.Child != visual.Handle) {
      return;
    }
    {
      std::lock_guard lock(diagnostics_mutex);
      if (session_state != SessionState::current || !current_session ||
          current_session->identity != expected_session) {
        return;
      }
    }

    if (mutation_type == Remove) {
      auto session = diagnostics_session.try_acquire(expected_session);
      if (session) {
        (void)process_remove_with_session(*session, visual);
      }
      return;
    }
    if (mutation_type == Add) {
      CallbackAddOperation operation{*this, relation, visual};
      (void)diagnostics_pipeline.process_add(expected_session, operation);
    }
  }

  void notify_watcher_destruction_for_test() noexcept {
    const auto observer =
        watcher_destruction_observer.load(std::memory_order_acquire);
    if (observer) {
      observer(watcher_destruction_context.load(std::memory_order_acquire));
    }
  }

  void notify_unload_boundary_for_test() noexcept {
    const auto observer =
        unload_boundary_observer.load(std::memory_order_acquire);
    if (observer) {
      observer(unload_boundary_context.load(std::memory_order_acquire));
    }
  }

  void notify_tap_precommit_for_test() noexcept {
    const auto observer =
        tap_precommit_observer.load(std::memory_order_acquire);
    if (observer) {
      observer(tap_precommit_context.load(std::memory_order_acquire));
    }
  }

  void notify_unload_rollback_for_test() noexcept {
    const auto observer =
        unload_rollback_observer.load(std::memory_order_acquire);
    if (observer) {
      observer(unload_rollback_context.load(std::memory_order_acquire));
    }
  }

  void notify_resource_snapshot_for_test() noexcept {
    const auto observer =
        resource_snapshot_observer.load(std::memory_order_acquire);
    if (observer) {
      observer(resource_snapshot_context.load(std::memory_order_acquire));
    }
  }

  HMODULE module = nullptr;
  BridgeLifecycle& lifecycle;
  ProbeCapsuleManager capsules;
  mutable std::mutex host_mutex;
  std::vector<TaskbarHost> hosts;
  std::vector<BoundRoot> bound_roots;
  HostRootBindingRegistry root_bindings;
  std::mutex anchor_binding_mutex;
  std::unordered_map<std::uint64_t, HostToken> anchor_bindings;
  mutable std::mutex diagnostics_mutex;
  std::atomic<WatcherDestructionObserver> watcher_destruction_observer{
      nullptr};
  std::atomic<void*> watcher_destruction_context{nullptr};
  std::atomic<UnloadBoundaryObserver> unload_boundary_observer{nullptr};
  std::atomic<void*> unload_boundary_context{nullptr};
  std::atomic<TapPrecommitObserver> tap_precommit_observer{nullptr};
  std::atomic<void*> tap_precommit_context{nullptr};
  std::atomic<UnloadRollbackObserver> unload_rollback_observer{nullptr};
  std::atomic<void*> unload_rollback_context{nullptr};
  std::atomic<UnloadRollbackObserver>
      unload_rollback_published_observer{nullptr};
  std::atomic<void*> unload_rollback_published_context{nullptr};
  std::atomic<ResourceSnapshotObserver> resource_snapshot_observer{nullptr};
  std::atomic<void*> resource_snapshot_context{nullptr};
  DiagnosticsSessionGate diagnostics_session;
  DiagnosticsCallbackPipeline diagnostics_pipeline;
  SessionState session_state = SessionState::empty;
  std::shared_ptr<SessionResources> current_session;
  std::shared_ptr<SessionTransition> session_transition;
  std::shared_ptr<SessionTransition> quiesced_cleanup;
  std::optional<SitePreparation> site_preparation;
  std::optional<UnloadTransition> unload_transition;
  std::uint64_t activation_preparation_token = 0;
  std::vector<SyntheticRetainedRoot> synthetic_retained_roots;
  bool fail_next_target_publication_for_test = false;
  std::atomic<bool> fail_next_advised_resource_allocation_for_test{false};
  std::atomic<bool> fail_next_unadvised_resource_allocation_for_test{false};
  std::atomic<unsigned int> fail_next_activation_allocation_for_test{0};
  std::atomic<bool> fail_next_activation_publication_for_test{false};
  std::atomic<bool> fail_next_tap_can_unload_for_test{false};
  std::atomic<bool> fail_next_unload_final_counts_for_test{false};
  std::atomic<bool> fail_next_unload_before_detach_for_test{false};
  bool fail_next_root_retirement_for_test = false;
  std::atomic<bool> fail_next_remove_admission_baseline_for_test{false};
  HMODULE xaml_module = nullptr;
  std::atomic<bool> diagnostics_initialized{false};
  std::atomic<std::uint64_t> diagnostics_epoch{0};
  std::atomic<bool> diagnostics_reference_released{false};
  AdmissionGate callback_admission{kMaximumCallbacks};
  AdmissionGate pending_admission{kMaximumPendingActions};
  PendingActionLedger pending_actions{kMaximumPendingActions};
  AdmissionGate root_binding_admission{kMaximumTaskbarHosts};
};

PendingAsyncStatus XamlTaskbarProbeTestPeer::map_pending_status_for_test(
    std::int32_t raw_status) noexcept {
  return map_winrt_pending_status(static_cast<wf::AsyncStatus>(raw_status));
}

bool XamlTaskbarProbeTestPeer::activate_diagnostics_session(
    XamlTaskbarProbe& probe,
    std::uint64_t epoch) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl && impl->activate_diagnostics_session(epoch);
}

bool XamlTaskbarProbeTestPeer::attach_cleanup_authority(
    XamlTaskbarProbe& probe,
    std::shared_ptr<CleanupAuthorityGate> authority) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !authority) {
    return false;
  }
  try {
    std::lock_guard lock(impl->host_mutex);
    XamlTaskbarProbe::Impl::BoundRoot binding{};
    binding.cleanup_authority = std::move(authority);
    impl->bound_roots.push_back(std::move(binding));
    return true;
  } catch (...) {
    return false;
  }
}

bool XamlTaskbarProbeTestPeer::attach_synthetic_retained_root(
    XamlTaskbarProbe& probe,
    std::shared_ptr<CleanupAuthorityGate> authority,
    std::function<bool()> cleanup) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !authority || !authority->accepting() || !cleanup) {
    return false;
  }
  try {
    std::lock_guard lock(impl->host_mutex);
    impl->synthetic_retained_roots.push_back(
        {std::move(authority), std::move(cleanup), false});
    return true;
  } catch (...) {
    return false;
  }
}

bool XamlTaskbarProbeTestPeer::attach_primitive_capsule_record(
    XamlTaskbarProbe& probe,
    CleanupAuthorizationBinding binding,
    std::shared_ptr<CleanupAuthorityGate> authority,
    std::shared_ptr<CleanupAuthorizationEvidenceSource> evidence_source,
    std::shared_ptr<CapsuleCleanupAccess> access) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl && impl->capsules.attach_primitive_record_for_test(
                     std::move(binding), std::move(authority),
                     std::move(evidence_source), std::move(access));
}

CapsuleCleanupResult
XamlTaskbarProbeTestPeer::trigger_primitive_capsule_record(
    XamlTaskbarProbe& probe,
    CapsuleCleanupTrigger trigger) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl
             ? impl->capsules.trigger_primitive_records_for_test(trigger)
             : CapsuleCleanupResult{};
}

bool XamlTaskbarProbeTestPeer::fail_next_target_publication(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  std::lock_guard lock(impl->diagnostics_mutex);
  if (impl->session_transition ||
      (impl->session_state != XamlTaskbarProbe::Impl::SessionState::current &&
       impl->session_state !=
           XamlTaskbarProbe::Impl::SessionState::current_unwatched)) {
    return false;
  }
  impl->fail_next_target_publication_for_test = true;
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_advised_resource_allocation(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  impl->fail_next_advised_resource_allocation_for_test.store(
      true, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_unadvised_resource_allocation(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  impl->fail_next_unadvised_resource_allocation_for_test.store(
      true, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_activation_allocation(
    XamlTaskbarProbe& probe,
    unsigned int stage) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || (stage != 1 && stage != 2)) {
    return false;
  }
  impl->fail_next_activation_allocation_for_test.store(
      stage, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_activation_publication(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  impl->fail_next_activation_publication_for_test.store(
      true, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_tap_can_unload(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  impl->fail_next_tap_can_unload_for_test.store(
      true, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_unload_final_counts(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  impl->fail_next_unload_final_counts_for_test.store(
      true, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_unload_before_detach(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  impl->fail_next_unload_before_detach_for_test.store(
      true, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_root_retirement(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  std::lock_guard lock(impl->diagnostics_mutex);
  if (impl->session_transition ||
      (impl->session_state != XamlTaskbarProbe::Impl::SessionState::current &&
       impl->session_state !=
           XamlTaskbarProbe::Impl::SessionState::current_unwatched)) {
    return false;
  }
  impl->fail_next_root_retirement_for_test = true;
  return true;
}

bool XamlTaskbarProbeTestPeer::diagnostics_mutex_available(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  std::atomic<bool> available{false};
  try {
    std::thread observer([impl, &available]() noexcept {
      if (impl->diagnostics_mutex.try_lock()) {
        available.store(true, std::memory_order_release);
        impl->diagnostics_mutex.unlock();
      }
    });
    observer.join();
  } catch (...) {
    return false;
  }
  return available.load(std::memory_order_acquire);
}

bool XamlTaskbarProbeTestPeer::set_watcher_destruction_observer(
    XamlTaskbarProbe& probe,
    WatcherDestructionObserver observer,
    void* context) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || (observer && !context)) {
    return false;
  }
  if (observer) {
    impl->watcher_destruction_context.store(
        context, std::memory_order_release);
    impl->watcher_destruction_observer.store(
        observer, std::memory_order_release);
  } else {
    impl->watcher_destruction_observer.store(
        nullptr, std::memory_order_release);
    impl->watcher_destruction_context.store(
        nullptr, std::memory_order_release);
  }
  return true;
}

bool XamlTaskbarProbeTestPeer::set_unload_boundary_observer(
    XamlTaskbarProbe& probe,
    UnloadBoundaryObserver observer,
    void* context) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || (observer && !context)) {
    return false;
  }
  if (observer) {
    impl->unload_boundary_context.store(context, std::memory_order_release);
    impl->unload_boundary_observer.store(observer,
                                         std::memory_order_release);
  } else {
    impl->unload_boundary_observer.store(nullptr, std::memory_order_release);
    impl->unload_boundary_context.store(nullptr, std::memory_order_release);
  }
  return true;
}

bool XamlTaskbarProbeTestPeer::set_tap_precommit_observer(
    XamlTaskbarProbe& probe,
    TapPrecommitObserver observer,
    void* context) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || (observer && !context)) {
    return false;
  }
  impl->tap_precommit_context.store(context, std::memory_order_release);
  impl->tap_precommit_observer.store(observer, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::set_unload_rollback_observer(
    XamlTaskbarProbe& probe,
    UnloadRollbackObserver observer,
    void* context) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || (observer && !context)) {
    return false;
  }
  if (observer) {
    impl->unload_rollback_context.store(context,
                                        std::memory_order_release);
    impl->unload_rollback_observer.store(observer,
                                         std::memory_order_release);
  } else {
    impl->unload_rollback_observer.store(nullptr,
                                         std::memory_order_release);
    impl->unload_rollback_context.store(nullptr,
                                        std::memory_order_release);
  }
  return true;
}

bool XamlTaskbarProbeTestPeer::set_unload_rollback_published_observer(
    XamlTaskbarProbe& probe,
    UnloadRollbackObserver observer,
    void* context) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || (observer && !context)) {
    return false;
  }
  if (observer) {
    impl->unload_rollback_published_context.store(
        context, std::memory_order_release);
    impl->unload_rollback_published_observer.store(
        observer, std::memory_order_release);
  } else {
    impl->unload_rollback_published_observer.store(
        nullptr, std::memory_order_release);
    impl->unload_rollback_published_context.store(
        nullptr, std::memory_order_release);
  }
  return true;
}

bool XamlTaskbarProbeTestPeer::unload_mutex_available(
    XamlTaskbarProbe& probe) noexcept {
  if (!probe.unload_mutex_.try_lock()) {
    return false;
  }
  probe.unload_mutex_.unlock();
  return true;
}

bool XamlTaskbarProbeTestPeer::session_is_unloading(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  std::lock_guard lock(impl->diagnostics_mutex);
  return impl->session_state == XamlTaskbarProbe::Impl::SessionState::unloading;
}

bool XamlTaskbarProbeTestPeer::has_impl(
    XamlTaskbarProbe& probe) noexcept {
  return static_cast<bool>(probe.impl_.load(std::memory_order_acquire));
}

bool XamlTaskbarProbeTestPeer::while_impl_snapshot(
    XamlTaskbarProbe& probe,
    const std::function<void()>& action) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !action) {
    return false;
  }
  try {
    action();
    return true;
  } catch (...) {
    return false;
  }
}

bool XamlTaskbarProbeTestPeer::reset_tap_object_admission() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (g_com_object_count != 0 || g_server_lock_count != 0 ||
      g_tap_object_admission_phase == TapAdmissionPhase::claimed ||
      g_tap_initialization_in_flight) {
    return false;
  }
  g_com_counter_fault = false;
  g_tap_object_admission_phase = TapAdmissionPhase::open;
  g_tap_claim_owner = 0;
  g_tap_claim_generation = 0;
  g_tap_claim_had_active_owner = false;
  g_fail_next_tap_reopen_for_test.store(false, std::memory_order_release);
  g_active_probe.store(nullptr, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_remove_admission_baseline(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  impl->fail_next_remove_admission_baseline_for_test.store(
      true, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::fail_next_tap_reopen() noexcept {
  g_fail_next_tap_reopen_for_test.store(true, std::memory_order_release);
  return true;
}

std::uint64_t XamlTaskbarProbeTestPeer::claim_tap_object_admission(
    std::uintptr_t owner) noexcept {
  return claim_tap_object_admission_for_unload(owner).generation;
}

bool XamlTaskbarProbeTestPeer::reopen_tap_object_admission(
    std::uintptr_t owner,
    std::uint64_t generation) noexcept {
  TapAdmissionClaim claim{owner, generation};
  return cq::bridge::reopen_tap_object_admission(claim);
}

bool XamlTaskbarProbeTestPeer::commit_tap_object_admission(
    std::uintptr_t owner,
    std::uint64_t generation) noexcept {
  TapAdmissionClaim claim{owner, generation};
  return cq::bridge::commit_tap_object_admission(claim);
}

bool XamlTaskbarProbeTestPeer::tap_object_admission_closed() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  return g_tap_object_admission_phase != TapAdmissionPhase::open;
}

bool XamlTaskbarProbeTestPeer::set_active_probe(
    XamlTaskbarProbe* probe) noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (g_tap_object_admission_phase != TapAdmissionPhase::open ||
      g_tap_initialization_in_flight) {
    return false;
  }
  auto* const active = g_active_probe.load(std::memory_order_acquire);
  if (probe && active && active != probe) {
    return false;
  }
  g_active_probe.store(probe, std::memory_order_release);
  return true;
}

XamlTaskbarProbe* XamlTaskbarProbeTestPeer::active_probe() noexcept {
  return g_active_probe.load(std::memory_order_acquire);
}

long XamlTaskbarProbeTestPeer::tap_object_count() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  return g_com_object_count;
}

long XamlTaskbarProbeTestPeer::tap_server_lock_count() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  return g_server_lock_count;
}

bool XamlTaskbarProbeTestPeer::tap_counter_fault() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  return g_com_counter_fault;
}

bool XamlTaskbarProbeTestPeer::set_tap_server_lock_count(
    long count) noexcept {
  if (count != 0 && count != (std::numeric_limits<long>::max)()) {
    return false;
  }
  std::lock_guard lock(g_tap_object_admission_mutex);
  g_server_lock_count = count;
  return true;
}

bool XamlTaskbarProbeTestPeer::set_resource_snapshot_observer(
    XamlTaskbarProbe& probe,
    ResourceSnapshotObserver observer,
    void* context) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || (observer && !context)) {
    return false;
  }
  impl->resource_snapshot_context.store(context, std::memory_order_release);
  impl->resource_snapshot_observer.store(observer, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::detach_transition_resources(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  std::shared_ptr<XamlTaskbarProbe::Impl::SessionResources> old_resources;
  std::shared_ptr<XamlTaskbarProbe::Impl::SessionResources> target_resources;
  bool old_watcher_unadvised = false;
  bool target_watcher_advised = false;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (!impl->session_transition) {
      return false;
    }
    old_resources =
        std::move(impl->session_transition->old_resources);
    target_resources =
        std::move(impl->session_transition->target_resources);
    old_watcher_unadvised =
        impl->session_transition->old_watcher_unadvised;
    target_watcher_advised =
        impl->session_transition->target_watcher_advised;
  }
  if (!old_watcher_unadvised && old_resources &&
      old_resources->advised_visual_service && old_resources->watcher &&
      FAILED(old_resources->advised_visual_service->UnadviseVisualTreeChange(
          old_resources->watcher.get()))) {
    return false;
  }
  if (target_watcher_advised && target_resources &&
      target_resources->advised_visual_service && target_resources->watcher &&
      FAILED(
          target_resources->advised_visual_service->UnadviseVisualTreeChange(
              target_resources->watcher.get()))) {
    return false;
  }
  return old_resources && target_resources;
}

bool XamlTaskbarProbeTestPeer::try_pending_admission(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl && impl->pending_admission.try_acquire().has_value();
}

bool XamlTaskbarProbeTestPeer::while_pending_admission(
    XamlTaskbarProbe& probe,
    const std::function<void()>& action) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !action) {
    return false;
  }
  auto lease = impl->pending_admission.try_acquire();
  if (!lease) {
    return false;
  }
  try {
    action();
    return true;
  } catch (...) {
    return false;
  }
}

bool XamlTaskbarProbeTestPeer::while_callback_admission(
    XamlTaskbarProbe& probe,
    const std::function<void()>& action) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !action) {
    return false;
  }
  auto lease = impl->callback_admission.try_acquire();
  if (!lease) {
    return false;
  }
  try {
    action();
    return true;
  } catch (...) {
    return false;
  }
}

bool XamlTaskbarProbeTestPeer::while_root_admission(
    XamlTaskbarProbe& probe,
    const std::function<void()>& action) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !action) {
    return false;
  }
  auto lease = impl->root_binding_admission.try_acquire();
  if (!lease) {
    return false;
  }
  try {
    action();
    return true;
  } catch (...) {
    return false;
  }
}

std::optional<ProbeSessionAdmissionSnapshot>
XamlTaskbarProbeTestPeer::session_admission_snapshot(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl) {
    return std::nullopt;
  }
  ProbeSessionAdmissionSnapshot snapshot;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    switch (impl->session_state) {
      case XamlTaskbarProbe::Impl::SessionState::empty:
        snapshot.state = ProbeSessionStateForTest::empty;
        break;
      case XamlTaskbarProbe::Impl::SessionState::initial_site:
        snapshot.state = ProbeSessionStateForTest::initial_site;
        break;
      case XamlTaskbarProbe::Impl::SessionState::current_unwatched:
        snapshot.state = ProbeSessionStateForTest::current_unwatched;
        break;
      case XamlTaskbarProbe::Impl::SessionState::current:
        snapshot.state = ProbeSessionStateForTest::current;
        break;
      case XamlTaskbarProbe::Impl::SessionState::closing:
        snapshot.state = ProbeSessionStateForTest::closing;
        break;
      case XamlTaskbarProbe::Impl::SessionState::prepared:
        snapshot.state = ProbeSessionStateForTest::prepared;
        break;
      case XamlTaskbarProbe::Impl::SessionState::watcher_needed:
        snapshot.state = ProbeSessionStateForTest::watcher_needed;
        break;
      case XamlTaskbarProbe::Impl::SessionState::advise_in_progress:
        snapshot.state = ProbeSessionStateForTest::advise_in_progress;
        break;
      case XamlTaskbarProbe::Impl::SessionState::advised_not_published:
        snapshot.state = ProbeSessionStateForTest::advised_not_published;
        break;
      case XamlTaskbarProbe::Impl::SessionState::quiesced:
        snapshot.state = ProbeSessionStateForTest::quiesced;
        break;
      case XamlTaskbarProbe::Impl::SessionState::unloading:
        snapshot.state = ProbeSessionStateForTest::unloading;
        break;
      case XamlTaskbarProbe::Impl::SessionState::unsafe_retained:
        snapshot.state = ProbeSessionStateForTest::unsafe_retained;
        break;
    }
    snapshot.transition_pending = impl->session_transition != nullptr;
    snapshot.transition_driver_active =
        impl->session_transition && impl->session_transition->driver_active;
    snapshot.pending_open = impl->pending_admission.accepting();
    snapshot.root_open = impl->root_binding_admission.accepting();
    snapshot.callback_open = impl->callback_admission.accepting();
  }
  return snapshot;
}

PendingScheduleResult XamlTaskbarProbeTestPeer::schedule_pending_for_test(
    XamlTaskbarProbe& probe,
    PendingModuleBoundary& module_boundary,
    PendingDispatchBoundary& dispatch_boundary,
    PendingDispatchCallback callback) noexcept {
  const auto owner = probe.impl_.load(std::memory_order_acquire);
  if (!owner) {
    return {};
  }
  auto lease = owner->pending_admission.try_acquire();
  if (!lease) {
    return {};
  }
  return owner->pending_actions.schedule(
      module_boundary, dispatch_boundary, owner, std::move(*lease),
      std::move(callback));
}

PendingLedgerOperationResult
XamlTaskbarProbeTestPeer::reap_pending_for_test(
    XamlTaskbarProbe& probe) noexcept {
  const auto owner = probe.impl_.load(std::memory_order_acquire);
  return owner ? owner->pending_actions.reap_all()
               : PendingLedgerOperationResult{};
}

std::size_t XamlTaskbarProbeTestPeer::pending_registry_count(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl ? impl->pending_actions.registry_count() : 0;
}

std::size_t XamlTaskbarProbeTestPeer::pending_retiring_count(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl ? impl->pending_actions.retiring_count() : 0;
}

bool XamlTaskbarProbeTestPeer::pending_mutex_available(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return !impl || impl->pending_actions.mutex_available();
}

std::optional<PendingActionStage> XamlTaskbarProbeTestPeer::pending_stage(
    XamlTaskbarProbe& probe,
    std::uint64_t id) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl ? impl->pending_actions.stage(id) : std::nullopt;
}

bool XamlTaskbarProbeTestPeer::attach_manager_record(
    ProbeCapsuleManager& manager,
    DWORD owner_thread_id,
    std::function<CapsuleCleanupStatus()> cleanup,
    std::shared_ptr<void> retained_owner,
    std::uint64_t& record_id) noexcept {
  return manager.attach_record_for_test(
      owner_thread_id, std::move(cleanup), std::move(retained_owner),
      record_id);
}

CapsuleCleanupStatus XamlTaskbarProbeTestPeer::remove_manager_record(
    ProbeCapsuleManager& manager,
    DWORD owner_thread_id,
    std::uint64_t record_id) noexcept {
  return manager.remove_record_for_test(owner_thread_id, record_id);
}

bool XamlTaskbarProbeTestPeer::manager_mutex_available(
    const ProbeCapsuleManager& manager) noexcept {
  return manager.mutex_available_for_test();
}

bool XamlTaskbarProbeTestPeer::set_manager_remove_snapshot_observer(
    ProbeCapsuleManager& manager,
    ManagerRemoveSnapshotObserver observer,
    void* context) noexcept {
  return manager.set_remove_snapshot_observer_for_test(observer, context);
}

bool XamlTaskbarProbeTestPeer::set_manager_attach_admission_observer(
    ProbeCapsuleManager& manager,
    ManagerAttachAdmissionObserver observer,
    void* context) noexcept {
  return manager.set_attach_admission_observer_for_test(observer, context);
}

bool XamlTaskbarProbeTestPeer::set_manager_retention_fault(
    ProbeCapsuleManager& manager,
    bool fault) noexcept {
  return manager.set_retention_fault_for_test(fault);
}

std::uint64_t XamlTaskbarProbeTestPeer::claim_manager_release(
    ProbeCapsuleManager& manager) noexcept {
  return manager.claim_release_for_test();
}

bool XamlTaskbarProbeTestPeer::cancel_manager_release(
    ProbeCapsuleManager& manager,
    std::uint64_t generation) noexcept {
  return manager.cancel_release_for_test(generation);
}

bool XamlTaskbarProbeTestPeer::commit_manager_release(
    ProbeCapsuleManager& manager,
    std::uint64_t generation) noexcept {
  return manager.commit_release_for_test(generation);
}

HandoffResult XamlTaskbarProbeTestPeer::run_handoff_for_test(
    XamlTaskbarProbe& probe,
    HandoffWin32Boundary& boundary,
    std::function<void()> action) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !action) {
    return HandoffResult::action_failed;
  }
  const auto current_thread = GetCurrentThreadId();
  const auto target_thread =
      current_thread == (std::numeric_limits<DWORD>::max)()
          ? current_thread - 1
          : current_thread + 1;
  const HandoffRetirementTarget target{
      .window = 0x7171,
      .process_id = GetCurrentProcessId(),
      .thread_id = target_thread,
      .retirement_token = 0,
  };
  const TaskbarHost host{
      .tray_window = reinterpret_cast<HWND>(0x7272),
      .xaml_window = reinterpret_cast<HWND>(target.window),
      .process_id = target.process_id,
      .thread_id = target.thread_id,
      .generation = 73,
  };
  return run_prevalidated_handoff(host, target, impl, action, boundary,
                                  true);
}

bool XamlTaskbarProbeTestPeer::dispatch_handoff_for_test(
    std::uintptr_t cookie) noexcept {
  HandoffContext* context = nullptr;
  bool counted = false;
  {
    std::lock_guard lock(g_handoff_mutex);
    const auto found = std::find_if(
        g_handoffs.begin(), g_handoffs.end(),
        [cookie](const auto& candidate) {
          return candidate && candidate->cookie == cookie &&
                 candidate->synthetic_for_test;
        });
    if (found == g_handoffs.end()) {
      return false;
    }
    context = found->get();
    counted = context->retirement.enter_hook_frame();
  }
  if (context->boundary &&
      context->boundary->retirement_token_matches(context->target)) {
    run_handoff_action(*context);
  }
  if (counted) {
    context->retirement.exit_hook_frame();
  }
  return true;
}

std::size_t XamlTaskbarProbeTestPeer::reap_handoffs_for_test(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl ? outstanding_handoffs(impl.get()) : 0;
}

bool XamlTaskbarProbeTestPeer::handoff_mutex_available() noexcept {
  if (!g_handoff_mutex.try_lock()) {
    return false;
  }
  g_handoff_mutex.unlock();
  return true;
}

bool XamlTaskbarProbeTestPeer::set_handoff_destruction_observer(
    HandoffDestructionObserver observer,
    void* context) noexcept {
  if (observer && !context) {
    return false;
  }
  if (!observer) {
    g_handoff_destruction_observer.store(nullptr,
                                         std::memory_order_release);
    g_handoff_destruction_context.store(nullptr,
                                        std::memory_order_release);
    return true;
  }
  g_handoff_destruction_context.store(context, std::memory_order_release);
  g_handoff_destruction_observer.store(observer, std::memory_order_release);
  return true;
}

bool XamlTaskbarProbeTestPeer::while_diagnostics_lease(
    XamlTaskbarProbe& probe,
    const std::function<void()>& action) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  if (!impl || !action) {
    return false;
  }
  const auto identity = impl->diagnostics_session.identity();
  if (!identity) {
    return false;
  }
  auto session = impl->diagnostics_session.try_acquire(*identity);
  if (!session) {
    return false;
  }
  try {
    action();
    return true;
  } catch (...) {
    return false;
  }
}

std::optional<DiagnosticsSessionIdentity>
XamlTaskbarProbeTestPeer::diagnostics_identity(
    XamlTaskbarProbe& probe) noexcept {
  const auto impl = probe.impl_.load(std::memory_order_acquire);
  return impl ? impl->diagnostics_session.identity() : std::nullopt;
}

XamlTaskbarProbe::XamlTaskbarProbe(void* module,
                                   BridgeLifecycle& lifecycle) noexcept {
  try {
    impl_.store(std::make_shared<Impl>(module, lifecycle),
                std::memory_order_release);
  } catch (...) {
  }
}

XamlTaskbarProbe::~XamlTaskbarProbe() {
  std::shared_ptr<Impl> retired;
  {
    std::lock_guard lock(unload_mutex_);
    retired = impl_.exchange(std::shared_ptr<Impl>{},
                             std::memory_order_acq_rel);
  }
  // COM final releases may synchronously reenter the probe.  The retained
  // Impl slot and unload mutex are already clear before members are destroyed.
  retired.reset();
}

bool XamlTaskbarProbe::initialize_existing_taskbar_threads() noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  const auto keep_alive = impl;
  // This entry point is also the worker's roughly two-second retry tick.  Poll
  // here so terminal dispatcher actions are retired even when no callback ever
  // ran, while the worker's bootstrap module reference remains held.
  (void)impl->pending_actions.reap_all();
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return false;
    }
  }
  auto discovered = discover_hosts();
  if (discovered.empty()) {
    return false;
  }

  bool forward_session = false;
  std::uint64_t session_epoch = 0;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    forward_session =
        !impl->site_preparation && !impl->session_transition &&
        impl->current_session &&
        (impl->session_state == Impl::SessionState::current ||
         impl->session_state == Impl::SessionState::current_unwatched);
    if (forward_session) {
      session_epoch = impl->current_session->identity.epoch;
    }
  }
  {
    std::lock_guard lock(impl->host_mutex);
    if (forward_session && session_epoch && !impl->hosts.empty() &&
        impl->bound_roots.size() == impl->hosts.size() &&
        impl->root_bindings.size() == impl->bound_roots.size() &&
        std::all_of(impl->hosts.begin(), impl->hosts.end(), validate_host) &&
        std::all_of(impl->bound_roots.begin(), impl->bound_roots.end(),
                    [session_epoch](const Impl::BoundRoot& binding) {
                      return binding.diagnostics_epoch == session_epoch &&
                             binding.cleanup_authority &&
                             binding.cleanup_authority->accepting();
                    })) {
      return true;
    }
  }

  std::vector<TaskbarHost> completed;
  try {
    completed.reserve(discovered.size());
  } catch (...) {
    return false;
  }
  for (auto host : discovered) {
    host.generation =
        g_next_host_generation.fetch_add(1, std::memory_order_relaxed);
    if (!host.generation) {
      return false;
    }
    TaskbarHost dispatch_host = host;
    dispatch_host.generation = 0;
    std::shared_ptr<std::atomic<bool>> bound;
    try {
      bound = std::make_shared<std::atomic<bool>>(false);
    } catch (...) {
      return false;
    }
    const auto bind = [impl = impl.get(), host, bound]() noexcept {
      bound->store(impl->bind_prevalidated_host_root(host),
                   std::memory_order_release);
    };
    if (run_on_host_thread(dispatch_host, impl, bind) !=
            HandoffResult::completed ||
        !bound->load(std::memory_order_acquire)) {
      for (const auto& rollback_host : completed) {
        const auto rollback = [impl = impl.get(), rollback_host]() noexcept {
          (void)impl->unbind_prevalidated_host_root(rollback_host);
        };
        (void)run_on_host_thread(rollback_host, impl, rollback);
      }
      return false;
    }
    completed.push_back(host);
  }
  {
    std::lock_guard lock(impl->host_mutex);
    impl->hosts = std::move(completed);
  }
  return true;
}

bool XamlTaskbarProbe::initialize_xaml_diagnostics() noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl || !impl->module) {
    return false;
  }
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return false;
    }
  }
  std::wstring module_path(32768, L'\0');
  const DWORD path_length = GetModuleFileNameW(
      impl->module, module_path.data(), static_cast<DWORD>(module_path.size()));
  if (!path_length || path_length >= module_path.size()) {
    return false;
  }
  module_path.resize(path_length);

  impl->xaml_module = LoadLibraryExW(
      L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
  if (!impl->xaml_module) {
    return false;
  }
  using InitializeDiagnostics = decltype(&InitializeXamlDiagnosticsEx);
  const auto initialize = reinterpret_cast<InitializeDiagnostics>(
      GetProcAddress(impl->xaml_module, "InitializeXamlDiagnosticsEx"));
  if (!initialize) {
    FreeLibrary(std::exchange(impl->xaml_module, nullptr));
    return false;
  }

  if (!begin_tap_initialization(this)) {
    FreeLibrary(std::exchange(impl->xaml_module, nullptr));
    return false;
  }
  HRESULT result = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
  for (unsigned int index = 1; index <= 64; ++index) {
    wchar_t connection_name[64]{};
    if (swprintf_s(connection_name, L"VisualDiagConnection%u", index) < 0) {
      result = E_FAIL;
      break;
    }
    result = initialize(connection_name, GetCurrentProcessId(), L"",
                        module_path.c_str(), kProbeTapClsid, L"");
    if (result != HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) {
      break;
    }
  }
  if (FAILED(result)) {
    (void)finish_tap_initialization(this, false);
    FreeLibrary(std::exchange(impl->xaml_module, nullptr));
    return false;
  }
  const auto session_epoch =
      g_next_diagnostics_epoch.fetch_add(1, std::memory_order_relaxed);
  if (!session_epoch || !impl->activate_diagnostics_session(session_epoch)) {
    impl->invalidate_all_bindings();
    (void)impl->diagnostics_session.close();
    (void)finish_tap_initialization(this, false);
    FreeLibrary(std::exchange(impl->xaml_module, nullptr));
    return false;
  }
  if (!finish_tap_initialization(this, true)) {
    impl->invalidate_all_bindings();
    (void)impl->diagnostics_session.close();
    FreeLibrary(std::exchange(impl->xaml_module, nullptr));
    return false;
  }
  return true;
}

bool XamlTaskbarProbe::advise_watcher() noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }
  std::shared_ptr<Impl::SessionTransition> transition;
  std::shared_ptr<Impl::SessionTransition> candidate;
  try {
    candidate = std::make_shared<Impl::SessionTransition>();
  } catch (...) {
    return false;
  }
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return false;
    }
    if (impl->session_transition) {
      transition = impl->session_transition;
      if (transition->kind != Impl::TransitionKind::initial_advise ||
          transition->driver_active ||
          (impl->session_state != Impl::SessionState::watcher_needed &&
           impl->session_state !=
               Impl::SessionState::advised_not_published)) {
        return false;
      }
      transition->driver_active = true;
      impl->session_state = Impl::SessionState::advise_in_progress;
    } else {
      if (impl->session_state != Impl::SessionState::current_unwatched ||
          !impl->current_session ||
          !impl->current_session->visual_service ||
          !impl->current_session->identity.epoch) {
        return false;
      }
      candidate->token =
          g_next_session_transition.fetch_add(1, std::memory_order_relaxed);
      if (!candidate->token) {
        return false;
      }
      candidate->kind = Impl::TransitionKind::initial_advise;
      candidate->stage = Impl::TransitionStage::prepare_target;
      candidate->old_resources = std::move(impl->current_session);
      candidate->watcher_required = true;
      candidate->driver_active = true;
      impl->session_transition = candidate;
      impl->session_state = Impl::SessionState::advise_in_progress;
      transition = std::move(candidate);
    }
  }

  impl->callback_admission.close();
  if (!transition->target_resources) {
    std::shared_ptr<Impl::SessionResources> target;
    try {
      if (!transition->old_resources) {
        throw std::bad_alloc{};
      }
      target = std::make_shared<Impl::SessionResources>(
          *transition->old_resources);
    } catch (...) {
      impl->pause_transition(transition, Impl::SessionState::watcher_needed);
      return false;
    }
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->session_transition != transition ||
          !transition->driver_active || transition->target_resources) {
        return false;
      }
      transition->target_resources = std::move(target);
      transition->stage = Impl::TransitionStage::advise_target;
    }
  }

  std::shared_ptr<Impl::SessionResources> retired_target;
  if (transition->target_watcher_advised) {
    const auto advised = transition->target_resources;
    std::shared_ptr<Impl::SessionResources> cleaned;
    try {
      if (impl->fail_next_unadvised_resource_allocation_for_test.exchange(
              false, std::memory_order_acq_rel)) {
        throw std::bad_alloc{};
      }
      if (!advised || !advised->advised_visual_service || !advised->watcher) {
        throw std::bad_alloc{};
      }
      cleaned = std::make_shared<Impl::SessionResources>(*advised);
      cleaned->watcher.reset();
      cleaned->advised_visual_service.reset();
    } catch (...) {
      impl->pause_transition(transition,
                              Impl::SessionState::advised_not_published);
      return false;
    }
    if (FAILED(advised->advised_visual_service->UnadviseVisualTreeChange(
            advised->watcher.get()))) {
      impl->pause_transition(transition,
                              Impl::SessionState::advised_not_published);
      return false;
    }
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->session_transition != transition ||
          !transition->driver_active) {
        return false;
      }
      retired_target = std::move(transition->target_resources);
      transition->target_resources = std::move(cleaned);
      transition->target_watcher_advised = false;
      impl->session_state = Impl::SessionState::watcher_needed;
    }
    retired_target.reset();
  }

  const auto target = transition->target_resources;
  if (!target || !target->visual_service || !target->identity.epoch) {
    impl->pause_transition(transition, Impl::SessionState::watcher_needed);
    return false;
  }
  auto* raw_watcher =
      new (std::nothrow) Impl::Watcher(impl, target->identity);
  if (!raw_watcher) {
    impl->pause_transition(transition, Impl::SessionState::watcher_needed);
    return false;
  }
  ComPtr<IVisualTreeServiceCallback2> watcher;
  watcher.attach(raw_watcher);
  std::shared_ptr<Impl::SessionResources> advised;
  try {
    advised = std::make_shared<Impl::SessionResources>(*target);
    advised->advised_visual_service = target->visual_service;
    advised->watcher = std::move(watcher);
  } catch (...) {
    impl->pause_transition(transition, Impl::SessionState::watcher_needed);
    return false;
  }
  if (FAILED(target->visual_service->AdviseVisualTreeChange(raw_watcher))) {
    impl->pause_transition(transition, Impl::SessionState::watcher_needed);
    return false;
  }

  if (impl->fail_next_advised_resource_allocation_for_test.exchange(
          false, std::memory_order_acq_rel)) {
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->session_transition != transition ||
          !transition->driver_active) {
        return false;
      }
      retired_target = std::move(transition->target_resources);
      transition->target_resources = advised;
      transition->target_watcher_advised = true;
      impl->session_state = Impl::SessionState::advised_not_published;
    }
    retired_target.reset();
    if (FAILED(advised->advised_visual_service->UnadviseVisualTreeChange(
            advised->watcher.get()))) {
      impl->pause_transition(transition,
                              Impl::SessionState::advised_not_published);
      return false;
    }
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->session_transition != transition ||
          !transition->driver_active) {
        return false;
      }
      retired_target = std::move(transition->target_resources);
      transition->target_resources = target;
      transition->target_watcher_advised = false;
      impl->session_state = Impl::SessionState::watcher_needed;
      transition->driver_active = false;
    }
    return false;
  }

  std::shared_ptr<Impl::SessionTransition> completed;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->session_transition != transition ||
        !transition->driver_active ||
        !impl->callback_admission.reopen_if_drained()) {
      transition->target_watcher_advised = true;
      retired_target = std::move(transition->target_resources);
      transition->target_resources = std::move(advised);
      impl->session_state = Impl::SessionState::advised_not_published;
      transition->driver_active = false;
      return false;
    }
    retired_target = std::move(transition->target_resources);
    transition->target_resources = advised;
    transition->target_watcher_advised = true;
    impl->current_session = advised;
    impl->session_state = Impl::SessionState::current;
    transition->stage = Impl::TransitionStage::complete;
    completed = std::move(impl->session_transition);
  }
  return true;
}

ProbeCleanupResult XamlTaskbarProbe::cancel_pending_work() noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return {};
  }
  const auto keep_alive = impl;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    }
  }
  impl->pending_admission.close();
  const auto before_cancel = impl->pending_actions.reap_all();
  const auto canceled = impl->pending_actions.cancel_all();
  const auto after_cancel = impl->pending_actions.reap_all();
  const bool operation_failed = before_cancel.operation_failed ||
                                canceled.operation_failed ||
                                after_cancel.operation_failed;
  const auto remaining =
      (std::max)(impl->pending_actions.outstanding_count(),
                 impl->pending_admission.live_count());
  return {operation_failed ? ProbeCleanupStatus::retryable_failure
                           : (remaining ? ProbeCleanupStatus::unsafe_in_flight
                                        : ProbeCleanupStatus::complete),
          remaining};
}

ProbeCleanupResult XamlTaskbarProbe::unadvise_watcher() noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return {};
  }
  std::shared_ptr<Impl::SessionTransition> transition;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    }
    if (impl->session_transition) {
      transition = impl->session_transition;
      if (transition->kind != Impl::TransitionKind::unadvise ||
          transition->driver_active) {
        return {ProbeCleanupStatus::unsafe_in_flight, 1};
      }
      transition->driver_active = true;
      impl->session_state = Impl::SessionState::closing;
    } else if (impl->session_state == Impl::SessionState::quiesced) {
      return {};
    } else if (impl->session_state != Impl::SessionState::current &&
               impl->session_state !=
                   Impl::SessionState::current_unwatched) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    } else {
      try {
        transition = std::make_shared<Impl::SessionTransition>();
      } catch (...) {
        return {ProbeCleanupStatus::retryable_failure, 1};
      }
      transition->token =
          g_next_session_transition.fetch_add(1, std::memory_order_relaxed);
      if (!transition->token) {
        return {ProbeCleanupStatus::retryable_failure, 1};
      }
      transition->kind = Impl::TransitionKind::unadvise;
      transition->stage = Impl::TransitionStage::close_admissions;
      transition->old_resources = std::move(impl->current_session);
      transition->watcher_required =
          impl->session_state == Impl::SessionState::current;
      transition->driver_active = true;
      impl->session_transition = transition;
      impl->session_state = Impl::SessionState::closing;
    }
  }

  impl->callback_admission.close();
  impl->pending_admission.close();
  impl->root_binding_admission.close();
  const auto session_close = impl->diagnostics_session.close();
  const auto pending = cancel_pending_work();
  if (impl->root_binding_admission.live_count()) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return {ProbeCleanupStatus::unsafe_in_flight,
            impl->root_binding_admission.live_count()};
  }
  const auto roots_started = impl->begin_root_transition(*transition);
  if (roots_started == Impl::TransitionWorkResult::failed ||
      pending.status == ProbeCleanupStatus::retryable_failure) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return {ProbeCleanupStatus::retryable_failure, 1};
  }
  const auto live = impl->callback_admission.live_count() +
                    impl->pending_admission.live_count() +
                    impl->root_binding_admission.live_count() +
                    impl->diagnostics_session.live_count();
  if (live || session_close == SessionCloseResult::in_flight ||
      roots_started == Impl::TransitionWorkResult::busy ||
      pending.status == ProbeCleanupStatus::unsafe_in_flight) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return {ProbeCleanupStatus::unsafe_in_flight, live ? live : 1U};
  }
  const auto cleanup_only =
      impl->enter_transition_cleanup_only(*transition);
  if (cleanup_only == Impl::TransitionWorkResult::busy) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return {ProbeCleanupStatus::unsafe_in_flight, 1};
  }
  if (cleanup_only == Impl::TransitionWorkResult::failed) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return {ProbeCleanupStatus::retryable_failure, 1};
  }

  transition->stage = Impl::TransitionStage::unadvise_old;
  const auto old = transition->old_resources;
  if (!old) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return {ProbeCleanupStatus::retryable_failure, 1};
  }
  if (old->watcher && !transition->old_watcher_unadvised) {
    if (!old->advised_visual_service ||
        FAILED(old->advised_visual_service->UnadviseVisualTreeChange(
            old->watcher.get()))) {
      impl->pause_transition(transition,
                              Impl::SessionState::unsafe_retained);
      return {ProbeCleanupStatus::retryable_failure, 1};
    }
    transition->old_watcher_unadvised = true;
  }

  std::shared_ptr<Impl::SessionResources> quiesced;
  try {
    quiesced = std::make_shared<Impl::SessionResources>(*old);
    quiesced->watcher.reset();
    quiesced->advised_visual_service.reset();
  } catch (...) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return {ProbeCleanupStatus::retryable_failure, 1};
  }
  std::shared_ptr<Impl::SessionResources> retired_old;
  std::shared_ptr<Impl::SessionTransition> completed_transition;
  std::shared_ptr<Impl::SessionTransition> retired_quiesced;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->session_transition != transition ||
        !transition->driver_active) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    }
    retired_old = std::move(transition->old_resources);
    impl->current_session = quiesced;
    retired_quiesced = std::move(impl->quiesced_cleanup);
    impl->quiesced_cleanup = transition;
    impl->session_state = Impl::SessionState::quiesced;
    transition->stage = Impl::TransitionStage::complete;
    transition->driver_active = false;
    completed_transition = std::move(impl->session_transition);
  }
  return {};
}

ProbeCleanupResult XamlTaskbarProbe::remove_all_capsules() noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return {};
  }
  std::shared_ptr<Impl::SessionTransition> quiesced;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    }
    if (impl->session_state == Impl::SessionState::quiesced &&
        impl->quiesced_cleanup) {
      quiesced = impl->quiesced_cleanup;
      if (quiesced->driver_active) {
        return {ProbeCleanupStatus::unsafe_in_flight, 1};
      }
      quiesced->driver_active = true;
    } else if (impl->session_transition) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    }
  }
  if (quiesced) {
    const auto result = impl->cleanup_transition_capsules(*quiesced);
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->quiesced_cleanup == quiesced) {
        quiesced->driver_active = false;
      }
    }
    const auto remaining = impl->capsules.tracked_element_count() +
                           impl->synthetic_capsule_count();
    if (result == Impl::TransitionWorkResult::busy) {
      return {ProbeCleanupStatus::unsafe_in_flight,
              remaining ? remaining : 1U};
    }
    if (result == Impl::TransitionWorkResult::failed) {
      return {ProbeCleanupStatus::retryable_failure,
              remaining ? remaining : 1U};
    }
    return {ProbeCleanupStatus::complete, remaining};
  }
  std::vector<TaskbarHost> hosts;
  try {
    std::lock_guard lock(impl->host_mutex);
    hosts.reserve(impl->bound_roots.size());
    for (const auto& binding : impl->bound_roots) {
      hosts.push_back(binding.host);
    }
  } catch (...) {
    return {ProbeCleanupStatus::retryable_failure,
            impl->capsules.tracked_element_count()};
  }
  ProbeCleanupStatus aggregate = ProbeCleanupStatus::complete;
  for (const auto& host : hosts) {
    std::shared_ptr<CapsuleCleanupResult> cleanup_result;
    try {
      cleanup_result = std::make_shared<CapsuleCleanupResult>(
          CapsuleCleanupResult{CapsuleCleanupStatus::retryable_failure,
                               impl->capsules.tracked_element_count()});
    } catch (...) {
      aggregate = ProbeCleanupStatus::retryable_failure;
      continue;
    }
    const auto cleanup = [impl = impl.get(), cleanup_result,
                          thread_id = host.thread_id]() noexcept {
      {
        std::vector<Impl::BoundRoot> bindings;
        try {
          std::lock_guard lock(impl->host_mutex);
          for (const auto& binding : impl->bound_roots) {
            if (binding.host.thread_id == thread_id) {
              bindings.push_back(binding);
            }
          }
        } catch (...) {
          return;
        }
        for (const auto& binding : bindings) {
          if (!impl->binding_is_current(binding)) {
            return;
          }
        }
      }
      *cleanup_result = impl->capsules.cleanup_current_thread();
    };
    const auto handoff =
        run_on_host_thread(host, impl, cleanup);
    if (handoff == HandoffResult::timed_out_in_flight) {
      aggregate = ProbeCleanupStatus::unsafe_in_flight;
    } else if (handoff != HandoffResult::completed ||
               cleanup_result->status != CapsuleCleanupStatus::complete) {
      if (aggregate != ProbeCleanupStatus::unsafe_in_flight) {
        aggregate = ProbeCleanupStatus::retryable_failure;
      }
    }
  }
  const auto remaining = impl->capsules.tracked_element_count();
  if (remaining && aggregate == ProbeCleanupStatus::complete) {
    aggregate = ProbeCleanupStatus::retryable_failure;
  }
  if (!remaining) {
    std::lock_guard lock(impl->anchor_binding_mutex);
    impl->anchor_bindings.clear();
  }
  return {aggregate, remaining};
}

ProbeCleanupResult XamlTaskbarProbe::uninitialize_taskbar_threads() noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return {};
  }
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    }
  }
  std::shared_ptr<Impl::SessionTransition> quiesced;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->session_state == Impl::SessionState::quiesced &&
        impl->quiesced_cleanup) {
      quiesced = impl->quiesced_cleanup;
      if (quiesced->driver_active) {
        return {ProbeCleanupStatus::unsafe_in_flight, 1};
      }
      quiesced->driver_active = true;
    } else if (impl->session_transition) {
      return {ProbeCleanupStatus::unsafe_in_flight, 1};
    }
  }
  if (quiesced) {
    const auto capsules = impl->capsules.tracked_element_count() +
                          impl->synthetic_capsule_count();
    Impl::TransitionWorkResult result = Impl::TransitionWorkResult::failed;
    if (!capsules) {
      result = impl->retire_transition_roots(*quiesced);
    }
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->quiesced_cleanup == quiesced) {
        quiesced->driver_active = false;
      }
    }
    std::size_t retained = 0;
    {
      std::lock_guard lock(impl->host_mutex);
      retained = impl->bound_roots.size() +
                 impl->synthetic_retained_roots.size();
    }
    retained = (std::max)(retained, impl->root_bindings.size());
    if (capsules || result == Impl::TransitionWorkResult::failed) {
      return {ProbeCleanupStatus::retryable_failure,
              retained ? retained : 1U};
    }
    if (result == Impl::TransitionWorkResult::busy) {
      return {ProbeCleanupStatus::unsafe_in_flight,
              retained ? retained : 1U};
    }
    return {ProbeCleanupStatus::complete, retained};
  }
  std::vector<TaskbarHost> hosts;
  try {
    std::lock_guard lock(impl->host_mutex);
    hosts.reserve(impl->bound_roots.size());
    for (const auto& binding : impl->bound_roots) {
      hosts.push_back(binding.host);
    }
  } catch (...) {
    return {ProbeCleanupStatus::retryable_failure,
            impl->root_bindings.size()};
  }
  ProbeCleanupStatus aggregate = ProbeCleanupStatus::complete;
  for (const auto& host : hosts) {
    std::shared_ptr<std::atomic<bool>> unbound;
    try {
      unbound = std::make_shared<std::atomic<bool>>(false);
    } catch (...) {
      aggregate = ProbeCleanupStatus::retryable_failure;
      continue;
    }
    const auto unmark = [impl = impl.get(), host, unbound]() noexcept {
      unbound->store(impl->unbind_prevalidated_host_root(host),
                     std::memory_order_release);
    };
    const auto handoff = run_on_host_thread(host, impl, unmark);
    if (handoff == HandoffResult::timed_out_in_flight) {
      aggregate = ProbeCleanupStatus::unsafe_in_flight;
    } else if ((handoff != HandoffResult::completed ||
                !unbound->load(std::memory_order_acquire)) &&
               aggregate != ProbeCleanupStatus::unsafe_in_flight) {
      aggregate = ProbeCleanupStatus::retryable_failure;
    }
  }
  std::size_t retained_roots = 0;
  {
    std::lock_guard lock(impl->host_mutex);
    retained_roots = impl->bound_roots.size();
  }
  const auto remaining =
      (std::max)(retained_roots, impl->root_bindings.size());
  if (!remaining) {
    std::lock_guard lock(impl->host_mutex);
    impl->hosts.clear();
  }
  return {aggregate, remaining};
}

std::size_t XamlTaskbarProbe::tracked_element_count() const noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  return impl ? impl->capsules.tracked_element_count() : 0;
}

ProbeResourceCounts XamlTaskbarProbe::resource_counts() const noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return {};
  }
  const auto keep_alive = impl;
  (void)impl->pending_actions.reap_all();
  const auto synthetic_capsules = impl->synthetic_capsule_count();
  const auto capsules =
      impl->capsules.tracked_element_count() + synthetic_capsules;
  std::size_t retained_roots = 0;
  {
    std::lock_guard lock(impl->host_mutex);
    retained_roots = impl->bound_roots.size() +
                     impl->synthetic_retained_roots.size();
  }
  const auto callback_leases = impl->callback_admission.live_count();
  const auto session_leases = impl->diagnostics_session.live_count();
  const auto callbacks =
      callback_leases > (std::numeric_limits<std::size_t>::max)() -
                            session_leases
          ? (std::numeric_limits<std::size_t>::max)()
          : callback_leases + session_leases;
  std::array<std::shared_ptr<Impl::SessionResources>, 5> session_resources;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    session_resources[0] = impl->current_session;
    if (impl->session_transition) {
      session_resources[1] = impl->session_transition->old_resources;
      session_resources[2] = impl->session_transition->target_resources;
    }
    if (impl->quiesced_cleanup) {
      session_resources[3] = impl->quiesced_cleanup->old_resources;
      session_resources[4] = impl->quiesced_cleanup->target_resources;
    }
  }
  impl->notify_resource_snapshot_for_test();
  std::size_t watcher_references = 0;
  for (std::size_t index = 0; index < session_resources.size(); ++index) {
    const auto& resources = session_resources[index];
    if (!resources || !resources->watcher) {
      continue;
    }
    const bool already_counted = std::any_of(
        session_resources.begin(), session_resources.begin() + index,
        [&resources](const auto& prior) {
          return prior && prior.get() == resources.get();
        });
    if (!already_counted) {
      ++watcher_references;
    }
  }
  const auto pending_actions =
      (std::max)(impl->pending_actions.outstanding_count(),
                 impl->pending_admission.live_count());
  return {callbacks,
          pending_actions,
          outstanding_handoffs(impl.get()),
          capsules,
          capsules,
          watcher_references,
          (std::max)(retained_roots, impl->root_bindings.size()),
          static_cast<std::size_t>(com_object_count_snapshot())};
}

bool XamlTaskbarProbe::release_for_unload() noexcept {
  std::unique_lock unload_lock(unload_mutex_);
  auto impl = impl_.load(std::memory_order_acquire);
  if (!impl) {
    return false;
  }

  std::uint64_t transition_token = 0;
  bool rollback_only = false;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->unload_transition) {
      if (impl->session_state != Impl::SessionState::unloading ||
          impl->unload_transition->driver_active) {
        return false;
      }
      impl->unload_transition->driver_active = true;
      transition_token = impl->unload_transition->token;
      rollback_only = true;
    } else if (impl->site_preparation || impl->session_transition ||
               (impl->quiesced_cleanup &&
                impl->quiesced_cleanup->driver_active) ||
               (impl->session_state != Impl::SessionState::quiesced &&
                impl->session_state != Impl::SessionState::empty)) {
      return false;
    } else {
      transition_token =
          g_next_unload_transition.fetch_add(1, std::memory_order_relaxed);
      if (!transition_token) {
        return false;
      }
      impl->unload_transition.emplace(Impl::UnloadTransition{
          .token = transition_token,
          .prior_state = impl->session_state,
          .session = impl->current_session,
          .quiesced = impl->quiesced_cleanup,
          .callback_was_accepting = impl->callback_admission.accepting(),
          .pending_was_accepting = impl->pending_admission.accepting(),
          .root_was_accepting = impl->root_binding_admission.accepting(),
          .driver_active = true,
      });
      impl->session_state = Impl::SessionState::unloading;
    }
  }

  const auto leave_rollback_pending = [&]() noexcept {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->unload_transition &&
        impl->unload_transition->token == transition_token &&
        impl->session_state == Impl::SessionState::unloading) {
      impl->unload_transition->driver_active = false;
    }
  };

  const auto finish_rollback =
      [&](TapAdmissionClaim& tap_claim,
          std::optional<ProbeCapsuleManager::ReleasePermit>&
              capsule_release) noexcept {
    Impl::UnloadTransition transition;
    bool transition_valid = false;
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      const bool exact_transition =
          impl->unload_transition &&
          impl->unload_transition->token == transition_token;
      if (exact_transition) {
        transition = *impl->unload_transition;
      }
      transition_valid =
          exact_transition &&
          impl->unload_transition->driver_active &&
          impl->session_state == Impl::SessionState::unloading &&
          !impl->session_transition &&
          impl->current_session == impl->unload_transition->session &&
          impl->quiesced_cleanup == impl->unload_transition->quiesced;
      if (!transition_valid) {
        if (exact_transition) {
          impl->unload_transition->driver_active = false;
        }
      }
    }

    capsule_release.reset();
    auto rollback_claim = transition.tap_claim ? transition.tap_claim
                                                : tap_claim;
    if (rollback_claim) {
      const auto reopened_claim = rollback_claim;
      if (!reopen_tap_object_admission(rollback_claim)) {
        leave_rollback_pending();
        return;
      }
      tap_claim = {};
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->unload_transition &&
          impl->unload_transition->token == transition_token &&
          impl->unload_transition->tap_claim.owner == reopened_claim.owner &&
          impl->unload_transition->tap_claim.generation ==
              reopened_claim.generation) {
        impl->unload_transition->tap_claim = {};
      }
      transition.tap_claim = {};
    }
    if (!transition_valid) {
      return;
    }

    if ((transition.callback_was_accepting &&
         impl->callback_admission.live_count()) ||
        (transition.pending_was_accepting &&
         impl->pending_admission.live_count()) ||
        (transition.root_was_accepting &&
         impl->root_binding_admission.live_count()) ||
        impl->diagnostics_session.live_count()) {
      leave_rollback_pending();
      return;
    }

    bool restored = true;
    if (transition.callback_was_accepting) {
      restored = impl->callback_admission.reopen_if_drained() && restored;
    }
    if (transition.pending_was_accepting) {
      restored = impl->pending_admission.reopen_if_drained() && restored;
    }
    if (transition.root_was_accepting) {
      restored = impl->root_binding_admission.reopen_if_drained() && restored;
    }
    if (!restored) {
      impl->callback_admission.close();
      impl->pending_admission.close();
      impl->root_binding_admission.close();
      leave_rollback_pending();
      return;
    }

    impl->notify_unload_rollback_for_test();
    const auto published_observer =
        impl->unload_rollback_published_observer.load(
            std::memory_order_acquire);
    void* const published_context =
        impl->unload_rollback_published_context.load(
            std::memory_order_acquire);
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      impl->unload_transition.reset();
      // This is the last publication and the last dereference of impl on a
      // successful rollback.  The published observer was snapshotted above.
      impl->session_state = transition.prior_state;
    }
    if (published_observer) {
      published_observer(published_context);
    }
  };

  TapAdmissionClaim tap_claim;
  std::optional<ProbeCapsuleManager::ReleasePermit> capsule_release;
  if (rollback_only) {
    finish_rollback(tap_claim, capsule_release);
    return false;
  }

  impl->callback_admission.close();
  impl->pending_admission.close();
  impl->root_binding_admission.close();
  (void)impl->diagnostics_session.close();

  const auto counts = resource_counts();
  if (counts.callbacks != 0 || counts.pending_actions != 0 ||
      counts.handoffs != 0 || counts.capsules != 0 || counts.timers != 0 ||
      counts.watcher_references != 0 || counts.host_bindings != 0) {
    finish_rollback(tap_claim, capsule_release);
    return false;
  }

  if (impl->fail_next_tap_can_unload_for_test.exchange(
          false, std::memory_order_acq_rel) ||
      !(tap_claim = claim_tap_object_admission_for_unload(
            reinterpret_cast<std::uintptr_t>(this)))) {
    finish_rollback(tap_claim, capsule_release);
    return false;
  }
  bool tap_claim_published = false;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->unload_transition &&
        impl->unload_transition->token == transition_token &&
        impl->unload_transition->driver_active &&
        impl->session_state == Impl::SessionState::unloading &&
        !impl->unload_transition->tap_claim) {
      impl->unload_transition->tap_claim = tap_claim;
      tap_claim_published = true;
    }
  }

  if (!tap_claim_published) {
    finish_rollback(tap_claim, capsule_release);
    return false;
  }
  impl->notify_tap_precommit_for_test();

  const auto final_counts = resource_counts();
  if (impl->fail_next_unload_final_counts_for_test.exchange(
          false, std::memory_order_acq_rel) ||
      impl->callback_admission.live_count() ||
      impl->pending_admission.live_count() ||
      impl->root_binding_admission.live_count() ||
      impl->diagnostics_session.live_count() || final_counts.callbacks != 0 ||
      final_counts.pending_actions != 0 || final_counts.handoffs != 0 ||
      final_counts.capsules != 0 || final_counts.timers != 0 ||
      final_counts.watcher_references != 0 ||
      final_counts.host_bindings != 0 || final_counts.com_objects != 0) {
    finish_rollback(tap_claim, capsule_release);
    return false;
  }

  capsule_release = impl->capsules.try_claim_release();
  if (!capsule_release ||
      impl->fail_next_unload_before_detach_for_test.exchange(
          false, std::memory_order_acq_rel)) {
    finish_rollback(tap_claim, capsule_release);
    return false;
  }

  std::optional<DiagnosticsSessionGate::DetachedSession> detached;
  std::shared_ptr<Impl::SessionResources> retired_session;
  std::shared_ptr<Impl::SessionTransition> retired_cleanup;
  std::shared_ptr<Impl::SessionResources> session;
  std::shared_ptr<Impl::SessionTransition> quiesced;
  HMODULE xaml_module = nullptr;
  UnloadBoundaryObserver boundary_observer = nullptr;
  void* boundary_context = nullptr;
  HMODULE diagnostics_module = nullptr;
  bool commit_started = false;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    const bool commit_valid =
        impl->session_state == Impl::SessionState::unloading &&
        impl->unload_transition &&
        impl->unload_transition->token == transition_token &&
        impl->unload_transition->driver_active &&
        !impl->session_transition &&
        impl->current_session == impl->unload_transition->session &&
        impl->quiesced_cleanup == impl->unload_transition->quiesced &&
        impl->unload_transition->tap_claim.owner == tap_claim.owner &&
        impl->unload_transition->tap_claim.generation ==
            tap_claim.generation &&
        tap_admission_claim_matches(tap_claim) &&
        impl->capsules.release_claim_matches(*capsule_release);
    if (commit_valid) {
      session = impl->unload_transition->session;
      quiesced = impl->unload_transition->quiesced;
      if (session && session->identity.epoch) {
        detached = impl->diagnostics_session.detach_closed(session->identity);
      }
      if (!session || !session->identity.epoch || detached) {
        commit_started = true;
        impl->unload_transition.reset();
        retired_session = std::move(impl->current_session);
        retired_cleanup = std::move(impl->quiesced_cleanup);
        xaml_module = std::exchange(impl->xaml_module, nullptr);
      }
    }
    if (commit_started) {
      boundary_observer =
          impl->unload_boundary_observer.load(std::memory_order_acquire);
      boundary_context =
          impl->unload_boundary_context.load(std::memory_order_acquire);
      if (impl->diagnostics_initialized.load(std::memory_order_acquire) &&
          !impl->diagnostics_reference_released.exchange(
              true, std::memory_order_acq_rel)) {
        diagnostics_module = impl->module;
      }
      impl->diagnostics_epoch.store(0, std::memory_order_release);
      impl->diagnostics_initialized.store(false, std::memory_order_release);
    }
  }
  if (!commit_started) {
    finish_rollback(tap_claim, capsule_release);
    return false;
  }

  // Both permits were exact-validated before detach.  Their commits are
  // no-fail, consume-once state transitions; any invariant violation stays
  // permanently closed and must never run ordinary rollback.
  impl->capsules.finalize_release(std::move(*capsule_release));
  commit_tap_object_admission_prevalidated(tap_claim);

  auto retired_impl =
      impl_.exchange(std::shared_ptr<Impl>{}, std::memory_order_acq_rel);
  if (retired_impl.get() != impl.get()) {
    std::lock_guard lock(g_tap_object_admission_mutex);
    g_com_counter_fault = true;
  }
  unload_lock.unlock();

  detached.reset();
  session.reset();
  quiesced.reset();
  retired_session.reset();
  retired_cleanup.reset();
  retired_impl.reset();
  impl.reset();

  if (boundary_observer) {
    boundary_observer(boundary_context);
  }
  if (diagnostics_module) {
    FreeLibrary(diagnostics_module);
  }
  if (xaml_module) {
    FreeLibrary(xaml_module);
  }
  return true;
}

long XamlTaskbarProbe::set_site(IUnknown* site) noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl) {
    return E_UNEXPECTED;
  }
  std::shared_ptr<Impl::SessionResources> prepared;
  std::shared_ptr<Impl::SessionResources> retired_initial;
  std::shared_ptr<Impl::SessionTransition> transition;
  std::shared_ptr<Impl::SessionTransition> transition_candidate;
  std::optional<Impl::SitePreparation> completed_preparation;
  std::uint64_t preparation_token = 0;
  Impl::SessionState preparation_prior_state = Impl::SessionState::empty;
  std::shared_ptr<Impl::SessionResources> preparation_base;
  bool installed_initial_site = false;

  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation) {
      return HRESULT_FROM_WIN32(ERROR_BUSY);
    }
    if (impl->session_transition) {
      transition = impl->session_transition;
      const bool exact_remove =
          !site && transition->kind == Impl::TransitionKind::remove &&
          !transition->requested_site_argument;
      const bool exact_replace =
          site && transition->kind == Impl::TransitionKind::replace &&
          transition->requested_site_argument == site;
      if (transition->driver_active || (!exact_remove && !exact_replace)) {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
      }
      transition->driver_active = true;
      impl->session_state =
          transition->stage == Impl::TransitionStage::advise_target
              ? Impl::SessionState::advise_in_progress
              : Impl::SessionState::closing;
    } else if (!site) {
      if (impl->session_state == Impl::SessionState::empty) {
        return S_OK;
      }
      if (impl->session_state == Impl::SessionState::initial_site) {
        retired_initial = std::move(impl->current_session);
        impl->session_state = Impl::SessionState::empty;
      } else if (impl->session_state == Impl::SessionState::current ||
                 impl->session_state ==
                     Impl::SessionState::current_unwatched) {
        try {
          transition = std::make_shared<Impl::SessionTransition>();
        } catch (...) {
          return E_OUTOFMEMORY;
        }
        transition->token =
            g_next_session_transition.fetch_add(1, std::memory_order_relaxed);
        if (!transition->token) {
          return E_FAIL;
        }
        transition->kind = Impl::TransitionKind::remove;
        transition->stage = Impl::TransitionStage::close_admissions;
        transition->old_resources = std::move(impl->current_session);
        transition->watcher_required =
            impl->session_state == Impl::SessionState::current;
        transition->driver_active = true;
        impl->session_transition = transition;
        impl->session_state = Impl::SessionState::closing;
      } else {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
      }
    } else {
      if (impl->session_state != Impl::SessionState::empty &&
          impl->session_state != Impl::SessionState::initial_site &&
          impl->session_state != Impl::SessionState::current &&
          impl->session_state != Impl::SessionState::current_unwatched) {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
      }
      preparation_token =
          g_next_session_transition.fetch_add(1, std::memory_order_relaxed);
      if (!preparation_token) {
        return E_FAIL;
      }
      preparation_prior_state = impl->session_state;
      preparation_base = impl->current_session;
      impl->site_preparation.emplace(Impl::SitePreparation{
          preparation_token, site, preparation_prior_state,
          preparation_base});
    }
  }

  const auto rollback_preparation = [&]() noexcept {
    if (!preparation_token) {
      return;
    }
    std::optional<Impl::SitePreparation> retired_preparation;
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->site_preparation &&
        impl->site_preparation->token == preparation_token &&
        impl->session_state == preparation_prior_state &&
        !impl->session_transition &&
        impl->current_session == preparation_base) {
      retired_preparation = std::move(impl->site_preparation);
      impl->site_preparation.reset();
    }
  };

  if (preparation_token) {
    ComPtr<IUnknown> replacement_site{site};
    ComPtr<IXamlDiagnostics> replacement_diagnostics;
    ComPtr<IVisualTreeService> replacement_service;
    HRESULT result = site->QueryInterface(__uuidof(IXamlDiagnostics),
                                          replacement_diagnostics.put_void());
    if (SUCCEEDED(result)) {
      result = site->QueryInterface(__uuidof(IVisualTreeService),
                                    replacement_service.put_void());
    }
    if (FAILED(result)) {
      rollback_preparation();
      return result;
    }
    const auto site_identity = Impl::com_identity(replacement_site.get());
    const auto diagnostics_identity =
        Impl::com_identity(replacement_diagnostics.get());
    if (!site_identity || !diagnostics_identity) {
      rollback_preparation();
      return E_NOINTERFACE;
    }
    try {
      prepared = std::make_shared<Impl::SessionResources>();
      prepared->site = std::move(replacement_site);
      prepared->diagnostics = std::move(replacement_diagnostics);
      prepared->visual_service = std::move(replacement_service);
      prepared->adapter =
          std::make_shared<Impl::XamlDiagnosticsQueryAdapter>(
              prepared->diagnostics);
      prepared->identity.site_identity = site_identity;
      prepared->identity.diagnostics_identity = diagnostics_identity;
    } catch (...) {
      rollback_preparation();
      return E_OUTOFMEMORY;
    }
    if (preparation_prior_state == Impl::SessionState::current ||
        preparation_prior_state == Impl::SessionState::current_unwatched) {
      try {
        transition_candidate = std::make_shared<Impl::SessionTransition>();
      } catch (...) {
        rollback_preparation();
        return E_OUTOFMEMORY;
      }
    }

    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (!impl->site_preparation ||
          impl->site_preparation->token != preparation_token ||
          impl->site_preparation->requested_site_argument != site ||
          impl->session_state != preparation_prior_state ||
          impl->session_transition ||
          impl->current_session != preparation_base) {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
      }
      completed_preparation = std::move(impl->site_preparation);
      impl->site_preparation.reset();
      if (preparation_base &&
          preparation_base->same_target(
              prepared->identity.site_identity,
              prepared->identity.diagnostics_identity)) {
        return S_OK;
      }
      if (preparation_prior_state == Impl::SessionState::empty) {
        impl->current_session = prepared;
        impl->session_state = Impl::SessionState::initial_site;
        installed_initial_site = true;
      } else if (preparation_prior_state == Impl::SessionState::initial_site) {
        retired_initial = std::move(impl->current_session);
        impl->current_session = prepared;
        impl->session_state = Impl::SessionState::initial_site;
        installed_initial_site = true;
      } else {
        transition = std::move(transition_candidate);
        transition->token = preparation_token;
        transition->kind = Impl::TransitionKind::replace;
        transition->stage = Impl::TransitionStage::close_admissions;
        transition->old_resources = std::move(impl->current_session);
        transition->target_resources = prepared;
        transition->requested_site_argument = site;
        transition->watcher_required =
            preparation_prior_state == Impl::SessionState::current;
        transition->driver_active = true;
        impl->session_transition = transition;
        impl->session_state = Impl::SessionState::closing;
      }
    }
  }

  if (!transition) {
    if (installed_initial_site &&
        !impl->diagnostics_reference_released.exchange(true) &&
        impl->module) {
      // InitializeXamlDiagnosticsEx adds a TAP module reference. The bootstrap
      // worker owns a separate reference, so this balances only diagnostics'
      // load.
      FreeLibrary(impl->module);
    }
    return S_OK;
  }

  impl->callback_admission.close();
  impl->pending_admission.close();
  impl->root_binding_admission.close();
  const auto session_close = impl->diagnostics_session.close();
  const auto pending = cancel_pending_work();
  if (impl->root_binding_admission.live_count()) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return HRESULT_FROM_WIN32(ERROR_BUSY);
  }
  const auto roots_started = impl->begin_root_transition(*transition);
  if (roots_started == Impl::TransitionWorkResult::failed ||
      pending.status == ProbeCleanupStatus::retryable_failure) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return E_FAIL;
  }
  if (session_close == SessionCloseResult::in_flight ||
      roots_started == Impl::TransitionWorkResult::busy ||
      pending.status == ProbeCleanupStatus::unsafe_in_flight ||
      impl->callback_admission.live_count() ||
      impl->pending_admission.live_count() ||
      impl->root_binding_admission.live_count()) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return HRESULT_FROM_WIN32(ERROR_BUSY);
  }

  const auto cleanup_only =
      impl->enter_transition_cleanup_only(*transition);
  if (cleanup_only == Impl::TransitionWorkResult::busy) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return HRESULT_FROM_WIN32(ERROR_BUSY);
  }
  if (cleanup_only == Impl::TransitionWorkResult::failed) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return E_FAIL;
  }

  transition->stage = Impl::TransitionStage::cleanup_capsules;
  const auto capsules = impl->cleanup_transition_capsules(*transition);
  if (capsules == Impl::TransitionWorkResult::busy) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return HRESULT_FROM_WIN32(ERROR_BUSY);
  }
  if (capsules == Impl::TransitionWorkResult::failed) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return E_FAIL;
  }

  transition->stage = Impl::TransitionStage::retire_roots;
  const auto roots_retired = impl->retire_transition_roots(*transition);
  if (roots_retired == Impl::TransitionWorkResult::busy) {
    impl->pause_transition(transition, Impl::SessionState::closing);
    return HRESULT_FROM_WIN32(ERROR_BUSY);
  }
  if (roots_retired == Impl::TransitionWorkResult::failed) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return E_FAIL;
  }

  transition->stage = Impl::TransitionStage::unadvise_old;
  const auto old = transition->old_resources;
  if (!old) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return E_FAIL;
  }
  if (old->watcher && !transition->old_watcher_unadvised) {
    if (!old->advised_visual_service ||
        FAILED(old->advised_visual_service->UnadviseVisualTreeChange(
            old->watcher.get()))) {
      impl->pause_transition(transition,
                              Impl::SessionState::unsafe_retained);
      return E_FAIL;
    }
    transition->old_watcher_unadvised = true;
  }

  if (transition->kind == Impl::TransitionKind::remove) {
    std::optional<DiagnosticsSessionGate::DetachedSession> detached;
    std::shared_ptr<Impl::SessionTransition> completed;
    long remove_result = S_OK;
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->session_transition != transition ||
          !transition->driver_active) {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
      }
      if (!transition->old_session_detached) {
        if (old->identity.epoch) {
          detached = impl->diagnostics_session.detach_closed(old->identity);
          if (!detached) {
            impl->session_state = Impl::SessionState::unsafe_retained;
            transition->driver_active = false;
            remove_result = E_FAIL;
          }
        }
        if (SUCCEEDED(remove_result)) {
          transition->old_session_detached = true;
        }
      }
      if (SUCCEEDED(remove_result)) {
        impl->diagnostics_epoch.store(0, std::memory_order_release);
        impl->diagnostics_initialized.store(false,
                                            std::memory_order_release);
        if (!impl->reopen_remove_admissions_if_drained()) {
          impl->session_state = Impl::SessionState::unsafe_retained;
          transition->driver_active = false;
          remove_result = E_FAIL;
        }
      }
      if (SUCCEEDED(remove_result)) {
        impl->session_state = Impl::SessionState::empty;
        transition->stage = Impl::TransitionStage::complete;
        completed = std::move(impl->session_transition);
      }
    }
    return remove_result;
  }

  std::shared_ptr<Impl::SessionResources> retired_target;
  if (!transition->target_resources ||
      !transition->target_resources->identity.epoch) {
    const auto replacement_epoch =
        g_next_diagnostics_epoch.fetch_add(1, std::memory_order_relaxed);
    if (!replacement_epoch || !transition->target_resources) {
      impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
      return E_FAIL;
    }
    std::shared_ptr<Impl::SessionResources> epoch_target;
    try {
      epoch_target = std::make_shared<Impl::SessionResources>(
          *transition->target_resources);
      epoch_target->identity.epoch = replacement_epoch;
    } catch (...) {
      impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
      return E_OUTOFMEMORY;
    }
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->session_transition != transition ||
          !transition->driver_active) {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
      }
      retired_target = std::move(transition->target_resources);
      transition->target_resources = std::move(epoch_target);
    }
    retired_target.reset();
  }

  if (transition->target_watcher_advised) {
    const auto advised = transition->target_resources;
    std::shared_ptr<Impl::SessionResources> cleaned;
    try {
      if (impl->fail_next_unadvised_resource_allocation_for_test.exchange(
              false, std::memory_order_acq_rel)) {
        throw std::bad_alloc{};
      }
      if (!advised || !advised->advised_visual_service || !advised->watcher) {
        throw std::bad_alloc{};
      }
      cleaned = std::make_shared<Impl::SessionResources>(*advised);
      cleaned->watcher.reset();
      cleaned->advised_visual_service.reset();
    } catch (...) {
      impl->pause_transition(transition,
                              Impl::SessionState::advised_not_published);
      return E_OUTOFMEMORY;
    }
    if (FAILED(advised->advised_visual_service->UnadviseVisualTreeChange(
            advised->watcher.get()))) {
      impl->pause_transition(transition,
                              Impl::SessionState::advised_not_published);
      return E_FAIL;
    }
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      retired_target = std::move(transition->target_resources);
      transition->target_resources = std::move(cleaned);
      transition->target_watcher_advised = false;
      impl->session_state = Impl::SessionState::watcher_needed;
    }
    retired_target.reset();
  }

  if (transition->watcher_required) {
    transition->stage = Impl::TransitionStage::advise_target;
    const auto target = transition->target_resources;
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      impl->session_state = Impl::SessionState::advise_in_progress;
    }
    auto* raw_watcher = target
                            ? new (std::nothrow)
                                  Impl::Watcher(impl, target->identity)
                            : nullptr;
    if (!raw_watcher) {
      impl->pause_transition(transition, Impl::SessionState::watcher_needed);
      return E_OUTOFMEMORY;
    }
    ComPtr<IVisualTreeServiceCallback2> watcher;
    watcher.attach(raw_watcher);
    std::shared_ptr<Impl::SessionResources> advised;
    try {
      advised = std::make_shared<Impl::SessionResources>(*target);
      advised->advised_visual_service = target->visual_service;
      advised->watcher = std::move(watcher);
    } catch (...) {
      impl->pause_transition(transition, Impl::SessionState::watcher_needed);
      return E_OUTOFMEMORY;
    }
    if (!target->visual_service ||
        FAILED(target->visual_service->AdviseVisualTreeChange(raw_watcher))) {
      impl->pause_transition(transition, Impl::SessionState::watcher_needed);
      return E_FAIL;
    }
    const bool inject_allocation_failure =
        impl->fail_next_advised_resource_allocation_for_test.exchange(
            false, std::memory_order_acq_rel);
    bool inject_publication_failure = false;
    {
      std::lock_guard lock(impl->diagnostics_mutex);
      if (impl->session_transition != transition ||
          !transition->driver_active) {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
      }
      retired_target = std::move(transition->target_resources);
      transition->target_resources = std::move(advised);
      transition->target_watcher_advised = true;
      inject_publication_failure =
          std::exchange(impl->fail_next_target_publication_for_test, false);
      if (inject_publication_failure) {
        impl->session_state = Impl::SessionState::advised_not_published;
        transition->driver_active = false;
      } else if (inject_allocation_failure) {
        impl->session_state = Impl::SessionState::advised_not_published;
      }
    }
    retired_target.reset();
    if (inject_publication_failure) {
      return E_FAIL;
    }
    if (inject_allocation_failure) {
      const auto retained_advised = transition->target_resources;
      if (!retained_advised || !retained_advised->advised_visual_service ||
          !retained_advised->watcher ||
          FAILED(retained_advised->advised_visual_service
                     ->UnadviseVisualTreeChange(
                         retained_advised->watcher.get()))) {
        impl->pause_transition(transition,
                                Impl::SessionState::advised_not_published);
        return E_OUTOFMEMORY;
      }
      {
        std::lock_guard lock(impl->diagnostics_mutex);
        if (impl->session_transition != transition ||
            !transition->driver_active) {
          return HRESULT_FROM_WIN32(ERROR_BUSY);
        }
        retired_target = std::move(transition->target_resources);
        transition->target_resources = target;
        transition->target_watcher_advised = false;
        impl->session_state = Impl::SessionState::watcher_needed;
        transition->driver_active = false;
      }
      return E_OUTOFMEMORY;
    }
  }

  transition->stage = Impl::TransitionStage::publish_target;
  const auto target = transition->target_resources;
  if (!target || !target->adapter) {
    impl->pause_transition(transition, Impl::SessionState::unsafe_retained);
    return E_FAIL;
  }
  auto detached = impl->diagnostics_session.replace_closed(
      old->identity, target->identity, target->adapter);
  if (!detached) {
    impl->pause_transition(
        transition, transition->target_watcher_advised
                        ? Impl::SessionState::advised_not_published
                        : Impl::SessionState::unsafe_retained);
    return E_FAIL;
  }

  std::shared_ptr<Impl::SessionTransition> completed;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    if (impl->session_transition != transition ||
        !transition->driver_active ||
        !impl->pending_admission.reopen_if_drained() ||
        !impl->root_binding_admission.reopen_if_drained() ||
        !impl->callback_admission.reopen_if_drained()) {
      return E_FAIL;
    }
    impl->current_session = target;
    impl->session_state = transition->watcher_required
                               ? Impl::SessionState::current
                               : Impl::SessionState::current_unwatched;
    impl->diagnostics_epoch.store(target->identity.epoch,
                                   std::memory_order_release);
    impl->diagnostics_initialized.store(true, std::memory_order_release);
    transition->stage = Impl::TransitionStage::complete;
    completed = std::move(impl->session_transition);
  }
  return S_OK;
}

long XamlTaskbarProbe::get_site(const void* interface_id, void** site) noexcept {
  const auto impl = this->impl_.load(std::memory_order_acquire);
  if (!impl || !interface_id || !site) {
    return E_POINTER;
  }
  *site = nullptr;
  std::shared_ptr<Impl::SessionResources> resources;
  {
    std::lock_guard lock(impl->diagnostics_mutex);
    resources = impl->session_transition
                    ? impl->session_transition->old_resources
                    : impl->current_session;
  }
  if (!resources || !resources->site) {
    return E_FAIL;
  }
  return resources->site->QueryInterface(
      *static_cast<const IID*>(interface_id), site);
}

namespace {

class ProbeTap final : public IObjectWithSite {
 public:
  [[nodiscard]] bool counted() const noexcept { return lifetime_.valid(); }

  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interface_id,
                                           void** object) noexcept override {
    if (!object) {
      return E_POINTER;
    }
    *object = nullptr;
    if (interface_id == __uuidof(IUnknown) ||
        interface_id == __uuidof(IObjectWithSite)) {
      *object = static_cast<IObjectWithSite*>(this);
      AddRef();
      return S_OK;
    }
    return E_NOINTERFACE;
  }

  ULONG STDMETHODCALLTYPE AddRef() noexcept override {
    return references_.fetch_add(1) + 1;
  }

  ULONG STDMETHODCALLTYPE Release() noexcept override {
    const ULONG references = references_.fetch_sub(1) - 1;
    if (!references) {
      delete this;
    }
    return references;
  }

  HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) noexcept override {
    XamlTaskbarProbe* probe = g_active_probe.load(std::memory_order_acquire);
    return probe ? probe->set_site(site) : E_UNEXPECTED;
  }

  HRESULT STDMETHODCALLTYPE GetSite(REFIID interface_id,
                                    void** site) noexcept override {
    XamlTaskbarProbe* probe = g_active_probe.load(std::memory_order_acquire);
    return probe ? probe->get_site(&interface_id, site) : E_UNEXPECTED;
  }

 private:
  ~ProbeTap() = default;
  ComObjectLifetime lifetime_;
  std::atomic<ULONG> references_{1};
};

class ProbeClassFactory final : public IClassFactory {
 public:
  [[nodiscard]] bool counted() const noexcept { return lifetime_.valid(); }

  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interface_id,
                                           void** object) noexcept override {
    if (!object) {
      return E_POINTER;
    }
    *object = nullptr;
    if (interface_id == __uuidof(IUnknown) ||
        interface_id == __uuidof(IClassFactory)) {
      *object = static_cast<IClassFactory*>(this);
      AddRef();
      return S_OK;
    }
    return E_NOINTERFACE;
  }

  ULONG STDMETHODCALLTYPE AddRef() noexcept override {
    return references_.fetch_add(1) + 1;
  }

  ULONG STDMETHODCALLTYPE Release() noexcept override {
    const ULONG references = references_.fetch_sub(1) - 1;
    if (!references) {
      delete this;
    }
    return references;
  }

  HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,
                                           REFIID interface_id,
                                           void** object) noexcept override {
    if (!object) {
      return E_POINTER;
    }
    *object = nullptr;
    if (outer) {
      return CLASS_E_NOAGGREGATION;
    }
    auto* tap = new (std::nothrow) ProbeTap();
    if (!tap) {
      return E_OUTOFMEMORY;
    }
    if (!tap->counted()) {
      tap->Release();
      return E_UNEXPECTED;
    }
    const HRESULT result = tap->QueryInterface(interface_id, object);
    tap->Release();
    return result;
  }

  HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) noexcept override {
    return update_server_lock_count(lock != FALSE) ? S_OK : E_UNEXPECTED;
  }

 private:
  ~ProbeClassFactory() = default;
  ComObjectLifetime lifetime_;
  std::atomic<ULONG> references_{1};
};

}  // namespace

long tap_get_class_object(const void* class_id,
                          const void* interface_id,
                          void** object) noexcept {
  if (!class_id || !interface_id || !object) {
    return E_POINTER;
  }
  *object = nullptr;
  if (*static_cast<const CLSID*>(class_id) != kProbeTapClsid) {
    return CLASS_E_CLASSNOTAVAILABLE;
  }
  std::lock_guard lock(g_tap_object_admission_mutex);
  if (g_tap_object_admission_phase != TapAdmissionPhase::open) {
    return CLASS_E_CLASSNOTAVAILABLE;
  }
  auto* factory = new (std::nothrow) ProbeClassFactory();
  if (!factory) {
    return E_OUTOFMEMORY;
  }
  if (!factory->counted()) {
    factory->Release();
    return E_UNEXPECTED;
  }
  const HRESULT result = factory->QueryInterface(
      *static_cast<const IID*>(interface_id), object);
  factory->Release();
  return result;
}

long tap_can_unload_now() noexcept {
  std::lock_guard lock(g_tap_object_admission_mutex);
  return g_tap_object_admission_phase != TapAdmissionPhase::claimed &&
                 !g_tap_initialization_in_flight && !g_com_counter_fault &&
                 g_com_object_count == 0 &&
                 g_server_lock_count == 0
             ? S_OK
             : S_FALSE;
}

}  // namespace cq::bridge
