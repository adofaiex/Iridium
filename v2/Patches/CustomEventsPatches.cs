using System;
using System.Collections.Generic;
using System.Text;
using GDMiniJSON;
using HarmonyLib;
using ADOFAI;

namespace Iridium.Patches
{
    /// <summary>
    /// Third-party custom events (CustomEvent) support.
    ///
    /// Charts made for third-party mods can contain events whose eventType is
    /// unknown to the game (e.g. "MyMod.CustomEvent"). The game's LevelEvent.Decode
    /// returns NoTypeFound for them, leaving info == null, and LevelData.Decode then
    /// crashes on levelEvent.info.taroDLCCheck → the chart fails to load.
    ///
    /// Iridium temporarily registers fake LevelEventInfo entries for every unknown
    /// event type found in the chart's actions/decorations, so decoding succeeds and
    /// the events survive save/load untouched. In the editor inspector these events
    /// are read-only (disable / delete only), since their real behavior comes from
    /// the third-party mod that is not installed.
    ///
    /// The same machinery also covers unknown event types in v3 (version &gt; 15)
    /// charts when the "v3 level compatibility" option is on: they must not break
    /// loading, but there is no need to backport their behavior.
    /// </summary>
    public static class CustomEventsPatches
    {
        private static readonly HashSet<string> FakeEventNames = new();
        private static readonly Dictionary<string, LevelEventInfo> FakeInfos = new();

        private static bool IsFakeEventName(string? name) => name != null && FakeEventNames.Contains(name);
        private static bool IsFakeInfo(LevelEventInfo? info) => info != null && info.name != null && FakeInfos.ContainsKey(info.name);

