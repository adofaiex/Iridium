using System;
using DG.Tweening;
using UnityEngine;

namespace Iridium.Core
{
    /// <summary>
    /// 自定义缓速引擎 — 替代 DOTween 的轻量级动画系统。
    /// 用于 MoveTrack / RecolorTrack / MoveDecoration 的 StartEffect 场景。
    /// 特点：零 GC 分配（struct Tween）、每帧 Update 驱动、对象池管理。
    /// </summary>
    public static class CustomEasingEngine
    {
        // ==================== 缓速函数计算 ====================

        private const float PiOver2 = (float)Math.PI / 2f;

        /// <summary>
        /// 计算指定 Ease 类型在 t/duration 时刻的插值值 [0, 1]。
        /// 移植自 DOTween EaseManager.Evaluate。
        /// </summary>
        public static float Evaluate(Ease ease, float time, float duration)
        {
            // Native fast path (iridium_core). Falls back to the managed
            // switch when the library is absent — v2 predates the hard
            // binding split; see git history for the v3 treatment.
            if (Iridium.Native.IridiumNative.Available)
                return Iridium.Native.IridiumNative.Evaluate((int)ease, time, duration, 1.70158f, 0f);

            if (duration <= 0f) return time >= 0f ? 1f : 0f;

            switch (ease)
            {
                case Ease.Linear: return time / duration;
                case Ease.InSine: return -(float)Math.Cos(time / duration * PiOver2) + 1f;
                case Ease.OutSine: return (float)Math.Sin(time / duration * PiOver2);
                case Ease.InOutSine: return -0.5f * ((float)Math.Cos((float)Math.PI * time / duration) - 1f);
                case Ease.InQuad: { var t = time / duration; return t * t; }
                case Ease.OutQuad: { var t = time / duration; return -t * (t - 2f); }
                case Ease.InOutQuad:
                {
                    var t = time / (duration * 0.5f);
                    return t < 1f ? 0.5f * t * t : -0.5f * ((t -= 1f) * (t - 2f) - 1f);
                }
                case Ease.InCubic: { var t = time / duration; return t * t * t; }
                case Ease.OutCubic: { var t = time / duration - 1f; return t * t * t + 1f; }
                case Ease.InOutCubic:
                {
                    var t = time / (duration * 0.5f);
                    return t < 1f ? 0.5f * t * t * t : 0.5f * ((t -= 2f) * t * t + 2f);
                }
                case Ease.InQuart: { var t = time / duration; return t * t * t * t; }
                case Ease.OutQuart: { var t = time / duration - 1f; return -(t * t * t * t - 1f); }
                case Ease.InOutQuart:
                {
                    var t = time / (duration * 0.5f);
                    return t < 1f ? 0.5f * t * t * t * t : -0.5f * ((t -= 2f) * t * t * t - 2f);
                }
                case Ease.InQuint: { var t = time / duration; return t * t * t * t * t; }
                case Ease.OutQuint: { var t = time / duration - 1f; return t * t * t * t * t + 1f; }
                case Ease.InOutQuint:
                {
                    var t = time / (duration * 0.5f);
                    return t < 1f ? 0.5f * t * t * t * t * t : 0.5f * ((t -= 2f) * t * t * t * t + 2f);
                }
                case Ease.InExpo: return time == 0f ? 0f : (float)Math.Pow(2f, 10f * (time / duration - 1f));
                case Ease.OutExpo: return time == duration ? 1f : -(float)Math.Pow(2f, -10f * time / duration) + 1f;
                case Ease.InOutExpo:
                {
                    if (time == 0f) return 0f;
                    if (time == duration) return 1f;
                    var t = time / (duration * 0.5f);
                    return t < 1f ? 0.5f * (float)Math.Pow(2f, 10f * (t - 1f))
                                      : 0.5f * (-(float)Math.Pow(2f, -10f * (t -= 1f)) + 2f);
                }
                case Ease.InCirc: return -(float)Math.Sqrt(1f - (time /= duration) * time) + 1f;
                case Ease.OutCirc: return (float)Math.Sqrt(1f - (time = time / duration - 1f) * time);
                case Ease.InOutCirc:
                {
                    var t = time / (duration * 0.5f);
                    return t < 1f ? -0.5f * ((float)Math.Sqrt(1f - t * t) - 1f)
                                     : 0.5f * ((float)Math.Sqrt(1f - (t -= 2f) * t) + 1f);
                }
                case Ease.InBack:
                {
                    var s = 1.70158f;
                    var t = time / duration;
                    return t * t * ((s + 1f) * t - s);
                }
                case Ease.OutBack:
                {
                    var s = 1.70158f;
                    var t = time / duration - 1f;
                    return t * t * ((s + 1f) * t + s) + 1f;
                }
                case Ease.InOutBack:
                {
                    var s = 1.70158f * 1.525f;
                    var t = time / (duration * 0.5f);
                    return t < 1f ? 0.5f * (t * t * ((s + 1f) * t - s))
                                     : 0.5f * ((t -= 2f) * t * ((s + 1f) * t + s) + 2f);
                }
                default: // OutQuad
                {
                    var t = time / duration;
                    return -t * (t - 2f);
                }
            }
        }

