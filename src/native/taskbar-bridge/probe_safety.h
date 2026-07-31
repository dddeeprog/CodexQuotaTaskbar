#pragma once

#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <vector>

namespace cq::bridge {

struct HostRootBinding final {
  std::uintptr_t host_identity = 0;
  std::uint32_t thread_id = 0;
  std::uintptr_t xaml_root_identity = 0;
  std::uintptr_t content_identity = 0;
  std::uint64_t host_generation = 0;
  std::uint64_t root_generation = 0;
  std::uint64_t diagnostics_epoch = 0;
};

struct VisualRootEvidence final {
  std::uintptr_t host_identity = 0;
  std::uint32_t thread_id = 0;
  std::uintptr_t xaml_root_identity = 0;
  std::uintptr_t content_identity = 0;
  std::uint64_t host_generation = 0;
  std::uint64_t root_generation = 0;
  std::uint64_t diagnostics_epoch = 0;
  bool host_is_still_valid = false;
};

enum class RootMatchResult : std::uint8_t {
  exact,
  missing_binding,
  wrong_thread,
  wrong_xaml_root,
  wrong_content,
  stale_host_generation,
  stale_root_generation,
  stale_diagnostics_epoch,
  destroyed_host,
};

class HostRootBindingRegistry final {
 public:
  [[nodiscard]] bool bind(const HostRootBinding& binding) noexcept;
  [[nodiscard]] bool invalidate(std::uintptr_t host_identity,
                                std::uint64_t host_generation,
                                std::uint64_t root_generation,
                                std::uint64_t diagnostics_epoch) noexcept;
  void invalidate_all() noexcept;
  [[nodiscard]] RootMatchResult match(
      const VisualRootEvidence& evidence) const noexcept;
  [[nodiscard]] std::size_t size() const noexcept;

 private:
  mutable std::mutex mutex_;
  std::vector<HostRootBinding> bindings_;
};

enum class HandoffResult : std::uint8_t {
  completed,
  timed_out_unclaimed,
  timed_out_in_flight,
  action_failed,
  cancelled,
};

class HandoffRequest final {
 public:
  using Action = std::function<void()>;

  explicit HandoffRequest(Action action);
  HandoffRequest(const HandoffRequest&) = delete;
  HandoffRequest& operator=(const HandoffRequest&) = delete;

  [[nodiscard]] bool try_claim() noexcept;
  void run_claimed_action() noexcept;
  void request_cancel() noexcept;
  [[nodiscard]] HandoffResult wait_for(
      std::chrono::milliseconds timeout) noexcept;

 private:
  enum class State : std::uint8_t {
    waiting,
    claimed,
    completed,
    action_failed,
    cancelled,
    timed_out_unclaimed,
  };

  [[nodiscard]] static HandoffResult result_for(State state) noexcept;
  [[nodiscard]] static bool terminal(State state) noexcept;

  Action action_;
  std::mutex mutex_;
  std::condition_variable condition_;
  State state_ = State::waiting;
  bool cancel_requested_ = false;
};

struct HandoffRetirementTarget final {
  std::uintptr_t window = 0;
  std::uint32_t process_id = 0;
  std::uint32_t thread_id = 0;
  std::uintptr_t retirement_token = 0;

