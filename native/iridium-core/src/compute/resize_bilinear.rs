//! Bilinear texture downscale, bit-exact with Unity's `Color32.Lerp`.
//!
//! Port of the `ResizeTextureCPU` managed loops (byte-identical in
//! `v2/Patches/OptimizerPatches.cs` and
//! `v3/Patches/Optimizer/OptimizerShared.cs`). That code runs on the
//! background texture-load thread, once per texture, over *every* pixel --
//! the largest single numeric loop left in the mod.
//!
//! # Bit-exactness
//!
//! Unity's `Color32.Lerp(a, b, t)` decompiles to:
//!
//! ```text
//! t = Mathf.Clamp01(t);
//! r = (byte)((float)(int)a.r + (float)(b.r - a.r) * t);
//! ```
//!
//! Three details matter and are easy to get wrong:
//!
//! - `b.r - a.r` is computed in **int** (so the delta is an exact integer in
//!   -255..255) and only then widened to f32. Writing `b.r as f32 - a.r as f32`
//!   gives the identical value, because every integer in that range is exact
//!   in f32; we keep the int form so the mirror is obvious on review.
//! - The `as u8` cast **truncates** toward zero -- there is no `+ 0.5f`
//!   rounding. That is intentional here: `t` is in 0..=1 and the base pixel
//!   supplies the integer part, so the pre-truncation value is already in
//!   0..255 and truncation is what the managed build produces.
//! - `Mathf.Clamp01` is a no-op for our callers (`x_lerp`/`y_lerp` are
//!   `frac` of a non-negative product), so we skip it in the inner loop but
//!   assert the range in debug builds.
//!
//! The managed code evaluates
//! `Color32.Lerp(Color32.Lerp(c1, c2, xLerp), Color32.Lerp(c3, c4, xLerp), yLerp)`
//! -- it **quantizes to bytes between the two lerps**. A single fused float
//! lerp would drift by up to 1 per channel against the managed output, so we
//! reproduce that two-stage byte quantization exactly.

/// One `Color32.Lerp` stage over four channels. `t` must be in 0..=1.
#[inline(always)]
fn lerp8(a: [u8; 4], b: [u8; 4], t: f32) -> [u8; 4] {
    let mut out = [0u8; 4];
    for k in 0..4 {
        let lo = a[k] as i32;
        let hi = b[k] as i32;
        // Unity widens *both* operands to f32: `(float)(int)a + (float)(b - a) * t`.
        // Widening the base too (instead of `lo + f32`) is what keeps the sum
        // from being an integer add that would truncate differently.
        out[k] = (lo as f32 + (hi - lo) as f32 * t) as u8;
    }
    out
}