        // ==================== Tween 数据结构 ====================

        /// <summary>
        /// 轻量级 Tween — 使用 struct 避免堆分配。
        /// 通过 _activeTweens 列表统一驱动，支持 float / Vector3 / Color 插值。
        /// </summary>
        public struct IrTween
        {
            public enum TweenState { Running, Completed, Killed }

            public TweenType Type;           // 标识符，用于字典存取（兼容原游戏 eventTweens）
            public float StartValue;          // 起始值（float 模式）
            public float EndValue;            // 目标值
            public float ElapsedTime;         // 已过时间
            public float Duration;            // 总时长
            public Ease EaseType;             // 缓速类型
            public TweenState State;
            public bool IsKilled;

            // 回调（使用 Action 避免闭包 GC）
            public Action<float>? OnUpdateFloat;     // float 更新回调
            public Action? OnComplete;               // 完成回调

            // Vector3 模式
            public bool IsVector3;
            public Vector3 StartVec3;
            public Vector3 EndVec3;
            public Action<Vector3>? OnUpdateVec3;

            // Color 模式
            public bool IsColor;
            public Color StartCol;
            public Color EndCol;
            public Action<Color>? OnUpdateCol;

            public void Update(float dt)
            {
                if (IsKilled || State == TweenState.Completed) return;

                ElapsedTime += dt;
                if (ElapsedTime >= Duration)
                {
                    ElapsedTime = Duration;
                    ApplyFinal();
                    State = TweenState.Completed;
                    OnComplete?.Invoke();
                    return;
                }

                float t = Evaluate(EaseType, ElapsedTime, Duration);
                ApplyInterpolated(t);
            }

            /// <summary>
            /// 批处理第一阶段：推进时间但不应用。返回 false 表示该 tween
            /// 已死亡/已完成（应被压缩掉）；completing 表示本次越界需收尾。
            /// </summary>
            public bool Advance(float dt, out bool completing)
            {
                completing = false;
                if (IsKilled || State == TweenState.Completed) return false;
                ElapsedTime += dt;
                if (ElapsedTime >= Duration)
                {
                    ElapsedTime = Duration;
                    completing = true;
                }
                return true;
            }

            /// <summary>批处理第二阶段：用已算好的进度应用插值。</summary>
            public void ApplyInterpolatedProgress(float t) => ApplyInterpolated(t);

            /// <summary>批处理收尾：应用终态并触发完成回调。</summary>
            public void FinalizeComplete()
            {
                ApplyFinal();
                State = TweenState.Completed;
                OnComplete?.Invoke();
            }

            public void Kill(bool complete = true)
            {
                if (IsKilled) return;
                IsKilled = true;
                if (complete && State != TweenState.Completed)
                {
                    ApplyFinal();
                    OnComplete?.Invoke();
                }
                State = TweenState.Killed;
            }

            private void ApplyInterpolated(float t)
            {
                if (IsVector3)
                {
                    var v = Vector3.LerpUnclamped(StartVec3, EndVec3, t);
                    OnUpdateVec3?.Invoke(v);
                }
                else if (IsColor)
                {
                    var c = Color.LerpUnclamped(StartCol, EndCol, t);
                    OnUpdateCol?.Invoke(c);
                }
                else
                {
                    float v = Mathf.LerpUnclamped(StartValue, EndValue, t);
                    OnUpdateFloat?.Invoke(v);
                }
            }

