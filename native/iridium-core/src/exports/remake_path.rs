//! FFI wrapper for the RemakePath rebuild kernel.
//! Math lives in `crate::compute::remake_path`.

use crate::ffi::{self, FloorPathInput, FloorPathOutput};

/// Path rebuild.
///
/// # Safety
/// `input`/`output` inner pointers must reference caller-allocated memory.
#[no_mangle]
pub unsafe extern "C" fn iridium_core_remake_path(
    input: *const FloorPathInput,
    output: *mut FloorPathOutput,
) -> i32 {
    crate::guard(|| crate::compute::remake_path::remake_path_impl(input, output))
}
