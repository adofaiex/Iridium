using System;
using System.Diagnostics;
using TMPro;
using UnityEngine;
using Unity.Profiling;

namespace Iridium.UI
{
    /// <summary>
    /// 性能拆解悬浮窗（UGUI + TextMeshPro）。
    ///
    /// 用 UGUI/TMP 而非 IMGUI：IMGUI 在 UnityModManager 下每帧走 OnGUI 旧事件
    /// 管线，会污染性能数据（尤其 draw call 计数）。
    ///
    /// 字体走游戏的 TMP 字体资源（RDConstants.data.latinFontTMPro），
    /// 与 JustEnoughAccuracy 的做法一致 —— 自建顶层 Canvas + Arial 内置字体
    /// 在打包后的游戏里取不到、不渲染。
    ///
    /// 显示：FPS / 1% low / Draw calls / SetPass / 脚本引擎耗时 / 合批耗时 /
    /// 活跃 tween / 已合批装饰物数。
    /// </summary>
    public class PerfOverlay : MonoBehaviour
    {
        private static PerfOverlay? _instance;

        private const int SampleWindow = 300;
        private static readonly float[] _frameMs = new float[SampleWindow];
        private static int _frameIdx;
        private static int _frameCount;
        private static readonly float[] _sortBuf = new float[SampleWindow];

        private static float _fps;
        private static float _low1;
        private static double _engineMs;
        private static double _batcherMs;

        private static ProfilerRecorder _drawCalls;
        private static ProfilerRecorder _setPass;
        private static ProfilerRecorder _mainThread;
        private static ProfilerRecorder _renderThread;
        private static bool _recordersInited;
        private static bool _recordersValid;
        private static bool _wantVisible;

        private static Stopwatch? _sw;

        private TextMeshProUGUI _text = null!;
        private float _accum;
        private const float UpdateInterval = 0.25f;

        public static bool Visible
        {
            get => _wantVisible && _instance != null && _instance.gameObject.activeSelf;
            set
            {
                _wantVisible = value;
                if (value && _instance == null) Create();
                if (_instance != null) _instance.gameObject.SetActive(value);
            }
        }

        // ---------------- 静态计时接口（Main 每帧调用） ----------------

        public static void TickFrame(float dt)
        {
            // 宿主 Canvas 随场景切换被销毁 → 按意图重建
            if (_wantVisible && (_instance == null || !_instance))
                Create();

            if (dt <= 0f) dt = 0.0001f;
            float ms = dt * 1000f;
            _frameMs[_frameIdx] = ms;
            _frameIdx = (_frameIdx + 1) % SampleWindow;
            if (_frameCount < SampleWindow) _frameCount++;

            if (ms > 0f)
                _fps = _fps <= 0f ? 1000f / ms : Mathf.Lerp(_fps, 1000f / ms, 0.05f);

            if (_frameCount >= 30)
            {
                Array.Copy(_frameMs, _sortBuf, _frameCount);
                Array.Sort(_sortBuf, 0, _frameCount);
                int idx = Mathf.Clamp((int)Math.Ceiling(_frameCount * 0.99f) - 1, 0, _frameCount - 1);
                float p99 = _sortBuf[idx];
                _low1 = p99 > 0f ? 1000f / p99 : 0f;
            }
        }

        public static Stopwatch Begin()
        {
            _sw ??= new Stopwatch();
            _sw.Restart();
            return _sw;
        }

        public static void RecordEngine(Stopwatch sw)
        {
            sw.Stop();
            _engineMs = _engineMs <= 0 ? sw.Elapsed.TotalMilliseconds
                : _engineMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;
        }

        public static void RecordBatcher(Stopwatch sw)
        {
            sw.Stop();
            _batcherMs = _batcherMs <= 0 ? sw.Elapsed.TotalMilliseconds
                : _batcherMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;
        }

        // ---------------- 实例与构建 ----------------

