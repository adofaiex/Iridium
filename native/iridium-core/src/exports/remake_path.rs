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
    crate::guard(|| {
        if input.is_null() || output.is_null() {
            return ffi::ERR_NULL_ARG;
        }
        // Copy the structs out instead of holding references into host memory.
        let input = unsafe { *input };
        let output = unsafe { *output };

        unsafe {
            crate::compute::remake_path::rebuild(
                input.angles,
                input.length_mults,
                input.steps,
                input.start_angle,
                input.tile_size,
                output.entry_angles,
                output.exit_angles,
                output.positions_x,
                output.positions_y,
                output.count,
            )
        }
    })
}