  [[nodiscard]] bool operator==(
      const HandoffRetirementTarget&) const noexcept = default;
};

enum class HandoffRetirementStage : std::uint8_t {
  waiting_for_token_publication,
  token_published_waiting_for_hook,
  no_hook_waiting_for_token_clear,
  hook_installed,
  unhook_in_progress,
  unhook_uncertain,
  unhooked_waiting_for_barrier,
  barrier_in_progress,
  retirement_ready_waiting_for_token_clear,
  token_clear_in_progress,
  token_clear_uncertain,
  token_cleared_waiting_for_detach,
  detached,
};

enum class HandoffRetirementAttempt : std::uint8_t {
  ready_to_detach,
  retryable_unsafe,
  permanently_unsafe,
};

enum class HandoffTokenClearResult : std::uint8_t {
  cleared,
  retryable_failure,
  permanently_unsafe,
};

class HandoffRetirementBoundary {
 public:
  virtual ~HandoffRetirementBoundary() = default;
  [[nodiscard]] virtual bool unhook(std::uintptr_t hook) noexcept = 0;
  [[nodiscard]] virtual bool send_barrier(
      const HandoffRetirementTarget& target) noexcept = 0;
  [[nodiscard]] virtual HandoffTokenClearResult clear_retirement_token(
      const HandoffRetirementTarget& target) noexcept = 0;
  virtual void release_module(std::uintptr_t module) noexcept = 0;
};

class HandoffWin32Boundary : public HandoffRetirementBoundary {
 public:
  [[nodiscard]] virtual bool acquire_self_module(
      std::uintptr_t callback_address,
      std::uintptr_t& module) noexcept = 0;
  [[nodiscard]] virtual bool publish_retirement_token(
      const HandoffRetirementTarget& target) noexcept = 0;
  [[nodiscard]] virtual bool retirement_token_matches(
      const HandoffRetirementTarget& target) noexcept = 0;
  [[nodiscard]] virtual std::uintptr_t install_callwndproc(
      std::uintptr_t callback_address,
      std::uint32_t thread_id) noexcept = 0;
  [[nodiscard]] virtual bool send_action(
      const HandoffRetirementTarget& target,
      std::uint32_t message,
      std::uintptr_t cookie) noexcept = 0;
};

class HandoffModuleRelease final {
 public:
  HandoffModuleRelease() noexcept = default;
  HandoffModuleRelease(const HandoffModuleRelease&) = delete;
  HandoffModuleRelease& operator=(const HandoffModuleRelease&) = delete;
  HandoffModuleRelease(HandoffModuleRelease&& other) noexcept;
  HandoffModuleRelease& operator=(HandoffModuleRelease&& other) noexcept;

  [[nodiscard]] bool release(HandoffRetirementBoundary& boundary) noexcept;
  [[nodiscard]] bool pending() const noexcept;

 private:
  friend class HandoffRetirement;
  explicit HandoffModuleRelease(std::uintptr_t module) noexcept;
  std::uintptr_t module_ = 0;
};

class HandoffRetirement final {
 public:
  HandoffRetirement(std::uintptr_t module,
                    HandoffRetirementTarget target) noexcept;
  HandoffRetirement(const HandoffRetirement&) = delete;
  HandoffRetirement& operator=(const HandoffRetirement&) = delete;

  [[nodiscard]] bool mark_token_published() noexcept;
  [[nodiscard]] bool attach_hook(std::uintptr_t hook) noexcept;
  [[nodiscard]] bool mark_hook_install_failed() noexcept;
  [[nodiscard]] bool enter_hook_frame() noexcept;
  void exit_hook_frame() noexcept;
  [[nodiscard]] HandoffRetirementAttempt attempt(
      HandoffRetirementBoundary& boundary) noexcept;
  [[nodiscard]] std::optional<HandoffModuleRelease>
  detach_ready() noexcept;
  [[nodiscard]] std::optional<HandoffModuleRelease>
  detach_without_published_token() noexcept;
  [[nodiscard]] HandoffRetirementStage stage() const noexcept;
  [[nodiscard]] bool outstanding() const noexcept;
  [[nodiscard]] std::size_t active_frames() const noexcept;
  [[nodiscard]] std::uint64_t entry_sequence() const noexcept;

 private:
  std::uintptr_t module_ = 0;
  std::uintptr_t hook_ = 0;
  const HandoffRetirementTarget target_;
  mutable std::mutex retirement_mutex_;
  HandoffRetirementStage stage_ =
      HandoffRetirementStage::waiting_for_token_publication;
  std::uint64_t attempt_token_ = 0;
  std::uint64_t verified_entry_sequence_ = 0;
  std::atomic<std::size_t> active_frames_{0};
  std::atomic<std::uint64_t> entry_sequence_{0};
  std::atomic<bool> frame_counter_fault_{false};
};

class AdmissionGate final {
 public:
  class Lease final {
   public:
    Lease() noexcept = default;
    Lease(const Lease&) = delete;
    Lease& operator=(const Lease&) = delete;
    Lease(Lease&& other) noexcept;
    Lease& operator=(Lease&& other) noexcept;
    ~Lease();

   private:
    friend class AdmissionGate;
    explicit Lease(AdmissionGate& owner) noexcept;
    void release() noexcept;
    AdmissionGate* owner_ = nullptr;
  };

