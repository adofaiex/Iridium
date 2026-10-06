//! Decoration hitbox spatial index exports.
//!
//! Thread model: one grid per thread. The host rebuilds once per frame
//! (`spatial_rebuild`) and then queries it any number of times
//! (`spatial_query`) from the same thread.

use crate::compute::spatial::Grid;
use crate::ffi;
use std::cell::RefCell;

thread_local! {
    static GRID: RefCell<Grid> = RefCell::new(Grid::default());
}

/// Rebuilds the index from `count` packed AABBs (4 f32 per item:
/// min_x, min_y, max_x, max_y). Zero items clears the index.
#[no_mangle]
pub unsafe extern "C" fn iridium_core_spatial_rebuild(count: u32, rects: *const f32) -> i32 {
    crate::guard(|| {
        let slice = ffi::slice(rects, count.saturating_mul(4));
        let slice = match slice {
            Some(s) => s,
            None => {
                if count == 0 {
                    &[]
                } else {
                    return ffi::ERR_NULL_ARG;
                }
            }
        };
        GRID.with(|cell| {
            cell.borrow_mut().rebuild(slice);
        });
        ffi::ERR_OK
    })
}

/// Writes indices of intersecting AABBs into `out_ids` (capacity `cap`).
///
/// Returns the number of candidates (which may exceed `cap`; only `cap`
/// entries are written in that case). Negative on error.
#[no_mangle]
pub unsafe extern "C" fn iridium_core_spatial_query(
    min_x: f32,
    min_y: f32,
    max_x: f32,
    max_y: f32,
    out_ids: *mut u32,
    cap: u32,
) -> i32 {
    crate::guard(|| {
        if out_ids.is_null() && cap != 0 {
            return ffi::ERR_NULL_ARG;
        }
        GRID.with(|cell| {
            let mut grid = cell.borrow_mut();
            let hits = grid.query([min_x, min_y, max_x, max_y]);
            let total = hits.len();
            let write = total.min(cap as usize);
            if write > 0 {
                std::ptr::copy_nonoverlapping(hits.as_ptr(), out_ids, write);
            }
            total as i32
        })
    })
}

/// Returns the number of items currently indexed (0 before first rebuild).
#[no_mangle]
pub extern "C" fn iridium_core_spatial_count() -> u32 {
    GRID.with(|cell| cell.borrow().len() as u32)
}