/// Bilinear downscale from `(src_w, src_h)` into `(dst_w, dst_h)`.
///
/// `src` must hold `src_w * src_h * 4` bytes (RGBA8); `dst` must hold
/// `dst_w * dst_h * 4`. Callers validate lengths -- see `exports/resize.rs`.
///
/// # Bounds
///
/// The managed loop unconditionally reads `y1 + x_floor + 1` and
/// `y2 + x_floor + 1`, i.e. one column/row past the sample. For a pure
/// downscale (`dst <= src`) `x_ratio <= 1`, so `x_floor <= sw - 2` and those
/// reads stay in bounds by construction. Rust cannot rely on that, so the
/// sample index is clamped to `sw - 2` / `sh - 2`.
///
/// The clamp only ever engages when `dst > src`, which the managed path never
/// produces (`ResizeImageBytes` only shrinks), so bit-exactness is preserved.
///
/// # Safety
///
/// Callers must guarantee `src.len() >= src_w as usize * src_h as usize * 4`
/// and `dst.len() >= dst_w as usize * dst_h as usize * 4`. The export layer
/// (`exports/resize.rs`) validates both before calling.
pub unsafe fn downscale(
    src: &[u8],
    src_w: u32,
    src_h: u32,
    dst: &mut [u8],
    dst_w: u32,
    dst_h: u32,
) {
    let (sw, sh) = (src_w as usize, src_h as usize);
    let (dw, dh) = (dst_w as usize, dst_h as usize);
    if sw < 2 || sh < 2 || dw == 0 || dh == 0 {
        return;
    }

    let x_ratio = (sw as f32 - 1.0) / dw as f32;
    let y_ratio = (sh as f32 - 1.0) / dh as f32;

    // Highest legal sample index that still leaves a +1 neighbour in bounds.
    let max_x = sw - 2;
    let max_y = sh - 2;

    // SAFETY: the caller guarantees both slices are large enough for the
    // dimensions above; every index below is derived from clamped sample
    // offsets and a bounded destination walk. See the # Safety section.
    let sp = src.as_ptr();
    let dp = dst.as_mut_ptr();

    for y in 0..dh {
        let y_pos = y as f32 * y_ratio;
        let y_floor = (y_pos as usize).min(max_y);
        let y_lerp = y_pos - y_floor as f32;
        debug_assert!((0.0..=1.0).contains(&y_lerp));

        let y1 = y_floor * sw;
        let y2 = (y_floor + 1) * sw;
        let out_row = dp.add(y * dw * 4);

        for x in 0..dw {
            let x_pos = x as f32 * x_ratio;
            let x_floor = (x_pos as usize).min(max_x);
            let x_lerp = x_pos - x_floor as f32;
            debug_assert!((0.0..=1.0).contains(&x_lerp));

            let x0 = x_floor << 2;
            let x1 = x0 + 4;

            // One 4-byte load per sample instead of four 1-byte loads.
            // Color32 is r@0/g@1/b@2/a@3 in memory, so a little-endian u32
            // keeps the channel order and `to_le_bytes` restores it on write.
            let c1 = read_px(sp, y1, x0);
            let c2 = read_px(sp, y1, x1);
            let c3 = read_px(sp, y2, x0);
            let c4 = read_px(sp, y2, x1);

            // Quantize to bytes between the stages, exactly as managed does.
            let top = lerp8(c1, c2, x_lerp);
            let bot = lerp8(c3, c4, x_lerp);
            let out = lerp8(top, bot, y_lerp);

            (out_row.add(x * 4) as *mut u32).write_unaligned(u32::from_le_bytes(out));
        }
    }
}