  explicit AdmissionGate(std::size_t maximum_live) noexcept;
  AdmissionGate(const AdmissionGate&) = delete;
  AdmissionGate& operator=(const AdmissionGate&) = delete;

  [[nodiscard]] std::optional<Lease> try_acquire() noexcept;
  void close() noexcept;
  [[nodiscard]] bool reopen_if_drained() noexcept;
  [[nodiscard]] bool accepting() const noexcept;
  [[nodiscard]] bool drained() const noexcept;
  [[nodiscard]] std::size_t live_count() const noexcept;
  [[nodiscard]] std::size_t counter_faults() const noexcept;

 private:
  friend class Lease;
  void release() noexcept;

  const std::size_t maximum_live_;
  mutable std::mutex mutex_;
  bool accepting_ = true;
  std::size_t live_ = 0;
  std::size_t counter_faults_ = 0;
};

// A queued dispatcher callback is not retired merely because its body ran or
// cancellation was requested.  CoreDispatcher's action status is the proof
// that the delegate has returned, so the ledger keeps every lifetime resource
// until a poll observes a terminal status.
enum class PendingAsyncStatus : std::uint8_t {
  started,
  completed,
  canceled,
  error,
  unknown,
};

class PendingAsyncAction {
 public:
  virtual ~PendingAsyncAction() = default;
  [[nodiscard]] virtual PendingAsyncStatus status() = 0;
  virtual void cancel() = 0;
};

using PendingDispatchCallback = std::function<void()>;

class PendingDispatchBoundary {
 public:
  virtual ~PendingDispatchBoundary() = default;
  [[nodiscard]] virtual std::shared_ptr<PendingAsyncAction> queue(
      std::shared_ptr<PendingDispatchCallback> callback) = 0;
};

class PendingModuleBoundary {
 public:
  virtual ~PendingModuleBoundary() = default;
  [[nodiscard]] virtual bool acquire_self_module(
      std::uintptr_t& module) noexcept = 0;
  virtual void release_module(std::uintptr_t module) noexcept = 0;
};

enum class PendingActionStage : std::uint8_t {
  preparing,
  queued,
  cancel_requested,
  retiring,
  retained_unknown,
};

enum class PendingScheduleStatus : std::uint8_t {
  queued,
  rejected,
  retained_unknown,
  resource_failure,
};

struct PendingScheduleResult final {
  PendingScheduleStatus status = PendingScheduleStatus::resource_failure;
  std::uint64_t id = 0;
};

struct PendingLedgerOperationResult final {
  bool operation_failed = false;
  std::size_t affected = 0;
  std::size_t retired = 0;
  std::size_t remaining = 0;
};

class PendingActionLedger final {
 public:
  explicit PendingActionLedger(std::size_t maximum_live) noexcept;
  PendingActionLedger(const PendingActionLedger&) = delete;
  PendingActionLedger& operator=(const PendingActionLedger&) = delete;
  ~PendingActionLedger();

  [[nodiscard]] PendingScheduleResult schedule(
      PendingModuleBoundary& module_boundary,
      PendingDispatchBoundary& dispatch_boundary,
      std::shared_ptr<void> owner,
      AdmissionGate::Lease lease,
      PendingDispatchCallback callback,
      bool fail_publication_for_test = false) noexcept;
  [[nodiscard]] PendingLedgerOperationResult reap_all() noexcept;
  [[nodiscard]] PendingLedgerOperationResult cancel_all() noexcept;
  [[nodiscard]] std::size_t outstanding_count() const noexcept;
  [[nodiscard]] std::size_t registry_count() const noexcept;
  [[nodiscard]] std::size_t retiring_count() const noexcept;
  [[nodiscard]] bool mutex_available() const noexcept;
  [[nodiscard]] std::optional<PendingActionStage> stage(
      std::uint64_t id) const noexcept;

 private:
  struct Record;

  const std::size_t maximum_live_;
  mutable std::mutex mutex_;
  std::vector<std::shared_ptr<Record>> records_;
  std::uint64_t next_id_ = 1;
  std::size_t retiring_ = 0;
  bool consistency_fault_ = false;
};

struct DiagnosticsSessionIdentity final {
  std::uint64_t epoch = 0;
  std::uintptr_t site_identity = 0;
  std::uintptr_t diagnostics_identity = 0;

