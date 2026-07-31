#pragma once

#include <stddef.h>
#include <stdint.h>

#define CQTB_BRIDGE_ABI_VERSION 1u
#define CQTB_ACTIVATION_ID_BYTES 16u

#if defined(_WIN32)
#define CQTB_CALL __stdcall
#if defined(CQTB_BRIDGE_BUILD)
#define CQTB_API __declspec(dllexport)
#else
#define CQTB_API __declspec(dllimport)
#endif
#else
#define CQTB_CALL
#define CQTB_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CQTB_ExplorerInstanceBindingV1 {
  uint32_t size;
  uint32_t explorer_pid;
  uint64_t explorer_creation_time_100ns;
  uint8_t activation_id[CQTB_ACTIVATION_ID_BYTES];
} CQTB_ExplorerInstanceBindingV1;

typedef struct CQTB_StartProbeRequestV1 {
  uint32_t size;
  uint32_t abi_version;
  CQTB_ExplorerInstanceBindingV1 binding;
  uint32_t reserved[4];
} CQTB_StartProbeRequestV1;

/*
  Task 5 handoff contract:
  - Load the bridge, call CQTB_StartProbe exactly once with the PID, process
    creation time, and a fresh 128-bit activation ID while retaining the same
    validated Explorer process handle/lease.
  - After StartProbe returns, open the derived StartReleased event, then signal
    it to transfer ownership of the injector's initial LoadLibrary reference to
    the bootstrap worker. Do not call remote FreeLibrary. The worker releases
    that initial reference during teardown while its own reference still pins
    the DLL, then performs FreeLibraryAndExitThread as its final instruction.
  - Ready/Shutdown/Quiesced events are derived from the sanitized binding as
    Local\\CQTB.Probe.v1.<pid-hex>.<creation-time-hex>.<activation-id-hex>.<kind>,
    where kind is StartReleased, Ready, Shutdown, or Quiesced.
    Event signals are lifecycle notifications, not compatibility receipts or
    authorization. A future receipt is valid only while the original validated
    Explorer process lease remains alive and still matches PID + creation time.
  - This ABI never issues a managed LiveGateReceipt or a forgeable success flag.
*/

CQTB_API uint32_t CQTB_CALL CQTB_GetBridgeAbiVersion(void);
CQTB_API int32_t CQTB_CALL CQTB_StartProbe(
    const CQTB_StartProbeRequestV1* request);

#ifdef __cplusplus
}

namespace cq::bridge {

enum class BindingValidation {
  valid,
  invalid_size,
  abi_mismatch,
  reserved_not_zero,
  invalid_activation_id,
  wrong_explorer_instance,
};

[[nodiscard]] BindingValidation validate_start_request(
    const CQTB_StartProbeRequestV1& request,
    uint32_t actual_process_id,
    uint64_t actual_creation_time_100ns) noexcept;

}  // namespace cq::bridge
#endif
