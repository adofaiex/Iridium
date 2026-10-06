//! FFI export layer — thin `#[no_mangle]` wrappers only.
//! Actual computation lives in `crate::compute`; data shapes in `crate::ffi`.

pub mod easing;
pub mod json;
pub mod parallax;
pub mod remake_path;
pub mod resize;
pub mod spatial;