  [[nodiscard]] bool operator==(
      const DiagnosticsSessionIdentity&) const noexcept = default;
};

class DiagnosticsQueryAdapter {
 public:
  virtual ~DiagnosticsQueryAdapter() = default;
  [[nodiscard]] virtual bool get_inspectable(std::uint64_t handle,
                                              void** object) noexcept = 0;
  [[nodiscard]] virtual bool get_handle(void* object,
                                        std::uint64_t* handle) noexcept = 0;
  [[nodiscard]] virtual bool inspect_xaml(void* object) noexcept = 0;
};

enum class SessionCloseResult : std::uint8_t {
  drained,
  in_flight,
};

class DiagnosticsSessionGate final {
 private:
  struct State;

 public:
  struct DetachedSession final {
    DiagnosticsSessionIdentity identity{};
    std::shared_ptr<DiagnosticsQueryAdapter> adapter;
  };

  class Lease final {
   public:
    Lease() noexcept = default;
    Lease(const Lease&) = delete;
    Lease& operator=(const Lease&) = delete;
    Lease(Lease&& other) noexcept;
    Lease& operator=(Lease&& other) noexcept;
    ~Lease();

    [[nodiscard]] bool get_inspectable(std::uint64_t handle,
                                       void** object) noexcept;
    [[nodiscard]] bool get_handle(void* object,
                                  std::uint64_t* handle) noexcept;
    [[nodiscard]] bool inspect_xaml(void* object) noexcept;
    [[nodiscard]] DiagnosticsSessionIdentity identity() const noexcept;

   private:
    friend class DiagnosticsSessionGate;
    Lease(std::shared_ptr<State> state,
          std::shared_ptr<DiagnosticsQueryAdapter> adapter,
          DiagnosticsSessionIdentity identity) noexcept;
    void release() noexcept;

    std::shared_ptr<State> state_;
    std::shared_ptr<DiagnosticsQueryAdapter> adapter_;
    DiagnosticsSessionIdentity identity_{};
  };

  DiagnosticsSessionGate() noexcept;
  DiagnosticsSessionGate(const DiagnosticsSessionGate&) = delete;
  DiagnosticsSessionGate& operator=(const DiagnosticsSessionGate&) = delete;

  [[nodiscard]] bool install(
      DiagnosticsSessionIdentity identity,
      std::shared_ptr<DiagnosticsQueryAdapter> adapter) noexcept;
  [[nodiscard]] std::optional<Lease> try_acquire(
      DiagnosticsSessionIdentity expected) noexcept;
  [[nodiscard]] std::optional<DiagnosticsSessionIdentity> identity()
      const noexcept;
  [[nodiscard]] SessionCloseResult close() noexcept;
  [[nodiscard]] std::optional<DetachedSession> detach_closed(
      DiagnosticsSessionIdentity expected) noexcept;
  [[nodiscard]] std::optional<DetachedSession> replace_closed(
      DiagnosticsSessionIdentity expected,
      DiagnosticsSessionIdentity replacement_identity,
      std::shared_ptr<DiagnosticsQueryAdapter> replacement_adapter) noexcept;
  [[nodiscard]] bool reset_if_drained() noexcept;
  [[nodiscard]] std::size_t live_count() const noexcept;

 private:
  std::shared_ptr<State> state_;
};

class DiagnosticsCallbackAddAdapter {
 public:
  virtual ~DiagnosticsCallbackAddAdapter() = default;
  [[nodiscard]] virtual bool process_add(
      DiagnosticsSessionGate::Lease& session) noexcept = 0;
};

class DiagnosticsCallbackPipeline final {
 public:
  explicit DiagnosticsCallbackPipeline(DiagnosticsSessionGate& session)
      noexcept;
  DiagnosticsCallbackPipeline(const DiagnosticsCallbackPipeline&) = delete;
  DiagnosticsCallbackPipeline& operator=(const DiagnosticsCallbackPipeline&) =
      delete;

  [[nodiscard]] bool process_add(
      DiagnosticsSessionIdentity expected,
      DiagnosticsCallbackAddAdapter& adapter) noexcept;

