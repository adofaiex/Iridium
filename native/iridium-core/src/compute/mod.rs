//! Pure computation kernels — no FFI types, so they unit-test and bench
//! standalone. `crate::exports` wraps them behind `#[no_mangle]`.

pub mod easing;
pub mod json;
pub mod parallax;
pub mod remake_path;
pub mod resize_bilinear;
pub mod spatial;
