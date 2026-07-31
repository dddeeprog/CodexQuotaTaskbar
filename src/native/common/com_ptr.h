#pragma once

#include <Unknwn.h>

#include <utility>

namespace cq::common {

template <typename Interface>
class ComPtr final {
 public:
  ComPtr() noexcept = default;
  ComPtr(std::nullptr_t) noexcept {}

  explicit ComPtr(Interface* value) noexcept : value_(value) {
    add_ref();
  }

  ComPtr(const ComPtr& other) noexcept : value_(other.value_) {
    add_ref();
  }

  ComPtr(ComPtr&& other) noexcept : value_(std::exchange(other.value_, nullptr)) {}

  ~ComPtr() { reset(); }

  ComPtr& operator=(const ComPtr& other) noexcept {
    if (this != &other) {
      ComPtr copy{other};
      swap(copy);
    }
    return *this;
  }

  ComPtr& operator=(ComPtr&& other) noexcept {
    if (this != &other) {
      reset();
      value_ = std::exchange(other.value_, nullptr);
    }
    return *this;
  }

  [[nodiscard]] Interface* get() const noexcept { return value_; }
  [[nodiscard]] Interface* operator->() const noexcept { return value_; }
  [[nodiscard]] explicit operator bool() const noexcept { return value_ != nullptr; }

  Interface** put() noexcept {
    reset();
    return &value_;
  }

  void** put_void() noexcept {
    return reinterpret_cast<void**>(put());
  }

  void attach(Interface* value) noexcept {
    reset();
    value_ = value;
  }

  [[nodiscard]] Interface* detach() noexcept {
    return std::exchange(value_, nullptr);
  }

  void reset() noexcept {
    if (value_) {
      value_->Release();
      value_ = nullptr;
    }
  }

  void swap(ComPtr& other) noexcept {
    std::swap(value_, other.value_);
  }

  template <typename Other>
  [[nodiscard]] HRESULT as(ComPtr<Other>& result) const noexcept {
    result.reset();
    if (!value_) {
      return E_POINTER;
    }
    return value_->QueryInterface(__uuidof(Other), result.put_void());
  }

 private:
  void add_ref() noexcept {
    if (value_) {
      value_->AddRef();
    }
  }

  Interface* value_ = nullptr;
};

}  // namespace cq::common
