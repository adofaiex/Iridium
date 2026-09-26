using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ADOFAI;
using DG.Tweening;
using GDMiniJSON;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// Iridium v2（ADOFAI 2.9.8）加载 / 播放 / 编辑 v3（3.0 ~ 3.4.0，谱面版本 16~19）谱面的兼容层。
    ///
    /// 按 PLAN.md（.adofai/disk/iridium/v2-v3-level-compat）实现：
    /// 1. 版本门：Decode 前临时把 settings.version 降到 15 放行 FutureVersion 检查，
    ///    Decode 结束后还原 dict 与 LevelData.version；Encode 时把硬编码的 15
    ///    改回打开时的真实版本，避免 v2 保存一次谱面就被降级。
    /// 2. v3 属性保留：2.9.8 的 LevelEvent.Decode 只认识自身属性表中的键，
    ///    v3 专属属性会在加载时被直接丢弃；LevelEvent.Encode 也不会写回未知键。
    ///    因此这里在 Decode 后把白名单属性存入旁路表（不注入 data，避免干扰
    ///    2.9.8 的 inspector / ApplyPropertiesToRealEvents 等逻辑），Encode 后
    ///    再把它们追加回 JSON，保证「v3 谱面 → v2 打开 → 保存」不丢数据。
    /// 3. 运行时 polyfill：
    ///    - SetFilterAdvanced.targetType（2.9.8 类本身已支持 Decoration，仅 Decode 写死 Camera）
    ///    - AddDecoration / MoveDecorations 的 originalSize 与 cropPivotOffset
    ///      （2.9.8 没有 textureScaleMultiplier / croppedPivotOffset 字段，用旁路表模拟）
    ///    - FreeRoam.outCam（2.9.8 UpdateFreeroam 无条件移动相机，按事件属性跳过）
    ///    - TrackSettings.trackShadowColor（2.9.8 SetTrackStyle 同样写 _ShadowColor，直接覆盖）
    ///    - BackgroundSettings.customBGPrefab（2.9.8 无对应资源体系，加载时提示降级）
    /// </summary>
    public static class V3LevelCompatPatches
    {
        /// <summary>2.9.8 能接受的最高谱面版本。</summary>
        private const int MaxV298Version = 15;

        /// <summary>首个 v3 谱面版本。</summary>
        public const int MinV3Version = 16;

        /// <summary>
        /// 需要保留 / 消费的 v3 专属属性（与 data/property-gap.json 一一对应）。
        /// Decode 时只抓取这张表里的键，避免把 settings 大字典里的其它小节
        /// （SongSettings/TrackSettings 等键都混在同一个 dict 里）错误地挂到每个事件上。
        /// </summary>
        private static readonly Dictionary<LevelEventType, string[]> V3PropertiesByEvent = new()
        {
            [LevelEventType.AddDecoration] = new[] { "originalSize", "cropPivotOffset" },
            [LevelEventType.MoveDecorations] = new[] { "originalSize", "cropPivotOffset" },
            [LevelEventType.SetFilterAdvanced] = new[] { "targetType", "warning" },
            [LevelEventType.FreeRoam] = new[] { "outCam", "shape" },
            [LevelEventType.MoveCamera] = new[]
            {
                "durationRandomMode", "durationRandomValue",
                "positionRandomMode", "positionRandomValue",
                "rotationRandomMode", "rotationRandomValue",
                "zoomRandomMode", "zoomRandomValue"
            },
            [LevelEventType.AddObject] = new[]
            {
                "bubbleAppearStartOffset", "bubbleAppearEndOffset",
                "bubbleDisappearOffset", "bubbleSpawnOffset"
            },
            [LevelEventType.AddParticle] = new[] { "playbackControl" },
            [LevelEventType.LevelSettings] = new[] { "export" },
            [LevelEventType.TrackSettings] = new[] { "trackShadowColor" },
            [LevelEventType.BackgroundSettings] = new[] { "customBGPrefab" },
        };

        /// <summary>LevelData.Encode 里 "version": 15 的整行替换（行首锚定，避免误伤字符串内容）。</summary>
        private static readonly Regex EncodeVersionLine = new(
            "^(?<prefix>[ \\t]*\"version\"[ \\t]*:[ \\t]*)15(?<suffix>[ \\t]*,?)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // ─────────────────────────── v3 属性旁路表 ───────────────────────────

        private static readonly ConditionalWeakTable<LevelEvent, Dictionary<string, object>> _v3EventData = new();

        /// <summary>v3 新增枚举值的安全回退（例如 ObjectDecorationType.PlayerBubble）。</summary>
        private sealed class EnumFallbackValue
        {
            public string Original = string.Empty;
            public string Fallback = string.Empty;
        }

        private static readonly ConditionalWeakTable<LevelEvent, Dictionary<string, EnumFallbackValue>> _v3EnumFallbacks = new();

        /// <summary>不属于事件属性、由 LevelData/Encode 单独处理的键。</summary>
        private static readonly HashSet<string> InternalEventKeys = new()
        {
            "floor", "eventType", "active", "visible", "locked",
            // 旧版本迁移别名 / FixDefaultValues 处理过的键，不参与往返
            "enabled", "failHitbox", "decText", "unscaledSize",
        };

        public static bool TryGetV3Property(LevelEvent? ev, string key, out object? value)
        {
            value = null;
            if (ev == null) return false;
            if (_v3EventData.TryGetValue(ev, out var extras) && extras.TryGetValue(key, out value))
                return true;
            return false;
        }

        public static bool TryGetV3Vector2(LevelEvent? ev, string key, out Vector2 value)
        {
            value = default;
            return TryGetV3Property(ev, key, out var raw) && TryConvertVector2(raw, out value);
        }

        public static bool TryGetV3Bool(LevelEvent? ev, string key, out bool value)
        {
            value = false;
            if (!TryGetV3Property(ev, key, out var raw) || raw == null) return false;
            if (raw is bool b)
            {
                value = b;
                return true;
            }
            try
            {
                value = Convert.ToBoolean(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertVector2(object? raw, out Vector2 value)
        {
            value = default;
            switch (raw)
            {
                case Vector2 v:
                    value = v;
                    return true;
                case float f:
                    value = new Vector2(f, f);
                    return true;
                case int i:
                    value = new Vector2(i, i);
                    return true;
                case long l:
                    value = new Vector2(l, l);
                    return true;
                case double d:
                    value = new Vector2((float)d, (float)d);
                    return true;
                case List<object> list when list.Count >= 2:
                    value = new Vector2(
                        Convert.ToSingle(list[0] ?? float.NaN),
                        Convert.ToSingle(list[1] ?? float.NaN));
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsSettingsEvent(LevelEventType type)
            => type == LevelEventType.LevelSettings
               || type == LevelEventType.TrackSettings
               || type == LevelEventType.BackgroundSettings;

        private static void CaptureV3Properties(LevelEvent instance, Dictionary<string, object>? dict, bool isGlobal)
        {
            _v3EventData.Remove(instance);
            if (dict == null || instance.info == null) return;

            Dictionary<string, object>? extras = null;
            bool captureWholeEvent = !isGlobal && _decodingV3Chart && !IsSettingsEvent(instance.eventType);

            if (captureWholeEvent)
            {
                // v3 普通事件：不在自身属性表里的键全部保留（未知事件 / 未来版本属性都不丢）
                var props = instance.info.propertiesInfo;
                foreach (var kv in dict)
                {
                    if (InternalEventKeys.Contains(kv.Key)) continue;
                    if (props != null && props.ContainsKey(kv.Key)) continue;
                    extras ??= new Dictionary<string, object>();
                    extras[kv.Key] = kv.Value;
                }
            }
            else if (V3PropertiesByEvent.TryGetValue(instance.eventType, out var keys))
            {
                // settings 事件的 dict 是整张 settings 字典，只能按白名单抓各自小节
                foreach (var key in keys)
                {
                    if (!dict.TryGetValue(key, out var raw)) continue;
                    extras ??= new Dictionary<string, object>();
                    extras[key] = raw;
                }
            }

            if (extras != null) _v3EventData.Add(instance, extras);
        }

        private static void CopyV3Properties(LevelEvent from, LevelEvent to)
        {
            _v3EventData.Remove(to);
            if (_v3EventData.TryGetValue(from, out var extras))
                _v3EventData.Add(to, new Dictionary<string, object>(extras));

            _v3EnumFallbacks.Remove(to);
            if (_v3EnumFallbacks.TryGetValue(from, out var fallbacks))
                _v3EnumFallbacks.Add(to, new Dictionary<string, EnumFallbackValue>(fallbacks));
        }

        private static string AppendField(string? result, string key, string rawJson)
        {
            var sb = new StringBuilder(result ?? string.Empty);
            if (sb.Length > 0) sb.Append(", ");
            sb.Append('"').Append(key).Append("\": ").Append(rawJson);
            return sb.ToString();
        }

        private static void AppendV3Properties(LevelEvent instance, ref string result)
        {
            if (!_v3EventData.TryGetValue(instance, out var extras) || extras.Count == 0) return;
            foreach (var kv in extras)
                result = AppendField(result, kv.Key, Json.Serialize(kv.Value));
        }

        // ─────────────────────── v3 新增枚举值的安全回退 ───────────────────────

        private static bool SafeEnumIsDefined(Type enumType, string name)
        {
            try
            {
                return Enum.IsDefined(enumType, name);
            }
            catch
            {
                return false;
            }
        }

        private static LevelEventInfo? ResolveEventInfo(
            string? explicitEventType, Dictionary<string, object>? dict, bool isGlobal)
        {
            string? typeName = explicitEventType;
            if (string.IsNullOrEmpty(typeName) && dict != null && dict.TryGetValue("eventType", out var et))
                typeName = et as string;
            if (string.IsNullOrEmpty(typeName)) return null;

            var table = isGlobal ? GCS.settingsInfo : GCS.levelEventsInfo;
            LevelEventInfo? info = null;
            table?.TryGetValue(typeName!, out info);
            return info;
        }

        private static string? GetEventTypeName(string? explicitEventType, Dictionary<string, object>? dict)
        {
            string? typeName = explicitEventType;
            if (string.IsNullOrEmpty(typeName) && dict != null && dict.TryGetValue("eventType", out var et))
                typeName = et as string;
            return typeName;
        }

        /// <summary>
        /// 3.4.0 把 SetFilterAdvanced.filterProperties 直接序列化成 JSON 对象
        /// （<c>"filterProperties": { "filter_x": ... }</c>），而 2.9.8 只会把属性值
        /// 当字符串拼成 <c>{value}</c> 再解析；对象形式会让 Json.Deserialize 返回 null，
        /// 随后 foreach 抛 NullReferenceException 导致整张谱加载失败。
        /// 这里把对象形式转成 2.9.8 认的「内部 JSON 字符串」形式；v3 保存时也能再读回。
        /// </summary>
        private static void NormalizeV3PropertyShapes(LevelEventInfo? info, Dictionary<string, object>? dict)
        {
            if (info?.propertiesInfo == null || dict == null) return;

            foreach (var kv in info.propertiesInfo)
            {
                var prop = kv.Value;
                if (prop == null) continue;

                if (prop.type == PropertyType.FilterProperties
                    && dict.TryGetValue(kv.Key, out var raw))
                {
                    if (raw is Dictionary<string, object> filterObj)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in filterObj)
                        {
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append(Json.Serialize(item.Key)).Append(": ").Append(Json.Serialize(item.Value));
                        }
                        dict[kv.Key] = sb.ToString();
                        continue;
                    }

                    // 字符串形式：解析失败同样会让 2.9.8 foreach null，先降级为空对象
                    if (raw is string filterText
                        && Json.Deserialize("{" + filterText + "}") is not Dictionary<string, object>)
                    {
                        dict[kv.Key] = string.Empty;
                        continue;
                    }
                }

                // 防御：v3 里这些类型目前与 2.9.8 同形，但值损坏时 2.9.8 会直接 NRE
                if (prop.type == PropertyType.Vector2 && dict.TryGetValue(kv.Key, out raw))
                {
                    bool ok = raw is float || raw is int || raw is long || raw is double
                              || raw is List<object> vec && vec.Count >= 2;
                    if (!ok) dict[kv.Key] = new List<object> { null!, null! };
                }
            }
        }

        /// <summary>3.x 给枚举加了新成员（审计结果：仅 ObjectDecorationType.PlayerBubble），
        /// 2.9.8 的 LevelEvent.Decode 会 Enum.Parse 失败直接抛异常导致整张谱面加载失败。
        /// 这里把未知值替换成默认值让解码通过，原值记录在旁路表并在 Encode 时还原。
        /// </summary>
        private static void PrepareUnknownEnumValues(
            LevelEvent instance, Dictionary<string, object>? dict, string? explicitEventType, bool isGlobal)
        {
            _v3EnumFallbacks.Remove(instance);
            if (dict == null || !_decodingV3Chart) return;

            var info = ResolveEventInfo(explicitEventType, dict, isGlobal);
            var props = info?.propertiesInfo;
            if (props == null) return;

            Dictionary<string, EnumFallbackValue>? fallbacks = null;
            foreach (var kv in props)
            {
                var prop = kv.Value;
                if (prop == null || prop.type != PropertyType.Enum || prop.enumType == null) continue;
                if (!dict.TryGetValue(kv.Key, out var raw)) continue;
                if (raw is not string value || value.Length == 0) continue;
                if (SafeEnumIsDefined(prop.enumType, value)) continue;

                string fallback = (prop.value_default as string) ?? string.Empty;
                if (fallback.Length == 0 || !SafeEnumIsDefined(prop.enumType, fallback))
                {
                    var names = Enum.GetNames(prop.enumType);
                    fallback = names.Length > 0 ? names[0] : string.Empty;
                }
                if (fallback.Length == 0) continue;

                fallbacks ??= new Dictionary<string, EnumFallbackValue>();
                fallbacks[kv.Key] = new EnumFallbackValue { Original = value, Fallback = fallback };
                dict[kv.Key] = fallback;

                Main.Logger?.Log(
                    $"[V3Compat] unsupported enum value {info!.name}.{kv.Key}='{value}', fallback to '{fallback}'");
            }

            if (fallbacks != null) _v3EnumFallbacks.Add(instance, fallbacks);
        }

        private static void RestoreEnumFallbacks(LevelEvent instance, ref string result)
        {
            if (!_v3EnumFallbacks.TryGetValue(instance, out var fallbacks) || fallbacks.Count == 0) return;

            foreach (var kv in fallbacks)
            {
                // 用户在编辑器里改过值就不要再还原
                if (instance.data != null && instance.data.TryGetValue(kv.Key, out var current))
                {
                    string currentText = current?.ToString() ?? string.Empty;
                    if (!string.Equals(currentText, kv.Value.Fallback, StringComparison.Ordinal)) continue;
                }

                string from = "\"" + kv.Key + "\": " + Json.Serialize(kv.Value.Fallback);
                if (!string.IsNullOrEmpty(result) && result.Contains(from))
                    result = result.Replace(from, "\"" + kv.Key + "\": " + Json.Serialize(kv.Value.Original));
                else
                    result = AppendField(result, kv.Key, Json.Serialize(kv.Value.Original));
            }
        }

        // ─────────────────────── 事件解码兜底（v3 谱面） ───────────────────────

        /// <summary>
        /// 把 data / disabled 补全成「至少能安全访问」的状态：
        /// 保留已解出的键，缺失的键填属性默认值并标记为 disabled（与 Decode 的语义一致）。
        /// </summary>
        private static void PopulateEventData(
            LevelEvent instance, Dictionary<string, object>? dict, LevelEventInfo? info, bool rawValues)
        {
            instance.data ??= new Dictionary<string, object>();
            instance.disabled ??= new Dictionary<string, bool>();

            var props = info?.propertiesInfo;
            if (props == null) return;

            foreach (var kv in props)
            {
                var prop = kv.Value;
                if (prop == null) continue;

                object? raw = null;
                bool present = dict != null && dict.TryGetValue(kv.Key, out raw);

                if (!instance.disabled.ContainsKey(kv.Key))
                    instance.disabled[kv.Key] = !present;
                if (instance.data.ContainsKey(kv.Key)) continue;
                instance.data[kv.Key] = present && rawValues ? raw : prop.value_default;
            }
        }

        /// <summary>
        /// 原版 Decode 对未知事件类型会留下 info == null，LevelData.Decode 随后在
        /// levelEvent.info.taroDLCCheck 直接 NRE。这里补一个只读 fake info（第三方事件
        /// 那套机制），保证整张谱不会因为单个未知事件加载失败。
        /// </summary>
        private static void AttachFallbackInfo(
            LevelEvent instance, Dictionary<string, object>? dict, string? explicitEventType, bool isGlobal)
        {
            if (!Enabled || !_decodingV3Chart || instance.info != null) return;

            var info = ResolveEventInfo(explicitEventType, dict, isGlobal);
            if (info == null && !isGlobal)
            {
                string? typeName = GetEventTypeName(explicitEventType, dict);
                if (!string.IsNullOrEmpty(typeName))
                    info = CustomEventsPatches.EnsureFakeInfo(typeName!, dict);
            }
            if (info == null) return;

            instance.info = info;
            if (instance.eventType == LevelEventType.None)
            {
                if (info.type != LevelEventType.None) instance.eventType = info.type;
                else if (dict != null && dict.TryGetValue("eventType", out var etRaw) && etRaw is string etName)
                    instance.eventType = RDUtils.ParseEnum(etName, LevelEventType.None);
            }
            PopulateEventData(instance, dict, info, rawValues: info.type == LevelEventType.None);
        }

        /// <summary>
        /// 单个事件解码失败时（原版 Decode 对某些 v3 值形态会 NRE / 抛异常），
        /// 不让异常冒泡炸掉整张谱：记录原始事件数据，补齐 info/data/disabled 后继续加载。
        /// 未实现的行为按第三方事件处理（只读、保存不丢），符合「不反向移植但不崩」的约定。
        /// </summary>
        private static void SalvageFailedEvent(
            LevelEvent instance, Dictionary<string, object>? dict, string? explicitEventType,
            bool isGlobal, Exception exception)
        {
            string typeName = GetEventTypeName(explicitEventType, dict) ?? "<unknown>";
            string floorText = "?";
            if (dict != null && dict.TryGetValue("floor", out var floorRaw))
                floorText = floorRaw?.ToString() ?? "?";
            string keys = dict == null ? "<null>" : string.Join(", ", dict.Keys);

            Main.Logger?.Log(
                $"[V3Compat] failed to decode event '{typeName}' floor={floorText}: {exception.Message}");
            Main.Logger?.Log($"[V3Compat] failed event keys: {keys}");
            try
            {
                Main.Logger?.Log($"[V3Compat] failed event dump: {Json.Serialize(dict)}");
            }
            catch
            {
                // dump 仅用于诊断，失败无所谓
            }

            if (dict != null && dict.TryGetValue("eventType", out var etRaw) && etRaw is string etName)
                instance.eventType = RDUtils.ParseEnum(etName, LevelEventType.None);

            var info = instance.info ?? ResolveEventInfo(explicitEventType, dict, isGlobal);
            if (info == null && !isGlobal && typeName != "<unknown>")
                info = CustomEventsPatches.EnsureFakeInfo(typeName, dict);

            if (info != null)
            {
                instance.info = info;
                if (instance.eventType == LevelEventType.None && info.type != LevelEventType.None)
                    instance.eventType = info.type;
            }
            PopulateEventData(instance, dict, info, rawValues: info == null || info.type == LevelEventType.None);

            if (dict != null && dict.TryGetValue("floor", out var floorValue))
            {
                try { instance.floor = Convert.ToInt32(floorValue); }
                catch { /* 保持原值 */ }
            }
        }

        // ─────────────────────────── 装饰物 polyfill 状态 ───────────────────────────

        private sealed class DecorationCompatState
        {
            public Vector2 TextureScaleMultiplier = Vector2.one;
            public Vector2 CroppedPivotOffset = Vector2.zero;
        }

        private sealed class MoveDecorationsCompatState
        {
            public bool HasOriginalSize;
            public Vector2 OriginalSize;
            public bool HasCropPivotOffset;
            public Vector2 CropPivotOffset;
        }

        private sealed class PendingFilterTarget
        {
            public FilterTargetType Value;
        }

        private static readonly ConditionalWeakTable<scrDecoration, DecorationCompatState> _decorationStates = new();
        private static readonly ConditionalWeakTable<ffxMoveDecorationsPlus, MoveDecorationsCompatState> _moveDecorationStates = new();
        private static readonly ConditionalWeakTable<ffxSetFilterAdvancedPlus, PendingFilterTarget> _pendingFilterTargets = new();

        private static DecorationCompatState GetDecorationState(scrDecoration dec)
            => _decorationStates.GetOrCreateValue(dec);

        /// <summary>与 3.4.0 scrVisualDecoration.SetTextureScaleMultiplier 一致：originalSize / 贴图尺寸。</summary>
        private static Vector2 ComputeTextureScaleMultiplier(scrVisualDecoration? dec, Vector2 originalSize)
        {
            var sprite = dec?.spriteRenderer != null ? dec.spriteRenderer.sprite : null;
            var texture = sprite != null ? sprite.texture : null;
            if (texture == null) return Vector2.one;

            float width = texture.width;
            float height = texture.height;
            if (width == 0f || height == 0f || originalSize.x == 0f || originalSize.y == 0f)
                return Vector2.one;

            return new Vector2(originalSize.x / width, originalSize.y / height);
        }

        private static void ApplyMoveDecorationState(scrVisualDecoration visual)
        {
            // SetPosition 内部会走 UpdatePosition -> UpdateLock -> SetScale，
            // 因此 tsm 变化也会一并生效（与 3.4.0 的调用顺序一致）。
            visual.SetPosition(visual.pivotPosVec, visual.pivotOffsetVec);
        }

        // ─────────────────────────── 开关联动 ───────────────────────────

        private static bool Enabled => Main.Settings.compatibility.enableV3LevelCompat;

        /// <summary>
        /// 当前是否正在解码 v3（version &gt; 15）谱面。供 CustomEventsPatches 判断
        /// 是否要把未知事件注册成「第三方事件」那样的只读 fake event。
        /// 两个 Prefix 的执行顺序不固定，所以这里同时看标志位与 dict 里的原始版本号。
        /// </summary>
        private static bool _decodingV3Chart;

        public static bool IsDecodingV3Chart(Dictionary<string, object>? dict = null)
        {
            if (_decodingV3Chart) return true;
            try
            {
                if (dict != null && dict.TryGetValue("settings", out var raw)
                    && raw is Dictionary<string, object> settings
                    && settings.TryGetValue("version", out var versionRaw)
                    && Convert.ToInt32(versionRaw) > MaxV298Version)
                {
                    return true;
                }
            }
            catch
            {
                // ignore malformed version
            }
            return false;
        }

        /// <summary>设置界面切换开关后调用，异步重挂 / 卸载本类全部补丁。</summary>
        public static void UpdatePatches()
        {
            try
            {
                foreach (var type in typeof(V3LevelCompatPatches).GetNestedTypes(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                        AsyncPatchManager.UpdatePatchByTypeAsync(type);
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[V3Compat] UpdatePatches failed: {e}");
            }
        }

        // ─────────────────────────── 1. 版本门 ───────────────────────────

        /// <summary>LevelData.Decode：临时降级 settings.version 放行 >15 的谱面，结束后还原。</summary>
        [HarmonyPatch(typeof(LevelData), nameof(LevelData.Decode))]
        public static class LevelDataDecodeVersionPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Dictionary<string, object> dict, out int __state)
            {
                __state = 0;
                if (!Enabled || dict == null) return;
                if (!dict.TryGetValue("settings", out var raw) || raw is not Dictionary<string, object> settings)
                    return;
                if (!settings.TryGetValue("version", out var versionRaw)) return;

                try
                {
                    int version = Convert.ToInt32(versionRaw);
                    if (version > MaxV298Version)
                    {
                        __state = version;
                        settings["version"] = MaxV298Version;
                        _decodingV3Chart = true;
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] version downgrade failed: {e.Message}");
                }
            }

            [HarmonyPostfix]
            public static void Postfix(LevelData __instance, Dictionary<string, object> dict, int __state)
            {
                _decodingV3Chart = false;
                if (__state <= MaxV298Version) return;

                // 还原 JSON 数据与字段，后续编辑 / 保存看到的都是真实版本
                if (dict != null && dict.TryGetValue("settings", out var raw)
                    && raw is Dictionary<string, object> settings)
                {
                    settings["version"] = __state;
                }
                if (__instance != null) __instance.version = __state;
            }

            [HarmonyFinalizer]
            public static void Finalizer(int __state)
            {
                _decodingV3Chart = false;
            }
        }

        /// <summary>LevelDataCLS.Decode（关卡列表）：同样放行 v3 谱面，避免被标记为 FutureVersion。</summary>
        [HarmonyPatch(typeof(LevelDataCLS), nameof(LevelDataCLS.Decode))]
        public static class LevelDataCLSDecodeVersionPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Dictionary<string, object> rootDict, out int __state)
            {
                __state = 0;
                if (!Enabled || rootDict == null) return;
                if (!rootDict.TryGetValue("settings", out var raw) || raw is not Dictionary<string, object> settings)
                    return;
                if (!settings.TryGetValue("version", out var versionRaw)) return;

                try
                {
                    int version = Convert.ToInt32(versionRaw);
                    if (version > MaxV298Version)
                    {
                        __state = version;
                        settings["version"] = MaxV298Version;
                        _decodingV3Chart = true;
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] CLS version downgrade failed: {e.Message}");
                }
            }

            [HarmonyPostfix]
            public static void Postfix(Dictionary<string, object> rootDict, int __state)
            {
                _decodingV3Chart = false;
                if (__state <= MaxV298Version) return;
                if (rootDict != null && rootDict.TryGetValue("settings", out var raw)
                    && raw is Dictionary<string, object> settings)
                {
                    settings["version"] = __state;
                }
            }

            [HarmonyFinalizer]
            public static void Finalizer(int __state)
            {
                _decodingV3Chart = false;
            }
        }

        /// <summary>LevelData.Encode：把固定写死的 "version": 15 改回真实版本。</summary>
        [HarmonyPatch(typeof(LevelData), nameof(LevelData.Encode))]
        public static class LevelDataEncodeVersionPatch
        {
            [HarmonyPostfix]
            public static void Postfix(LevelData __instance, ref string __result)
            {
                if (!Enabled || __instance == null || string.IsNullOrEmpty(__result)) return;
                if (__instance.version <= MaxV298Version) return;

                try
                {
                    __result = EncodeVersionLine.Replace(
                        __result,
                        m => m.Groups["prefix"].Value + __instance.version + m.Groups["suffix"].Value,
                        1);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] version encode patch failed: {e.Message}");
                }
            }
        }

        // ─────────────────────────── 2. v3 属性保留 ───────────────────────────

        [HarmonyPatch(typeof(LevelEvent), nameof(LevelEvent.Decode))]
        public static class LevelEventDecodeExtrasPatch
        {
            /// <summary>
            /// 在原版 Decode 之前把 v3 的「新值形态」规整成 2.9.8 能解析的形式
            /// （如 filterProperties 的对象形式 → 内部 JSON 字符串），并处理新增枚举值。
            /// </summary>
            [HarmonyPrefix]
            public static void Prefix(LevelEvent __instance, Dictionary<string, object> dict,
                string? explicitEventType, bool isGlobal)
            {
                try
                {
                    if (__instance == null || !Enabled || !_decodingV3Chart) return;
                    var info = ResolveEventInfo(explicitEventType, dict, isGlobal);
                    NormalizeV3PropertyShapes(info, dict);
                    PrepareUnknownEnumValues(__instance, dict, explicitEventType, isGlobal);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] pre-decode normalize failed: {e}");
                }
            }

            [HarmonyPostfix]
            public static void Postfix(LevelEvent __instance, Dictionary<string, object> dict,
                string? explicitEventType, bool isGlobal)
            {
                try
                {
                    if (__instance == null) return;
                    AttachFallbackInfo(__instance, dict, explicitEventType, isGlobal);
                    CaptureV3Properties(__instance, dict, isGlobal);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] capture extras failed: {e}");
                }
            }

            /// <summary>
            /// 原版 Decode 在 v3 谱面上仍可能因未知值形态抛异常（NRE 等）。
            /// 只对 v3 谱面兜底：记录失败事件并降级为可加载的默认值 / 只读事件，
            /// 避免单个事件把整张谱的加载炸掉。
            /// </summary>
            [HarmonyFinalizer]
            public static Exception? Finalizer(
                Exception? __exception, LevelEvent __instance, Dictionary<string, object>? dict,
                string? explicitEventType, bool isGlobal)
            {
                if (__exception == null) return null;
                if (!Enabled || !_decodingV3Chart) return __exception;

                try
                {
                    SalvageFailedEvent(__instance, dict, explicitEventType, isGlobal, __exception);
                    CaptureV3Properties(__instance, dict, isGlobal);
                    return null;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] salvage failed, rethrowing: {e}");
                    return __exception;
                }
            }
        }

        [HarmonyPatch(typeof(LevelEvent), nameof(LevelEvent.Encode))]
        public static class LevelEventEncodeExtrasPatch
        {
            [HarmonyPostfix]
            public static void Postfix(LevelEvent __instance, ref string __result)
            {
                try
                {
                    if (__instance == null) return;
                    RestoreEnumFallbacks(__instance, ref __result);
                    AppendV3Properties(__instance, ref __result);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] encode extras failed: {e}");
                }
            }
        }

        /// <summary>LevelData.Copy() 走 LevelEvent.Copy()，让旁路表跟着副本走。</summary>
        [HarmonyPatch(typeof(LevelEvent), nameof(LevelEvent.Copy))]
        public static class LevelEventCopyExtrasPatch
        {
            [HarmonyPostfix]
            public static void Postfix(LevelEvent __instance, LevelEvent __result)
            {
                try
                {
                    if (__instance == null || __result == null) return;
                    CopyV3Properties(__instance, __result);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] copy extras failed: {e}");
                }
            }
        }

        // ─────────────────────────── 3.1 SetFilterAdvanced.targetType ───────────────────────────

        /// <summary>
        /// 2.9.8 的 Decode 把 targetType 写死为 Camera 后立即调用 Setup()，
        /// 所以在 Decode 前记下真实值，Setup 前再写回实例。
        /// </summary>
        [HarmonyPatch(typeof(ffxSetFilterAdvancedPlus), nameof(ffxSetFilterAdvancedPlus.Decode))]
        public static class FilterAdvancedTargetTypeDecodePatch
        {
            [HarmonyPrefix]
            public static void Prefix(ffxSetFilterAdvancedPlus __instance, LevelEvent? evnt)
            {
                try
                {
                    _pendingFilterTargets.Remove(__instance);
                    if (!Enabled || evnt == null) return;
                    if (!TryGetV3Property(evnt, "targetType", out var raw) || raw == null) return;

                    FilterTargetType target = FilterTargetType.Camera;
                    if (raw is FilterTargetType e)
                    {
                        target = e;
                    }
                    else if (raw is string s && Enum.TryParse(s, ignoreCase: true, out FilterTargetType parsed))
                    {
                        target = parsed;
                    }
                    else if (raw is long l && Enum.IsDefined(typeof(FilterTargetType), (int)l))
                    {
                        target = (FilterTargetType)(int)l;
                    }

                    var pending = new PendingFilterTarget { Value = target };
                    _pendingFilterTargets.Add(__instance, pending);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] filter target decode failed: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(ffxSetFilterAdvancedPlus), nameof(ffxSetFilterAdvancedPlus.Setup))]
        public static class FilterAdvancedTargetTypeSetupPatch
        {
            [HarmonyPrefix]
            public static void Prefix(ffxSetFilterAdvancedPlus __instance)
            {
                try
                {
                    if (!_pendingFilterTargets.TryGetValue(__instance, out var pending)) return;
                    _pendingFilterTargets.Remove(__instance);
                    __instance.targetType = pending.Value;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] filter target setup failed: {e.Message}");
                }
            }
        }

        // ───────────── 3.2 AddDecoration / MoveDecorations：originalSize + cropPivotOffset ─────────────

        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.Setup))]
        public static class DecorationSetupCompatPatch
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecoration __instance)
            {
                try
                {
                    if (!Enabled || __instance == null || __instance is not scrVisualDecoration visual) return;
                    var ev = __instance.sourceLevelEvent;
                    if (ev == null) return;

                    bool hasOriginalSize = TryGetV3Vector2(ev, "originalSize", out var originalSize);
                    bool hasCropPivot = TryGetV3Vector2(ev, "cropPivotOffset", out var cropPivot);
                    if (!hasOriginalSize && !hasCropPivot) return;

                    var state = GetDecorationState(__instance);
                    if (hasOriginalSize)
                        state.TextureScaleMultiplier = ComputeTextureScaleMultiplier(visual, originalSize);
                    if (hasCropPivot)
                        state.CroppedPivotOffset = cropPivot / 100f;

                    ApplyMoveDecorationState(visual);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] decoration setup polyfill failed: {e}");
                }
            }
        }

        /// <summary>
        /// 3.4.0 的 scrDecoration.SetPosition 把 (pivotOffset + croppedPivotOffset) / trueScaleMultiplier
        /// 写入 childTransform.localPosition；2.9.8 二者都不存在，用旁路表补上。
        /// </summary>
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetPosition))]
        public static class DecorationSetPositionCompatPatch
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecoration __instance)
            {
                try
                {
                    if (!Enabled || __instance == null || __instance.parallax == null) return;
                    if (!_decorationStates.TryGetValue(__instance, out var state)) return;

                    Vector2 tsm = state.TextureScaleMultiplier;
                    Vector2 cropped = state.CroppedPivotOffset;
                    if (tsm == Vector2.one && cropped == Vector2.zero) return;

                    float tx = Mathf.Approximately(tsm.x, 0f) ? 1f : tsm.x;
                    float ty = Mathf.Approximately(tsm.y, 0f) ? 1f : tsm.y;
                    Vector2 local = new(
                        (__instance.pivotOffsetVec.x + cropped.x) / tx,
                        (__instance.pivotOffsetVec.y + cropped.y) / ty);

                    if (__instance.childTransform != null)
                        __instance.childTransform.localPosition = local;

                    if (ADOBase.editor != null && __instance.selectionBordersObject != null)
                        __instance.selectionBordersObject.transform.localPosition = local;

                    __instance.UpdatePosition();
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] SetPosition polyfill failed: {e}");
                }
            }
        }

        /// <summary>3.4.0 的 camScaleMultiplier 含 textureScaleMultiplier，这里在 SetScale 后补乘。</summary>
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetScale))]
        public static class DecorationSetScaleCompatPatch
        {
            [HarmonyPostfix]
            public static void Postfix(scrDecoration __instance)
            {
                try
                {
                    if (!Enabled || __instance == null || __instance.pivotTrans == null) return;
                    if (!_decorationStates.TryGetValue(__instance, out var state)) return;

                    Vector2 tsm = state.TextureScaleMultiplier;
                    if (tsm == Vector2.one) return;

                    Vector3 scale = __instance.pivotTrans.localScale;
                    __instance.pivotTrans.localScale = new Vector3(
                        scale.x * tsm.x,
                        scale.y * tsm.y,
                        scale.z);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] SetScale polyfill failed: {e}");
                }
            }
        }

        /// <summary>MoveDecorations 的 originalSize / cropPivotOffset 只作用于视觉装饰物。</summary>
        [HarmonyPatch(typeof(ffxMoveDecorationsPlus), nameof(ffxMoveDecorationsPlus.Decode))]
        public static class MoveDecorationsDecodeCompatPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ffxMoveDecorationsPlus __instance, LevelEvent? evnt)
            {
                try
                {
                    _moveDecorationStates.Remove(__instance);
                    if (!Enabled || __instance == null || evnt == null) return;

                    bool hasOriginalSize = TryGetV3Vector2(evnt, "originalSize", out var originalSize);
                    bool hasCropPivot = TryGetV3Vector2(evnt, "cropPivotOffset", out var cropPivot);
                    if (!hasOriginalSize && !hasCropPivot) return;

                    var state = new MoveDecorationsCompatState
                    {
                        HasOriginalSize = hasOriginalSize,
                        OriginalSize = originalSize,
                        HasCropPivotOffset = hasCropPivot,
                        CropPivotOffset = cropPivot,
                    };
                    _moveDecorationStates.Add(__instance, state);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] MoveDecorations decode failed: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(ffxMoveDecorationsPlus), nameof(ffxMoveDecorationsPlus.StartEffect))]
        public static class MoveDecorationsStartEffectCompatPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ffxMoveDecorationsPlus __instance)
            {
                try
                {
                    if (!Enabled || __instance == null) return;
                    if (!_moveDecorationStates.TryGetValue(__instance, out var moveState)) return;
                    if (!moveState.HasOriginalSize && !moveState.HasCropPivotOffset) return;

                    var manager = __instance.decManager;
                    var tags = __instance.targetTags;
                    if (manager == null || tags == null) return;

                    var seen = new HashSet<scrDecoration>();
                    foreach (var tag in tags)
                    {
                        if (tag == null) continue;
                        if (!manager.taggedDecorations.TryGetValue(tag, out var list) || list == null) continue;

                        foreach (var dec in list)
                        {
                            if (dec == null || !seen.Add(dec)) continue;
                            if (dec is not scrVisualDecoration visual) continue;

                            var state = GetDecorationState(visual);
                            if (moveState.HasOriginalSize)
                                state.TextureScaleMultiplier = ComputeTextureScaleMultiplier(visual, moveState.OriginalSize);
                            if (moveState.HasCropPivotOffset)
                                state.CroppedPivotOffset = moveState.CropPivotOffset / 100f;

                            ApplyMoveDecorationState(visual);
                        }
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] MoveDecorations polyfill failed: {e}");
                }
            }
        }

        // ─────────────────────────── 3.3 FreeRoam.outCam ───────────────────────────

        /// <summary>
        /// 3.x 在 UpdateFreeroam 里用 scrFloor.moveCamAtFreeroamEnd 决定是否回调相机；
        /// 2.9.8 无条件调用 MoveCameraToTile。这里把该调用替换成 outCam 感知版本。
        /// </summary>
        [HarmonyPatch(typeof(scrController), nameof(scrController.UpdateFreeroam))]
        public static class FreeRoamOutCamPatch
        {
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = AccessTools.Method(typeof(scrController), nameof(scrController.MoveCameraToTile));
                var replacement = AccessTools.Method(
                    typeof(V3LevelCompatPatches), nameof(MoveCameraToTileOutCamAware));

                foreach (var instruction in instructions)
                {
                    if (original != null && replacement != null && instruction.Calls(original))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                    yield return instruction;
                }
            }
        }

        /// <summary>与 scrController.MoveCameraToTile 同签名，供 transpiler 替换调用（this 作为第一个参数）。</summary>
        public static void MoveCameraToTileOutCamAware(
            scrController self, scrFloor floor, scrFloor from, float fSecs, Ease ease, float zoom)
        {
            try
            {
                if (self == null) return;
                if (ShouldSkipFreeroamCamera()) return;
                self.MoveCameraToTile(floor, from, fSecs, ease, zoom);
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[V3Compat] outCam polyfill failed: {e}");
            }
        }

        private static bool ShouldSkipFreeroamCamera()
        {
            try
            {
                var controller = scrController.instance;
                var lm = ADOBase.lm;
                var startTiles = lm != null ? lm.listFreeroamStartTiles : null;
                if (controller == null || startTiles == null) return false;

                int section = controller.curFreeRoamSection;
                if (section < 0 || section >= startTiles.Count) return false;

                var startFloor = startTiles[section];
                if (startFloor == null) return false;

                return TryGetOutCam(startFloor, out bool outCam) && !outCam;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetOutCam(scrFloor startFloor, out bool outCam)
        {
            outCam = true;
            var events = scnGame.instance != null ? scnGame.instance.events : null;
            if (events == null) return false;

            foreach (var ev in events)
            {
                if (ev == null || ev.eventType != LevelEventType.FreeRoam) continue;
                if (ev.floor != startFloor.seqID) continue;

                // 找到对应 FreeRoam 事件：缺省 true（与 3.x scrFloor.moveCamAtFreeroamEnd 默认值一致）
                if (TryGetV3Bool(ev, "outCam", out bool value)) outCam = value;
                return true;
            }
            return false;
        }

        // ─────────────────────────── 3.4 TrackSettings.trackShadowColor ───────────────────────────

        /// <summary>
        /// 3.x 把 trackShadowColor 传进 SetTrackStyle 覆盖 _ShadowColor；
        /// 2.9.8 的 SetTrackStyle 已经写同一个材质属性，只是颜色固定，这里覆写即可。
        /// </summary>
        [HarmonyPatch(typeof(scrFloor), nameof(scrFloor.SetTrackStyle))]
        public static class TrackShadowColorPatch
        {
            [HarmonyPostfix]
            public static void Postfix(scrFloor __instance)
            {
                try
                {
                    if (!Enabled || __instance == null) return;
                    if (!TryGetTrackShadowColor(out var color)) return;
                    if (__instance.floorRenderer is not FloorMeshRenderer meshRenderer) return;
                    var material = meshRenderer.material;
                    if (material != null) material.SetColor("_ShadowColor", color);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] trackShadowColor polyfill failed: {e}");
                }
            }
        }

        private static bool TryGetTrackShadowColor(out Color color)
        {
            color = default;
            try
            {
                var levelData = scnGame.instance != null ? scnGame.instance.levelData : null;
                var trackSettings = levelData != null ? levelData.trackSettings : null;
                if (trackSettings == null) return false;
                if (!TryGetV3Property(trackSettings, "trackShadowColor", out var raw)) return false;
                if (raw is not string hex || string.IsNullOrEmpty(hex)) return false;
                return RDUtils.TryHexToColor(hex, out color);
            }
            catch
            {
                return false;
            }
        }

        // ─────────────────────────── 3.5 BackgroundSettings.customBGPrefab ───────────────────────────

        /// <summary>
        /// 2.9.8 没有 Resources/InternalLevels/Shared/Prefabs 体系，无法还原自定义背景；
        /// 加载成功后提示玩家该属性被忽略（属性本身仍会被保留到保存）。
        /// </summary>
        [HarmonyPatch(typeof(scnGame), "LoadLevel")]
        public static class CustomBackgroundPrefabNoticePatch
        {
            private static string? _lastNoticeKey;

            [HarmonyPostfix]
            public static void Postfix(LoadResult status)
            {
                try
                {
                    if (!Enabled || status != LoadResult.Successful) return;

                    var levelData = scnGame.instance != null ? scnGame.instance.levelData : null;
                    var backgroundSettings = levelData != null ? levelData.backgroundSettings : null;
                    if (backgroundSettings == null) return;
                    if (!TryGetV3Property(backgroundSettings, "customBGPrefab", out var raw)) return;

                    var prefab = raw as string;
                    if (prefab == null || prefab.Length == 0) return;

                    // 同一谱面提示一次即可
                    string key = (ADOBase.levelPath ?? string.Empty) + "|" + prefab;
                    if (_lastNoticeKey == key) return;
                    _lastNoticeKey = key;

                    Main.Logger?.Log(Localization.Get("V3CompatCustomBGPrefabIgnored", prefab));
                    UI.VRAMNotificationUI.Show(Localization.Get("V3CompatCustomBGPrefabIgnored", prefab));
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[V3Compat] customBGPrefab notice failed: {e}");
                }
            }
        }
    }
}