            private void ApplyFinal()
            {
                if (IsVector3)
                    OnUpdateVec3?.Invoke(EndVec3);
                else if (IsColor)
                    OnUpdateCol?.Invoke(EndCol);
                else
                    OnUpdateFloat?.Invoke(EndValue);
            }

            public readonly bool IsActive => !IsKilled && State != TweenState.Completed;
        }

        // ==================== 全局 Tween 管理 ====================

        /// <summary>所有活跃的 Tweens（每帧遍历更新）</summary>
        private static readonly System.Collections.Generic.List<IrTween> _activeTweens = new(256);

        /// <summary>当前帧新增的 Tweens（避免遍历时修改）</summary>
        private static readonly System.Collections.Generic.List<IrTween> _pendingAdd = new(64);

        /// <summary>是否已初始化</summary>
        private static bool _initialized;

        /// <summary>当前活跃 Tween 数量</summary>
        public static int ActiveCount => _activeTweens.Count;

        /// <summary>
        /// 初始化引擎（注册到 Main.Update）。
        /// 只需调用一次。
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
        }

        /// <summary>批处理缓冲（SoA）；按需扩容。</summary>
        private static int[] _batchEase = new int[64];
        private static float[] _batchTime = new float[64];
        private static float[] _batchDur = new float[64];
        private static float[] _batchAmp = new float[64];
        private static float[] _batchPer = new float[64];
        private static float[] _batchOut = new float[64];
        private static int[] _batchIndex = new int[64];

        /// <summary>低于该数量时逐条求值（FFI 开销大于收益）。</summary>
        private const int BatchThreshold = 8;

        static CustomEasingEngine()
        {
            for (int i = 0; i < _batchAmp.Length; i++) _batchAmp[i] = 1.70158f;
        }

        private static void EnsureBatchCapacity(int n)
        {
            if (_batchEase.Length >= n) return;
            int cap = _batchEase.Length;
            while (cap < n) cap *= 2;
            _batchEase = new int[cap];
            _batchTime = new float[cap];
            _batchDur = new float[cap];
            _batchAmp = new float[cap];
            _batchPer = new float[cap];
            _batchOut = new float[cap];
            _batchIndex = new int[cap];
            for (int i = 0; i < cap; i++) _batchAmp[i] = 1.70158f;
        }

        /// <summary>
        /// 每帧调用 — 驱动所有活跃 Tweens。
        /// 使用双指针原地压缩算法：活跃 tween 前移，死亡 tween 末尾一次性清除。
        /// 数量足够时缓动求值走 native 批处理（一次 FFI），否则逐条。
        /// 必须在 Main.Update 中调用。
        /// </summary>
        public static void Update(float deltaTime)
        {
            if (!_initialized) return;

            // 合入上一帧新增的
            if (_pendingAdd.Count > 0)
            {
                _activeTweens.AddRange(_pendingAdd);
                _pendingAdd.Clear();
            }

            int count = _activeTweens.Count;
            if (count == 0) return;

            if (Iridium.Native.IridiumNative.HasEvaluateBatch && count >= BatchThreshold)
            {
                UpdateBatched(deltaTime, count);
                return;
            }

            // 双指针原地压缩：write 跟踪下一个存活位置，read 遍历所有元素
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                var tween = _activeTweens[read];
                tween.Update(deltaTime);
                if (tween.IsActive)
                {
                    _activeTweens[write] = tween;
                    write++;
                }
                // 死亡的 tween 直接跳过（不写入任何位置）
            }