 private:
  DiagnosticsSessionGate& session_;
};

inline constexpr std::size_t kMaximumCleanupAncestryDepth = 512;

enum class CapsuleCleanupTrigger : std::uint8_t {
  timer,
  explicit_removal,
  shutdown,
  rollback,
};

struct CleanupIdentityPath final {
  std::array<std::uintptr_t, kMaximumCleanupAncestryDepth> identities{};
  std::size_t count = 0;
  bool complete = false;
};

struct CleanupRetainedIdentity final {
  std::uintptr_t identity = 0;
  std::shared_ptr<void> owner;
};

class CleanupAncestryRetention final {
 public:
  [[nodiscard]] bool initialize(
      CleanupIdentityPath path,
      std::vector<CleanupRetainedIdentity> owners) noexcept;
  [[nodiscard]] bool matches(const CleanupIdentityPath& path) const noexcept;
  [[nodiscard]] std::size_t size() const noexcept;

 private:
  CleanupIdentityPath path_{};
  std::vector<CleanupRetainedIdentity> owners_;
  bool initialized_ = false;
};

struct CleanupAuthorizationBinding final {
  std::uint64_t record_key = 0;
  std::uint32_t owner_thread_id = 0;
  std::uintptr_t host_identity = 0;
  std::uintptr_t xaml_root_identity = 0;
  std::uintptr_t content_identity = 0;
  std::uint64_t host_generation = 0;
  std::uint64_t root_generation = 0;
  std::uint64_t diagnostics_epoch = 0;
  std::uintptr_t anchor_identity = 0;
  std::uintptr_t grid_identity = 0;
  CleanupIdentityPath anchor_to_content{};
};

struct CleanupAuthorizationEvidence final {
  std::uint32_t owner_thread_id = 0;
  std::uintptr_t host_identity = 0;
  std::uintptr_t xaml_root_identity = 0;
  std::uintptr_t content_identity = 0;
  std::uint64_t host_generation = 0;
  std::uint64_t root_generation = 0;
  std::uint64_t diagnostics_epoch = 0;
  std::uintptr_t anchor_identity = 0;
  std::uintptr_t grid_identity = 0;
  std::uintptr_t anchor_xaml_root_identity = 0;
  std::uintptr_t anchor_content_identity = 0;
  std::uintptr_t grid_xaml_root_identity = 0;
  std::uintptr_t grid_content_identity = 0;
  CleanupIdentityPath anchor_to_content{};
  bool host_is_still_valid = false;
};

[[nodiscard]] bool cleanup_binding_is_well_formed(
    const CleanupAuthorizationBinding& binding) noexcept;

class CleanupAuthorizationEvidenceSource {
 public:
  virtual ~CleanupAuthorizationEvidenceSource() = default;
  [[nodiscard]] virtual bool resolve(
      CapsuleCleanupTrigger trigger,
      CleanupAuthorizationEvidence& evidence) noexcept = 0;
};

class CleanupAuthorization;
class CapsuleCleanupWorkflow;
class ProbeCapsuleManager;

enum class CleanupAuthorityMode : std::uint8_t {
  forward,
  closed,
  cleanup_only,
  revoked,
};

class CleanupAuthorityGate final {
 private:
  struct State;

  class Lease final {
   public:
    Lease(const Lease&) = delete;
    Lease& operator=(const Lease&) = delete;
    Lease(Lease&& other) noexcept;
    Lease& operator=(Lease&& other) noexcept;
    ~Lease();

   private:
    friend class CleanupAuthorityGate;
    friend class CleanupAuthorization;
    friend class CapsuleCleanupTransaction;
    friend class CapsuleCleanupWorkflow;
    explicit Lease(std::shared_ptr<State> state) noexcept;
    void release() noexcept;
    [[nodiscard]] bool valid() const noexcept;

    std::shared_ptr<State> state_;
  };

 public:
  class TransitionPermit final {
   public:
    TransitionPermit(const TransitionPermit&) = delete;
    TransitionPermit& operator=(const TransitionPermit&) = delete;
    TransitionPermit(TransitionPermit&& other) noexcept;
    TransitionPermit& operator=(TransitionPermit&& other) noexcept;
    ~TransitionPermit() = default;

   private:
    friend class CleanupAuthorityGate;
    friend class CapsuleCleanupWorkflow;
    friend class ProbeCapsuleManager;
    TransitionPermit(std::shared_ptr<State> state,
                     std::uint64_t transition_token) noexcept;
    [[nodiscard]] bool valid_for(
        const CleanupAuthorityGate& gate) const noexcept;

