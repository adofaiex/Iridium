//! Bilinear texture-resize exports.
//!
//! Stateless: the host passes pinned RGBA8 buffers in and out. The managed
//! side pins `Color32[]` arrays directly -- `Color32` is
//! `LayoutKind.Explicit` with r@0/g@1/b@2/a@3, so no repacking is needed.

use crate::compute::resize_bilinear;
use crate::ffi;

#[no_mangle]
pub unsafe extern "C" fn iridium_core_resize_bilinear(
    src_w: u32,
    src_h: u32,
    dst_w: u32,
    dst_h: u32,
    src: *const u8,
    dst: *mut u8,
) -> i32 {
    crate::guard(|| {
        let sw = src_w as usize;
        let sh = src_h as usize;
        let dw = dst_w as usize;
        let dh = dst_h as usize;
        if sw == 0 || sh == 0 || dw == 0 || dh == 0 {
            return ffi::ERR_BAD_LEN;
        }
        let src = match ffi::slice(src, (sw * sh * 4) as u32) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let dst = match ffi::slice_mut(dst, (dw * dh * 4) as u32) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        // `ffi::slice` built both views with exactly the lengths the kernel
        // indexes against, so the # Safety contract holds by construction.
        resize_bilinear::downscale(src, src_w, src_h, dst, dst_w, dst_h);
        ffi::ERR_OK
    })
}