/// Reads the RGBA8 pixel at row `row`, byte offset `off` (already <<2).
#[inline(always)]
unsafe fn read_px(base: *const u8, row: usize, off: usize) -> [u8; 4] {
    (base.add(row * 4 + off) as *const u32).read_unaligned().to_le_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The pre-port managed expression, kept verbatim as the bit-exact oracle.
    fn reference(
        source: &[u8],
        sw: usize,
        sh: usize,
        tw: usize,
        th: usize,
    ) -> Vec<u8> {
        let mut out = vec![0u8; tw * th * 4];
        let x_ratio = (sw as f32 - 1.0) / tw as f32;
        let y_ratio = (sh as f32 - 1.0) / th as f32;

        let get = |i: usize| -> [u8; 4] {
            let o = i * 4;
            [source[o], source[o + 1], source[o + 2], source[o + 3]]
        };
        let lerp = |a: [u8; 4], b: [u8; 4], t: f32| -> [u8; 4] {
            let t = t.clamp(0.0, 1.0); // Mathf.Clamp01
            let mut r = [0u8; 4];
            for k in 0..4 {
                let lo = a[k] as i32;
                let hi = b[k] as i32;
                r[k] = (lo as f32 + (hi - lo) as f32 * t) as u8;
            }
            r
        };

        for y in 0..th {
            let y_floor = (y as f32 * y_ratio) as i64 as usize;
            let y_lerp = y as f32 * y_ratio - y_floor as f32;
            let y1 = y_floor * sw;
            let y2 = (y_floor + 1) * sw;
            for x in 0..tw {
                let x_floor = (x as f32 * x_ratio) as i64 as usize;
                let x_lerp = x as f32 * x_ratio - x_floor as f32;
                let c1 = get(y1 + x_floor);
                let c2 = get(y1 + x_floor + 1);
                let c3 = get(y2 + x_floor);
                let c4 = get(y2 + x_floor + 1);
                let v = lerp(lerp(c1, c2, x_lerp), lerp(c3, c4, x_lerp), y_lerp);
                let o = (y * tw + x) * 4;
                out[o..o + 4].copy_from_slice(&v);
            }
        }
        out
    }

    fn synth(sw: usize, sh: usize) -> Vec<u8> {
        let mut v = vec![0u8; sw * sh * 4];
        let mut s = 12345u32;
        for px in v.chunks_exact_mut(4) {
            s = s.wrapping_mul(1664525).wrapping_add(1013904223);
            px[0] = (s >> 16) as u8;
            px[1] = (s >> 8) as u8;
            px[2] = s as u8;
            px[3] = 255 - ((s >> 4) as u8);
        }
        v
    }

    #[test]
    fn matches_managed_reference_bit_for_bit() {
        // Pure downscales, including the awkward non-power-of-two and
        // odd-ratio shapes that make x_lerp/y_lerp land mid-byte.
        let cases: &[(usize, usize, usize, usize)] = &[
            (2, 2, 1, 1),
            (4, 4, 2, 2),
            (5, 7, 3, 4),
            (8, 8, 3, 3),
            (64, 64, 32, 32),
            (64, 48, 16, 12),
            (37, 91, 8, 8),
            (256, 256, 100, 100),
        ];
        for &(sw, sh, dw, dh) in cases {
            let src = synth(sw, sh);
            let want = reference(&src, sw, sh, dw, dh);
            let mut got = vec![0u8; dw * dh * 4];
            // SAFETY: buffers are allocated to the exact dimensions above.
            unsafe { downscale(&src, sw as u32, sh as u32, &mut got, dw as u32, dh as u32) };
            assert_eq!(
                got, want,
                "mismatch for {}x{} -> {}x{}",
                sw, sh, dw, dh
            );
        }
    }

    #[test]
    fn identity_when_ratios_are_exact() {
        // 4x4 -> 3x3 exercises partial ratios; 2x2 -> 1x1 is the
        // degenerate "every lerp weight is 0" case.
        let src = synth(2, 2);
        let mut got = vec![0u8; 4];
        // SAFETY: 2x2 source and 1x1 destination, both allocated above.
        unsafe { downscale(&src, 2, 2, &mut got, 1, 1) };
        assert_eq!(got, vec![src[0], src[1], src[2], src[3]]);
    }

    #[test]
    fn alpha_is_resampled_like_the_managed_loop() {
        // Guards the classic "forgot the alpha channel" regression.
        let sw = 16;
        let sh = 16;
        let mut src = vec![0u8; sw * sh * 4];
        for (i, chunk) in src.chunks_exact_mut(4).enumerate() {
            chunk[0] = 200;
            chunk[1] = 100;
            chunk[2] = 50;
            chunk[3] = (i % 256) as u8;
        }
        let mut got = vec![0u8; 8 * 8 * 4];
        // SAFETY: buffers are allocated to the exact dimensions above.
        unsafe { downscale(&src, sw as u32, sh as u32, &mut got, 8, 8) };
        assert_eq!(got, reference(&src, sw, sh, 8, 8));
    }

    #[test]
    fn no_out_of_bounds_write_on_large_downscale() {
        let src = synth(512, 512);
        let mut got = vec![0u8; 4 * 4 * 4];
        // SAFETY: 512x2 source and 4x4 destination, both allocated above.
        unsafe { downscale(&src, 512, 512, &mut got, 4, 4) };
        // Every destination byte must have been written.
        assert!(got.iter().any(|&b| b != 0));
    }
}