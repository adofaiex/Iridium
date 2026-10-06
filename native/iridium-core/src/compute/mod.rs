//! Pure computation kernels — no FFI types here, easy to unit-test and bench.
//! Callers: `crate::exports` wraps these behind `#[no_mangle]` boundaries.

pub mod easing;
pub mod remake_path;