        private static void Create()
        {
            try
            {
                var host = ResolveHostCanvas();
                if (host == null)
                {
                    Main.Logger?.Error("[PerfOverlay] no host canvas available");
                    return;
                }

                var go = new GameObject("Iridium PerfOverlay", typeof(RectTransform));
                go.transform.SetParent(host.transform, false);
                _instance = go.AddComponent<PerfOverlay>();
                _instance.Build();
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[PerfOverlay] create failed: {ex}");
            }
        }

        /// <summary>把悬浮窗挂到游戏已有 Canvas 下（与 JustEnoughAccuracy 一致）。</summary>
        private static Canvas? ResolveHostCanvas()
        {
            var ctl = scrController.instance;
            if (ctl != null && ctl.detailedResults != null)
            {
                var textCanvas = ctl.detailedResults.textComponent?.canvas;
                if (textCanvas != null) return textCanvas;
            }
            if (scrUIController.instance != null && scrUIController.instance.canvas != null)
                return scrUIController.instance.canvas;
            return null;
        }

        private static TMP_FontAsset? ResolveFont()
        {
            try
            {
                if (RDConstants.data != null)
                {
                    if (RDConstants.data.latinFontTMPro != null) return RDConstants.data.latinFontTMPro;
                    if (RDConstants.data.chineseFontTMPro != null) return RDConstants.data.chineseFontTMPro;
                }
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[PerfOverlay] font resolve failed: {ex.Message}");
            }
            return null;
        }

        private void Build()
        {
            var rt = (RectTransform)transform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(12, -12);
            rt.sizeDelta = new Vector2(380, 130);

            var bgGo = new GameObject("BG", typeof(RectTransform));
            bgGo.transform.SetParent(transform, false);
            var brt = (RectTransform)bgGo.transform;
            brt.anchorMin = Vector2.zero;
            brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero;
            brt.offsetMax = Vector2.zero;
            var img = bgGo.AddComponent<UnityEngine.UI.Image>();
            img.color = new Color(0f, 0f, 0f, 0.62f);
            img.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(transform, false);
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(6, 4);
            trt.offsetMax = new Vector2(-6, -4);

            _text = textGo.AddComponent<TextMeshProUGUI>();
            _text.fontSize = 15f;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.color = Color.white;
            _text.raycastTarget = false;
            _text.enableWordWrapping = false;
            var font = ResolveFont();
            if (font != null) _text.font = font;

            Main.Logger?.Log($"[PerfOverlay] built (host={transform.parent?.name}, font={(font != null ? font.name : "null")})");
        }

        private void Update()
        {
            _accum += Time.unscaledDeltaTime;
            if (_accum < UpdateInterval) return;
            _accum = 0f;

            if (!_recordersInited)
            {
                _recordersInited = true;
                try
                {
                    _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                    _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
                    try { _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1, ProfilerRecorderOptions.Default); } catch { }
                    try { _renderThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 1, ProfilerRecorderOptions.Default); } catch { }
                    _recordersValid = _drawCalls.Valid;
                }
                catch
                {
                    _recordersValid = false;
                }
            }

            long draws = _recordersValid && _drawCalls.Valid ? _drawCalls.LastValue : -1;
            long setPass = _recordersValid && _setPass.Valid ? _setPass.LastValue : -1;
            int tweenCount = Core.CustomEasingEngine.ActiveCount;
            int batched = Patches.Optimizer.StaticDecorationBatcher.Enabled
                ? Patches.Optimizer.StaticDecorationBatcher.BatchedCount : -1;

            _text.text =
                $"FPS {_fps:F0}   1% low {_low1:F0}\n" +
                $"Draw calls: {(draws >= 0 ? draws.ToString() : "n/a")}   SetPass: {(setPass >= 0 ? setPass.ToString() : "n/a")}\n" +
                $"脚本引擎: {_engineMs:F2} ms   合批: {_batcherMs:F2} ms\n" +
                $"活跃 tween: {tweenCount}" +
                (batched >= 0 ? $"   已合批: {batched}" : "");
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
