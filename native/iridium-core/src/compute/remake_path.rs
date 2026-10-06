//! RemakePath geometry kernel — a faithful port of Iridium's
//! RebuildPositionsAndAngles (which matches vanilla scrLevelMaker).
//!
//! Semantics that must not drift (all verified against the C# source):
//! - Angles are ABSOLUTE: exit = f64(f32(-deg + 90)) * pi/180; 999.0f marks
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
    origin_x: f32,
    origin_y: f32,
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

    // Full rebuild: origin (0,0) and 3/2 pi. Incremental: the anchor tile's
    // stored position/entry, so f32 accumulation order matches the host's
    // `anchor + o1 + o2 + ...` exactly.
    let mut entry_angle = start_angle;
    let mut x = origin_x;
    let mut y = origin_y;
    entry[0] = entry_angle;
    px[0] = x;
    py[0] = y;

    for i in 0..steps as usize {
        let deg = angles[i];
        let (exit_angle, midspin) = if deg == NO_ANGLE {
            (entry_angle, true)
        } else {
            // C# evaluates `(-ang + 90f)` in f32 before widening; match it so
            // native and managed rebuilds agree bit-for-bit.
            (((-deg + 90.0f32) as f64) * (PI / 180.0), false)
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

#[cfg(test)]
mod tests {
    use super::*;

    fn run(angles: &[f32], start: f64, ox: f32, oy: f32) -> (Vec<f64>, Vec<f64>, Vec<f32>, Vec<f32>) {
        let n = angles.len();
        let mults = vec![1.0f32; n];
        let mut entry = vec![0f64; n + 1];
        let mut exit = vec![0f64; n + 1];
        let mut px = vec![0f32; n + 1];
        let mut py = vec![0f32; n + 1];
        let mut count = [0u32];
        let rc = unsafe {
            rebuild(
                angles.as_ptr(), mults.as_ptr(), n as u32, start, 0.631, ox, oy,
                entry.as_mut_ptr(), exit.as_mut_ptr(), px.as_mut_ptr(), py.as_mut_ptr(),
                count.as_mut_ptr(),
            )
        };
        assert_eq!(rc, crate::ffi::ERR_OK);
        assert_eq!(count[0] as usize, n + 1);
        (entry, exit, px, py)
    }

    #[test]
    fn incremental_matches_full_tail_exactly() {
        let mut angles = Vec::new();
        let mut a = 30.0f32;
        for i in 0..5000 {
            a = (a * 1.37 + i as f32 * 0.9) % 360.0;
            angles.push(if i % 97 == 0 { 999.0 } else { a });
        }
        let (fe, fx, fpx, fpy) = run(&angles, std::f64::consts::PI * 1.5, 0.0, 0.0);
        for &k in &[1usize, 17, 2500, 4999] {
            let (e, x, px, py) = run(&angles[k..], fe[k], fpx[k], fpy[k]);
            for j in 0..angles.len() - k {
                assert_eq!(e[j].to_bits(), fe[k + j].to_bits(), "entry k={k} j={j}");
                assert_eq!(x[j].to_bits(), fx[k + j].to_bits(), "exit k={k} j={j}");
                assert_eq!(px[j + 1].to_bits(), fpx[k + j + 1].to_bits(), "px k={k} j={j}");
                assert_eq!(py[j + 1].to_bits(), fpy[k + j + 1].to_bits(), "py k={k} j={j}");
            }
        }
    }
}
