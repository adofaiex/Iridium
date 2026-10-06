//! RemakePath geometry kernel — a faithful port of Iridium's
//! RebuildPositionsAndAngles (which matches vanilla scrLevelMaker).
//!
//! Semantics that must not drift (all verified against the C# source):
//! - Angles are ABSOLUTE: exit = (-deg + 90) * pi/180 in f64; 999.0f marks
//!   midspin (exit := entry, floor flagged).
//! - entry[i+1] = (exit[i] + pi) % 2pi, f64 throughout.
//! - Positions accumulate in f32 along getVectorFromAngle(exit, tileSize)
//!   = (sin, cos); floor 0 sits at the origin.
//! - The final tile's exit = entry + pi.

/// Per-step geometry result, SoA over `steps + 1` tiles.
///
/// # Safety
/// All pointers must reference caller-allocated memory with `steps + 1`
/// slots for angles and `steps + 1` for positions (floor 0 included).
pub(crate) unsafe fn rebuild(
    angles: *const f32,
    length_mults: *const f32,
    steps: u32,
    start_angle: f64,
    tile_size: f32,
    entry_out: *mut f64,
    exit_out: *mut f64,
    pos_x_out: *mut f32,
    pos_y_out: *mut f32,
    count_out: *mut u32,
) -> i32 {
    let angles = match crate::ffi::slice(angles, steps) {
        Some(s) => s,
        None => return crate::ffi::ERR_NULL_ARG,
    };
    let mults = match crate::ffi::slice(length_mults, steps) {
        Some(s) => s,
        None => return crate::ffi::ERR_NULL_ARG,
    };
    let entry = match crate::ffi::slice_mut(entry_out, steps + 1) {
        Some(s) => s,
        None => return crate::ffi::ERR_BAD_LEN,
    };
    let exit = match crate::ffi::slice_mut(exit_out, steps + 1) {
        Some(s) => s,
        None => return crate::ffi::ERR_BAD_LEN,
    };
    let px = match crate::ffi::slice_mut(pos_x_out, steps + 1) {
        Some(s) => s,
        None => return crate::ffi::ERR_BAD_LEN,
    };
    let py = match crate::ffi::slice_mut(pos_y_out, steps + 1) {
        Some(s) => s,
        None => return crate::ffi::ERR_BAD_LEN,
    };
    let count = match crate::ffi::slice_mut(count_out, 1) {
        Some(s) => s,
        None => return crate::ffi::ERR_NULL_ARG,
    };

    const PI: f64 = std::f64::consts::PI;
    const TWO_PI: f64 = 2.0 * PI;
    const NO_ANGLE: f32 = 999.0;

    let mut entry_angle = start_angle; // floor 0: 3/2 pi
    let mut x = 0.0f32;
    let mut y = 0.0f32;
    entry[0] = entry_angle;
    px[0] = 0.0;
    py[0] = 0.0;

    for i in 0..steps as usize {
        let deg = angles[i];
        let (exit_angle, midspin) = if deg == NO_ANGLE {
            (entry_angle, true)
        } else {
            ((-deg as f64 + 90.0) * (PI / 180.0), false)
        };
        // midspin flag would be written back per-floor by the host; expose
        // it implicitly: entry == exit marks a midspin floor.
        let _ = midspin;

        exit[i] = exit_angle;

        // Position advances along the exit direction (sin, cos), f32 accum.
        let len = tile_size * mults[i];
        let (s, c) = (exit_angle.sin(), exit_angle.cos());
        x += (s * len as f64) as f32;
        y += (c * len as f64) as f32;
        px[i + 1] = x;
        py[i + 1] = y;

        entry_angle = (exit_angle + PI) % TWO_PI;
        entry[i + 1] = entry_angle;
    }

    // Last tile: exit = entry + pi (vanilla tail behavior).
    exit[steps as usize] = entry_angle + PI;
    count[0] = steps + 1;
    crate::ffi::ERR_OK
}