    std::shared_ptr<State> state_;
    std::uint64_t transition_token_ = 0;
  };

  class MutationLease final {
   public:
    MutationLease(const MutationLease&) = delete;
    MutationLease& operator=(const MutationLease&) = delete;
    MutationLease(MutationLease&& other) noexcept;
    MutationLease& operator=(MutationLease&& other) noexcept;
    ~MutationLease();

   private:
    friend class CleanupAuthorityGate;
    friend class ProbeCapsuleManager;
    explicit MutationLease(std::shared_ptr<State> state) noexcept;
    void release() noexcept;
    [[nodiscard]] bool valid_for(
        const CleanupAuthorityGate& gate) const noexcept;

    std::shared_ptr<State> state_;
  };

  CleanupAuthorityGate() noexcept;
  CleanupAuthorityGate(const CleanupAuthorityGate&) = delete;
  CleanupAuthorityGate& operator=(const CleanupAuthorityGate&) = delete;

  [[nodiscard]] SessionCloseResult close() noexcept;
  [[nodiscard]] bool reopen_if_drained() noexcept;
  [[nodiscard]] std::optional<TransitionPermit> begin_transition(
      std::uint64_t transition_token,
      SessionCloseResult& close_result) noexcept;
  [[nodiscard]] bool enter_cleanup_only(
      std::uint64_t transition_token,
      const TransitionPermit& permit) noexcept;
  [[nodiscard]] std::optional<SessionCloseResult> close_cleanup_only(
      std::uint64_t transition_token,
      const TransitionPermit& permit) noexcept;
  [[nodiscard]] SessionCloseResult revoke_permanently() noexcept;
  [[nodiscard]] std::optional<MutationLease> try_acquire_mutation() noexcept;
  [[nodiscard]] bool available() const noexcept;
  [[nodiscard]] bool accepting() const noexcept;
  [[nodiscard]] CleanupAuthorityMode mode() const noexcept;
  [[nodiscard]] std::size_t live_count() const noexcept;

 private:
  friend class CleanupAuthorization;
  friend class CapsuleCleanupWorkflow;
  [[nodiscard]] std::optional<Lease> try_acquire() noexcept;
  [[nodiscard]] std::optional<Lease> try_acquire_transition(
      const TransitionPermit& permit) noexcept;

  std::shared_ptr<State> state_;
};

class CleanupAuthorization final {
 public:
  CleanupAuthorization(const CleanupAuthorization&) = delete;
  CleanupAuthorization& operator=(const CleanupAuthorization&) = delete;
  CleanupAuthorization(CleanupAuthorization&& other) noexcept;
  CleanupAuthorization& operator=(CleanupAuthorization&& other) noexcept;
  ~CleanupAuthorization() = default;

 private:
  friend class CapsuleCleanupTransaction;
  friend class CapsuleCleanupWorkflow;
  CleanupAuthorization(
      CleanupAuthorityGate::Lease&& authority,
      std::uint64_t issuer_identity,
      const CleanupAuthorizationBinding& binding,
      CapsuleCleanupTrigger trigger,
      std::uint64_t attempt_nonce) noexcept;

  CleanupAuthorityGate::Lease authority_;
  std::uint64_t issuer_identity_ = 0;
  std::uint64_t record_key_ = 0;
  CapsuleCleanupTrigger trigger_ = CapsuleCleanupTrigger::timer;
  std::uintptr_t host_identity_ = 0;
  std::uintptr_t xaml_root_identity_ = 0;
  std::uintptr_t content_identity_ = 0;
  std::uint64_t host_generation_ = 0;
  std::uint64_t root_generation_ = 0;
  std::uint64_t diagnostics_epoch_ = 0;
  std::uintptr_t anchor_identity_ = 0;
  std::uintptr_t grid_identity_ = 0;
  std::uint64_t attempt_nonce_ = 0;
  bool consumed_ = true;
};

enum class CapsuleParentRelationship : std::uint8_t {
  recorded_parent,
  detached,
  different_parent,
  access_failure,
};

enum class OriginalChildState : std::uint8_t {
  present_exact,
  missing,
  identity_mismatch,
  access_failure,
};

class CapsuleCleanupAccess {
 public:
  virtual ~CapsuleCleanupAccess() = default;
  [[nodiscard]] virtual CapsuleParentRelationship
  capsule_parent_relationship() noexcept = 0;
  [[nodiscard]] virtual bool remove_capsule_from_recorded_parent() noexcept = 0;
  [[nodiscard]] virtual bool clear_columns() noexcept = 0;
  [[nodiscard]] virtual bool append_column(std::size_t index) noexcept = 0;
  [[nodiscard]] virtual OriginalChildState original_child_state(
      std::size_t index) noexcept = 0;
  [[nodiscard]] virtual bool restore_original_child_column(
      std::size_t index) noexcept = 0;
};

enum class CapsuleTransactionResult : std::uint8_t {
  complete,
  already_complete,
  retryable_failure,
};

class CapsuleCleanupTransaction final {
 public:
  CapsuleCleanupTransaction(std::size_t column_count,
                            std::size_t child_count) noexcept;
  CapsuleCleanupTransaction(const CapsuleCleanupTransaction&) = delete;
  CapsuleCleanupTransaction& operator=(const CapsuleCleanupTransaction&) =
      delete;

