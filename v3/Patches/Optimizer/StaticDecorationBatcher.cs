using Iridium.Config;
using Iridium.Core;
using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
    /// <summary>
    /// 静态装饰物合批渲染器（M2/M3）。
    ///
    /// 接管"无滤镜、无遮罩、非平铺、普通混合、当前无活跃 tween"的 scrVisualDecoration：
    /// 每个批次的四角烘焙进共享 Mesh，按 (主贴图, 层, sortingOrder) 分组 ——
    /// 每组一个 MeshRenderer（Sprites/Default + 图集页 RT），一个 draw call。
    /// 组间按 sortingOrder 正确排序，组内按 depth 排序的顶点顺序绘制。
    ///
    /// 每帧仅做位置遍历：W(t) = base + (cam - base) * parallaxMult + offK + cornerOffset，
    /// 与原版 scrParallax.SetTrans 逐项一致（base = 实时 pivotPosVec）。旋转/缩放/pivot
    /// 几何烘焙进四角偏移，经 SetRotation/SetScale 脏检查钩子在变化时重捕获。
    /// 结构变化（装饰物增删/可见性/深度变化）为增量操作：与最后一个 quad 换位后截断，
    /// 不做全量重建。装饰物被引擎 tween 接管时自动退出批次并还原原版渲染器，
    /// tween 结束后自动回收。
    ///
    /// 依赖：需与自定义缓速引擎同时开启（tween 感知来自引擎事件）。
    /// </summary>
    public static class StaticDecorationBatcher
    {
        private static bool _enabled;
        private static bool _hooked;
        private static Harmony? _harmony;

        private const int PageSize = 4096;
        private const int MaxRegionDim = 1024;
        private const int RegionPadding = 2;

        // ---------------- 条目状态 ----------------

        private sealed class Item
        {
            public scrVisualDecoration Deco = null!;
            public bool Managed;
            public AtlasSlot? Slot;
            public int SlotPos = -1;
            public int VertexIndex = -1;
            public int IdleFrames;                          // 在 slot.Items 中的位置
            public Vector3[] CornerOffsets = new Vector3[4];  // 世界顶点 - 视差根位置（含 z）
            /// <summary>
            /// 四角捕获版本号：每次 <see cref="RefreshCapture"/> 自增。SoA 收集时用它
            /// 判断角偏移是否需要重拷（多数帧完全不用碰 Corners）。
            /// </summary>
            public int CaptureVersion;
            /// <summary>
            /// 已摊平进 SoA 角缓冲的版本号。由收集循环维护 —— 与索引无关，
            /// 装饰物增删导致顺序变化也不会把旧角偏移错配给别的装饰物。
            /// </summary>
            public int StagedVersion = -1;
            public Color32 VColor;
        }

        private sealed class AtlasSlot
        {
            public Texture2D Source = null!;
            public RenderTexture Page = null!;
            public int Layer;
            public int SortingOrder;
            public string SortingLayer = "Bg";
            public Rect UV;                                   // 图集内归一化区域
            public Vector2[] BaseUV = new Vector2[4];         // quad 原始 UV
            public int[] TriCache = new int[6];               // quad 三角索引（相对四角）
            public bool UvInit;
            public GameObject? GO;
            public Mesh? Mesh;
            public Material? Material;
            public List<Item> Items = new();
            public List<Vector3> Verts = new();
            public List<Color32> Colors = new();
            public List<Vector2> Uvs = new();
            public List<int> Tris = new();
            public bool StructuralDirty;
            public bool PositionDirty;
        }

        private sealed class Page
        {
            public RenderTexture RT = null!;
            public int UsedWidth, UsedHeight;
        }

        private sealed class SlotKey : IEquatable<SlotKey>
        {
            public readonly int TextureId;
            public readonly int Layer;
            public readonly int Order;
            public SlotKey(int textureId, int layer, int order)
            {
                TextureId = textureId; Layer = layer; Order = order;
            }
            public bool Equals(SlotKey? other) =>
                other != null && TextureId == other.TextureId && Layer == other.Layer && Order == other.Order;
            public override bool Equals(object? obj) => Equals(obj as SlotKey);
            public override int GetHashCode() => TextureId ^ (Layer << 8) ^ Order;
        }

        private static readonly Dictionary<scrDecoration, Item> _items = new();
        private static readonly Dictionary<SlotKey, AtlasSlot> _slots = new();
        private static readonly List<Page> _pages = new();
        private static readonly HashSet<AtlasSlot> _uvInitDone = new();
        private static readonly Dictionary<scrDecoration, int> _pending = new();
        private static readonly List<scrDecoration> _pendingUpdate = new();
        private static readonly List<scrDecoration> _pendingNeedIncrement = new();

        private static readonly AccessTools.FieldRef<scrVisualDecoration, bool>? _meshEnabledRef =
            AccessTools.FieldRefAccess<scrVisualDecoration, bool>("meshRendererEnabled");

        private static readonly AccessTools.FieldRef<scrVisualDecoration, DecorationBlendMode>? _blendModeRef =
            AccessTools.FieldRefAccess<scrVisualDecoration, DecorationBlendMode>("blendMode");

        // 3.4.0 新增裁剪枢轴（croppedPivotOffset，定义在基类 scrDecoration）；
        // 3.3.x 无此字段。FieldRefAccess 在字段缺失时会抛异常，必须先取 FieldInfo
        // 判空——否则旧版本会在静态初始化时 TypeInitializationException。
        // 非零裁剪枢轴的装饰物在 MoveDecorations tween 下位置基准会变化，
        // 合批只在旋转/缩放时重捕获四角，因此直接排除。
        private static readonly FieldInfo? _croppedPivotField =
            AccessTools.Field(typeof(scrVisualDecoration), "croppedPivotOffset");

        private static readonly AccessTools.FieldRef<scrVisualDecoration, Vector2>? _croppedPivotOffsetRef =
            _croppedPivotField == null ? null : AccessTools.FieldRefAccess<scrVisualDecoration, Vector2>(_croppedPivotField);

        // ---------------- 开关生命周期 ----------------

        public static bool Enabled => _enabled;

        /// <summary>
        /// 该装饰物当前是否由合批渲染器接管。接管后其位置/材质由顶点遍历负责，
        /// 原版每帧的 UpdatePosition/UpdateShader 应被跳过（见 FfxOptimizationPatches）。
        /// </summary>
        public static bool IsBatched(scrDecoration dec)
            => _enabled && _items.TryGetValue(dec, out var item) && item.Managed;

        /// <summary>当前已合批（实际由批次渲染）的装饰物数量，用于性能悬浮窗。</summary>
        public static int BatchedCount
        {
            get
            {
                int n = 0;
                foreach (var item in _items.Values)
                    if (item.Managed) n++;
                return n;
            }
        }

        public static void SetEnabled(bool value)
        {
            if (value == _enabled) return;
            _enabled = value;
            // 重置原生降级标记：开关切换可能发生在原生库刚被替换之后。
            _nativeKnownBad = false;
            if (value)
            {
                if (!_hooked)
                {
                    _harmony = new Harmony("Iridium.StaticDecorationBatcher");
                    _harmony.CreateClassProcessor(typeof(FinishUpdateDecorationsHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(ResetSceneHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(SetRotationRefreshHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(SetScaleRefreshHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(SetVisibleHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(SetColorHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(SetOpacityHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(SetDepthHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(ScnGameDestroyHook)).Patch();
                    _harmony.CreateClassProcessor(typeof(LogicUpdateSkipHook)).Patch();
                    _hooked = true;
                }
                CustomEasingEngine.TargetTweensBecameActive += OnTargetDynamic;
                CustomEasingEngine.TargetTweensAllDead += OnTargetStatic;
                RebuildAll();
                Main.Logger?.Log("[DecoBatch] enabled");
            }
            else
            {
                CustomEasingEngine.TargetTweensBecameActive -= OnTargetDynamic;
                CustomEasingEngine.TargetTweensAllDead -= OnTargetStatic;
                RestoreAll();
                Main.Logger?.Log("[DecoBatch] disabled");
            }
        }

        // ---------------- 资格判定 ----------------

        private static bool IsEligible(scrVisualDecoration dec)
        {
            if (Main.Settings?.optimizer.enableStaticDecorationBatching != true) return false;
            // 编辑器（非试玩）态完全让位原版：合批是为游玩态烘焙的静态快照，
            // 编辑器里改装饰物需要原版 LogicUpdate 的实时刷新。
            if (ADOBase.isEditingLevel) return false;
            if (dec.cfpCache != null && dec.cfpCache.Length > 0) return false;   // 滤镜走 RT 管线
            if (dec.isMask()) return false;
            if (dec.repeatX != 1f || dec.repeatY != 1f) return false;            // 平铺无法进图集
            if (dec.stickToFloor || dec.followPlanet != null) return false;      // 位置被外部逐帧驱动
            if (dec.parallax == null) return false;
            if (dec.parallax.dontAlterX || dec.parallax.dontAlterY || dec.parallax.clampToScreen) return false;
            if (dec.lockScale) return false;                                     // 顶点随相机缩放变化
            if (_blendModeRef == null || _blendModeRef(dec) != DecorationBlendMode.None) return false;
            if (_croppedPivotOffsetRef != null && _croppedPivotOffsetRef(dec) != Vector2.zero) return false;

            var mat = dec.meshRenderer != null && dec.meshRenderer.sharedMaterial != null
                ? dec.meshRenderer.sharedMaterial.mainTexture as Texture2D : null;
            if (mat == null || mat.width > MaxRegionDim || mat.height > MaxRegionDim) return false;

            var mf = dec.meshRenderer!.GetComponent<MeshFilter>();
            if (mf?.sharedMesh == null || mf.sharedMesh.vertexCount != 4 || mf.sharedMesh.triangles.Length != 6)
                return false; // 仅接管简单 quad

            return true;
        }

        /// <summary>捕获当前世界四角偏移（旋转/缩放/pivot 变化后重新调用）。</summary>
        private static void RefreshCapture(scrVisualDecoration vis, Item item)
        {
            var mf = vis.meshRenderer != null ? vis.meshRenderer.GetComponent<MeshFilter>() : null;
            var mesh = mf?.sharedMesh;
            if (mesh == null || vis.parallax == null) return;

            var l2w = vis.meshRenderer!.transform.localToWorldMatrix;
            var verts = mesh.vertices;
            var p0 = vis.parallax.transform.position;
            for (int i = 0; i < 4; i++)
                item.CornerOffsets[i] = l2w.MultiplyPoint3x4(verts[i]) - p0;
            // 自增即可（回绕比较无关紧要：只需要「变了」这一信号）
            item.CaptureVersion++;
        }

        // ---------------- 增量进出批次 ----------------

        private static void TryManage(scrVisualDecoration vis)
        {
            if (_items.TryGetValue(vis, out var item))
            {
                if (item.Managed) return;
                if (!IsEligible(vis) || !vis.GetVisible() || CustomEasingEngine.HasActiveTweens(vis))
                    return;
            }
            else
            {
                if (!IsEligible(vis) || !vis.GetVisible() || CustomEasingEngine.HasActiveTweens(vis))
                    return;
                item = new Item { Deco = vis };
                _items[vis] = item;
            }

            var tex = vis.meshRenderer!.sharedMaterial!.mainTexture as Texture2D;
            if (tex == null) return;

            var mf = vis.meshRenderer.GetComponent<MeshFilter>();
            var mesh = mf?.sharedMesh;
            if (mesh == null) return;

            RefreshCapture(vis, item);

            int layer = vis.gameObject.layer;
            int order = vis.meshRenderer.sortingOrder;
            var key = new SlotKey(tex.GetInstanceID(), layer, order);

            if (!_slots.TryGetValue(key, out var slot))
            {
                var page = AcquirePageFor(tex);
                if (page == null) return;
                var pageRT = page.RT;
                var uvRect = BlitIntoPage(page, tex);

                var go = new GameObject("Iridium DecoBatch")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    layer = layer
                };
                var mfNew = go.AddComponent<MeshFilter>();
                slot = new AtlasSlot
                {
                    Source = tex,
                    Page = pageRT,
                    Layer = layer,
                    SortingOrder = order,
                    SortingLayer = order <= 0 ? "Bg" : "Default",
                    UV = uvRect
                };
                mfNew.sharedMesh = slot.Mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                // 世界空间网格：固定大包围盒，免每帧 RecalculateBounds（放弃剔除换 CPU）
                slot.Mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e4f));
                var mr = go.AddComponent<MeshRenderer>();
                slot.Material = new Material(Shader.Find("Sprites/Default"))
                {
                    mainTexture = pageRT,
                    hideFlags = HideFlags.HideAndDontSave
                };
                mr.sharedMaterial = slot.Material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.sortingLayerName = slot.SortingLayer;
                mr.sortingOrder = order;
                _slots[key] = slot;
                _uvInitDone.Remove(slot);
            }

            if (!_uvInitDone.Contains(slot))
            {
                var uvs = mesh.uv;
                var tris = mesh.triangles;
                for (int c = 0; c < 4 && c < uvs.Length; c++) slot.BaseUV[c] = uvs[c];
                for (int t = 0; t < 6 && t < tris.Length; t++) slot.TriCache[t] = tris[t];
                _uvInitDone.Add(slot);
            }

            item.Slot = slot;
            item.Managed = true;
            item.VColor = vis.color.WithAlpha(vis.opacity);
            AddQuad(slot, item);
        }

        /// <summary>从批次摘除并还原原版渲染器。</summary>
        private static void Unmanage(Item item, bool restoreVanilla)
        {
            if (item.Managed && item.Slot != null)
                RemoveQuad(item.Slot, item);
            item.Managed = false;
            if (restoreVanilla && item.Deco != null)
            {
                SetMeshRendererEnabled(item.Deco, false);
                if (item.Deco.meshRendererObj != null)
                    item.Deco.meshRendererObj.SetActive(true);
            }
        }

        private static void SetMeshRendererEnabled(scrVisualDecoration vis, bool enabled)
        {
            if (_meshEnabledRef != null) _meshEnabledRef(vis) = enabled;
        }

        private static void OnTargetDynamic(object target)
        {
            if (target is scrVisualDecoration vis0) _pending.Remove(vis0);
            if (target is scrVisualDecoration vis && _items.TryGetValue(vis, out var item) && item.Managed)
                Unmanage(item, restoreVanilla: true);
        }

        private static void OnTargetStatic(object target)
        {
            // tween 刚结束：不立即入批，先静置计时（避免事件密集时反复进出批次）
            if (target is scrVisualDecoration vis && !_items.ContainsKey(vis))
                _pending[vis] = 0;
        }

        // ---------------- 增量 quad 增删 ----------------

        private static void AddQuad(AtlasSlot slot, Item item)
        {
            int quad = slot.Items.Count;
            item.SlotPos = quad;
            item.VertexIndex = quad * 4;
            slot.Items.Add(item);
            for (int c = 0; c < 4; c++)
            {
                slot.Verts.Add(Vector3.zero); // 下一帧位置遍历填充
                slot.Colors.Add(item.VColor);
                slot.Uvs.Add(slot.UV.min + Vector2.Scale(slot.BaseUV[c], slot.UV.size));
            }
            for (int k = 0; k < 6; k++)
                slot.Tris.Add(slot.TriCache[k] + quad * 4);
            slot.StructuralDirty = true;
        }

        private static void RemoveQuad(AtlasSlot slot, Item item)
        {
            int pos = item.SlotPos;
            int last = slot.Items.Count - 1;
            if (pos < 0 || pos > last) { item.Slot = null; return; }

            if (pos != last)
            {
                // 与最后一个 quad 换位：移动它的顶点/颜色/UV/三角/条目
                var moved = slot.Items[last];
                slot.Items[pos] = moved;
                moved.SlotPos = pos;
                moved.VertexIndex = pos * 4;
                for (int c = 0; c < 4; c++)
                {
                    slot.Verts[pos * 4 + c] = slot.Verts[last * 4 + c];
                    slot.Colors[pos * 4 + c] = slot.Colors[last * 4 + c];
                    slot.Uvs[pos * 4 + c] = slot.Uvs[last * 4 + c];
                }
                for (int k = 0; k < 6; k++)
                    slot.Tris[pos * 6 + k] = slot.TriCache[k] + pos * 4;
            }
            slot.Items.RemoveAt(last);
            slot.Verts.RemoveRange(last * 4, 4);
            slot.Colors.RemoveRange(last * 4, 4);
            slot.Uvs.RemoveRange(last * 4, 4);
            slot.Tris.RemoveRange(last * 6, 6);
            item.Slot = null;
            item.SlotPos = -1;
            item.VertexIndex = -1;
            slot.StructuralDirty = true;
        }

        // ---------------- 全量重扫 ----------------

        /// <summary>全量重扫（装饰物重建 / 复位后调用）。图集与槽位跨重建复用。</summary>
        public static void RebuildAll()
        {
            if (!_enabled) return;
            try
            {
                // 摘除全部（保留图集/槽位 —— 贴图实例存活时无需重 blit）
                foreach (var item in _items.Values)
                {
                    if (item.Managed && item.Slot != null)
                        RemoveQuad(item.Slot, item);
                    item.Managed = false;
                }

                var manager = scrDecorationManager.instance;
                if (manager == null) return;

                int total = 0, visCount = 0;
                foreach (var dec in manager.allDecorations)
                {
                    total++;
                    if (dec is not scrVisualDecoration vis) continue;
                    visCount++;
                    TryManage(vis);
                }
                Main.Logger?.Log($"[DecoBatch] RebuildAll: all={total} visual={visCount} managed={BatchedCount} enabled={_enabled} editing={ADOBase.isEditingLevel}");
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[DecoBatch] RebuildAll failed: {ex}");
            }
        }

        // ---------------- 每帧：位置遍历 ----------------

        /// <summary>tween 结束后的静置阈值：连续这么多帧无 tween 才纳入批次。</summary>
        private const int BatchIdleThreshold = 15;

        public static void FrameRender()
        {
            if (!_enabled) return;
            var controller = ADOBase.controller;
            if (controller?.camy == null) return;

            // 静置晋升：tween 结束满阈值才入批，避免密集事件下反复进出
            if (_pending.Count > 0)
            {
                _pendingUpdate.Clear();
                foreach (var kvp in _pending)
                {
                    if (kvp.Value + 1 >= BatchIdleThreshold)
                        _pendingUpdate.Add(kvp.Key);
                    else
                        _pendingNeedIncrement.Add(kvp.Key);
                }
                foreach (var k in _pendingNeedIncrement)
                    _pending[k] = _pending[k] + 1;
                foreach (var k in _pendingUpdate)
                {
                    _pending.Remove(k);
                    if (k is scrVisualDecoration vis0) TryManage(vis0);
                }
            }

            if (_items.Count == 0) return;
            var cam = controller.camy.transform.position;

            // 逐个装饰物读取 Unity 侧输入（字段直读，无虚调用），然后交给原生
            // 内核算 4 个角。原生不可用时走等价的托管回退。
            if (!ComputePositionsNative(cam))
                ComputePositionsManaged(cam);

            // 脏页上传：结构变化 → 全量；仅位置变化 → 只传顶点
            foreach (var slot in _slots.Values)
            {
                if (slot.Mesh == null) continue;
                if (slot.StructuralDirty)
                {
                    slot.Mesh.Clear();
                    slot.Mesh.SetVertices(slot.Verts);
                    slot.Mesh.SetColors(slot.Colors);
                    slot.Mesh.SetUVs(0, slot.Uvs);
                    slot.Mesh.SetTriangles(slot.Tris, 0);
                    slot.StructuralDirty = false;
                    slot.PositionDirty = false;
                    if (slot.GO != null) slot.GO.SetActive(slot.Items.Count > 0);
                }
                else if (slot.PositionDirty)
                {
                    slot.Mesh.SetVertices(slot.Verts);
                    slot.PositionDirty = false;
                }
            }
        }

        // ---------------- 每帧位置计算：SoA 收集缓冲 ----------------

        /// <summary>
        /// 摊平的 SoA 缓冲。全部是进程内常驻数组，容量只增不减，每帧不清零、
        /// 不重新分配；托管回退与原生路径共用同一份布局。
        /// <para>
        /// 关键：<see cref="Corners"/> 跨帧保留，四角偏移只在 <see cref="RefreshCapture"/>
        /// 变化时更新（由 <see cref="GatherPositions"/> 里的版本号判定），不每帧重拷。
        /// </para>
        /// </summary>
        private sealed class ParallelScratch
        {
            /// <summary>当帧有效的装饰物（已剔除不可见/未受管）。</summary>
            public readonly List<Item> Items = new();

            /// <summary>结果顶点，12 浮点/项（4 角 × xyz），摊平存放。</summary>
            public float[] Verts = Array.Empty<float>();
            /// <summary>逐项脏标记：内核置 1 表示该项四角发生变化。</summary>
            public uint[] Dirty = Array.Empty<uint>();
            public float[] BaseXy = Array.Empty<float>();
            public float[] MultXy = Array.Empty<float>();
            public float[] OffsetXy = Array.Empty<float>();
            /// <summary>四角偏移，12 浮点/项，内核只读；跨帧持久。</summary>
            public float[] Corners = Array.Empty<float>();
            /// <summary>每项在 Verts 中的起始下标（= i * 12）。</summary>
            public int[] VertexStart = Array.Empty<int>();

            public void Ensure(int n)
            {
                if (n <= 0) return;
                if (Verts.Length < n * 12) Verts = new float[n * 12];
                if (Corners.Length < n * 12) Corners = new float[n * 12];
                if (Dirty.Length < n) Dirty = new uint[n];
                if (BaseXy.Length < n * 2) BaseXy = new float[n * 2];
                if (MultXy.Length < n * 2) MultXy = new float[n * 2];
                if (OffsetXy.Length < n * 2) OffsetXy = new float[n * 2];
                if (VertexStart.Length < n) VertexStart = new int[n];
                if (Items.Capacity < n) Items.Capacity = n;
            }
        }

        private static readonly ParallelScratch _scratch = new();
        /// <summary>原生导出缺失或调用失败后不再重试（每次重试都是白付费的 FFI 开销）。</summary>
        private static bool _nativeKnownBad;

        /// <summary>
        /// 第一遍（托管）：摘除失效装饰物，把仍受管装饰物的输入摊平到 SoA 数组，
        /// 并把结果顶点区对齐到各 Slot 的当前顶点（这样内核能只回填变化项）。
        /// </summary>
        private static int GatherPositions(Vector3 cam)
        {
            var pos = _scratch;
            pos.Items.Clear();
            pos.Ensure(_items.Count);

            int n = 0;
            foreach (var item in _items.Values)
            {
                if (!item.Managed || item.Slot == null) continue;

                var dec = item.Deco;
                if (!dec.GetVisible())
                {
                    Unmanage(item, restoreVanilla: true);
                    continue;
                }

                var slot = item.Slot;
                int vi = item.VertexIndex;
                // 结构脏时顶点区可能还没填（首次入批 / 换位后），跳过本帧由托管兜底。
                if (vi < 0 || vi + 4 > slot.Verts.Count)
                    continue;

                var b0 = dec.pivotPosVec;                                    // 实时读取
                var mult = dec.parallax!.multiplier;
                var offK = dec.parallaxOffset * dec.scaleMultiplier;

                int vo = n * 12;
                pos.BaseXy[n * 2] = b0.x;
                pos.BaseXy[n * 2 + 1] = b0.y;
                pos.MultXy[n * 2] = mult.x;
                pos.MultXy[n * 2 + 1] = mult.y;
                pos.OffsetXy[n * 2] = offK.x;
                pos.OffsetXy[n * 2 + 1] = offK.y;

                // 四角偏移只在捕获版本变化时重拷（旋转/缩放/pivot 才是罕见事件）。
                if (item.StagedVersion != item.CaptureVersion)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        var corner = item.CornerOffsets[c];
                        pos.Corners[vo + c * 3] = corner.x;
                        pos.Corners[vo + c * 3 + 1] = corner.y;
                        pos.Corners[vo + c * 3 + 2] = corner.z;
                    }
                    item.StagedVersion = item.CaptureVersion;
                }

                // 顶点镜像：让内核能只回填真正变化的项。
                for (int c = 0; c < 4; c++)
                {
                    var v = slot.Verts[vi + c];
                    pos.Verts[vo + c * 3] = v.x;
                    pos.Verts[vo + c * 3 + 1] = v.y;
                    pos.Verts[vo + c * 3 + 2] = v.z;
                }
                pos.Dirty[n] = 0;
                pos.VertexStart[n] = vo;
                pos.Items.Add(item);
                n++;
            }
            return n;
        }

        /// <summary>
        /// 原生路径：收集后一次 FFI 调用算完所有角，再把脏项按 slot 顶点下标回填。
        /// 原生缺导出或调用失败时返回 false，由调用方走托管回退。
        /// </summary>
        private static bool ComputePositionsNative(Vector3 cam)
        {
            if (_nativeKnownBad || !Iridium.Native.IridiumNative.HasParallaxCompute) return false;

            int n = GatherPositions(cam);
            if (n == 0) return true;
            var pos = _scratch;

            if (!Iridium.Native.IridiumNative.ParallaxCompute(
                    n, cam.x, cam.y,
                    pos.BaseXy, pos.MultXy, pos.OffsetXy, pos.Corners,
                    pos.Verts, pos.Dirty))
            {
                _nativeKnownBad = true;
                return false;
            }

            // 逐项回填：仅回填原生标记为脏的装饰物，未变的不动 slot.Verts。
            var items = pos.Items;
            for (int i = 0; i < n; i++)
            {
                if (pos.Dirty[i] == 0) continue;
                var item = items[i];
                var slot = item.Slot!;
                int vi = item.VertexIndex;
                int vo = pos.VertexStart[i];
                for (int c = 0; c < 4; c++)
                {
                    slot.Verts[vi + c] = new Vector3(pos.Verts[vo + c * 3], pos.Verts[vo + c * 3 + 1], pos.Verts[vo + c * 3 + 2]);
                }
                slot.PositionDirty = true;
            }
            return true;
        }

        /// <summary>
        /// 托管回退：与原生内核逐位等价的内联循环（原生不可用时）。
        /// 同样先摊平进 SoA，再在摊平的顶点镜像上做脏检查 —— 与原生路径
        /// 保持完全相同的「哪些 slot 需要上传」判定，避免两条路径抖动不同。
        /// </summary>
        private static void ComputePositionsManaged(Vector3 cam)
        {
            int n = GatherPositions(cam);
            var pos = _scratch;
            var items = pos.Items;

            for (int i = 0; i < n; i++)
            {
                int vo = pos.VertexStart[i];
                float b0x = pos.BaseXy[i * 2], b0y = pos.BaseXy[i * 2 + 1];
                float mx = pos.MultXy[i * 2], my = pos.MultXy[i * 2 + 1];
                float kx = pos.OffsetXy[i * 2], ky = pos.OffsetXy[i * 2 + 1];
                float dxc = cam.x - b0x, dyc = cam.y - b0y;
                bool moved = false;
                for (int c = 0; c < 4; c++)
                {
                    int co = vo + c * 3;
                    float x = b0x + dxc * mx + kx + pos.Corners[co];
                    float y = b0y + dyc * my + ky + pos.Corners[co + 1];
                    float z = pos.Corners[co + 2];
                    if (pos.Verts[co] != x || pos.Verts[co + 1] != y || pos.Verts[co + 2] != z)
                    {
                        pos.Verts[co] = x;
                        pos.Verts[co + 1] = y;
                        pos.Verts[co + 2] = z;
                        moved = true;
                    }
                }
                if (!moved) continue;

                var slot = items[i].Slot!;
                int vi = items[i].VertexIndex;
                for (int c = 0; c < 4; c++)
                {
                    int co = vo + c * 3;
                    slot.Verts[vi + c] = new Vector3(pos.Verts[co], pos.Verts[co + 1], pos.Verts[co + 2]);
                }
                slot.PositionDirty = true;
            }
        }

        // ---------------- 图集 ----------------

        private static Page? AcquirePageFor(Texture2D tex)
        {
            int w = Mathf.Min(tex.width, MaxRegionDim) + RegionPadding;
            int h = Mathf.Min(tex.height, MaxRegionDim) + RegionPadding;
            foreach (var p in _pages)
            {
                if (p.UsedHeight + h <= PageSize && p.UsedWidth + w <= PageSize)
                    return p;
            }
            if (_pages.Count >= 16)
            {
                Main.Logger?.Error("[DecoBatch] atlas pages exhausted");
                return null;
            }
            var rt = new RenderTexture(PageSize, PageSize, 0)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var np = new Page { RT = rt };
            _pages.Add(np);
            return np;
        }

        private static Rect BlitIntoPage(Page page, Texture2D tex)
        {
            int w = Mathf.Min(tex.width, MaxRegionDim);
            int h = Mathf.Min(tex.height, MaxRegionDim);
            int rx = page.UsedWidth, ry = page.UsedHeight;
            page.UsedWidth += w + RegionPadding;
            if (page.UsedWidth >= PageSize)
            {
                page.UsedWidth = 0;
                page.UsedHeight += h + RegionPadding;
            }
            Graphics.Blit(tex, page.RT,
                new Vector2(w / (float)PageSize, h / (float)PageSize),
                new Vector2(rx / (float)PageSize, ry / (float)PageSize));
            return Rect.MinMaxRect(rx / (float)PageSize, ry / (float)PageSize,
                (rx + w) / (float)PageSize, (ry + h) / (float)PageSize);
        }

        // ---------------- 清理 ----------------

        private static void UnmanageAll()
        {
            foreach (var item in _items.Values)
                Unmanage(item, restoreVanilla: true);
        }

        private static void ClearBatchesAndAtlas()
        {
            UnmanageAll();
            foreach (var slot in _slots.Values)
            {
                if (slot.GO != null) UnityEngine.Object.Destroy(slot.GO);
                if (slot.Mesh != null) UnityEngine.Object.Destroy(slot.Mesh);
                if (slot.Material != null) UnityEngine.Object.Destroy(slot.Material);
            }
            _slots.Clear();
            foreach (var page in _pages)
                page.RT.Release();
            _pages.Clear();
            _uvInitDone.Clear();
        }

        private static void RestoreAll()
        {
            ClearBatchesAndAtlas();
        }

        // ---------------- Harmony 钩子 ----------------

        [HarmonyPatch(typeof(scrDecorationManager), "FinishUpdateDecorations")]
        private static class FinishUpdateDecorationsHook
        {
            [HarmonyPostfix]
            public static void Postfix() => RebuildAll();
        }

        [HarmonyPatch(typeof(scnGame), "ResetScene")]
        private static class ResetSceneHook
        {
            [HarmonyPostfix]
            public static void Postfix() => RebuildAll();
        }

        [HarmonyPatch(typeof(scnGame), "OnDestroy")]
        private static class ScnGameDestroyHook
        {
            [HarmonyPostfix]
            public static void Postfix() => ClearBatchesAndAtlas();
        }

        /// <summary>
        /// 已被合批接管的装饰物：位置/材质由顶点遍历负责，跳过原版 LogicUpdate
        /// （避免每帧重复的视差重算与 transform 写入）。判定盒仍需更新。
        /// 独立于 Ffx 优化开关，保证任何配置下都不做重复劳动。
        /// </summary>
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.LogicUpdate))]
        private static class LogicUpdateSkipHook
        {
            [HarmonyPrefix]
            public static bool Prefix(scrDecoration __instance, bool disableUpdateShader)
            {
                if (ADOBase.isEditingLevel) return true;   // 编辑器态让位原版
                if (!IsBatched(__instance)) return true;
                if (__instance.useHitbox) __instance.UpdateHitboxState();
                return false;
            }
        }

        /// <summary>SetRotation 脏检查放行原版写入后，重捕获四角偏移。</summary>
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetRotation))]
        private static class SetRotationRefreshHook
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecoration __instance)
            {
                if (_enabled && __instance is scrVisualDecoration vis
                    && _items.TryGetValue(vis, out var item) && item.Managed)
                    RefreshCapture(vis, item);
            }
        }

        /// <summary>SetScale 脏检查放行原版写入后，重捕获四角偏移。</summary>
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetScale))]
        private static class SetScaleRefreshHook
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecoration __instance)
            {
                if (_enabled && __instance is scrVisualDecoration vis
                    && _items.TryGetValue(vis, out var item) && item.Managed)
                    RefreshCapture(vis, item);
            }
        }

        /// <summary>可见性变化：隐藏 → 摘除；显示 → 尝试回收。</summary>
        [HarmonyPatch(typeof(scrVisualDecoration), "SetVisible")]
        private static class SetVisibleHook
        {
            [HarmonyPostfix]
            public static void Postfix(scrVisualDecoration __instance)
            {
                if (!_enabled) return;
                if (_items.TryGetValue(__instance, out var item))
                {
                    if (item.Managed && !__instance.GetVisible())
                        Unmanage(item, restoreVanilla: true);
                }
                else if (__instance.GetVisible())
                {
                    TryManage(__instance);
                }
            }
        }

        /// <summary>颜色变化 → 顶点色更新。</summary>
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetColor))]
        private static class SetColorHook
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecoration __instance)
            {
                if (!_enabled || __instance is not scrVisualDecoration vis) return;
                if (_items.TryGetValue(vis, out var item) && item.Managed && item.Slot != null)
                {
                    item.VColor = vis.color.WithAlpha(vis.opacity);
                    for (int c = 0; c < 4; c++)
                        item.Slot.Colors[item.VertexIndex + c] = item.VColor;
                }
            }
        }

        /// <summary>透明度变化 → 顶点色 alpha 更新。</summary>
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetOpacity))]
        private static class SetOpacityHook
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecoration __instance)
            {
                if (!_enabled || __instance is not scrVisualDecoration vis) return;
                if (_items.TryGetValue(vis, out var item) && item.Managed && item.Slot != null)
                {
                    item.VColor = vis.color.WithAlpha(vis.opacity);
                    for (int c = 0; c < 4; c++)
                        item.Slot.Colors[item.VertexIndex + c] = item.VColor;
                }
            }
        }

        /// <summary>深度变化 → 摘除后按新 (层, order) 重新入批。</summary>
        [HarmonyPatch(typeof(scrVisualDecoration), "SetDepth")]
        private static class SetDepthHook
        {
            [HarmonyPostfix]
            public static void Postfix(scrVisualDecoration __instance)
            {
                if (!_enabled) return;
                if (_items.TryGetValue(__instance, out var item) && item.Managed)
                {
                    // 深度变化会改变 layer/sortingOrder → 摘除后重新分组
                    Unmanage(item, restoreVanilla: false);
                    TryManage(__instance);
                }
            }
        }
    }
}
