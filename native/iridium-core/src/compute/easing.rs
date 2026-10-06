//! Easing curves, ported from C# CustomEasingEngine (DOTween semantics).
//!
//! Ease ids are DG.Tweening.Ease numeric values (stable across game versions).
//! Precision mirrors the C# source: curves that C# computes via Math.*
//! (f64) then cast to f32 stay f64 here; pure-f32 curves stay f32. This is
//! the batch inner loop — the transcendental-heavy part of tween updates.

// DG.Tweening.Ease numeric values
pub const LINEAR: i32 = 1;
pub const IN_SINE: i32 = 2;
pub const OUT_SINE: i32 = 3;
pub const IN_OUT_SINE: i32 = 4;
pub const IN_QUAD: i32 = 5;
pub const OUT_QUAD: i32 = 6;
pub const IN_OUT_QUAD: i32 = 7;
pub const IN_CUBIC: i32 = 8;
pub const OUT_CUBIC: i32 = 9;
pub const IN_OUT_CUBIC: i32 = 10;
pub const IN_QUART: i32 = 11;
pub const OUT_QUART: i32 = 12;
pub const IN_OUT_QUART: i32 = 13;
pub const IN_QUINT: i32 = 14;
pub const OUT_QUINT: i32 = 15;
pub const IN_OUT_QUINT: i32 = 16;
pub const IN_EXPO: i32 = 17;
pub const OUT_EXPO: i32 = 18;
pub const IN_OUT_EXPO: i32 = 19;
pub const IN_CIRC: i32 = 20;
pub const OUT_CIRC: i32 = 21;
pub const IN_OUT_CIRC: i32 = 22;
pub const IN_ELASTIC: i32 = 23;
pub const OUT_ELASTIC: i32 = 24;
pub const IN_OUT_ELASTIC: i32 = 25;
pub const IN_BACK: i32 = 26;
pub const OUT_BACK: i32 = 27;
pub const IN_OUT_BACK: i32 = 28;
pub const IN_BOUNCE: i32 = 29;
pub const OUT_BOUNCE: i32 = 30;
pub const IN_OUT_BOUNCE: i32 = 31;
pub const FLASH: i32 = 32;
pub const IN_FLASH: i32 = 33;
pub const OUT_FLASH: i32 = 34;
pub const IN_OUT_FLASH: i32 = 35;

