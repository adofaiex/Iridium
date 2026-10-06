//! FFI export layer — thin `#[no_mangle]` wrappers only.
//! Actual computation lives in `crate::compute`; data shapes in `crate::ffi`.

pub mod easing;
pub mod remake_path;
