use crate::ffi;

/// Single-curve evaluation (FFI wrapper).
#[no_mangle]
pub unsafe extern "C" fn iridium_core_evaluate(
    ease: i32,
    time: f32,
    duration: f32,
    amplitude: f32,
    period: f32,
) -> f32 {
    // Pure math on by-value floats; no failure modes.
    crate::compute::easing::evaluate(ease, time, duration, amplitude, period)
}

/// Batch evaluation (SoA in, one flat out buffer).
///
/// # Safety
/// All pointers must be valid for `len` elements; `out` is caller-allocated.
#[no_mangle]
pub unsafe extern "C" fn iridium_core_evaluate_batch(
    ease_ids: *const i32,
    times: *const f32,
    durations: *const f32,
    amplitudes: *const f32,
    periods: *const f32,
    len: u32,
    out_progress: *mut f32,
) -> i32 {
    crate::guard(|| {
        let ease_ids = match ffi::slice(ease_ids, len) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let times = match ffi::slice(times, len) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let durations = match ffi::slice(durations, len) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let amplitudes = match ffi::slice(amplitudes, len) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let periods = match ffi::slice(periods, len) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };
        let out = match ffi::slice_mut(out_progress, len) {
            Some(s) => s,
            None => return ffi::ERR_NULL_ARG,
        };

        for i in 0..len as usize {
            out[i] = crate::compute::easing::evaluate(
                ease_ids[i], times[i], durations[i], amplitudes[i], periods[i],
            );
        }
        ffi::ERR_OK
    })
}
