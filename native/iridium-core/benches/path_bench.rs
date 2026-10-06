//! Standalone benchmark: times remake_path over a 150k-tile chart
//! (Iridium's target upper bound) on realistic in-cache data.
//! Self-contained: includes the implementation modules directly and
//! provides the `guard` shim that lib.rs would normally supply.

use std::time::Instant;

#[path = "../src/ffi.rs"]
mod ffi;
#[path = "../src/remake_path.rs"]
mod remake_path;

// remake_path calls `crate::guard`; supply it at the bench crate root.
fn guard(f: impl FnOnce() -> i32) -> i32 {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).unwrap_or(ffi::ERR_PANIC)
}

use ffi::{FloorPathInput, FloorPathOutput};

fn main() {
    const N: u32 = 150_000;
    const ITERS: u32 = 2_000;

    let mut angles = Vec::with_capacity(N as usize);
    let mut mults = Vec::with_capacity(N as usize);
    let mut a = 45.0f32;
    for i in 0..N {
        a = (a * 1.13 + i as f32 * 0.7) % 360.0;
        angles.push(a);
        mults.push(1.0);
    }

    let mut entry = vec![0f32; N as usize + 1];
    let mut exit = vec![0f32; N as usize + 1];
    let mut px = vec![0f32; N as usize];
    let mut py = vec![0f32; N as usize];
    let mut count = [0u32];

    let input = FloorPathInput {
        angles: angles.as_ptr(),
        angles_len: N,
        start_angle: std::f32::consts::PI * 1.5,
        tile_size: 0.631,
        length_mults: mults.as_ptr(),
    };
    let mut output = FloorPathOutput {
        entry_angles: entry.as_mut_ptr(),
        exit_angles: exit.as_mut_ptr(),
        positions_x: px.as_mut_ptr(),
        positions_y: py.as_mut_ptr(),
        count: count.as_mut_ptr(),
    };

    let rc = unsafe { remake_path::iridium_core_remake_path(&input, &mut output) };
    assert_eq!(rc, ffi::ERR_OK);
    assert_eq!(count[0], N + 1);

    // Warmup
    for _ in 0..100 {
        unsafe { remake_path::iridium_core_remake_path(&input, &mut output) };
    }

    let start = Instant::now();
    for _ in 0..ITERS {
        unsafe { remake_path::iridium_core_remake_path(&input, &mut output) };
    }
    let elapsed = start.elapsed();
    let per_call_us = elapsed.as_secs_f64() * 1e6 / (ITERS as f64);
    let iters_label = ITERS;
    println!(
        "rust remake_path: {} iters over {} tiles in {:?} => {:.1} us/call",
        iters_label, N, elapsed, per_call_us
    );
}
