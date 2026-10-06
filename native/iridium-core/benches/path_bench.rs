//! Standalone benchmarks:
//!  - remake_path over a 150k-tile chart (Iridium's target upper bound)
//!  - JSON parse over a synthetic 30k-event level document
//!  - spatial grid rebuild + query over 10k AABBs
//!
//! Self-contained: includes the implementation modules directly and provides
//! the `guard` shim that lib.rs would normally supply.

use std::hint::black_box;
use std::time::Instant;

#[path = "../src/ffi.rs"]
mod ffi;
#[path = "../src/compute/easing.rs"]
mod easing;
#[path = "../src/compute/json.rs"]
mod json;
#[path = "../src/compute/remake_path.rs"]
mod remake_path;
#[path = "../src/compute/spatial.rs"]
mod spatial;

// Kernels call `crate::guard`; supply it at the bench crate root.
#[allow(dead_code)]
fn guard(f: impl FnOnce() -> i32) -> i32 {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).unwrap_or(ffi::ERR_PANIC)
}

fn main() {
    bench_remake_path();
    bench_json();
    bench_spatial();
}

fn bench_remake_path() {
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

    let mut entry = vec![0f64; N as usize + 1];
    let mut exit = vec![0f64; N as usize + 1];
    let mut px = vec![0f32; N as usize + 1];
    let mut py = vec![0f32; N as usize + 1];
    let mut count = [0u32];

    let rc = unsafe {
        remake_path::rebuild(
            angles.as_ptr(),
            mults.as_ptr(),
            N,
            std::f64::consts::PI * 1.5,
            0.631,
            0.0,
            0.0,
            entry.as_mut_ptr(),
            exit.as_mut_ptr(),
            px.as_mut_ptr(),
            py.as_mut_ptr(),
            count.as_mut_ptr(),
        )
    };
    assert_eq!(rc, ffi::ERR_OK);
    assert_eq!(count[0], N + 1);

    for _ in 0..100 {
        unsafe {
            remake_path::rebuild(
                angles.as_ptr(),
                mults.as_ptr(),
                N,
                std::f64::consts::PI * 1.5,
                0.631,
                0.0,
                0.0,
                entry.as_mut_ptr(),
                exit.as_mut_ptr(),
                px.as_mut_ptr(),
                py.as_mut_ptr(),
                count.as_mut_ptr(),
            )
        };
    }

    let start = Instant::now();
    for _ in 0..ITERS {
        unsafe {
            remake_path::rebuild(
                black_box(angles.as_ptr()),
                black_box(mults.as_ptr()),
                N,
                std::f64::consts::PI * 1.5,
                0.631,
                0.0,
                0.0,
                entry.as_mut_ptr(),
                exit.as_mut_ptr(),
                px.as_mut_ptr(),
                py.as_mut_ptr(),
                count.as_mut_ptr(),
            )
        };
    }
    let elapsed = start.elapsed();
    std::hint::black_box(count[0]);
    std::hint::black_box(entry[0]);
    std::hint::black_box(px[N as usize]);
    println!(
        "rust remake_path: {ITERS} iters @ {N} tiles => {:.1} us/call",
        elapsed.as_secs_f64() * 1e6 / ITERS as f64
    );
}

fn bench_json() {
    let mut s = String::with_capacity(8 << 20);
    s.push_str("{\"settings\":{\"song\":\"Bench\",\"version\":19},\"actions\":[");
    for i in 0..30_000 {
        if i > 0 {
            s.push(',');
        }
        s.push_str(&format!(
            "{{\"floor\":{i},\"eventType\":\"MoveTrack\",\"duration\":0.5,\"target\":\"R\",\"x\":{},\"active\":true,\"note\":null}}",
            i as f32 * 1.5
        ));
    }
    s.push_str("]}");
    let text: Vec<u16> = s.encode_utf16().collect();

    let mut doc = json::Doc::default();
    const ITERS: u32 = 10;

    let start = Instant::now();
    for _ in 0..ITERS {
        json::parse(&text, &mut doc).expect("parse");
    }
    let elapsed = start.elapsed();
    println!(
        "rust json: {ITERS} iters @ {} KB => {:.2} ms/call, {} nodes, {} strings",
        text.len() * 2 / 1024,
        elapsed.as_secs_f64() * 1e3 / ITERS as f64,
        doc.kinds.len(),
        doc.str_off.len()
    );
}

fn bench_spatial() {
    const N: usize = 10_000;
    let mut rects = Vec::with_capacity(N * 4);
    for i in 0..N {
        let x = (i % 100) as f32 * 2.0;
        let y = (i / 100) as f32 * 2.0;
        rects.extend_from_slice(&[x, y, x + 1.5, y + 1.5]);
    }

    let mut grid = spatial::Grid::default();
    let start = Instant::now();
    for _ in 0..100 {
        grid.rebuild(&rects);
    }
    let rebuild_us = start.elapsed().as_secs_f64() * 1e6 / 100.0;

    let mut total = 0usize;
    let start = Instant::now();
    for _ in 0..1000 {
        for q in 0..N / 10 {
            let x = (q % 50) as f32 * 4.0;
            let y = (q / 50) as f32 * 4.0;
            let hits = grid.query([x, y, x + 5.0, y + 5.0]);
            total += hits.len();
        }
    }
    let query_us = start.elapsed().as_secs_f64() * 1e6 / 1000.0;
    println!(
        "rust spatial: rebuild {N} AABBs => {rebuild_us:.1} us; {} queries/frame => {query_us:.1} us (hits {total})",
        N / 10
    );
}
