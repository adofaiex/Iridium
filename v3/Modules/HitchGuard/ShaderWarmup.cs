using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Iridium.Config;

namespace Iridium.Modules.HitchGuard
{
    /// <summary>
    /// 关卡切换后渐进式 Shader 预热：把首次渲染新滤镜/特效时的 shader 编译突刺
    /// 摊到加载收尾后的若干帧里。按 shader 名去重，整个进程生命周期不重复预热。
    /// 队列每帧只处理少量条目，保证预热本身不引入新的卡顿。
    /// </summary>
    [IriPatch(Path = "optimizer/hitchGuard", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,enableShaderWarmup")]
    [HarmonyPatch(typeof(scnGame), nameof(scnGame.FinishCustomLevelLoading))]
    public static class ShaderWarmup
    {
        private sealed class WarmItem
        {
            public Shader Shader = null!;
            public string Name = "";
        }

        // 去重：本进程生命周期内已预热过的 shader 名。
        private static readonly HashSet<string> _warmed = new();
        // 待预热队列：每帧最多处理 budget 个。
        private static readonly Queue<WarmItem> _pending = new();

        /// <summary>每帧预热的 shader 数（预热本身也要避免突刺）。</summary>
        private const int BudgetPerFrame = 2;

        public static void Reset()
        {
            _pending.Clear();
        }

        /// <summary>关卡加载收尾：收集场景内所有 renderer 的 shader，入队渐进预热。</summary>
        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                CollectAndEnqueue();
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[ShaderWarmup] collect failed: {e.Message}");
            }
        }

        internal static void CollectAndEnqueue()
        {
            int queued = 0;
            // Include inactive: 装饰物在激活前也可能持有材质；只读 sharedMaterial 不实例化材质。
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    var shader = m.shader;
                    if (shader == null) continue;
                    string name = shader.name;
                    if (string.IsNullOrEmpty(name) || !_warmed.Add(name)) continue;
                    _pending.Enqueue(new WarmItem { Shader = shader, Name = name });
                    queued++;
                }
            }
            if (queued > 0)
                Main.Logger?.Log($"[ShaderWarmup] queued {queued} shader(s) for warmup");
        }

        /// <summary>由 Main.OnUpdate 驱动：每帧渐进处理队列。</summary>
        public static void Tick()
        {
            if (_pending.Count == 0) return;
            int budget = BudgetPerFrame;
            while (budget-- > 0 && _pending.Count > 0)
            {
                var item = _pending.Dequeue();
                try
                {
                    WarmShader(item.Shader);
                    _warmed.Add(item.Name);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[ShaderWarmup] warm {item.Name} failed: {e.Message}");
                }
            }
        }

        /// <summary>
        /// 预热单个 shader：触发 Unity/驱动编译其变体。渲染到 4x4 离屏 RenderTexture
        ///（GL immediate 模式），不碰主画面——即使在 OnUpdate 里调用也不会闪现杂色。
        /// </summary>
        private static void WarmShader(Shader shader)
        {
            if (shader == null) return;
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var rt = RenderTexture.GetTemporary(4, 4, 0);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                // 每个可用 pass 画一次最小三角形，迫使驱动编译该 pass 的变体。
                GL.Clear(false, true, Color.clear);
                GL.PushMatrix();
                GL.LoadOrtho();
                for (int i = 0; i < material.passCount; i++)
                {
                    try
                    {
                        material.SetPass(i);
                        GL.Begin(GL.TRIANGLES);
                        GL.Vertex3(0f, 0f, 0f);
                        GL.Vertex3(1f, 0f, 0f);
                        GL.Vertex3(0f, 1f, 0f);
                        GL.End();
                    }
                    catch { /* 单个 pass 失败不应中断整体预热 */ }
                }
                GL.PopMatrix();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(material);
            }
        }

        private static bool Enabled => Main.Settings?.optimizer.enableShaderWarmup == true;
    }
}