        /// <summary>
        /// uGUI 会把点击过的 Button 留在 EventSystem.currentSelectedGameObject 上：
        /// 第三方事件的 Tab 会一直保持「选中 / 聚焦」高亮，之后按 Enter/Space
        /// 还会再次触发它。处理完 fake tab 点击后主动释放焦点。
        /// （与隔壁 Litematica 的 ReleaseUiFocus 同思路）
        /// </summary>
        private static void ReleaseUiFocus()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem != null)
                eventSystem.SetSelectedGameObject(null);
        }

        private static void HideFakePanels(ADOFAI.InspectorPanel? panel)
        {
            if (panel == null) return;
            if (panel.panelsList != null)
                foreach (var p in panel.panelsList)
                    if (p != null)
                        p.gameObject.SetActive(false);
            // The original ShowPanel is skipped when we intercept, so its
            // title/message cleanup never runs; clear them here too.
            var titleCanvas = HarmonyLib.AccessTools.Field(typeof(InspectorPanel), "titleCanvas")?.GetValue(panel) as UnityEngine.GameObject;
            titleCanvas?.SetActive(false);
            var msgCanvas = HarmonyLib.AccessTools.Field(typeof(InspectorPanel), "messageCanvas")?.GetValue(panel) as UnityEngine.GameObject;
            msgCanvas?.SetActive(false);
        }

        /// <summary>
        /// Build a fake LevelEventInfo whose propertiesInfo mirrors every key found
        /// in the event data, so LevelEvent.Encode round-trips the event untouched.
        /// </summary>
        private static LevelEventInfo BuildFakeInfo(string eventName, Dictionary<string, object> eventDict)
        {
            if (FakeInfos.TryGetValue(eventName, out var cached)) return cached;

            var info = new LevelEventInfo
            {
                name = eventName,
                type = LevelEventType.None,
                propertiesInfo = new Dictionary<string, ADOFAI.PropertyInfo>(),
                categories = new List<LevelEventCategory>(),
                executionTime = LevelEventExecutionTime.OnBar,
                allowFirstFloor = false,
                isDecoration = false,
                useGroups = false,
                stretchViewport = false,
            };

            foreach (var kv in eventDict)
            {
                switch (kv.Key)
                {
                    case "eventType":
                    case "floor":
                    case "active":
                    case "visible":
                    case "locked":
                        continue;
                }
                var propDict = new Dictionary<string, object>
                {
                    ["name"] = kv.Key,
                    ["type"] = "String",
                };
                info.propertiesInfo[kv.Key] = new ADOFAI.PropertyInfo(propDict, info);
            }

            FakeInfos[eventName] = info;
            FakeEventNames.Add(eventName);
            return info;
        }

        /// <summary>
        /// 为未知事件类型准备（必要时注册）一个只读 fake LevelEventInfo。
        /// v3 谱面兼容的兜底路径（事件解码失败 / 未知事件漏注册）会用到。
        /// </summary>
        internal static LevelEventInfo? EnsureFakeInfo(string? eventName, Dictionary<string, object>? eventDict)
        {
            if (string.IsNullOrEmpty(eventName) || GCS.levelEventsInfo == null) return null;
            if (GCS.levelEventsInfo.TryGetValue(eventName!, out var existing) && existing != null)
                return existing;

            var info = BuildFakeInfo(eventName!, eventDict ?? new Dictionary<string, object>());
            GCS.levelEventsInfo[eventName!] = info;
            return info;
        }

        private static void ClearFakeEvents()
        {
            if (GCS.levelEventsInfo != null)
                foreach (var name in FakeEventNames)
                    GCS.levelEventsInfo.Remove(name);
            FakeEventNames.Clear();
            FakeInfos.Clear();
        }

        /// <summary>设置界面切换「v3 谱面兼容 / 忽略第三方Mod」后调用，重挂本类补丁。</summary>
        public static void UpdatePatches()
        {
            try
            {
                foreach (var type in typeof(CustomEventsPatches).GetNestedTypes(
                             System.Reflection.BindingFlags.Public
                             | System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.Static))
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                        AsyncPatchManager.UpdatePatchByTypeAsync(type);
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[CustomEvents] UpdatePatches failed: {e}");
            }
        }

        private static void ScanAndRegister(Dictionary<string, object>? dict)
        {
            if (dict == null || GCS.levelEventsInfo == null) return;
            if (!Main.Settings.compatibility.ignoreRequiredMods
                && !V3LevelCompatPatches.IsDecodingV3Chart(dict)) return;

            ScanEventArray(dict, "actions");
            ScanEventArray(dict, "decorations");
        }

        private static void ScanEventArray(Dictionary<string, object> dict, string arrayKey)
        {
            if (!dict.TryGetValue(arrayKey, out var raw) || raw is not List<object> list) return;

            foreach (var item in list)
            {
                if (item is not Dictionary<string, object> ev) continue;
                if (!ev.TryGetValue("eventType", out var et) || et is not string name) continue;
                if (name.Length == 0) continue;

                if (!GCS.levelEventsInfo.TryGetValue(name, out var existing))
                {
                    GCS.levelEventsInfo[name] = BuildFakeInfo(name, ev);
                    continue;
                }

                // Same custom type may appear with different key sets; merge new
                // keys into the cached fake info so every instance round-trips.
                if (IsFakeEventName(name)) MergeFakeInfoKeys(existing, ev);
            }
        }

        private static void MergeFakeInfoKeys(LevelEventInfo info, Dictionary<string, object> ev)
        {
            if (info.propertiesInfo == null) return;
            foreach (var kv in ev)
            {
                switch (kv.Key)
                {
                    case "eventType":
                    case "floor":
                    case "active":
                    case "visible":
                    case "locked":
                        continue;
                }
                if (info.propertiesInfo.ContainsKey(kv.Key)) continue;
                var propDict = new Dictionary<string, object> { ["name"] = kv.Key, ["type"] = "String" };
                info.propertiesInfo[kv.Key] = new ADOFAI.PropertyInfo(propDict, info);
            }
        }

        /// <summary>
        /// Before a chart decodes: drop previously registered fake events and
        /// register fake infos for every unknown event type in this chart.
        /// </summary>
        [HarmonyPatch(typeof(LevelData), nameof(LevelData.Decode))]
        public static class ScanRegisterPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Dictionary<string, object> dict)
            {
                try
                {
                    ClearFakeEvents();
                    ScanAndRegister(dict);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] scan error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Same as ScanRegisterPatch but for the level select path
        /// (LevelDataCLS.Decode is used by scnCLS, pause, practice and portals).
        /// </summary>
        [HarmonyPatch(typeof(LevelDataCLS), nameof(LevelDataCLS.Decode))]
        public static class ScanRegisterCLSPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Dictionary<string, object> rootDict)
            {
                try
                {
                    ClearFakeEvents();
                    ScanAndRegister(rootDict);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] CLS scan error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Decode fake events manually so info/data are fully populated and the
        /// chart loads without crashing on levelEvent.info.taroDLCCheck.
        /// </summary>
        [HarmonyPatch(typeof(LevelEvent), nameof(LevelEvent.Decode))]
        public static class FakeEventDecodePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(LevelEvent __instance, Dictionary<string, object> dict, ref LevelEvent.DecodeResult __result)
            {
                if (dict == null || !dict.TryGetValue("eventType", out var et) || et is not string name)
                    return true;
                if (!IsFakeEventName(name)) return true;

                try
                {
                    var info = FakeInfos[name];
                    __instance.eventType = LevelEventType.None;
                    __instance.info = info;

                    if (dict.TryGetValue("floor", out var floorVal))
                        __instance.floor = Convert.ToInt32(floorVal);
                    else
                        __instance.floor = -1;

                    __instance.active = !dict.TryGetValue("active", out var activeVal) || activeVal is not bool activeFlag || activeFlag;
                    __instance.visible = !dict.TryGetValue("visible", out var visibleVal) || visibleVal is not bool visibleFlag || visibleFlag;
                    __instance.locked = dict.TryGetValue("locked", out var lockedVal) && lockedVal is bool lockedFlag && lockedFlag;

                    __instance.data = new Dictionary<string, object>();
                    __instance.disabled = new Dictionary<string, bool>();

                    foreach (var kv in info.propertiesInfo)
                    {
                        if (dict.ContainsKey(kv.Key))
                        {
                            __instance.data[kv.Key] = dict[kv.Key];
                            __instance.disabled[kv.Key] = false;
                        }
                        else
                        {
                            __instance.data[kv.Key] = kv.Value.value_default;
                            __instance.disabled[kv.Key] = true;
                        }
                    }

                    __result = LevelEvent.DecodeResult.Success;
                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] decode error: {e.Message}");
                    return true;
                }
            }
        }

        /// <summary>
        /// Encode fake (custom / unknown) events by hand.
        ///
        /// The 2.9.8 Encode() builds its output from info.propertiesInfo (whose
        /// mirrored keys are all typed as String), casts every value to string and
        /// would both crash on non-string values and write eventType "None" —
        /// so fake events could never round-trip. Emit the original eventType
        /// name plus each stored raw value instead.
        /// </summary>
        [HarmonyPatch(typeof(LevelEvent), nameof(LevelEvent.Encode))]
        public static class FakeEventEncodePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(LevelEvent __instance, bool settings, ref string __result)
            {
                if (settings || __instance == null || !IsFakeInfo(__instance.info)) return true;
                try
                {
                    __result = EncodeFakeEvent(__instance);
                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] encode error: {e.Message}");
                    return true;
                }
            }

            private static string EncodeFakeEvent(LevelEvent ev)
            {
                var sb = new StringBuilder(256);
                bool first = true;

                void Field(string key, string rawJson)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append('"').Append(key).Append("\": ").Append(rawJson);
                }

                if (ev.floor != -1) Field("floor", ev.floor.ToString());
                Field("eventType", Json.Serialize(ev.info.name));
                if (!ev.active) Field("active", "false");
                if (!ev.visible) Field("visible", "false");
                if (ev.locked) Field("locked", "true");

                if (ev.data != null)
                {
                    foreach (var kv in ev.data)
                    {
                        switch (kv.Key)
                        {
                            case "floor":
                            case "eventType":
                            case "active":
                            case "visible":
                            case "locked":
                                continue;
                        }
                        // Keys absent from the original JSON decode as disabled
                        // defaults; keep them out of the encoded output.
                        if (ev.disabled != null && ev.disabled.TryGetValue(kv.Key, out bool disabled) && disabled)
                            continue;
                        Field(kv.Key, Json.Serialize(kv.Value));
                    }
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// The fake event's property panel is read-only: disable every control.
        /// </summary>
        [HarmonyPatch(typeof(PropertiesPanel), nameof(PropertiesPanel.Init))]
        public static class ReadOnlyPanelPatch
        {
            [HarmonyPostfix]
            public static void Postfix(PropertiesPanel __instance, LevelEventInfo levelEventInfo)
            {
                if (!IsFakeInfo(levelEventInfo)) return;
                try
                {
                    foreach (var ps in __instance.propertySelectables)
                        ps.control.SetEnabled(false);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] panel disable error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Event list items: fake events show their original name instead of "None"
        /// and a generic icon instead of the Flash/fallback icon.
        /// </summary>
        [HarmonyPatch(typeof(ListItem_Event), nameof(ListItem_Event.SetEvent))]
        public static class ListItemEventPatch
        {
            private static UnityEngine.Sprite _fakeIcon;
            private static readonly System.Reflection.MethodInfo BaseSetEvent = HarmonyLib.AccessTools.Method(typeof(ListItem), nameof(ListItem.SetEvent));
            private static readonly System.Reflection.FieldInfo ItemNameField = HarmonyLib.AccessTools.Field(typeof(ListItem), "itemName");

            [HarmonyPrefix]
            public static bool Prefix(ListItem_Event __instance, LevelEvent ev)
            {
                if (ev == null || !IsFakeInfo(ev.info)) return true;
                try
                {
                    BaseSetEvent.Invoke(__instance, new object[] { ev });

                    if (_fakeIcon == null)
                        _fakeIcon = UnityEngine.Resources.Load<UnityEngine.Sprite>("LevelEditor/LevelEvents/DefaultNullDecoration");

                    if (ItemNameField?.GetValue(__instance) is object nameText)
                    {
                        var textProp = nameText.GetType().GetProperty("text");
                        textProp?.SetValue(nameText, ev.info.name);
                    }
                    __instance.transform.name = ev.info.name;
                    if (_fakeIcon != null)
                        __instance.itemTypeImage.sprite = _fakeIcon;
                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] list item error: {e.Message}");
                    return true;
                }
            }
        }

        /// <summary>
        /// Floor event indicators: fake events use the generic icon instead of
        /// levelEventIcons[None] (which would crash or show a wrong sprite).
        /// </summary>
        [HarmonyPatch(typeof(EventIndicator), nameof(EventIndicator.Init))]
        public static class EventIndicatorPatch
        {
            private static UnityEngine.Sprite _fakeIcon;

            [HarmonyPrefix]
            public static bool Prefix(EventIndicator __instance, LevelEvent baseEvent)
            {
                if (baseEvent == null || !IsFakeInfo(baseEvent.info)) return true;
                try
                {
                    if (_fakeIcon == null)
                        _fakeIcon = UnityEngine.Resources.Load<UnityEngine.Sprite>("LevelEditor/LevelEvents/DefaultNullDecoration");
                    if (_fakeIcon != null)
                        __instance.icon.sprite = _fakeIcon;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] indicator error: {e.Message}");
                }
                return true;
            }
        }

        /// <summary>
        /// Hook the inspector's ShowPanel: when a fake event is selected, render its
        /// own property panel with the original event name and the read-only notice.
        /// </summary>
        [HarmonyPatch(typeof(InspectorPanel), nameof(InspectorPanel.ShowPanel))]
        public static class ShowPanelFakeEventPatch
        {
            private static readonly System.Reflection.FieldInfo TitleField =
                HarmonyLib.AccessTools.Field(typeof(InspectorPanel), "title");
            private static readonly System.Reflection.FieldInfo TitleCanvasField =
                HarmonyLib.AccessTools.Field(typeof(InspectorPanel), "titleCanvas");
            private static readonly System.Reflection.FieldInfo MsgCanvasField =
                HarmonyLib.AccessTools.Field(typeof(InspectorPanel), "messageCanvas");
            private static readonly System.Reflection.FieldInfo MsgTextField =
                HarmonyLib.AccessTools.Field(typeof(InspectorPanel), "messageText");

            [HarmonyPrefix]
            public static bool Prefix(InspectorPanel __instance, LevelEventType eventType, int eventIndex)
            {
                if (eventType != LevelEventType.None) return true;
                try
                {
                    // Switching to a floor without events clears the selection first;
                    // GetSelectedFloorEvents would then crash on selectedFloors[0].
                    if (ADOBase.editor == null || ADOBase.editor.selectedFloors == null || ADOBase.editor.selectedFloors.Count == 0)
                    {
                        HideFakePanels(__instance);
                        return false;
                    }
                    var floorEvents = ADOBase.editor.GetSelectedFloorEvents(eventType);
                    if (floorEvents == null || eventIndex < 0 || eventIndex >= floorEvents.Count || floorEvents[eventIndex] == null || !IsFakeInfo(floorEvents[eventIndex].info))
                    {
                        // No fake event to show: hide every fake panel so the original
                        // ShowPanel(None) logic cannot re-activate them (panel leakage).
                        HideFakePanels(__instance);
                        return false;
                    }
                    var ev = floorEvents[eventIndex];

                    PropertiesPanel panel = null;
                    foreach (var p in __instance.panelsList)
                    {
                        if (p != null && p.gameObject.name == ev.info.name)
                        {
                            panel = p;
                            p.gameObject.SetActive(true);
                        }
                        else if (p != null)
                        {
                            p.gameObject.SetActive(false);
                        }
                    }
                    if (panel == null)
                    {
                        var go = UnityEngine.Object.Instantiate(ADOBase.gc.prefab_propertiesPanel);
                        go.transform.SetParent(__instance.panels, false);
                        go.name = ev.info.name;
                        panel = go.GetComponent<PropertiesPanel>();
                        panel.levelEventType = LevelEventType.None;
                        panel.Init(__instance, ev.info);
                        __instance.panelsList.Add(panel);
                    }
                    panel.gameObject.SetActive(true);

                    if (TitleCanvasField?.GetValue(__instance) is UnityEngine.GameObject titleCanvas)
                        titleCanvas.SetActive(true);
                    if (TitleField?.GetValue(__instance) is object title)
                        title.GetType().GetProperty("text")?.SetValue(title, ev.info.name);
                    panel.SetProperties(ev);
                    foreach (var ps in panel.propertySelectables)
                        ps.control.SetEnabled(false);

                    __instance.selectedEvent = ev;
                    __instance.selectedEventType = LevelEventType.None;

                    var fakeTab = EnsureFakeTab(__instance, ev.info.name);
                    fakeTab.gameObject.SetActive(true);
                    fakeTab.eventIndex = eventIndex;
                    // Sync selection of every tab (the original ShowPanel tab loop
                    // is skipped by this prefix, so official tabs would keep focus).
                    foreach (UnityEngine.Transform t in __instance.tabs)
                    {
                        var tab = t.GetComponent<ADOFAI.InspectorTab>();
                        if (tab == null) continue;
                        if (tab.levelEventType == LevelEventType.None)
                            tab.SetSelected(tab.button.name == ev.info.name);
                        else
                            tab.SetSelected(false);
                    }

                    if (MsgCanvasField?.GetValue(__instance) is UnityEngine.GameObject msgCanvas &&
                        MsgTextField?.GetValue(__instance) is object msgText)
                    {
                        msgCanvas.SetActive(true);
                        msgText.GetType().GetProperty("text")?.SetValue(msgText, Localization.Get("CustomEventReadOnlyNotice"));
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] ShowPanel hook error: {e.Message}");
                    return true;
                }
            }
        }


        /// <summary>
        /// Find or create the inspector tab for fake (third-party) events. The
        /// tab is levelEventType.None with the DefaultNullDecoration icon; its
        /// click lands in ShowPanel(None, eventIndex), which ShowPanelFakeEventPatch
        /// routes to the correct fake event.
        /// </summary>
        private static ADOFAI.InspectorTab EnsureFakeTab(ADOFAI.InspectorPanel panel, string eventName)
        {
            foreach (UnityEngine.Transform t in panel.tabs)
            {
                var tab = t.GetComponent<ADOFAI.InspectorTab>();
                if (tab != null && tab.levelEventType == LevelEventType.None && tab.button.name == eventName)
                    return tab;
            }
            var go = UnityEngine.Object.Instantiate(ADOBase.gc.prefab_tab);
            go.name = "Fake:" + eventName;
            go.transform.SetParent(panel.tabs, false);
            go.transform.SetAsLastSibling();
            var fakeTab = go.GetComponent<ADOFAI.InspectorTab>();
            fakeTab.panel = panel;
            fakeTab.levelEventType = LevelEventType.None;
            fakeTab.icon.sprite = UnityEngine.Resources.Load<UnityEngine.Sprite>("LevelEditor/LevelEvents/DefaultNullDecoration");
            fakeTab.button.name = eventName;
            fakeTab.GetComponent<UnityEngine.RectTransform>().AnchorPosY(-68f * panel.tabs.childCount);
            // Official event tabs are flipped (FlipTab: ScaleX(-1)); mirror that so
            // the tab background/icon face the same way.
            fakeTab.button.transform.localScale = new UnityEngine.Vector3(-1f, 1f, 1f);
            fakeTab.icon.transform.localScale = new UnityEngine.Vector3(-1f, 1f, 1f);
            if (fakeTab.cycleButtons != null)
                fakeTab.cycleButtons.transform.localScale = new UnityEngine.Vector3(-1f, 1f, 1f);
            fakeTab.SetSelected(false);
            return fakeTab;
        }

        /// <summary>
        /// ShowTabsForFloor only toggles tabs created at Init time, so fake events
        /// never get a tab and cannot be switched to while official events exist.
        /// Create/show the fake event tab when the floor has fake events.
        /// </summary>
        [HarmonyPatch(typeof(InspectorPanel), nameof(InspectorPanel.ShowTabsForFloor))]
        public static class ShowTabsForFloorPatch
        {
            [HarmonyPostfix]
            public static void Postfix(InspectorPanel __instance, int floorID)
            {
                try
                {
                    var floorEvents = scnEditor.instance?.GetFloorEvents(floorID, LevelEventType.None);
                    if (floorEvents == null) return;

                    // Distinct fake event names on this floor, with their index in
                    // the floor's None-event list (used as the tab's eventIndex).
                    var order = new List<string>();
                    var indexByName = new Dictionary<string, int>();
                    for (int i = 0; i < floorEvents.Count; i++)
                    {
                        var e = floorEvents[i];
                        if (e != null && IsFakeInfo(e.info) && !indexByName.ContainsKey(e.info.name))
                        {
                            indexByName[e.info.name] = i;
                            order.Add(e.info.name);
                        }
                    }
                    if (order.Count == 0) return;

                    // Hide fake tabs whose event is no longer on this floor.
                    foreach (UnityEngine.Transform t in __instance.tabs)
                    {
                        var tab = t.GetComponent<ADOFAI.InspectorTab>();
                        if (tab != null && tab.levelEventType == LevelEventType.None && !indexByName.ContainsKey(tab.button.name))
                            tab.gameObject.SetActive(false);
                    }

                    foreach (var name in order)
                    {
                        var tab = EnsureFakeTab(__instance, name);
                        tab.gameObject.SetActive(true);
                        tab.eventIndex = indexByName[name];
                        bool selected = __instance.selectedEvent != null &&
                            IsFakeInfo(__instance.selectedEvent.info) &&
                            __instance.selectedEvent.info.name == name;
                        tab.SetSelected(selected);
                    }

                    // The original ShowTabsForFloor repositions every tab by visible
                    // order, but our fake tabs never match its name lookup, so they
                    // end up stacked on the previous tab. Re-pack all visible tabs
                    // (official + fake) exactly like the game does.
                    int visible = 0;
                    foreach (UnityEngine.Transform t in __instance.tabs)
                    {
                        if (!t.gameObject.activeSelf) continue;
                        t.GetComponent<UnityEngine.RectTransform>().SetAnchorPosY(-68f * visible);
                        visible++;
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] tab error: {e.Message}");
                }
            }
        }


        /// <summary>
        /// Fake event tabs all share levelEventType.None, so the original
        /// SetSelected would show cycleButtons when the floor has >1 fake events
        /// ("ghost counter"). Each fake event has its own tab, so hide it always.
        /// </summary>
        [HarmonyPatch(typeof(InspectorTab), nameof(InspectorTab.SetSelected))]
        public static class FakeTabSetSelectedPatch
        {
            [HarmonyPostfix]
            public static void Postfix(InspectorTab __instance)
            {
                if (__instance.levelEventType != LevelEventType.None) return;
                if (__instance.cycleButtons != null)
                    __instance.cycleButtons.gameObject.SetActive(false);
                // The original SetSelected sized the tab to 120px (cycleButtons
                // space) because it counts all fake events as one type. Shrink it.
                var rt = __instance.GetComponent<UnityEngine.RectTransform>();
                DG.Tweening.DOTween.Kill(rt);
                var size = rt.sizeDelta;
                rt.sizeDelta = new UnityEngine.Vector2(0f, size.y);
            }
        }

        /// <summary>
        /// The original OnPointerClick treats two fake tabs as the same type
        /// (both are None) and toggles the inspector instead of switching events.
        /// Route fake tab clicks directly to ShowPanel(None, eventIndex).
        /// </summary>
        [HarmonyPatch(typeof(InspectorTab), nameof(InspectorTab.OnPointerClick))]
        public static class FakeTabClickPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(InspectorTab __instance, UnityEngine.EventSystems.PointerEventData eventData)
            {
                if (__instance.levelEventType != LevelEventType.None) return true;
                try
                {
                    if (eventData.button == UnityEngine.EventSystems.PointerEventData.InputButton.Left)
                    {
                        var floorEvents = ADOBase.editor.GetSelectedFloorEvents(LevelEventType.None);
                        int idx = -1;
                        if (floorEvents != null)
                        {
                            for (int i = 0; i < floorEvents.Count; i++)
                                if (floorEvents[i] != null && IsFakeInfo(floorEvents[i].info) && floorEvents[i].info.name == __instance.button.name)
                                { idx = i; break; }
                        }
                        if (idx < 0) return true;
                        ADOBase.editor.DecideInspectorTabsAtSelected();
                        __instance.panel.selectedEventType = LevelEventType.None;
                        __instance.panel.ShowPanel(LevelEventType.None, idx);
                        ReleaseUiFocus();
                        return false;
                    }
                    if (eventData.button == UnityEngine.EventSystems.PointerEventData.InputButton.Right && __instance.panel.floorPanel)
                    {
                        var floorEvents = ADOBase.editor.GetSelectedFloorEvents(LevelEventType.None);
                        if (floorEvents != null && __instance.eventIndex >= 0 && __instance.eventIndex < floorEvents.Count)
                        {
                            var ev = floorEvents[__instance.eventIndex];
                            if (ev != null && IsFakeInfo(ev.info))
                            {
                                ADOBase.editor.RemoveEvent(ev);
                                var remaining = ADOBase.editor.GetSelectedFloorEvents(LevelEventType.None);
                                if (remaining != null && remaining.Count > 0)
                                    __instance.panel.ShowPanel(LevelEventType.None, 0);
                                else
                                    __instance.panel.HideAllInspectorTabs();
                                ADOBase.editor.ApplyEventsToFloors();
                                return false;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] tab click error: {e.Message}");
                    return true;
                }
                return true;
            }
        }

        /// <summary>
        /// scnEditor.RemoveEventAtSelected returns early for LevelEventType.None,
        /// which would make fake events undeletable. Handle the deletion ourselves.
        /// </summary>
        [HarmonyPatch(typeof(scnEditor), "RemoveEventAtSelected")]
        public static class RemoveEventAtSelectedPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance, LevelEventType eventType)
            {
                if (eventType != LevelEventType.None) return true;
                try
                {
                    var selectedEvent = __instance.levelEventsPanel.selectedEvent;
                    if (selectedEvent == null || !IsFakeInfo(selectedEvent.info)) return true;
                    if (__instance.selectedFloors == null || __instance.selectedFloors.Count == 0) return true;

                    __instance.RemoveEvent(selectedEvent);

                    var remaining = __instance.GetSelectedFloorEvents(LevelEventType.None);
                    if (remaining != null && remaining.Count > 0)
                    {
                        // Still have fake events on the floor: force the inspector to
                        // reload and show the next one.
                        __instance.levelEventsPanel.ShowPanel(LevelEventType.None, 0);
                    }
                    else
                    {
                        // All fake events gone: force the inspector to close.
                        __instance.levelEventsPanel.HideAllInspectorTabs();
                    }
                    __instance.ApplyEventsToFloors();
                    __instance.ShowEventIndicators(__instance.selectedFloors[0]);
                    __instance.floorButtonCanvas.transform.position = __instance.selectedFloors[0].transform.position;
                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[CustomEvents] remove error: {e.Message}");
                    return true;
                }
            }
        }
    }
}