#[inline]
pub fn evaluate(ease: i32, time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    if duration <= 0.0 {
        return if time >= 0.0 { 1.0 } else { 0.0 };
    }

    match ease {
        LINEAR => time / duration,
        IN_SINE => {
            (-((time / duration) as f64 * std::f64::consts::FRAC_PI_2).cos() + 1.0) as f32
        }
        OUT_SINE => (((time / duration) as f64 * std::f64::consts::FRAC_PI_2).sin()) as f32,
        IN_OUT_SINE => {
            (-0.5 * (((time as f64) * std::f64::consts::PI / duration as f64).cos() - 1.0)) as f32
        }
        IN_QUAD => {
            let t = time / duration;
            t * t
        }
        OUT_QUAD => {
            let t = time / duration;
            -t * (t - 2.0)
        }
        IN_OUT_QUAD => {
            let t = time / (duration * 0.5);
            if t < 1.0 {
                0.5 * t * t
            } else {
                let t = t - 1.0;
                -0.5 * (t * (t - 2.0) - 1.0)
            }
        }
        IN_CUBIC => {
            let t = time / duration;
            t * t * t
        }
        OUT_CUBIC => {
            let t = time / duration - 1.0;
            t * t * t + 1.0
        }
        IN_OUT_CUBIC => {
            let t = time / (duration * 0.5);
            if t < 1.0 {
                0.5 * t * t * t
            } else {
                let t = t - 2.0;
                0.5 * (t * t * t + 2.0)
            }
        }
        IN_QUART => {
            let t = time / duration;
            t * t * t * t
        }
        OUT_QUART => {
            let t = time / duration - 1.0;
            -(t * t * t * t - 1.0)
        }
        IN_OUT_QUART => {
            let t = time / (duration * 0.5);
            if t < 1.0 {
                0.5 * t * t * t * t
            } else {
                let t = t - 2.0;
                -0.5 * (t * t * t * t - 2.0)
            }
        }
        IN_QUINT => {
            let t = time / duration;
            t * t * t * t * t
        }
        OUT_QUINT => {
            let t = time / duration;
            t * t * t * t * t + 1.0
        }
        IN_OUT_QUINT => {
            let t = time / (duration * 0.5);
            if t < 1.0 {
                0.5 * t * t * t * t * t
            } else {
                let t = t - 2.0;
                0.5 * (t * t * t * t * t + 2.0)
            }
        }
        IN_EXPO => {
            if time == 0.0 {
                0.0
            } else {
                (2.0f64.powf(10.0 * (time / duration - 1.0) as f64)) as f32
            }
        }
        OUT_EXPO => {
            if time == duration {
                1.0
            } else {
                (-2.0f64.powf(-10.0 * time as f64 / duration as f64) + 1.0) as f32
            }
        }
        IN_OUT_EXPO => {
            if time == 0.0 {
                0.0
            } else if time == duration {
                1.0
            } else {
                let t = time / (duration * 0.5);
                if t < 1.0 {
                    (0.5 * 2.0f64.powf(10.0 * (t - 1.0) as f64)) as f32
                } else {
                    let t = t - 1.0;
                    (0.5 * (-2.0f64.powf(-10.0 * t as f64) + 2.0)) as f32
                }
            }
        }
        IN_CIRC => {
            let t = time / duration;
            (-(1.0f64 - (t * t) as f64).sqrt() + 1.0) as f32
        }
        OUT_CIRC => {
            let t = time / duration - 1.0;
            ((1.0f64 - (t * t) as f64).sqrt()) as f32
        }
        IN_OUT_CIRC => {
            let t = time / (duration * 0.5);
            if t < 1.0 {
                (-0.5 * ((1.0f64 - t as f64 * t as f64).sqrt() - 1.0)) as f32
            } else {
                let t = t - 2.0;
                (0.5 * ((1.0f64 - t as f64 * t as f64).sqrt() + 1.0)) as f32
            }
        }
        IN_BACK => {
            let t = time / duration;
            t * t * ((amplitude + 1.0) * t - amplitude)
        }
        OUT_BACK => {
            let t = time / duration - 1.0;
            t * t * ((amplitude + 1.0) * t + amplitude) + 1.0
        }
        IN_OUT_BACK => {
            let s = amplitude * 1.525;
            let t = time / (duration * 0.5);
            if t < 1.0 {
                0.5 * (t * t * ((s + 1.0) * t - s))
            } else {
                let t = t - 2.0;
                0.5 * (t * t * ((s + 1.0) * t + s) + 2.0)
            }
        }
        IN_BOUNCE => bounce_in(time, duration),
        OUT_BOUNCE => bounce_out(time, duration),
        IN_OUT_BOUNCE => bounce_in_out(time, duration),
        IN_ELASTIC => elastic_in(time, duration, amplitude, period),
        OUT_ELASTIC => elastic_out(time, duration, amplitude, period),
        IN_OUT_ELASTIC => elastic_in_out(time, duration, amplitude, period),
        FLASH => flash(time, duration, amplitude, period),
        IN_FLASH => flash_in(time, duration, amplitude, period),
        OUT_FLASH => flash_out(time, duration, amplitude, period),
        IN_OUT_FLASH => flash_in_out(time, duration, amplitude, period),
        // C# default: OutQuad
        _ => {
            let t = time / duration;
            -t * (t - 2.0)
        }
    }
}

fn bounce_out(mut time: f32, duration: f32) -> f32 {
    time /= duration;
    if time < 0.363_636_37 {
        return 7.5625 * time * time;
    }
    if time < 0.727_272_75 {
        time -= 0.545_454_56;
        return 7.5625 * time * time + 0.75;
    }
    if time < 0.909_090_94 {
        time -= 0.818_181_8;
        return 7.5625 * time * time + 0.9375;
    }
    time -= 21.0 / 22.0;
    7.5625 * time * time + 63.0 / 64.0
}

fn bounce_in(time: f32, duration: f32) -> f32 {
    1.0 - bounce_out(duration - time, duration)
}

fn bounce_in_out(time: f32, duration: f32) -> f32 {
    if time < duration * 0.5 {
        return bounce_in(time * 2.0, duration) * 0.5;
    }
    bounce_out(time * 2.0 - duration, duration) * 0.5 + 0.5
}

// Elastic: C# computes Pow/Sin/Asin in f64, casts to f32 per term.
// period == 0 defaults inside (C# passes it by value; the ref mutation
// never escapes Evaluate).

fn elastic_params(amplitude: f32, period: f32) -> (f32, f32) {
    if amplitude < 1.0 {
        (1.0, period / 4.0)
    } else {
        let s = (period / (std::f64::consts::PI * 2.0) as f32)
            * (1.0f64 / amplitude as f64).asin() as f32;
        (amplitude, s)
    }
}

