#pragma once

#include "probe_capsule.h"
#include "probe_safety.h"

#include <cstdint>
#include <functional>
#include <memory>
#include <optional>

namespace cq::bridge {

class XamlTaskbarProbe;
using WatcherDestructionObserver = void (*)(void* context) noexcept;
using UnloadBoundaryObserver = void (*)(void* context) noexcept;
using TapPrecommitObserver = void (*)(void* context) noexcept;
using UnloadRollbackObserver = void (*)(void* context) noexcept;
using ResourceSnapshotObserver = void (*)(void* context) noexcept;
using HandoffDestructionObserver = void (*)(void* context) noexcept;
using ManagerAttachAdmissionObserver = void (*)(void* context) noexcept;

enum class ProbeSessionStateForTest : std::uint8_t {
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

struct ProbeSessionAdmissionSnapshot final {
  ProbeSessionStateForTest state = ProbeSessionStateForTest::empty;
  bool pending_open = false;
  bool root_open = false;
  bool callback_open = false;
  bool transition_pending = false;
  bool transition_driver_active = false;
};

// Non-exported peer used by the offline native tests to exercise the real
// SetSite orchestration without loading XAML diagnostics into Explorer.
class XamlTaskbarProbeTestPeer final {
 public:
  [[nodiscard]] static PendingAsyncStatus map_pending_status_for_test(
      std::int32_t raw_status) noexcept;
  [[nodiscard]] static bool activate_diagnostics_session(
      XamlTaskbarProbe& probe,
      std::uint64_t epoch) noexcept;
  [[nodiscard]] static bool attach_cleanup_authority(
      XamlTaskbarProbe& probe,
      std::shared_ptr<CleanupAuthorityGate> authority) noexcept;
  [[nodiscard]] static bool attach_synthetic_retained_root(
      XamlTaskbarProbe& probe,
      std::shared_ptr<CleanupAuthorityGate> authority,
      std::function<bool()> cleanup) noexcept;
  [[nodiscard]] static bool attach_primitive_capsule_record(
      XamlTaskbarProbe& probe,
      CleanupAuthorizationBinding binding,
      std::shared_ptr<CleanupAuthorityGate> authority,
      std::shared_ptr<CleanupAuthorizationEvidenceSource> evidence_source,
      std::shared_ptr<CapsuleCleanupAccess> access) noexcept;
  [[nodiscard]] static CapsuleCleanupResult
  trigger_primitive_capsule_record(XamlTaskbarProbe& probe,
                                   CapsuleCleanupTrigger trigger) noexcept;
  [[nodiscard]] static bool fail_next_target_publication(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_advised_resource_allocation(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_unadvised_resource_allocation(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_activation_allocation(
      XamlTaskbarProbe& probe,
      unsigned int stage) noexcept;
  [[nodiscard]] static bool fail_next_activation_publication(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_tap_can_unload(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_unload_final_counts(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_unload_before_detach(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_root_retirement(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool fail_next_remove_admission_baseline(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool diagnostics_mutex_available(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool set_watcher_destruction_observer(
      XamlTaskbarProbe& probe,
      WatcherDestructionObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool set_unload_boundary_observer(
      XamlTaskbarProbe& probe,
      UnloadBoundaryObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool set_tap_precommit_observer(
      XamlTaskbarProbe& probe,
      TapPrecommitObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool set_unload_rollback_observer(
      XamlTaskbarProbe& probe,
      UnloadRollbackObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool set_unload_rollback_published_observer(
      XamlTaskbarProbe& probe,
      UnloadRollbackObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool unload_mutex_available(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool session_is_unloading(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool has_impl(XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool while_impl_snapshot(
      XamlTaskbarProbe& probe,
      const std::function<void()>& action) noexcept;
  [[nodiscard]] static bool reset_tap_object_admission() noexcept;
  [[nodiscard]] static bool fail_next_tap_reopen() noexcept;
  [[nodiscard]] static std::uint64_t claim_tap_object_admission(
      std::uintptr_t owner) noexcept;
  [[nodiscard]] static bool reopen_tap_object_admission(
      std::uintptr_t owner,
      std::uint64_t generation) noexcept;
  [[nodiscard]] static bool commit_tap_object_admission(
      std::uintptr_t owner,
      std::uint64_t generation) noexcept;
  [[nodiscard]] static bool tap_object_admission_closed() noexcept;
  [[nodiscard]] static bool set_active_probe(
      XamlTaskbarProbe* probe) noexcept;
  [[nodiscard]] static XamlTaskbarProbe* active_probe() noexcept;
  [[nodiscard]] static long tap_object_count() noexcept;
  [[nodiscard]] static long tap_server_lock_count() noexcept;
  [[nodiscard]] static bool tap_counter_fault() noexcept;
  [[nodiscard]] static bool set_tap_server_lock_count(long count) noexcept;
  [[nodiscard]] static bool set_resource_snapshot_observer(
      XamlTaskbarProbe& probe,
      ResourceSnapshotObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool detach_transition_resources(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool try_pending_admission(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool while_pending_admission(
      XamlTaskbarProbe& probe,
      const std::function<void()>& action) noexcept;
  [[nodiscard]] static bool while_callback_admission(
      XamlTaskbarProbe& probe,
      const std::function<void()>& action) noexcept;
  [[nodiscard]] static bool while_root_admission(
      XamlTaskbarProbe& probe,
      const std::function<void()>& action) noexcept;
  [[nodiscard]] static std::optional<ProbeSessionAdmissionSnapshot>
  session_admission_snapshot(XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static PendingScheduleResult schedule_pending_for_test(
      XamlTaskbarProbe& probe,
      PendingModuleBoundary& module_boundary,
      PendingDispatchBoundary& dispatch_boundary,
      PendingDispatchCallback callback) noexcept;
  [[nodiscard]] static PendingLedgerOperationResult reap_pending_for_test(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static std::size_t pending_registry_count(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static std::size_t pending_retiring_count(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool pending_mutex_available(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static std::optional<PendingActionStage> pending_stage(
      XamlTaskbarProbe& probe,
      std::uint64_t id) noexcept;
  [[nodiscard]] static bool attach_manager_record(
      ProbeCapsuleManager& manager,
      DWORD owner_thread_id,
      std::function<CapsuleCleanupStatus()> cleanup,
      std::shared_ptr<void> retained_owner,
      std::uint64_t& record_id) noexcept;
  [[nodiscard]] static CapsuleCleanupStatus remove_manager_record(
      ProbeCapsuleManager& manager,
      DWORD owner_thread_id,
      std::uint64_t record_id) noexcept;
  [[nodiscard]] static bool manager_mutex_available(
      const ProbeCapsuleManager& manager) noexcept;
  [[nodiscard]] static bool set_manager_remove_snapshot_observer(
      ProbeCapsuleManager& manager,
      ManagerRemoveSnapshotObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool set_manager_attach_admission_observer(
      ProbeCapsuleManager& manager,
      ManagerAttachAdmissionObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool set_manager_retention_fault(
      ProbeCapsuleManager& manager,
      bool fault) noexcept;
  [[nodiscard]] static std::uint64_t claim_manager_release(
      ProbeCapsuleManager& manager) noexcept;
  [[nodiscard]] static bool cancel_manager_release(
      ProbeCapsuleManager& manager,
      std::uint64_t generation) noexcept;
  [[nodiscard]] static bool commit_manager_release(
      ProbeCapsuleManager& manager,
      std::uint64_t generation) noexcept;
  [[nodiscard]] static HandoffResult run_handoff_for_test(
      XamlTaskbarProbe& probe,
      HandoffWin32Boundary& boundary,
      std::function<void()> action) noexcept;
  [[nodiscard]] static bool dispatch_handoff_for_test(
      std::uintptr_t cookie) noexcept;
  [[nodiscard]] static std::size_t reap_handoffs_for_test(
      XamlTaskbarProbe& probe) noexcept;
  [[nodiscard]] static bool handoff_mutex_available() noexcept;
  [[nodiscard]] static bool set_handoff_destruction_observer(
      HandoffDestructionObserver observer,
      void* context) noexcept;
  [[nodiscard]] static bool while_diagnostics_lease(
      XamlTaskbarProbe& probe,
      const std::function<void()>& action) noexcept;
  [[nodiscard]] static std::optional<DiagnosticsSessionIdentity>
  diagnostics_identity(XamlTaskbarProbe& probe) noexcept;
};

}  // namespace cq::bridge
