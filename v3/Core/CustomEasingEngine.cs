using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace Iridium.Core
{
    /// <summary>
    /// 自定义缓速引擎 — 完全脱离 DOTween 的轻量级动画系统。
    ///
    /// 设计要点（相对旧版的根本性修正）：
    /// 1. IrTween 为 class（引用句柄），由引擎以 (Target, TweenType) 为键统一管理，
    ///    同键自动 Kill(complete) 再创建 —— 与原版 moveTweens 的覆盖语义一致，
    ///    修复了旧版"占位 null 导致重叠事件互相打架"的问题。
    /// 2. 属性的读/写直接以 typed switch 实现（无 delegate、无闭包、无装箱），
    ///    创建与每帧更新的托管分配为零。
    /// 3. 支持 Goto（时间轴 scrub）与按事件（owner）批量 Kill，
    ///    对接原版 ScrubToTime / ffxPlusBase.Kill 的语义。
    /// </summary>
    public static class CustomEasingEngine
    {
        // ==================== 缓速函数计算 ====================

        /// <summary>
        /// 计算指定 Ease 类型在 time/duration 时刻的插值值 [0, 1]
        ///（DOTween 语义，amplitude=1.70158, period=0 为默认参数）。
        /// 数学实现位于 iridium_core（Rust）；库缺失时引擎整体不生效。
        /// </summary>
        public static float Evaluate(Ease ease, float time, float duration)
        {
            return Evaluate(ease, time, duration, 1.70158f, 0f);
        }

        public static float Evaluate(Ease ease, float time, float duration, float amplitude, float period)
        {
            // Hard native binding: the easing math lives in iridium_core.
            // No library -> the whole engine is inert (patches gate on this
            // and never intercept StartEffect).
            return Iridium.Native.IridiumNative.Evaluate((int)ease, time, duration, amplitude, period);
        }
        // ==================== Tween 句柄 ====================

        public enum ValueKind { Float, Vec2, Color }

        public sealed class IrTween
        {
            public object Target = null!;     // scrFloor / scrDecoration
            public object? Owner;             // 发起事件（ffxPlusBase），用于按事件 Kill/Goto
            public TweenType Type;
            public ValueKind Kind;

            // Floor 的 Transform 在创建时缓存，避免每帧 Apply 的 transform 原生调用
            public Transform? CachedTransform;

            public float StartValue, EndValue;
            public Vector2 StartVec2, EndVec2;
            public Color StartColor, EndColor;

            public float Duration;
            public float ElapsedTime;
            public Ease EaseType;

            public bool Dead;          // 被 Kill / 自然结束
            public bool Completed;

            public bool IsAlive => !Dead;

            public void Tick(float dt)
            {
                if (Dead) return;
                ElapsedTime += dt;
                if (ElapsedTime >= Duration)
                {
                    ElapsedTime = Duration;
                    ApplyAt(Duration);
                    Completed = true;
                    Dead = true;
                    return;
                }
                ApplyAt(ElapsedTime);
            }

            /// <summary>
            /// 推进时间但不应用（批处理求值用）：越界时钳位并标记完成。
            /// </summary>
            public void Advance(float dt)
            {
                if (Dead) return;
                ElapsedTime += dt;
                if (ElapsedTime >= Duration)
                {
                    ElapsedTime = Duration;
                    Completed = true;
                    Dead = true;
                }
            }

            /// <summary>立即应用当前进度的状态（创建时调用，消除与原版 DOTween 的时序差）。</summary>
            public void ApplyCurrent()
            {
                if (!Dead) ApplyAt(ElapsedTime);
            }

            /// <summary>跳转到指定进度（秒），应用对应状态；超过时长则完成。语义同 DOTween.Goto(andPlay:true)。</summary>
            public void Goto(float seconds)
            {
                if (Dead) return;
                ElapsedTime = Mathf.Clamp(seconds, 0f, Duration);
                if (ElapsedTime >= Duration)
                {
                    ApplyAt(Duration);
                    Completed = true;
                    Dead = true;
                    return;
                }
                ApplyAt(ElapsedTime);
            }

            /// <summary>语义同 DOTween Kill(complete)。complete=true 时瞬移到终态。</summary>
            public void Kill(bool complete)
            {
                if (Dead) { Completed |= complete && ElapsedTime >= Duration; return; }
                if (complete)
                {
                    ApplyAt(Duration);
                    Completed = true;
                }
                Dead = true;
            }

            /// <summary>用已算好的插值进度应用状态（批处理路径）。</summary>
            public void ApplyProgress(float t)
            {
                switch (Kind)
                {
                    case ValueKind.Vec2:
                        Apply(Target, CachedTransform, Type, Vector2.LerpUnclamped(StartVec2, EndVec2, t));
                        break;
                    case ValueKind.Color:
                        Apply(Target, Type, Color.LerpUnclamped(StartColor, EndColor, t));
                        break;
                    default:
                        Apply(Target, CachedTransform, Type, Mathf.LerpUnclamped(StartValue, EndValue, t));
                        break;
                }
            }

            private void ApplyAt(float time)
            {
                ApplyProgress(Evaluate(EaseType, time, Duration));
            }
        }

        // ==================== 属性读/写（typed switch，零分配） ====================

        private static float GetCurrentFloat(object target, TweenType type)
        {
            if (target is scrFloor floor)
            {
                switch (type)
                {
                    case TweenType.PositionX: return floor.transform.position.x;
                    case TweenType.PositionY: return floor.transform.position.y;
                    case TweenType.Rotation: return floor.tweenRot.z;
                    case TweenType.ScaleX: return floor.transform.localScale.x;
                    case TweenType.ScaleY: return floor.transform.localScale.y;
                    case TweenType.Opacity: return floor.opacity;
                    case TweenType.Glow: return floor.glowMultiplier;
                }
            }
            else if (target is scrDecoration dec)
            {
                switch (type)
                {
                    case TweenType.PositionX: return dec.pivotPosVec.x;
                    case TweenType.PositionY: return dec.pivotPosVec.y;
                    case TweenType.ParallaxOffsetX: return dec.parallaxOffset.x;
                    case TweenType.ParallaxOffsetY: return dec.parallaxOffset.y;
                    case TweenType.PivotX: return dec.pivotOffsetVec.x;
                    case TweenType.PivotY: return dec.pivotOffsetVec.y;
                    case TweenType.Rotation: return dec.rotAngle;
                    case TweenType.ScaleX: return dec.scaleVec.x;
                    case TweenType.ScaleY: return dec.scaleVec.y;
                    case TweenType.Opacity: return dec.opacity;
                }
            }
            return 0f;
        }

        private static Vector2 GetCurrentVec2(object target, TweenType type)
        {
            if (target is scrDecoration dec && type == TweenType.Parallax)
                return dec.parallax.multiplier;
            return Vector2.zero;
        }

        private static Color GetCurrentColor(object target, TweenType type)
        {
            if (target is scrDecoration dec && type == TweenType.Color)
                return dec.color;
            if (target is scrFloor floor && type == TweenType.Color)
                return floor.floorRenderer != null ? floor.floorRenderer.color : Color.white;
            return Color.white;
        }

        private static void Apply(object target, Transform? cachedTform, TweenType type, float value)
        {
            if (target is scrFloor floor)
            {
                Transform t = cachedTform != null ? cachedTform : floor.transform;
                switch (type)
                {
                    case TweenType.PositionX: t.MoveX(value); break;
                    case TweenType.PositionY: t.MoveY(value); break;
                    case TweenType.Rotation:
                        floor.tweenRot.z = value;
                        t.eulerAngles = floor.tweenRot;
                        break;
                    case TweenType.ScaleX:
                    {
                        Vector3 ls = t.localScale;
                        t.localScale = new Vector3(value, ls.y, ls.z);
                        break;
                    }
                    case TweenType.ScaleY:
                    {
                        Vector3 ls = t.localScale;
                        t.localScale = new Vector3(ls.x, value, ls.z);
                        break;
                    }
                    case TweenType.Opacity:
                        floor.dontChangeMySprite = true;
                        floor.opacity = value;
                        break;
                    case TweenType.Glow:
                        floor.glowMultiplier = value;
                        break;
                }
            }
            else if (target is scrDecoration dec)
            {
                switch (type)
                {
                    case TweenType.PositionX: dec.SetPositionX(value, dec.pivotOffsetVec); break;
                    case TweenType.PositionY: dec.SetPositionY(value, dec.pivotOffsetVec); break;
                    case TweenType.ParallaxOffsetX: dec.SetParallaxOffsetX(value); break;
                    case TweenType.ParallaxOffsetY: dec.SetParallaxOffsetY(value); break;
                    case TweenType.PivotX: dec.SetPivotX(value); break;
                    case TweenType.PivotY: dec.SetPivotY(value); break;
                    case TweenType.Rotation: dec.SetRotation(value); break;
                    case TweenType.ScaleX: dec.SetScale(new Vector2(value, dec.scaleVec.y)); break;
                    case TweenType.ScaleY: dec.SetScale(new Vector2(dec.scaleVec.x, value)); break;
                    case TweenType.Opacity: dec.SetOpacity(value); break;
                }
            }
        }

        private static void Apply(object target, TweenType type, float value)
            => Apply(target, null, type, value);

        private static void Apply(object target, TweenType type, Vector2 value)
            => Apply(target, null, type, value);

        private static void Apply(object target, Transform? cachedTform, TweenType type, Vector2 value)
        {
            if (target is scrDecoration dec && type == TweenType.Parallax)
                dec.parallax.multiplier = value;
        }

        private static void Apply(object target, TweenType type, Color value)
        {
            if (target is scrDecoration dec && type == TweenType.Color)
                dec.SetColor(value);
            else if (target is scrFloor floor && type == TweenType.Color)
                floor.SetColor(value);
        }

        // ==================== 全局 Tween 管理 ====================

        private static readonly List<IrTween> _active = new List<IrTween>(256);
        private static readonly List<IrTween> _pendingAdd = new List<IrTween>(64);
        private static readonly Dictionary<(object, TweenType), IrTween> _byKey = new Dictionary<(object, TweenType), IrTween>();
        private static readonly Dictionary<object, List<IrTween>> _byOwner = new Dictionary<object, List<IrTween>>();
        private static readonly Dictionary<object, List<IrTween>> _byTarget = new Dictionary<object, List<IrTween>>();
        private static bool _initialized;
        private static int _lastUpdateFrame = -1;

        public static int ActiveCount => _active.Count;

        /// <summary>某目标出现首个存活引擎 tween（供合批渲染器摘除该目标）。</summary>
        public static event Action<object>? TargetTweensBecameActive;

        /// <summary>某目标的全部引擎 tween 死亡（供合批渲染器回收该目标）。</summary>
        public static event Action<object>? TargetTweensAllDead;

        private static void NotifyTargetActive(object target)
        {
            if (_byTarget.TryGetValue(target, out var list) && list.Exists(t => t.IsAlive))
                TargetTweensBecameActive?.Invoke(target);
        }

        /// <summary>Update 末尾对账：没有任何存活 tween 的目标移出索引并通知。</summary>
        private static void ReconcileDeadTargets()
        {
            if (_byTarget.Count == 0) return;
            List<object>? dead = null;
            foreach (var kvp in _byTarget)
            {
                bool alive = false;
                foreach (var t in kvp.Value)
                {
                    if (t.IsAlive) { alive = true; break; }
                }
                if (!alive)
                {
                    (dead ??= new List<object>()).Add(kvp.Key);
                }
            }
            if (dead == null) return;
            foreach (var target in dead)
            {
                _byTarget.Remove(target);
                TargetTweensAllDead?.Invoke(target);
            }
        }

        public static void Initialize()
        {
            _initialized = true;
        }

        /// <summary>批量缓冲：位置/时长/进度 SoA，按需扩容。</summary>
        private static int[] _batchEase = new int[64];
        private static float[] _batchTime = new float[64];
        private static float[] _batchDur = new float[64];
        private static float[] _batchAmp = new float[64];
        private static float[] _batchPer = new float[64];
        private static float[] _batchOut = new float[64];
        private static IrTween[] _batchMap = new IrTween[64];

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
            _batchMap = new IrTween[cap];
            for (int i = 0; i < cap; i++) _batchAmp[i] = 1.70158f;
        }

        /// <summary>每帧驱动。双指针原地压缩，死亡的 tween 一次性截断。
        /// frameCount 幂等：同帧多次调用只生效一次。
        /// 求值走 native 批处理（SoA 一次 FFI）；库缺失或数量少时逐条。</summary>
        public static void Update(float deltaTime)
        {
            if (!_initialized) return;
            if (_lastUpdateFrame == Time.frameCount) return;
            _lastUpdateFrame = Time.frameCount;

            if (_pendingAdd.Count > 0)
            {
                foreach (var t in _pendingAdd) t.ApplyCurrent();
                _active.AddRange(_pendingAdd);
                _pendingAdd.Clear();
            }

            int count = _active.Count;
            if (count == 0) return;

            if (Iridium.Native.IridiumNative.HasEvaluateBatch && count >= BatchThreshold)
            {
                EnsureBatchCapacity(count);
                int n = 0;
                for (int read = 0; read < count; read++)
                {
                    var tween = _active[read];
                    if (tween.Dead) continue; // 已被 Kill，直接压缩掉
                    tween.Advance(deltaTime);
                    _batchEase[n] = (int)tween.EaseType;
                    _batchTime[n] = tween.ElapsedTime;
                    _batchDur[n] = tween.Duration;
                    _batchMap[n] = tween;
                    n++;
                }

                bool evaluated = n > 0 && Iridium.Native.IridiumNative.EvaluateBatch(
                    _batchEase, _batchTime, _batchDur, _batchAmp, _batchPer, _batchOut, n);

                if (!evaluated)
                {
                    for (int i = 0; i < n; i++)
                    {
                        var tween = _batchMap[i];
                        _batchOut[i] = Evaluate(tween.EaseType, tween.ElapsedTime, tween.Duration);
                    }
                }

                int write = 0;
                for (int i = 0; i < n; i++)
                {
                    var tween = _batchMap[i];
                    tween.ApplyProgress(_batchOut[i]);
                    if (!tween.Dead)
                    {
                        _active[write] = tween;
                        write++;
                    }
                }
                if (write < count)
                    _active.RemoveRange(write, count - write);
            }
            else
            {
                int write = 0;
                for (int read = 0; read < count; read++)
                {
                    var tween = _active[read];
                    tween.Tick(deltaTime);
                    if (!tween.Dead)
                    {
                        _active[write] = tween;
                        write++;
                    }
                }
                if (write < count)
                    _active.RemoveRange(write, count - write);
            }

            ReconcileDeadTargets();
        }

        // ==================== 创建 API ====================

        /// <summary>
        /// 启动一个 float tween。若 (target, type) 已有活跃 tween，先 Kill(complete:true)
        /// ——与原版 moveTweens 覆盖语义一致。当前值已等于目标值时不创建（同原版判定）。
        /// </summary>
        public static IrTween? Start(object target, TweenType type, float endValue, float duration, Ease ease, object? owner = null)
        {
            KillByKey(target, type, complete: true);
            float current = GetCurrentFloat(target, type);
            if (duration <= 0f)
            {
                Apply(target, type, endValue);
                return null;
            }
            if (Mathf.Approximately(current, endValue)) return null;

            var tween = new IrTween
            {
                Target = target,
                Owner = owner,
                Type = type,
                Kind = ValueKind.Float,
                StartValue = current,
                EndValue = endValue,
                Duration = duration,
                EaseType = ease,
                CachedTransform = (target as scrFloor)?.transform
            };
            Register(tween);
            return tween;
        }

        public static IrTween? StartVec2(object target, TweenType type, Vector2 endValue, float duration, Ease ease, object? owner = null)
        {
            KillByKey(target, type, complete: true);
            if (duration <= 0f)
            {
                Apply(target, type, endValue);
                return null;
            }
            Vector2 current = GetCurrentVec2(target, type);
            var tween = new IrTween
            {
                Target = target,
                Owner = owner,
                Type = type,
                Kind = ValueKind.Vec2,
                StartVec2 = current,
                EndVec2 = endValue,
                Duration = duration,
                EaseType = ease
            };
            Register(tween);
            return tween;
        }

        public static IrTween? StartColor(object target, TweenType type, Color endValue, float duration, Ease ease, object? owner = null)
        {
            KillByKey(target, type, complete: true);
            if (duration <= 0f)
            {
                Apply(target, type, endValue);
                return null;
            }
            Color current = GetCurrentColor(target, type);
            var tween = new IrTween
            {
                Target = target,
                Owner = owner,
                Type = type,
                Kind = ValueKind.Color,
                StartColor = current,
                EndColor = endValue,
                Duration = duration,
                EaseType = ease
            };
            Register(tween);
            return tween;
        }

        private static void Register(IrTween tween)
        {
            _pendingAdd.Add(tween);
            _byKey[(tween.Target, tween.Type)] = tween;
            if (tween.Owner != null)
            {
                if (!_byOwner.TryGetValue(tween.Owner, out var list))
                {
                    list = new List<IrTween>();
                    _byOwner[tween.Owner] = list;
                }
                list.Add(tween);
            }
            if (!_byTarget.TryGetValue(tween.Target, out var targetList))
            {
                targetList = new List<IrTween>();
                _byTarget[tween.Target] = targetList;
            }
            targetList.Add(tween);
            TargetTweensBecameActive?.Invoke(tween.Target);
        }

        // ==================== Kill / Goto ====================

        public static void KillByKey(object target, TweenType type, bool complete)
        {
            var key = (target, type);
            if (_byKey.TryGetValue(key, out var tween))
            {
                tween.Kill(complete);
                _byKey.Remove(key);
            }
        }

        /// <summary>某目标对象上是否还有存活的引擎 tween（供外部跳过判定用）。</summary>
        public static bool HasActiveTweens(object target)
        {
            return _byTarget.TryGetValue(target, out var list) && list.Exists(t => t.IsAlive);
        }

        /// <summary>杀死某事件（ffxPlusBase）创建的全部 tween。</summary>
        public static void KillOwned(object owner, bool complete)
        {
            if (_byOwner.TryGetValue(owner, out var list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var tween = list[i];
                    if (tween.IsAlive) tween.Kill(complete);
                    _byKey.Remove((tween.Target, tween.Type));
                }
                list.Clear();
            }
        }

        /// <summary>
        /// 杀死挂在某目标对象上的全部 tween（complete=false 冻结在当前值）。
        /// 用于游戏复位时机（ResetToLevelStart / ResetDecorations），使引擎 tween
        /// 与原版"被杀死的 DOTween tween 不再写入"的行为一致。
        /// 通过 _byTarget 索引 O(命中数) 完成，不随地板总数增长。
        /// </summary>
        public static void KillTarget(object target, bool complete)
        {
            if (!_byTarget.TryGetValue(target, out var list)) return;
            for (int i = 0; i < list.Count; i++)
            {
                var tween = list[i];
                if (tween.IsAlive)
                {
                    tween.Kill(complete);
                    _byKey.Remove((target, tween.Type));
                }
            }
            list.Clear();
        }

        /// <summary>杀死全部装饰物 tween（ResetDecorations 复位时机）。</summary>
        public static void KillAllDecorations(bool complete)
        {
            KillTargetWhere(t => t is scrDecoration, complete);
        }

        private static void KillTargetWhere(Func<object, bool> predicate, bool complete)
        {
            // 直接遍历目标索引（KillTarget 只 Clear 列表，不增删键，枚举安全）；
            // 条目数 = 有 tween 的对象数，远小于 tween 总数。
            foreach (var kvp in _byTarget)
            {
                if (!predicate(kvp.Key)) continue;
                var list = kvp.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    var tween = list[i];
                    if (tween.IsAlive)
                    {
                        tween.Kill(complete);
                        _byKey.Remove((kvp.Key, tween.Type));
                    }
                }
                list.Clear();
            }
        }

        /// <summary>把某事件创建的全部 tween 跳转到指定进度（秒），语义同 ScrubToTime 的 Goto(andPlay:true)。</summary>
        public static void GotoOwned(object owner, float seconds)
        {
            if (_byOwner.TryGetValue(owner, out var list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].IsAlive) list[i].Goto(seconds);
                }
            }
        }

        /// <summary>清空所有活跃 tweens（不触发终态）。</summary>
        public static void ClearAll()
        {
            _active.Clear();
            _pendingAdd.Clear();
            _byKey.Clear();
            _byOwner.Clear();
            _byTarget.Clear();
        }

        /// <summary>杀死所有活跃 tweens。complete=true 时先应用终态（对应 DOTween.KillAll(complete)）。</summary>
        public static void KillAll(bool complete = true)
        {
            if (_pendingAdd.Count > 0)
            {
                _active.AddRange(_pendingAdd);
                _pendingAdd.Clear();
            }
            for (int i = 0; i < _active.Count; i++)
            {
                _active[i].Kill(complete);
            }
            ClearAll();
        }
    }
}
