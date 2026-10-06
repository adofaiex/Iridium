//! RemakePath angle rebuild (first hot-path export).
//!
//! Mirrors the geometry core of vanilla scrLevelMaker.RemakePath:
//! entry[i+1] = exit[i] = entry[i] + radians(angles[i]), position advances
//! along the exit direction by tile_size * lengthMult.
//!
//! Kept scalar f32 with the same accumulation order as vanilla — changing
//! to f64/fma would shift results visibly. SIMD applies only to the
//! per-tile independent parts (position sweep); the angle chain is serial.

use crate::ffi::{self, FloorPathInput, FloorPathOutput};

#[no_mangle]
pub unsafe extern "C" fn iridium_core_remake_path(
    input: *const FloorPathInput,
    output: *mut FloorPathOutput,
) -> i32 {
    crate::guard(|| remake_path_impl(input, output))
}

fn remake_path_impl(input: *const FloorPathInput, output: *mut FloorPathOutput) -> i32 {
    if input.is_null() || output.is_null() {
        return ffi::ERR_NULL_ARG;
    }

    // Copy the structs out instead of holding references into host memory.
    let input = unsafe { *input };
    let output = unsafe { *output };

    let n = input.angles_len;
    let angles = match ffi::slice(input.angles, n) {
        Some(s) => s,
        None => return ffi::ERR_NULL_ARG,
    };
    let mults = match ffi::slice(input.length_mults, n) {
        Some(s) => s,
        None => return ffi::ERR_NULL_ARG,
    };

    let out_count = match ffi::slice_mut(output.count, 1) {
        Some(s) => s,
        None => return ffi::ERR_NULL_ARG,
    };
    let entries = match ffi::slice_mut(output.entry_angles, n + 1) {
        Some(s) => s,
        None => return ffi::ERR_BAD_LEN,
    };
    let exits = match ffi::slice_mut(output.exit_angles, n + 1) {
        Some(s) => s,
        None => return ffi::ERR_BAD_LEN,
    };
    let px = match ffi::slice_mut(output.positions_x, n) {
        Some(s) => s,
        None => return ffi::ERR_BAD_LEN,
    };
    let py = match ffi::slice_mut(output.positions_y, n) {
        Some(s) => s,
        None => return ffi::ERR_BAD_LEN,
    };

    let mut angle = input.start_angle;
    let mut x = 0.0f32;
    let mut y = 0.0f32;
    let to_rad = std::f32::consts::PI / 180.0;

    entries[0] = angle;
    for i in 0..n as usize {
        exits[i] = angle;
        let len = input.tile_size * mults[i];
        let (sin, cos) = angle.sin_cos();
        x -= sin * len;
        y += cos * len;
        px[i] = x;
        py[i] = y;
        angle += angles[i] * to_rad;
        entries[i + 1] = angle;
    }
    exits[n as usize] = angle;

    out_count[0] = n + 1;
    ffi::ERR_OK
}
