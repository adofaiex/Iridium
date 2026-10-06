//! FFI wrapper for the RemakePath rebuild kernel.
//! Math lives in `crate::compute::remake_path`.

use crate::ffi::{self, FloorPathInput, FloorPathOutput};

/// Full path rebuild (floor 0 at the origin).
///
/// # Safety
/// `input`/`output` inner pointers must reference caller-allocated memory.
#[no_mangle]
pub unsafe extern "C" fn iridium_core_remake_path(
    input: *const FloorPathInput,
    output: *mut FloorPathOutput,
) -> i32 {
    remake(input, output, 0.0, 0.0)
}

/// Incremental path rebuild starting at an anchor tile: `input.angles`
/// points at the anchor's angle, `input.start_angle` is the anchor's entry
/// angle and (`origin_x`, `origin_y`) its world position. Output slot 0 is
/// the anchor itself.
///
/// # Safety
/// `input`/`output` inner pointers must reference caller-allocated memory.
#[no_mangle]
pub unsafe extern "C" fn iridium_core_remake_path_from(
    input: *const FloorPathInput,
    origin_x: f32,
    origin_y: f32,
    output: *mut FloorPathOutput,
) -> i32 {
    remake(input, output, origin_x, origin_y)
}

unsafe fn remake(
    input: *const FloorPathInput,
    output: *mut FloorPathOutput,
    origin_x: f32,
    origin_y: f32,
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
                origin_x,
                origin_y,
                output.entry_angles,
                output.exit_angles,
                output.positions_x,
                output.positions_y,
                output.count,
            )
        }
    })
}
