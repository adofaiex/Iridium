using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

namespace Iridium.Runtime
{
    /// <summary>
    /// A Mono patch that can expose both a compatibility implementation and an
    /// IL implementation. The backend chooses one mode without changing the
    /// patch registration or its lifetime.
    /// </summary>
    public abstract class MonoAdaptivePatch : IPatchDefinition
    {
        private MethodBase? _target;
        private readonly List<MethodInfo> _appliedMethods = new();

        public abstract string Id { get; }
        public virtual RuntimeKind[] SupportedRuntimes => new[] { RuntimeKind.Mono };

        protected abstract MethodBase? GetTargetMethod();
        protected virtual MethodInfo? Prefix => null;
        protected virtual MethodInfo? Postfix => null;
        protected virtual MethodInfo? Transpiler => null;

        internal PatchResult Apply(Harmony harmony, bool useTranspiler)
        {
            _target = GetTargetMethod();
            if (_target == null)
                return PatchResult.NotFound($"Target not found for {GetType().Name}.");

            // IL 模式下没有 Transpiler 实现的补丁回退到 Prefix/Postfix，
            // 保证功能在两种补丁模式下都生效（如纹理压缩的尺寸补偿组）。
            if (useTranspiler && Transpiler != null)
            {
                harmony.Patch(_target, transpiler: new HarmonyMethod(Transpiler));
                _appliedMethods.Add(Transpiler);
            }
            else
            {
                if (Prefix == null && Postfix == null)
                    return PatchResult.Failed($"{GetType().Name} has no Prefix/Postfix implementation.");

                var prefix = Prefix == null ? null : new HarmonyMethod(Prefix);
                var postfix = Postfix == null ? null : new HarmonyMethod(Postfix);
                harmony.Patch(_target, prefix: prefix, postfix: postfix);
                if (Prefix != null) _appliedMethods.Add(Prefix);
                if (Postfix != null) _appliedMethods.Add(Postfix);
            }

            PatchExceptionGuard.Attach(harmony, _target);
            return PatchResult.Applied(GetType().Name);
        }

        internal PatchResult Remove(Harmony harmony)
        {
            if (_target == null)
                return PatchResult.Removed(GetType().Name);

            foreach (var method in _appliedMethods)
                harmony.Unpatch(_target, method);

            _appliedMethods.Clear();
            _target = null;
            return PatchResult.Removed(GetType().Name);
        }
    }
}
