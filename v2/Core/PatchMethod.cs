using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Iridium.Core
{
    /// <summary>
    /// 补丁类型标识
    /// </summary>
    [Flags]
    public enum PatchTypes
    {
        NONE = 0,
        Prefix = 1,
        Postfix = 2,
        PP = 3,
    }

    /// <summary>
    /// 泛型桥接类 — 用真实静态方法替代 DynamicMethod，确保 Harmony 2.x 完全兼容
    /// 每个 StdPatchMethod&lt;T, Res&gt; 子类都有唯一的泛型参数，所以静态字段天然隔离
    /// </summary>
    internal static class PatchBridge<T, Res>
    {
        internal static StdPatchMethod<T, Res>? Instance;

        public static bool Prefix(T __instance, object[] __args)
        {
            var inst = Instance;
            if (inst == null) return true;
            inst.instance = __instance;
            return inst.Method(__args);
        }

        public static bool PrefixStatic(object[] __args)
        {
            var inst = Instance;
            if (inst == null) return true;
            return inst.Method(__args);
        }

        public static void Postfix(T __instance, object[] __args, ref Res __result)
        {
            var inst = Instance;
            if (inst == null) return;
            inst.instance = __instance;
            inst.result = __result;
            inst.Method(__args);
            __result = inst.result;
        }

        public static void PostfixStatic(object[] __args, ref Res __result)
        {
            var inst = Instance;
            if (inst == null) return;
            inst.result = __result;
            inst.Method(__args);
            __result = inst.result;
        }

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase originalMethod)
        {
            var inst = Instance;
            if (inst == null) return instructions;
            return inst.IL(instructions, generator, originalMethod);
        }
    }

    /// <summary>
    /// 所有补丁方法的基类
    /// </summary>
    public abstract class BasePatchMethod
    {
        public static List<BasePatchMethod> _methods = new(16);

        public BasePatchMethod(PatchTypes t)
        {
            type = t;
            il = Main.Settings?.patchMode.useILPatch ?? false;
            lock (_methods)
            {
                _methods.Add(this);
                id = _methods.Count - 1;
            }
        }

        public readonly PatchTypes type;
        protected internal readonly int id;
        public volatile bool il;
        internal MethodInfo? patchedResult;

        public abstract MethodBase GetTargetMethod();
        public abstract void StartPatch();
        public abstract void StopPatch();

        public void SetILMode(bool useIL)
        {
            if (useIL == il) return;
            il = useIL;
            StopPatch();
            StartPatch();
        }

        public bool IsPatched => patchedResult != null;

        public static void SyncILModeFromSettings()
        {
            bool useIL = Main.Settings?.patchMode.useILPatch ?? false;
            lock (_methods)
            {
                foreach (var m in _methods)
                {
                    if (m.IsPatched && m.il != useIL)
                        m.SetILMode(useIL);
                    else
                        m.il = useIL;
                }
            }
        }

        internal virtual void ForceReset()
        {
            patchedResult = null;
        }
    }

    /// <summary>
    /// 标准补丁方法抽象类
    /// T: 方法所属类型（静态方法用 object）
    /// Res: 方法返回值类型（void 用 object）
    /// 只需实现 Method(object[])，自动支持 Prefix/Postfix 和 Transpiler 两种模式
    /// </summary>
    public abstract class StdPatchMethod<T, Res> : BasePatchMethod
    {
        public StdPatchMethod(PatchTypes t) : base(t) { }

        public Res? result;
        public T? instance;

        public abstract bool Method(object[] args);

        /// <summary>
        /// Transpiler 实现 — 在 IL 层面注入 Method() 调用
        /// </summary>
        protected internal IEnumerable<CodeInstruction> IL(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase originalMethod)
        {
            if (type == 0 || (type & PatchTypes.PP) == 0)
                throw new Exception("Patch type must be Prefix, Postfix, or PP");

            var instrList = instructions.ToList();
            var parameters = originalMethod.GetParameters();
            int paramCount = parameters.Length;
            bool isStatic = originalMethod.IsStatic;

            bool hasPrefix = (type & PatchTypes.Prefix) != 0;
            bool hasPostfix = (type & PatchTypes.Postfix) != 0;

            if (hasPrefix)
            {
                if (!isStatic)
                {
                    foreach (var ci in EmitStoreInstance(OpCodes.Ldarg_0))
                        yield return ci;
                }

                var argsLocal = generator.DeclareLocal(typeof(object[]));
                foreach (var ci in EmitBuildArgs(argsLocal, parameters, isStatic))
                    yield return ci;

                var skipLabel = generator.DefineLabel();
                foreach (var ci in EmitCallMethod(argsLocal))
                    yield return ci;
                yield return new CodeInstruction(OpCodes.Brtrue_S, skipLabel);
                yield return new CodeInstruction(OpCodes.Ret);
                yield return new CodeInstruction(OpCodes.Nop).WithLabels(skipLabel);
            }

            foreach (CodeInstruction ci in instrList)
            {
                if (hasPostfix && ci.opcode == OpCodes.Ret)
                {
                    if (!isStatic)
                    {
                        foreach (var ci2 in EmitStoreInstance(OpCodes.Ldarg_0))
                            yield return ci2;
                    }

                    var postArgsLocal = generator.DeclareLocal(typeof(object[]));
                    foreach (var ci2 in EmitBuildArgs(postArgsLocal, parameters, isStatic))
                        yield return ci2;

                    var postSkip = generator.DefineLabel();
                    foreach (var ci2 in EmitCallMethod(postArgsLocal))
                        yield return ci2;
                    yield return new CodeInstruction(OpCodes.Brtrue_S, postSkip);
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Ret);
                    ci.WithLabels(postSkip);
                }

                yield return ci;
            }

            yield break;

            IEnumerable<CodeInstruction> EmitStoreInstance(OpCode loadValue)
            {
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    typeof(BasePatchMethod).GetField(nameof(_methods))!);
                yield return new CodeInstruction(OpCodes.Ldc_I4, id);
                yield return new CodeInstruction(OpCodes.Call,
                    typeof(List<BasePatchMethod>).GetMethod("get_Item")!);
                yield return new CodeInstruction(OpCodes.Castclass, typeof(StdPatchMethod<T, Res>));
                yield return new CodeInstruction(loadValue);
                yield return new CodeInstruction(OpCodes.Stfld,
                    typeof(StdPatchMethod<T, Res>).GetField(nameof(instance))!);
            }

            IEnumerable<CodeInstruction> EmitBuildArgs(LocalBuilder argsLocal, ParameterInfo[] parms, bool staticMethod)
            {
                yield return new CodeInstruction(OpCodes.Ldc_I4, paramCount);
                yield return new CodeInstruction(OpCodes.Newarr, typeof(object));
                yield return new CodeInstruction(OpCodes.Stloc, argsLocal.LocalIndex);

                for (int i = 0; i < paramCount; i++)
                {
                    yield return new CodeInstruction(OpCodes.Ldloc, argsLocal.LocalIndex);
                    yield return new CodeInstruction(OpCodes.Ldc_I4, i);
                    yield return new CodeInstruction(OpCodes.Ldarg,
                        i + (staticMethod ? 0 : 1));
                    if (parms[i].ParameterType.IsValueType)
                        yield return new CodeInstruction(OpCodes.Box, parms[i].ParameterType);
                    yield return new CodeInstruction(OpCodes.Stelem_Ref);
                }
            }

            IEnumerable<CodeInstruction> EmitCallMethod(LocalBuilder argsLocal)
            {
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    typeof(BasePatchMethod).GetField(nameof(_methods))!);
                yield return new CodeInstruction(OpCodes.Ldc_I4, id);
                yield return new CodeInstruction(OpCodes.Call,
                    typeof(List<BasePatchMethod>).GetMethod("get_Item")!);
                yield return new CodeInstruction(OpCodes.Castclass, typeof(StdPatchMethod<T, Res>));
                yield return new CodeInstruction(OpCodes.Ldloc, argsLocal.LocalIndex);
                yield return new CodeInstruction(OpCodes.Callvirt,
                    typeof(StdPatchMethod<T, Res>).GetMethod(nameof(Method),
                        BindingFlags.Instance | BindingFlags.Public)!);
            }
        }

        // PatchBridge<T, Res> 的真实静态方法取代 DynamicMethod，确保 Harmony 2.x 完全兼容

        private HarmonyMethod? _hmPrefix;
        private HarmonyMethod? _hmPostfix;
        private HarmonyMethod? _hmTranspiler;

        public override void StartPatch()
        {
            var target = GetTargetMethod();
            if (target == null)
            {
                Main.Logger?.Error($"[PatchMethod] GetTargetMethod() returned null for {GetType().Name}");
                return;
            }

            var harmony = Main.Harmony;
            if (harmony == null)
            {
                Main.Logger?.Error("[PatchMethod] Main.Harmony is null");
                return;
            }

            PatchBridge<T, Res>.Instance = this;

            if (il)
            {
                _hmTranspiler = new HarmonyMethod(typeof(PatchBridge<T, Res>),
                    nameof(PatchBridge<T, Res>.Transpiler));
                harmony.Patch(target, transpiler: _hmTranspiler);
                patchedResult = _hmTranspiler.method;
                Main.Logger?.Log($"[PatchMethod] {GetType().Name}[{id}] patched as Transpiler");
            }
            else
            {
                bool isStatic = target.IsStatic;

                if ((type & PatchTypes.Prefix) != 0)
                {
                    var methodName = isStatic
                        ? nameof(PatchBridge<T, Res>.PrefixStatic)
                        : nameof(PatchBridge<T, Res>.Prefix);
                    _hmPrefix = new HarmonyMethod(typeof(PatchBridge<T, Res>), methodName);
                }

                if ((type & PatchTypes.Postfix) != 0)
                {
                    var methodName = isStatic
                        ? nameof(PatchBridge<T, Res>.PostfixStatic)
                        : nameof(PatchBridge<T, Res>.Postfix);
                    _hmPostfix = new HarmonyMethod(typeof(PatchBridge<T, Res>), methodName);
                }

                harmony.Patch(target, prefix: _hmPrefix, postfix: _hmPostfix);
                patchedResult = _hmPrefix?.method ?? _hmPostfix?.method;
                Main.Logger?.Log($"[PatchMethod] {GetType().Name}[{id}] patched as Prefix/Postfix");
            }
        }

        public override void StopPatch()
        {
            var target = GetTargetMethod();
            if (target == null) return;

            var harmony = Main.Harmony;
            if (harmony == null) return;

            if (_hmPrefix != null)
            {
                harmony.Unpatch(target, _hmPrefix.method);
                _hmPrefix = null;
            }

            if (_hmPostfix != null)
            {
                harmony.Unpatch(target, _hmPostfix.method);
                _hmPostfix = null;
            }

            if (_hmTranspiler != null)
            {
                harmony.Unpatch(target, _hmTranspiler.method);
                _hmTranspiler = null;
            }

            patchedResult = null;
            PatchBridge<T, Res>.Instance = null;
            Main.Logger?.Log($"[PatchMethod] {GetType().Name}[{id}] unpatched");
        }

        internal override void ForceReset()
        {
            base.ForceReset();
            _hmPrefix = null;
            _hmPostfix = null;
            _hmTranspiler = null;
            PatchBridge<T, Res>.Instance = null;
        }
    }
}
