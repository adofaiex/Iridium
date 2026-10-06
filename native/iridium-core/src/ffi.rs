//! FFI data shapes, mirrored by Mono-side `v3/Native/IridiumNative.cs`.
//! Flat arrays only (SoA) — no strings, objects or GC handles across the
//! boundary. Breaking layout changes must bump ABI_VERSION.

/// Error codes: 0 = success, negative = failure.
pub const ERR_OK: i32 = 0;
pub const ERR_NULL_ARG: i32 = -1;
pub const ERR_BAD_LEN: i32 = -2;
pub const ERR_PANIC: i32 = -3;

/// Per-tile base data for path rebuild (SoA).
#[repr(C)]
#[derive(Clone, Copy)]
pub struct FloorPathInput {
    /// Turn angle per tile in degrees (vanilla floorAngles[i]).
    pub angles: *const f32,
    pub angles_len: u32,
    /// Start angle in radians (vanilla 270deg or chart startAngle).
    pub start_angle: f32,
    /// Vanilla controller.baseFloorDimensions.x.
    pub tile_size: f32,
    /// Per-tile length multiplier (vanilla lengthMult).
    pub length_mults: *const f32,
}

/// Rebuild results; buffers are caller-allocated.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct FloorPathOutput {
    /// Entry angle per tile in radians (scrFloor.entryangle), n+1 entries.
    pub entry_angles: *mut f32,
    /// Exit angle per tile in radians (scrFloor.exitangle), n+1 entries.
    pub exit_angles: *mut f32,
    /// Tile center world position (transform.position), n entries.
    pub positions_x: *mut f32,
    pub positions_y: *mut f32,
    /// Tiles written (n+1), filled by the callee.
    pub count: *mut u32,
}

#[inline]
pub(crate) fn slice<'a, T>(ptr: *const T, len: u32) -> Option<&'a [T]> {
    if ptr.is_null() {
        None
    } else {
        // len == 0 is valid (empty chart)
        Some(unsafe { std::slice::from_raw_parts(ptr, len as usize) })
    }
}

#[inline]
pub(crate) fn slice_mut<'a, T>(ptr: *mut T, len: u32) -> Option<&'a mut [T]> {
    if ptr.is_null() {
        None
    } else {
        Some(unsafe { std::slice::from_raw_parts_mut(ptr, len as usize) })
    }
}
