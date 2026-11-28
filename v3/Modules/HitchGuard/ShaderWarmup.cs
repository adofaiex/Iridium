using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Iridium.Config;
using Iridium.Patches;

namespace Iridium.Modules.HitchGuard
{
    /// <summary>
    /// 关卡切换后渐进式 Shader 预热：把首次渲染新滤镜/特效时的 shader 编译突刺
    /// 摊到加载收尾后的若干帧里。按 "shader 名 + 材质关键字组合" 去重，逐变体预热；
    /// 只有成功预热的变体才记入去重，失败的会在下次关卡收集时重试。
    /// 换关时丢弃上一关未完成的队列，优先预热当前关的变体。
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
            public string Key = "";
            public string[] Keywords = Array.Empty<string>();
        }

        // 去重：本进程生命周期内已成功预热的变体（shader 名 + 关键字组合）。
        private static readonly HashSet<string> _warmed = new();
        // 在飞去重：已入队尚未处理的变体键，避免同一变体重复入队。
        private static readonly HashSet<string> _inFlight = new();
        // 待预热队列：每帧最多处理 budget 个。
        private static readonly Queue<WarmItem> _pending = new();

        /// <summary>每帧预热的 shader 数（预热本身也要避免突刺）。</summary>
        private const int BudgetPerFrame = 2;

        public static void Reset()
        {
            _pending.Clear();
            _inFlight.Clear();
        }

        /// <summary>关卡加载收尾：收集场景内所有 renderer 的 shader 变体，入队渐进预热。</summary>
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
            // 换关：丢弃上一关未完成的队列（旧关 shader 多已卸载），只保留已成功的去重记录
            _pending.Clear();
            _inFlight.Clear();

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
                    if (string.IsNullOrEmpty(name)) continue;

                    // 材质关键字决定实际编译的 shader 变体：按 名字+关键字组合 去重
                    string[] keywords = m.shaderKeywords ?? Array.Empty<string>();
                    Array.Sort(keywords);
                    string key = keywords.Length > 0 ? name + "|" + string.Join(",", keywords) : name;
                    if (_warmed.Contains(key) || !_inFlight.Add(key)) continue;
                    _pending.Enqueue(new WarmItem { Shader = shader, Name = name, Key = key, Keywords = keywords });
                    queued++;
                }
            }
            if (queued > 0)
                Main.Logger?.Log($"[ShaderWarmup] queued {queued} shader variant(s) for warmup");
        }

        /// <summary>由 Main.OnUpdate 驱动：每帧渐进处理队列。</summary>
        public static void Tick()
        {
            if (_pending.Count == 0) return;
            int budget = BudgetPerFrame;
            while (budget-- > 0 && _pending.Count > 0)
            {
                var item = _pending.Dequeue();
                _inFlight.Remove(item.Key);
                try
                {
                    WarmShader(item);
                    _warmed.Add(item.Key); // 只有成功才记入去重，失败的下次收集时会重试
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[ShaderWarmup] warm {item.Name} failed: {e.Message}");
                }
            }
        }

        /// <summary>
        /// 预热单个 shader 变体：触发 Unity/驱动编译其变体。渲染到 4x4 离屏 RenderTexture
        ///（GL immediate 模式），不碰主画面——即使在 OnUpdate 里调用也不会闪现杂色。
        /// </summary>
        private static void WarmShader(WarmItem item)
        {
            if (item.Shader == null) return;
            var material = new Material(item.Shader) { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture? rt = null;
            var prev = RenderTexture.active;
            try
            {
                // RT 分配放在 try 内：分配失败也能在 finally 里销毁 material，不泄漏
                rt = RenderTexture.GetTemporary(4, 4, 0);
                RenderTexture.active = rt;
                // 应用场景材质的关键字组合，使 SetPass 编译的是实际使用的变体而非默认变体
                if (item.Keywords.Length > 0) material.shaderKeywords = item.Keywords;

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
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(material);
            }
        }

        private static bool Enabled => Main.Settings?.optimizer.enableShaderWarmup == true;
    }
}