            // 一次性截断尾部所有死亡 tween（内部只是 Size--，无数组移位）
            if (write < count)
                _activeTweens.RemoveRange(write, count - write);
        }

        private static void UpdateBatched(float deltaTime, int count)
        {
            EnsureBatchCapacity(count);

            // 第一阶段：推进时间，收集非完成项的求值输入（不应用）。
            int n = 0;
            for (int read = 0; read < count; read++)
            {
                var tween = _activeTweens[read];
                if (!tween.Advance(deltaTime, out bool completing))
                    continue; // Killed / Completed：直接压缩掉

                _activeTweens[read] = tween; // Advance 修改了 ElapsedTime

                if (completing)
                {
                    _batchIndex[read] = -1; // 标记：稍后 FinalizeComplete
                    continue;
                }
                _batchEase[n] = (int)tween.EaseType;
                _batchTime[n] = tween.ElapsedTime;
                _batchDur[n] = tween.Duration;
                _batchIndex[read] = n;
                n++;
            }

            bool evaluated = n > 0 && Iridium.Native.IridiumNative.EvaluateBatch(
                _batchEase, _batchTime, _batchDur, _batchAmp, _batchPer, _batchOut, n);

            // 第二阶段：按原顺序应用插值 / 收尾，同时压缩。
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                var tween = _activeTweens[read];
                if (!tween.IsActive) continue;

                int slot = read < _batchIndex.Length ? _batchIndex[read] : -2;
                if (slot == -1)
                {
                    tween.FinalizeComplete();
                }
                else if (slot >= 0 && slot < n)
                {
                    float t = evaluated
                        ? _batchOut[slot]
                        : Evaluate(tween.EaseType, tween.ElapsedTime, tween.Duration);
                    tween.ApplyInterpolatedProgress(t);
                }
                else
                {
                    // 无槽位（不应发生）：退回逐条。
                    tween.Update(0f);
                }

                if (tween.IsActive)
                {
                    _activeTweens[write] = tween;
                    write++;
                }
            }

            if (write < count)
                _activeTweens.RemoveRange(write, count - write);
        }

        /// <summary>清空所有活跃 Tweens（不触发回调）</summary>
        public static void ClearAll()
        {
            _activeTweens.Clear();
            _pendingAdd.Clear();
        }

        /// <summary>
        /// 杀死所有活跃 Tweens（触发 OnComplete 回调，使目标值到达终态）。
        /// 用于在游戏退出播放/倒带时与 DOTween.KillAll 同步清理。
        /// </summary>
        public static void KillAll()
        {
            // 先合入待新增的，确保所有 tween 都在列表中
            if (_pendingAdd.Count > 0)
            {
                _activeTweens.AddRange(_pendingAdd);
                _pendingAdd.Clear();
            }
            // 对每个活跃 tween 杀死（complete=true → 应用终态 + 触发 OnComplete）
            for (int i = 0; i < _activeTweens.Count; i++)
            {
                _activeTweens[i].Kill(complete: true);
            }
            ClearAll();
        }

        // ==================== 工厂方法 ====================

        /// <summary>创建一个 float Tween 并立即加入更新队列</summary>
        public static IrTween To(
            Func<float> getter, Action<float> setter,
            float endValue, float duration, Ease ease,
            Action? onComplete = null)
        {
            var tween = new IrTween
            {
                StartValue = getter(),
                EndValue = endValue,
                Duration = duration,
                EaseType = ease,
                State = IrTween.TweenState.Running,
                OnUpdateFloat = setter,
                OnComplete = onComplete
            };

            _pendingAdd.Add(tween);
            return tween;
        }

        /// <summary>创建一个 Vector3 Tween</summary>
        public static IrTween ToVector3(
            Func<Vector3> getter, Action<Vector3> setter,
            Vector3 endValue, float duration, Ease ease,
            Action? onComplete = null)
        {
            var tween = new IrTween
            {
                IsVector3 = true,
                StartVec3 = getter(),
                EndVec3 = endValue,
                Duration = duration,
                EaseType = ease,
                State = IrTween.TweenState.Running,
                OnUpdateVec3 = setter,
                OnComplete = onComplete
            };

            _pendingAdd.Add(tween);
            return tween;
        }

        /// <summary>创建一个 Color Tween</summary>
        public static IrTween ToColor(
            Func<Color> getter, Action<Color> setter,
            Color endValue, float duration, Ease ease,
            Action? onComplete = null)
        {
            var tween = new IrTween
            {
                IsColor = true,
                StartCol = getter(),
                EndCol = endValue,
                Duration = duration,
                EaseType = ease,
                State = IrTween.TweenState.Running,
                OnUpdateCol = setter,
                OnComplete = onComplete
            };

            _pendingAdd.Add(tween);
            return tween;
        }

        /// <summary>创建一个带起始值的 float Tween（用于已知 startValue 的场景）</summary>
        public static IrTween ToFrom(
            float startValue, Action<float> setter,
            float endValue, float duration, Ease ease,
            Action? onComplete = null)
        {
            var tween = new IrTween
            {
                StartValue = startValue,
                EndValue = endValue,
                Duration = duration,
                EaseType = ease,
                State = IrTween.TweenState.Running,
                OnUpdateFloat = setter,
                OnComplete = onComplete
            };

            _pendingAdd.Add(tween);
            return tween;
        }
    }
}
