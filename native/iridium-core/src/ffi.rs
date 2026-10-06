//! FFI data shapes, mirrored by Mono-side `v3/Native/IridiumNative.cs`.
//! Flat arrays only (SoA) — no strings, objects or GC handles across the
//! boundary. Breaking layout changes must bump ABI_VERSION.

/// Error codes: 0 = success, negative = failure.
pub const ERR_OK: i32 = 0;
pub const ERR_NULL_ARG: i32 = -1;
pub const ERR_BAD_LEN: i32 = -2;
pub const ERR_PANIC: i32 = -3;
/// Input could not be parsed; the host should fall back to the managed path.
pub const ERR_PARSE: i32 = -4;

/// Path-rebuild input (SoA). Field order is padding-free on all ABIs:
/// two pointers, one f64, then two 4-byte scalars.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct FloorPathInput {
    /// Absolute turn angle per step in degrees; 999.0 = midspin sentinel.
    pub angles: *const f32,
    /// Per-step length multiplier (all ones unless mods scale tiles).
    pub length_mults: *const f32,
    /// First tile entry angle in radians (vanilla 3/2 pi).
    pub start_angle: f64,
    /// Vanilla controller.tileSize (tile spacing radius).
    pub tile_size: f32,
    /// Number of direction steps (= floor count - 1).
    pub steps: u32,
}

/// Path-rebuild output; all buffers caller-allocated with `steps + 1` slots.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct FloorPathOutput {
    /// Entry angle per tile in radians (scrFloor.entryangle, f64 in vanilla).
    pub entry_angles: *mut f64,
    /// Exit angle per tile in radians; the last tile gets entry + pi.
    pub exit_angles: *mut f64,
    /// Tile center world position (floor 0 at origin).
    pub positions_x: *mut f32,
    pub positions_y: *mut f32,
    /// Tiles written (steps + 1), filled by the callee.
    pub count: *mut u32,
}

/// JSON DOM view; all pointers reference thread-local parser buffers and stay
/// valid until the next `json_parse`/`json_release` on the same thread.
#[repr(C)]
pub struct JsonView {
    pub kinds: *const u8,
    pub a: *const u32,
    pub b: *const u32,
    pub values: *const f32,
    pub children: *const u32,
    pub strings: *const u16,
    pub str_off: *const u32,
    pub str_len: *const u32,
    pub node_count: u32,
    pub child_count: u32,
    pub string_count: u32,
    pub string_pool_len: u32,
    pub root: u32,
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