fn elastic_in(mut time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    if time == 0.0 {
        return 0.0;
    }
    time /= duration;
    if time == 1.0 {
        return 1.0;
    }
    let period = if period == 0.0 { duration * 0.3 } else { period };
    let (amplitude, s) = elastic_params(amplitude, period);
    let t = time - 1.0;
    let pow = 2.0f64.powf(10.0 * t as f64) as f32;
    let sin = (((t * duration - s) as f64) * (std::f64::consts::PI * 2.0) / period as f64).sin()
        as f32;
    -(amplitude * pow * sin)
}

fn elastic_out(mut time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    if time == 0.0 {
        return 0.0;
    }
    time /= duration;
    if time == 1.0 {
        return 1.0;
    }
    let period = if period == 0.0 { duration * 0.3 } else { period };
    let (amplitude, s) = elastic_params(amplitude, period);
    let pow = 2.0f64.powf(-10.0 * time as f64) as f32;
    let sin = (((time * duration - s) as f64) * (std::f64::consts::PI * 2.0) / period as f64).sin()
        as f32;
    amplitude * pow * sin + 1.0
}

fn elastic_in_out(mut time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    if time == 0.0 {
        return 0.0;
    }
    time /= duration * 0.5;
    if time == 2.0 {
        return 1.0;
    }
    let period = if period == 0.0 {
        duration * 0.450_000_02
    } else {
        period
    };
    let (amplitude, s) = elastic_params(amplitude, period);
    let two_pi = std::f64::consts::PI * 2.0;
    if time < 1.0 {
        let t = time - 1.0;
        let pow = 2.0f64.powf(10.0 * t as f64) as f32;
        let sin = (((t * duration - s) as f64) * two_pi / period as f64).sin() as f32;
        return -0.5 * (amplitude * pow * sin);
    }
    let t = time - 1.0;
    let pow = 2.0f64.powf(-10.0 * t as f64) as f32;
    let sin = (((t * duration - s) as f64) * two_pi / period as f64).sin() as f32;
    amplitude * pow * sin * 0.5 + 1.0
}

// Flash: ported from DOTween Flash + WeightedEase via the C# source.

fn flash_weighted(
    amplitude: f32,
    mut period: f32,
    mut step_index: i32,
    _step_duration: f32,
    dir: f32,
    mut res: f32,
) -> f32 {
    if dir > 0.0 && (amplitude as i32) % 2 == 0 {
        step_index += 1;
    } else if dir < 0.0 && (amplitude as i32) % 2 != 0 {
        step_index += 1;
    }
    let mut extra = 0.0f32;
    let mut delta = 0.0f32;
    if period > 0.0 {
        let whole = amplitude.trunc();
        extra = amplitude - whole;
        if whole % 2.0 > 0.0 {
            extra = 1.0 - extra;
        }
        extra = extra * step_index as f32 / amplitude;
        delta = res * (amplitude - step_index as f32) / amplitude;
    } else if period < 0.0 {
        period = -period;
        delta = res * step_index as f32 / amplitude;
    }
    let diff = delta - res;
    res += diff * period + extra;
    if res > 1.0 {
        res = 1.0;
    }
    res
}

fn flash_common(time: f32, duration: f32, amplitude: f32) -> (i32, f32, f32, f32) {
    // (step, step_duration, time', dir)
    let step = (time / duration * amplitude).ceil() as i32;
    let step_duration = duration / amplitude;
    let mut t = time - step_duration * (step - 1) as f32;
    let dir = if step % 2 != 0 { 1.0 } else { -1.0 };
    if dir < 0.0 {
        t -= step_duration;
    }
    (step, step_duration, t, dir)
}

fn flash(time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    let (step, step_duration, t, dir) = flash_common(time, duration, amplitude);
    let res = t * dir / step_duration;
    flash_weighted(amplitude, period, step, step_duration, dir, res)
}

fn flash_in(time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    let (step, step_duration, mut t, dir) = flash_common(time, duration, amplitude);
    t *= dir;
    t /= step_duration;
    let res = t * t;
    flash_weighted(amplitude, period, step, step_duration, dir, res)
}

fn flash_out(time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    let (step, step_duration, mut t, dir) = flash_common(time, duration, amplitude);
    t *= dir;
    t /= step_duration;
    let res = -(t * (t - 2.0));
    flash_weighted(amplitude, period, step, step_duration, dir, res)
}

fn flash_in_out(time: f32, duration: f32, amplitude: f32, period: f32) -> f32 {
    let (step, step_duration, mut t, dir) = flash_common(time, duration, amplitude);
    t *= dir;
    t /= step_duration * 0.5;
    let res = if t < 1.0 {
        0.5 * t * t
    } else {
        let t = t - 1.0;
        -0.5 * (t * (t - 2.0) - 1.0)
    };
    flash_weighted(amplitude, period, step, step_duration, dir, res)
}
