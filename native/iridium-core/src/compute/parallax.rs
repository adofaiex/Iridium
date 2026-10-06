//! Static decoration batcher per-frame vertex transform.
//!
//! The host (C# `StaticDecorationBatcher`) owns the Unity objects and reads
//! the per-decoration inputs; this kernel is the pure arithmetic inner loop
//! that was previously `items × 4` managed iterations per frame.
//!
//! Per decoration, for each of its 4 quad corners:
//!
//! ```text
//! x = b0.x + (cam.x - b0.x) * mult.x + offK.x + corner.x
//! y = b0.y + (cam.y - b0.y) * mult.y + offK.y + corner.y
//! z = corner.z
//! ```
//!
//! with `b0 = pivotPosVec`, `mult = parallax.multiplier`, and
//! `offK = parallaxOffset * scaleMultiplier` — identical to the original
//! managed expression `b0 + (cam - b0) * mult + offK + corner` with the
//! per-axis operations kept in the same order so results are bit-exact
//! against the `System.Numerics.Vector2` reference below.
//!
//! The kernel also reports, per decoration, whether any corner actually moved
//! (`dirty` bitmask, one flag per decoration), so the host can skip the
//! `Mesh.SetVertices` upload for slots whose decorations all held still.

/// Per-decoration gathered inputs, struct-of-arrays, 3 slots each for the
/// 4 corners (`corner` is 3 floats per corner × 4 corners = 12 per item).
pub struct Inputs<'a> {
    /// `pivotPosVec` per decoration (x, y).
    pub base_xy: &'a [f32],
    /// `parallax.multiplier` per decoration (x, y).
    pub mult_xy: &'a [f32],
    /// `parallaxOffset * scaleMultiplier` per decoration (x, y).
    pub offset_xy: &'a [f32],
    /// Quad corner offsets, 4 corners × 3 floats per decoration.
    pub corners: &'a [f32],
}

