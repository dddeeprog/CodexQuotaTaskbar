#pragma once

#include <Windows.h>

namespace cq::common {

[[nodiscard]] inline HRESULT last_error_hresult(
    DWORD error = GetLastError()) noexcept {
  return error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error);
}

}  // namespace cq::common
