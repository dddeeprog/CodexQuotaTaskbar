#pragma once

#include "probe_safety.h"

#include <inspectable.h>

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>

namespace cq::bridge {

class XamlTaskbarProbeTestPeer;
using ManagerRemoveSnapshotObserver = void (*)(void* context) noexcept;

inline constexpr double kProbeCapsuleWidthDip = 190.0;
inline constexpr double kProbeCapsuleHeightDip = 36.0;
inline constexpr std::uint32_t kProbeCapsuleDisplayMilliseconds = 5000;
inline constexpr wchar_t kProbeCapsuleText[] = L"CQ \u00b7 PROBE";

enum class CapsuleCleanupStatus : std::uint8_t {
  complete,
  retryable_failure,
};

using AuthorizedCapsuleInsertion = bool (*)(
    void* context,
    CleanupAuthorityGate::MutationLease mutation_authority) noexcept;

[[nodiscard]] bool run_authorized_capsule_insertion(
    const std::shared_ptr<CleanupAuthorityGate>& authority,
    AuthorizedCapsuleInsertion insertion,
    void* context) noexcept;

struct CapsuleCleanupResult final {
  CapsuleCleanupStatus status = CapsuleCleanupStatus::complete;
  std::size_t remaining = 0;
};

class ProbeCapsuleManager final {
 private:
  struct Impl;

 public:
  class ReleasePermit final {
   public:
    ReleasePermit() noexcept = default;
    ReleasePermit(const ReleasePermit&) = delete;
    ReleasePermit& operator=(const ReleasePermit&) = delete;
    ReleasePermit(ReleasePermit&& other) noexcept;
    ReleasePermit& operator=(ReleasePermit&& other) noexcept;
    ~ReleasePermit();

    [[nodiscard]] explicit operator bool() const noexcept;

   private:
    friend class ProbeCapsuleManager;
    ReleasePermit(std::shared_ptr<Impl> state,
                  std::uint64_t generation) noexcept;
    void cancel() noexcept;

    std::shared_ptr<Impl> state_;
    std::uint64_t generation_ = 0;
  };

  ProbeCapsuleManager() noexcept;
  ~ProbeCapsuleManager();
  ProbeCapsuleManager(const ProbeCapsuleManager&) = delete;
  ProbeCapsuleManager& operator=(const ProbeCapsuleManager&) = delete;

  [[nodiscard]] bool insert(std::uint64_t anchor_handle,
                            IInspectable* anchor,
                            IInspectable* parent_grid,
                            CleanupAuthorizationBinding binding,
                            std::shared_ptr<CleanupAuthorityGate> authority,
                            std::shared_ptr<CleanupAuthorizationEvidenceSource>
                                evidence_source,
                            CleanupAuthorityGate::MutationLease
                                mutation_authority) noexcept;
  [[nodiscard]] CapsuleCleanupResult remove_anchor_on_current_thread(
      std::uint64_t anchor_handle) noexcept;
  [[nodiscard]] CapsuleCleanupResult cleanup_current_thread() noexcept;
  [[nodiscard]] CapsuleCleanupResult cleanup_current_thread_for_transition(
      const CleanupAuthorityGate::TransitionPermit& permit) noexcept;
  [[nodiscard]] std::size_t tracked_element_count() const noexcept;
  [[nodiscard]] std::optional<ReleasePermit> try_claim_release() noexcept;
  [[nodiscard]] bool release_claim_matches(
      const ReleasePermit& permit) const noexcept;
  void finalize_release(ReleasePermit&& permit) noexcept;
  [[nodiscard]] bool release() noexcept;

 private:
  friend class XamlTaskbarProbeTestPeer;
  [[nodiscard]] bool attach_primitive_record_for_test(
      CleanupAuthorizationBinding binding,
      std::shared_ptr<CleanupAuthorityGate> authority,
      std::shared_ptr<CleanupAuthorizationEvidenceSource> evidence_source,
      std::shared_ptr<CapsuleCleanupAccess> access) noexcept;
  [[nodiscard]] CapsuleCleanupResult trigger_primitive_records_for_test(
      CapsuleCleanupTrigger trigger) noexcept;
  [[nodiscard]] bool attach_record_for_test(
      DWORD owner_thread_id,
      std::function<CapsuleCleanupStatus()> cleanup,
      std::shared_ptr<void> retained_owner,
      std::uint64_t& record_id) noexcept;
  [[nodiscard]] CapsuleCleanupStatus remove_record_for_test(
      DWORD owner_thread_id,
      std::uint64_t record_id) noexcept;
  [[nodiscard]] bool mutex_available_for_test() const noexcept;
  [[nodiscard]] bool set_remove_snapshot_observer_for_test(
      ManagerRemoveSnapshotObserver observer,
      void* context) noexcept;
  [[nodiscard]] bool set_attach_admission_observer_for_test(
      void (*observer)(void* context) noexcept,
      void* context) noexcept;
  [[nodiscard]] bool set_retention_fault_for_test(bool fault) noexcept;
  [[nodiscard]] std::uint64_t claim_release_for_test() noexcept;
  [[nodiscard]] bool cancel_release_for_test(
      std::uint64_t generation) noexcept;
  [[nodiscard]] bool commit_release_for_test(
      std::uint64_t generation) noexcept;
  static void destroy_impl(Impl* state) noexcept;

  std::shared_ptr<Impl> impl_;
};

}  // namespace cq::bridge
