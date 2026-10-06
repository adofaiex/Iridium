//! Iridium native hot-path library (C ABI).
//!
//! Loaded by the Mono side via dlopen/LoadLibraryEx. Native is an optional
//! enhancement: on version mismatch or load failure the host falls back to
//! the pure C# path.

pub mod ffi;
pub mod remake_path;

use std::panic::AssertUnwindSafe;

/// Bumped on breaking ABI changes (layout, semantics, argument order).
pub const ABI_VERSION: u32 = 1;

pub const LIB_VERSION: &str = env!("CARGO_PKG_VERSION");
const LIB_VERSION_C: &str = concat!(env!("CARGO_PKG_VERSION"), "\0");

/// Catches panics at the FFI boundary; never unwinds into the host.
#[inline]
pub fn guard(f: impl FnOnce() -> i32) -> i32 {
    std::panic::catch_unwind(AssertUnwindSafe(f)).unwrap_or(ffi::ERR_PANIC)
}

#[no_mangle]
pub extern "C" fn iridium_core_abi_version() -> u32 {
    ABI_VERSION
}

/// Static, NUL-terminated, valid for the process lifetime.
#[no_mangle]
pub extern "C" fn iridium_core_lib_version() -> *const std::os::raw::c_char {
    LIB_VERSION_C.as_ptr() as *const std::os::raw::c_char
}

/// Symbol-resolution health check.
#[no_mangle]
pub extern "C" fn iridium_core_ping() -> u32 {
    1
}

// no_std-style footprint: std's fmt/panicking machinery is the size driver;
// avoid pulling it in on the happy path.
