using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ADOFAI;
using HarmonyLib;

namespace Iridium.Patches
{
    /// <summary>
    /// 大谱面加载优化：削减解码 / 事件分组里的重复扩容分配（v2 / 2.9.8）。
    ///
    ///  - LevelEvent.Decode 每个事件都会 new Dictionary&lt;string,object&gt;() 与
    ///    Dictionary&lt;string,bool&gt;()，默认容量 0，插入属性时要多次扩容+数组复制。
    ///    这里按「该事件在 JSON 里的真实键数」自适应预分配（Prefix 记录 dict.Count），
    ///    既省掉扩容，又不对小事件浪费容量（固定 32 槽会让几十万事件白占几百 MB）。
    ///  - scnGame.ApplyEventsToFloors 给每块砖 new List&lt;LevelEvent&gt;()，首次 Add 才分配数组；
    ///    预分配 4 个位置，省掉每块砖一次小数组分配。
    ///
    /// 与「大谱面加载优化」开关挂钩，不依赖优化器总开关。
    /// </summary>
    public static class DecodeAllocationPatch
    {
        private static readonly ConstructorInfo? DictObjCtor =
            AccessTools.Constructor(typeof(Dictionary<string, object>), Type.EmptyTypes);
        private static readonly ConstructorInfo? DictBoolCtor =
            AccessTools.Constructor(typeof(Dictionary<string, bool>), Type.EmptyTypes);

        /// <summary>当前正在解码的事件 JSON 键数（Prefix 写入，transpiler 生成的工厂读取）。</summary>
        private static int _currentKeyCount;

        /// <summary>data 字典工厂：容量按事件真实键数取，避免固定大容量浪费。</summary>
        public static Dictionary<string, object> NewDataDict()
            => new Dictionary<string, object>(Math.Max(4, _currentKeyCount));

        /// <summary>disabled 字典工厂（键集合与 data 基本一致）。</summary>
        public static Dictionary<string, bool> NewDisabledDict()
            => new Dictionary<string, bool>(Math.Max(4, _currentKeyCount));

        private static bool SameClosedCtor(ConstructorInfo? candidate, ConstructorInfo? target)
        {
            if (candidate == null || target == null) return false;
            if (ReferenceEquals(candidate, target)) return true;
            try
            {
                if (candidate.Module != target.Module || candidate.MetadataToken != target.MetadataToken)
                    return false;

                var cd = candidate.DeclaringType;
                var td = target.DeclaringType;
                bool candGeneric = cd != null && cd.IsGenericType;
                bool targetGeneric = td != null && td.IsGenericType;
                if (candGeneric != targetGeneric) return false;
                if (!candGeneric) return true;

                var ca = cd!.GetGenericArguments();
                var ta = td!.GetGenericArguments();
                if (ca.Length != ta.Length) return false;
                for (int i = 0; i < ca.Length; i++)
                    if (ca[i].FullName != ta[i].FullName) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        [HarmonyPatch(typeof(LevelEvent), nameof(LevelEvent.Decode))]
        public static class DecodeDictionaryCapacityPatch
        {
            /// <summary>记录本次事件的 JSON 键数，供工厂方法决定字典容量。</summary>
            [HarmonyPrefix]
            public static void Prefix(Dictionary<string, object> __0)
            {
                _currentKeyCount = __0 != null ? __0.Count : 0;
            }

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var dataFactory = AccessTools.Method(typeof(DecodeAllocationPatch), nameof(NewDataDict));
                var disabledFactory = AccessTools.Method(typeof(DecodeAllocationPatch), nameof(NewDisabledDict));
                foreach (var ci in instructions)
                {
                    if (ci.opcode == OpCodes.Newobj && ci.operand is ConstructorInfo ctor)
                    {
                        if (SameClosedCtor(ctor, DictObjCtor))
                        {
                            ci.opcode = OpCodes.Call;
                            ci.operand = dataFactory;
                        }
                        else if (SameClosedCtor(ctor, DictBoolCtor))
                        {
                            ci.opcode = OpCodes.Call;
                            ci.operand = disabledFactory;
                        }
                    }
                    yield return ci;
                }
            }
        }

        private static readonly ConstructorInfo? ListCtor =
            AccessTools.Constructor(typeof(List<LevelEvent>), Type.EmptyTypes);
        private static readonly ConstructorInfo? ListCapCtor =
            AccessTools.Constructor(typeof(List<LevelEvent>), new[] { typeof(int) });

        [HarmonyPatch(typeof(scnGame), nameof(scnGame.ApplyEventsToFloors),
            new[] { typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>) })]
        public static class EventListCapacityPatch
        {
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var ci in instructions)
                {
                    if (ci.opcode == OpCodes.Newobj && ci.operand is ConstructorInfo ctor
                        && SameClosedCtor(ctor, ListCtor))
                    {
                        yield return new CodeInstruction(OpCodes.Ldc_I4_4);
                        ci.operand = ListCapCtor;
                    }
                    yield return ci;
                }
            }
        }
    }
}
