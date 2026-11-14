using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ADOFAI;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    public static class ParticleOptimizationPatches
    {
        private class ParticleUpdateState
        {
            public Vector2 lastScale;
            public float lastSimSpeed;
            public float lastPitch;
            public bool initialized;
        }

        private static readonly ConditionalWeakTable<scrParticleDecoration, ParticleUpdateState> _states = new();

        // 池化
        private const int MaxPoolSize = 256;
        private static Transform _poolRoot = null!;
        private static readonly Stack<GameObject> _objectPool = new();

        private static void DrainPool(int keep = 0)
        {
            while (_objectPool.Count > keep)
            {
                var go = _objectPool.Pop();
                if (go != null)
                    global::UnityEngine.Object.Destroy(go);
            }
        }

        private static Transform GetPoolRoot()
        {
            if (_poolRoot == null)
            {
                var go = new GameObject("_Iridium_ParticlePool");
                global::UnityEngine.Object.DontDestroyOnLoad(go);
                go.SetActive(false);
                _poolRoot = go.transform;
            }
            return _poolRoot;
        }

        private static bool PartOpt =>
            Main.Settings?.optimizer is { enableOptimizer: true, optimizeParticle: true };

        private static bool PartInactive =>
            PartOpt && Main.Settings.optimizer.optimizeParticleInactive;

        private static bool PartCulling =>
            PartOpt && Main.Settings.optimizer.optimizeParticleCulling;

        private static bool PartLod =>
            PartOpt && Main.Settings.optimizer.optimizeParticleLod;

        /// <summary>
        /// 1) ResetParticle Postfix: 设 cullingMode = Pause → 离屏自动暂停 CPU 模拟
        /// </summary>
        [HarmonyPatch(typeof(scrParticleDecoration), "ResetParticle")]
        public static class ParticleCullingModePatch
        {
            [HarmonyPostfix]
            public static void Postfix(scrParticleDecoration __instance)
            {
                if (!PartCulling) return;
                var main = __instance.particleSystem.main;
                main.cullingMode = ParticleSystemCullingMode.Pause;
            }
        }

        /// <summary>
        /// 2) Update Prefix:
        ///    - 粒子已停止 + 非初始化 → 跳过整帧
        ///    - 不可见 → 跳过整帧
        ///    - Dirty-flag: scale/speed 没变 → 跳过 Unity API 调用
        /// </summary>
        [HarmonyPatch(typeof(scrParticleDecoration), "Update")]
        public static class ParticleUpdateSkipPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrParticleDecoration __instance)
            {
                if (!PartInactive) return true;

                var ps = __instance.particleSystem;
                if (ps == null) return true;

                if (!ps.isPlaying && !__instance.atStart)
                    return false;
                if (!__instance.GetVisible())
                    return false;

                var state = _states.GetValue(__instance, _ => new ParticleUpdateState());
                float pitch = ADOBase.conductor?.song?.pitch ?? 1f;
                bool scaleChanged = !state.initialized || state.lastScale != __instance.scale;
                bool speedChanged = !state.initialized ||
                    state.lastSimSpeed != __instance.simulationSpeed ||
                    state.lastPitch != pitch;

                if (!scaleChanged && !speedChanged && !__instance.atStart)
                    return false;

                state.initialized = true;
                state.lastScale = __instance.scale;
                state.lastSimSpeed = __instance.simulationSpeed;
                state.lastPitch = pitch;
                return true;
            }
        }

        /// <summary>
        /// 3) ClearDecorations Prefix: 清场时把粒子 GO 回收进池，避免 DestroyImmediate
        /// </summary>
        [HarmonyPatch(typeof(scrDecorationManager), "ClearDecorations")]
        public static class ParticlePoolOnClearPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrDecorationManager __instance)
            {
                if (!PartInactive) return;

                var root = GetPoolRoot();
                for (int i = __instance.allDecorations.Count - 1; i >= 0; i--)
                {
                    if (__instance.allDecorations[i] is scrParticleDecoration pd && pd.gameObject != null)
                    {
                        pd.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        pd.gameObject.transform.SetParent(root);
                        pd.gameObject.SetActive(false);
                        if (_objectPool.Count < MaxPoolSize)
                            _objectPool.Push(pd.gameObject);
                        else
                            global::UnityEngine.Object.Destroy(pd.gameObject);
                    }
                }
            }
        }

        /// <summary>
        /// 4) CreateDecoration Postfix: 创建粒子时优先从池取
        /// </summary>
        [HarmonyPatch(typeof(scrDecorationManager), "CreateDecoration")]
        public static class ParticlePoolOnCreatePatch
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecorationManager __instance, LevelEvent levelEvent, int index)
            {
                if (!PartInactive) return;
                if (levelEvent.eventType != LevelEventType.AddParticle) return;
                if (_objectPool.Count == 0) return;

                // 最后一项是刚刚 Create 出来的
                var freshDec = __instance.allDecorations[__instance.allDecorations.Count - 1] as scrParticleDecoration;
                if (freshDec == null) return;

                var pooledGO = _objectPool.Pop();
                var pooledDec = pooledGO.GetComponent<scrParticleDecoration>();
                if (pooledDec == null) { _objectPool.Push(pooledGO); return; }

                int idx = __instance.allDecorations.IndexOf(freshDec);
                if (idx < 0) { _objectPool.Push(pooledGO); return; }

                // 交换：池对象替换新创建的
                var ev = freshDec.sourceLevelEvent;
                __instance.allDecorations[idx] = pooledDec;

                pooledGO.transform.SetParent(__instance.transform);
                pooledGO.SetActive(true);
                pooledDec.manager = __instance;
                pooledDec.sourceLevelEvent = ev;
                pooledDec.decType = DecorationType.Particle;
                pooledDec.Setup(ev, out _, true);
                pooledDec.UpdateHitbox();

                global::UnityEngine.Object.Destroy(freshDec.gameObject);
            }
        }

        /// <summary>
        /// 5) LogicUpdate LOD: 由 FfxOptimizationPatches 的 LateUpdate 循环调用
        /// </summary>
        internal static bool ShouldSkipParticleLogic(scrDecoration dec)
        {
            if (!PartLod) return false;
            if (dec is not scrParticleDecoration) return false;
            if (dec.useHitbox) return false;
            if (dec.eventTweens != null && dec.eventTweens.Count > 0)
                foreach (var tw in dec.eventTweens.Values)
                    if (tw != null && tw.IsActive() && tw.IsPlaying())
                        return false;
            if (dec.parallax != null &&
                (dec.parallax.multiplier.x != 1f || dec.parallax.multiplier.y != 1f))
                return false;
            return true;
        }
    }
}