/// Computes world positions into `out_verts` (12 floats per decoration:
/// `xy z` × 4 corners, in corner order) and sets `out_dirty[i] = 1` when any
/// of decoration `i`'s four corners differs from what is already in
/// `out_verts`.
///
/// `out_verts` is both input (previous frame's positions, for the dirty check)
/// and output, so the host uploads exactly the slots that changed.
///
/// Returns the number of decorations whose corners moved.
pub fn compute(cam: [f32; 2], inputs: &Inputs<'_>, out_verts: &mut [f32], out_dirty: &mut [u32]) -> usize {
    let n = inputs.base_xy.len() / 2;
    let mut moved = 0usize;
    for i in 0..n {
        let b0x = inputs.base_xy[i * 2];
        let b0y = inputs.base_xy[i * 2 + 1];
        let mx = inputs.mult_xy[i * 2];
        let my = inputs.mult_xy[i * 2 + 1];
        let kx = inputs.offset_xy[i * 2];
        let ky = inputs.offset_xy[i * 2 + 1];
        // Hoisted per decoration: `cam - b0` is shared by all 4 corners.
        let dxc = cam[0] - b0x;
        let dyc = cam[1] - b0y;

        let vo = i * 12;
        let co = i * 12;
        let mut dirty = false;
        for c in 0..4 {
            let cz = inputs.corners[co + c * 3 + 2];
            let x = b0x + dxc * mx + kx + inputs.corners[co + c * 3];
            let y = b0y + dyc * my + ky + inputs.corners[co + c * 3 + 1];
            let zo = vo + c * 3;
            if out_verts[zo] != x || out_verts[zo + 1] != y || out_verts[zo + 2] != cz {
                out_verts[zo] = x;
                out_verts[zo + 1] = y;
                out_verts[zo + 2] = cz;
                dirty = true;
            }
        }
        if dirty {
            out_dirty[i] = 1;
            moved += 1;
        }
    }
    moved
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The pre-port managed expression, kept as the bit-exactness oracle.
    fn reference(
        cam: [f32; 2],
        base_xy: &[f32],
        mult_xy: &[f32],
        offset_xy: &[f32],
        corners: &[f32],
        out: &mut [f32],
    ) {
        let n = base_xy.len() / 2;
        for i in 0..n {
            let (b0x, b0y) = (base_xy[i * 2], base_xy[i * 2 + 1]);
            let (mx, my) = (mult_xy[i * 2], mult_xy[i * 2 + 1]);
            let (kx, ky) = (offset_xy[i * 2], offset_xy[i * 2 + 1]);
            for c in 0..4 {
                let cx = corners[i * 12 + c * 3];
                let cy = corners[i * 12 + c * 3 + 1];
                let cz = corners[i * 12 + c * 3 + 2];
                let x = b0x + (cam[0] - b0x) * mx + kx + cx;
                let y = b0y + (cam[1] - b0y) * my + ky + cy;
                out[i * 12 + c * 3] = x;
                out[i * 12 + c * 3 + 1] = y;
                out[i * 12 + c * 3 + 2] = cz;
            }
        }
    }

    fn fixture(n: usize) -> (Vec<f32>, Vec<f32>, Vec<f32>, Vec<f32>) {
        let mut base = Vec::with_capacity(n * 2);
        let mut mult = Vec::with_capacity(n * 2);
        let mut off = Vec::with_capacity(n * 2);
        let mut corners = Vec::with_capacity(n * 12);
        for i in 0..n {
            let f = i as f32;
            base.extend_from_slice(&[f * 1.37, -f * 0.61 + 3.0]);
            mult.extend_from_slice(&[0.5 + (i % 7) as f32 * 0.13, 1.5 - (i % 5) as f32 * 0.21]);
            off.extend_from_slice(&[f * 0.125, f * -0.25]);
            for c in 0..4 {
                corners.extend_from_slice(&[(c as f32 - 1.5) * 0.5, (c as f32 % 2.0) * 0.75, (c as f32 - 1.5) * 0.1]);
            }
        }
        (base, mult, off, corners)
    }

    #[test]
    fn matches_managed_reference_bit_for_bit() {
        for n in [1usize, 3, 64, 4096] {
            let (base, mult, off, corners) = fixture(n);
            let cam = [12.5f32, -7.25];
            let mut want = vec![0f32; n * 12];
            reference(cam, &base, &mult, &off, &corners, &mut want);

            let mut got = vec![f32::NAN; n * 12];
            let mut dirty = vec![0u32; n];
            let moved = compute(cam, &Inputs { base_xy: &base, mult_xy: &mult, offset_xy: &off, corners: &corners }, &mut got, &mut dirty);

            assert_eq!(got, want, "n = {n}");
            // Pre-filled with NaN → everything counts as moved.
            assert_eq!(moved, n);
            assert!(dirty.iter().all(|&d| d == 1));
        }
    }

    #[test]
    fn dirty_only_flags_moved_decorations() {
        let (base, mult, off, corners) = fixture(4);
        let cam = [1.0, 2.0];
        let mut verts = vec![0f32; 4 * 12];
        let mut dirty = vec![0u32; 4];
        compute(cam, &Inputs { base_xy: &base, mult_xy: &mult, offset_xy: &off, corners: &corners }, &mut verts, &mut dirty);
        assert_eq!(dirty, vec![1, 1, 1, 1]);

        // Same camera → nothing moves.
        dirty.iter_mut().for_each(|d| *d = 0);
        let moved = compute(cam, &Inputs { base_xy: &base, mult_xy: &mult, offset_xy: &off, corners: &corners }, &mut verts, &mut dirty);
        assert_eq!(moved, 0);
        assert_eq!(dirty, vec![0; 4]);

        // Camera nudge → every decoration moves (all track parallax).
        dirty.iter_mut().for_each(|d| *d = 0);
        let cam2 = [1.5, 2.0];
        let moved = compute(cam2, &Inputs { base_xy: &base, mult_xy: &mult, offset_xy: &off, corners: &corners }, &mut verts, &mut dirty);
        assert_eq!(moved, 4);
        assert_eq!(dirty, vec![1; 4]);
    }

    #[test]
    fn multiplier_zero_pins_to_base_plus_offset() {
        let base = [3.0f32, -1.0];
        let mult = [0.0f32, 0.0];
        let off = [0.5f32, -0.25];
        let corners = [0.1f32, 0.2, 0.3, -0.1, 0.2, -0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9];
        let mut verts = vec![0f32; 12];
        let mut dirty = vec![0u32; 1];
        // Camera anywhere: multiplier 0 kills the parallax term.
        compute([999.0, -999.0], &Inputs { base_xy: &base, mult_xy: &mult, offset_xy: &off, corners: &corners }, &mut verts, &mut dirty);
        assert_eq!(&verts[0..3], &[3.0 + 0.0 + 0.5 + 0.1, -1.0 + 0.0 - 0.25 + 0.2, 0.3]);
    }

    #[test]
    fn zero_decorations_is_a_no_op() {
        let mut verts: [f32; 0] = [];
        let mut dirty: [u32; 0] = [];
        assert_eq!(compute([0.0; 2], &Inputs { base_xy: &[], mult_xy: &[], offset_xy: &[], corners: &[] }, &mut verts, &mut dirty), 0);
    }
}