  [[nodiscard]] bool complete() const noexcept;

 private:
  friend class CapsuleCleanupWorkflow;
  [[nodiscard]] CapsuleTransactionResult cleanup_authorized(
      CleanupAuthorization& authorization,
      std::uint64_t issuer_identity,
      const CleanupAuthorizationBinding& binding,
      CapsuleCleanupTrigger trigger,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] CapsuleTransactionResult cleanup_locked(
      CapsuleCleanupAccess& access) noexcept;

  const std::size_t column_count_;
  const std::size_t child_count_;
  mutable std::mutex mutex_;
  bool complete_ = false;
};

class CapsuleCleanupWorkflow final {
 public:
  CapsuleCleanupWorkflow(CleanupAuthorizationBinding binding,
                         std::shared_ptr<CleanupAuthorityGate> authority,
                         std::size_t column_count,
                         std::size_t child_count) noexcept;
  CapsuleCleanupWorkflow(const CapsuleCleanupWorkflow&) = delete;
  CapsuleCleanupWorkflow& operator=(const CapsuleCleanupWorkflow&) = delete;

  [[nodiscard]] std::optional<CleanupAuthorization> authorize(
      CapsuleCleanupTrigger trigger,
      CleanupAuthorizationEvidenceSource& evidence_source) noexcept;
  [[nodiscard]] CapsuleTransactionResult consume(
      CapsuleCleanupTrigger trigger,
      CleanupAuthorization&& authorization,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] CapsuleTransactionResult attempt(
      CapsuleCleanupTrigger trigger,
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] CapsuleTransactionResult attempt_transition(
      CapsuleCleanupTrigger trigger,
      const CleanupAuthorityGate::TransitionPermit& permit,
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] bool complete() const noexcept;
  [[nodiscard]] bool valid() const noexcept;

 private:
  [[nodiscard]] std::uint64_t next_attempt_nonce() noexcept;

  const CleanupAuthorizationBinding binding_;
  const std::shared_ptr<CleanupAuthorityGate> authority_;
  const std::uint64_t issuer_identity_;
  std::atomic<std::uint64_t> next_attempt_nonce_{1};
  CapsuleCleanupTransaction transaction_;
};

class CapsuleCleanupRecord final {
 public:
  CapsuleCleanupRecord(CleanupAuthorizationBinding binding,
                       std::shared_ptr<CleanupAuthorityGate> authority,
                       std::size_t column_count,
                       std::size_t child_count) noexcept;
  CapsuleCleanupRecord(const CapsuleCleanupRecord&) = delete;
  CapsuleCleanupRecord& operator=(const CapsuleCleanupRecord&) = delete;

  [[nodiscard]] CapsuleTransactionResult on_timer(
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] CapsuleTransactionResult on_explicit_removal(
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] CapsuleTransactionResult on_shutdown(
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] CapsuleTransactionResult on_transition_shutdown(
      const CleanupAuthorityGate::TransitionPermit& permit,
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] CapsuleTransactionResult on_rollback(
      CleanupAuthorizationEvidenceSource& evidence_source,
      CapsuleCleanupAccess& access) noexcept;
  [[nodiscard]] bool complete() const noexcept;
  [[nodiscard]] bool valid() const noexcept;

 private:
  CapsuleCleanupWorkflow workflow_;
};

}  // namespace cq::bridge
