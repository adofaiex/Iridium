//! Static decoration batcher vertex transform export.
//!
//! Stateless: the host gathers SoA arrays each frame and passes them in.

use crate::compute::parallax::{self, Inputs};
use crate::ffi;

#[no_mangle]
pub unsafe extern "C" fn iridium_core_parallax_compute(
    count: u32,
    cam_x: f32,
    cam_y: f32,
    base_xy: *const f32,
    mult_xy: *const f32,
    offset_xy: *const f32,
    corners: *const f32,
    out_verts: *mut f32,
    out_dirty: *mut u32,
) -> i32 {
    crate::guard(|| {
        if count == 0 {
            return ffi::ERR_OK;
        }
        let n = count as usize;
        let base = match ffi::slice(base_xy, (n * 2) as u32) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let mult = match ffi::slice(mult_xy, (n * 2) as u32) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let offset = match ffi::slice(offset_xy, (n * 2) as u32) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let corners = match ffi::slice(corners, (n * 12) as u32) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let verts = match ffi::slice_mut(out_verts, (n * 12) as u32) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let dirty = match ffi::slice_mut(out_dirty, count) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };

        let inputs = Inputs {
            base_xy: base,
            mult_xy: mult,
            offset_xy: offset,
            corners,
        };
        parallax::compute([cam_x, cam_y], &inputs, verts, dirty);
        ffi::ERR_OK
    })
}