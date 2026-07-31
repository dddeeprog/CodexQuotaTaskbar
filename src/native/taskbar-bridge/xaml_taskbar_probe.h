#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

struct IUnknown;

namespace cq::bridge {

class BridgeLifecycle;
class XamlTaskbarProbeTestPeer;

inline constexpr wchar_t kSystemTrayFrameType[] = L"SystemTray.SystemTrayFrame";
inline constexpr std::size_t kMaximumVisualTypeNameLength = 128;

struct VisualNodeFacts {
  std::wstring type_name;
  bool parent_is_grid;
  bool belongs_to_validated_taskbar_window;
};

enum class ProbeDecision : std::uint8_t {
  ignore,
  accept,
};

enum class ProbeCleanupStatus : std::uint8_t {
  complete,
  retryable_failure,
  unsafe_in_flight,
};

struct ProbeCleanupResult final {
  ProbeCleanupStatus status = ProbeCleanupStatus::complete;
  std::size_t remaining = 0;
};

struct ProbeResourceCounts final {
  std::size_t callbacks = 0;
  std::size_t pending_actions = 0;
  std::size_t handoffs = 0;
  std::size_t capsules = 0;
  std::size_t timers = 0;
  std::size_t watcher_references = 0;
  std::size_t host_bindings = 0;
  std::size_t com_objects = 0;

  [[nodiscard]] bool empty() const noexcept;
};

[[nodiscard]] ProbeDecision evaluate_visual_node(
    const VisualNodeFacts& facts) noexcept;

class XamlTaskbarProbe final {
 public:
  XamlTaskbarProbe(void* module, BridgeLifecycle& lifecycle) noexcept;
  ~XamlTaskbarProbe();
  XamlTaskbarProbe(const XamlTaskbarProbe&) = delete;
  XamlTaskbarProbe& operator=(const XamlTaskbarProbe&) = delete;

  [[nodiscard]] bool initialize_existing_taskbar_threads() noexcept;
  [[nodiscard]] bool initialize_xaml_diagnostics() noexcept;
  [[nodiscard]] bool advise_watcher() noexcept;
  [[nodiscard]] ProbeCleanupResult cancel_pending_work() noexcept;
  [[nodiscard]] ProbeCleanupResult unadvise_watcher() noexcept;
  [[nodiscard]] ProbeCleanupResult remove_all_capsules() noexcept;
  [[nodiscard]] ProbeCleanupResult uninitialize_taskbar_threads() noexcept;
  [[nodiscard]] std::size_t tracked_element_count() const noexcept;
  [[nodiscard]] ProbeResourceCounts resource_counts() const noexcept;
  [[nodiscard]] bool release_for_unload() noexcept;

  [[nodiscard]] long set_site(IUnknown* site) noexcept;
  [[nodiscard]] long get_site(const void* interface_id, void** site) noexcept;

 private:
  friend class XamlTaskbarProbeTestPeer;
  struct Impl;

  // Public entries need a retained Impl snapshot that can linearize with the
  // final unload detach.  A short mutex-backed slot provides that lifetime
  // boundary without making shared_ptr's implementation-specific atomic-wait
  // runtime part of the bridge's deployment contract.
  class ImplSlot final {
   public:
    [[nodiscard]] std::shared_ptr<Impl> load(
        std::memory_order) const noexcept {
      std::lock_guard lock(mutex_);
      return value_;
    }

    void store(std::shared_ptr<Impl> value, std::memory_order) noexcept {
      {
        std::lock_guard lock(mutex_);
        value.swap(value_);
      }
      // A replaced Impl, if any, is released only after the slot is unlocked.
    }

    [[nodiscard]] std::shared_ptr<Impl> exchange(
        std::shared_ptr<Impl> value, std::memory_order) noexcept {
      std::lock_guard lock(mutex_);
      value.swap(value_);
      return value;
    }

   private:
    mutable std::mutex mutex_;
    std::shared_ptr<Impl> value_;
  };

  ImplSlot impl_;
  mutable std::mutex unload_mutex_;
};

[[nodiscard]] long tap_get_class_object(const void* class_id,
                                        const void* interface_id,
                                        void** object) noexcept;
[[nodiscard]] long tap_can_unload_now() noexcept;

}  // namespace cq::bridge
