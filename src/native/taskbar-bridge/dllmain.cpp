#include "bridge_exports.h"
#include "bridge_runtime.h"
#include "xaml_taskbar_probe.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstdint>

extern "C" CQTB_API std::uint32_t CQTB_CALL
CQTB_GetBridgeAbiVersion() {
  return CQTB_BRIDGE_ABI_VERSION;
}

extern "C" CQTB_API std::int32_t CQTB_CALL CQTB_StartProbe(
    const CQTB_StartProbeRequestV1* request) {
  if (!request) {
    return E_POINTER;
  }
  CQTB_StartProbeRequestV1 copy{};
  __try {
    copy = *request;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return E_INVALIDARG;
  }
  return cq::bridge::start_probe_worker(copy);
}

extern "C" HRESULT __stdcall DllGetClassObject(
    REFCLSID class_id,
    REFIID interface_id,
    void** object) {
  return cq::bridge::tap_get_class_object(&class_id, &interface_id, object);
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
  return cq::bridge::tap_can_unload_now();
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*) {
  if (reason == DLL_PROCESS_ATTACH) {
    cq::bridge::cache_bridge_module(instance);
    DisableThreadLibraryCalls(instance);
  }
  return TRUE;
}
