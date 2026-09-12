using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Iridium.Runtime
{
    /// <summary>
    /// Mono implementation. Harmony-specific patch application is isolated
    /// here so an IL2CPP runtime can provide a different backend.
    /// </summary>
    public sealed class MonoHarmonyBackend : IPatchBackend
    {
        private readonly Dictionary<string, List<(MethodBase Original, MethodInfo PatchMethod)>> _patchedBindings = new();
        private readonly Dictionary<string, MonoAdaptivePatch> _adaptivePatches = new();

        public RuntimeKind Runtime => RuntimeKind.Mono;
        public bool IsReady => Harmony != null;
        public Harmony? Harmony { get; private set; }
        private bool _useTranspiler;

        public void Initialize(string ownerId)
        {
            Harmony ??= new Harmony(ownerId);
        }

        public void SetPerformanceMode(bool useTranspiler)
        {
            _useTranspiler = useTranspiler;
        }

        public PatchResult Apply(string patchId, object definition)
        {
            if (Harmony == null)
                return PatchResult.Failed($"Mono Harmony backend is not initialized for '{patchId}'.");

            if (!(definition is Type type))
            {
                if (definition is MonoAdaptivePatch adaptive)
                {
                    try
                    {
                        var result = adaptive.Apply(Harmony, _useTranspiler);
                        if (result.Succeeded)
                            _adaptivePatches[patchId] = adaptive;
                        return result;
                    }
                    catch (Exception error)
                    {
                        return PatchResult.Failed(error.Message);
                    }
                }

                return PatchResult.Failed($"Mono patch '{patchId}' did not provide a supported definition.");
            }

            try
            {
                var originals = Harmony.CreateClassProcessor(type).Patch();
                if (originals == null || originals.Count == 0)
                    return PatchResult.NotFound(type.Name);

                // Safety net: a compiled-against-old-API patch may throw at
                // runtime (e.g. MissingMethodException after a game update).
                // The finalizer keeps such exceptions from breaking the game.
                foreach (var patchedOriginal in originals)
                    PatchExceptionGuard.Attach(Harmony, patchedOriginal);

                var bindings = new List<(MethodBase Original, MethodInfo PatchMethod)>();
                foreach (var original in originals)
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(original);
                    if (info == null) continue;

                    foreach (var patch in info.Prefixes)
                        AddBinding(bindings, original, patch.owner, patch.PatchMethod, type);
                    foreach (var patch in info.Postfixes)
                        AddBinding(bindings, original, patch.owner, patch.PatchMethod, type);
                    foreach (var patch in info.Transpilers)
                        AddBinding(bindings, original, patch.owner, patch.PatchMethod, type);
                    foreach (var patch in info.Finalizers)
                        AddBinding(bindings, original, patch.owner, patch.PatchMethod, type);
                }

                // Harmony may return patch methods whose declaring type is a
                // generated wrapper, so binding discovery is not a reliable
                // indication that the patch was applied. The original method
                // list is the authoritative result from CreateClassProcessor.
                _patchedBindings[patchId] = bindings;
                return PatchResult.Applied(type.Name);
            }
            catch (Exception error)
            {
                try { UnpatchType(type); }
                catch (Exception cleanupError)
                {
                    System.Diagnostics.Debug.WriteLine($"[PatchBackend] Cleanup failed for {type.Name}: {cleanupError}");
                }

                return PatchResult.Failed(error.Message);
            }
        }

        public PatchResult Remove(string patchId, object definition)
        {
            if (Harmony == null)
                return PatchResult.Failed($"Mono Harmony backend is not initialized for '{patchId}'.");

            if (!(definition is Type type))
            {
                if (definition is MonoAdaptivePatch adaptive)
                {
                    try
                    {
                        var result = adaptive.Remove(Harmony);
                        _adaptivePatches.Remove(patchId);
                        return result;
                    }
                    catch (Exception error)
                    {
                        return PatchResult.Failed(error.Message);
                    }
                }

                return PatchResult.Failed($"Mono patch '{patchId}' did not provide a supported definition.");
            }

            try
            {
                if (_patchedBindings.TryGetValue(patchId, out var bindings) && bindings.Count > 0)
                {
                    foreach (var (original, patchMethod) in bindings)
                        Harmony.Unpatch(original, patchMethod);
                    _patchedBindings.Remove(patchId);
                }
                else
                {
                    UnpatchType(type);
                    _patchedBindings.Remove(patchId);
                }

                return PatchResult.Removed(type.Name);
            }
            catch (Exception error)
            {
                return PatchResult.Failed(error.Message);
            }
        }

        public void RemoveAll()
        {
            if (Harmony != null)
            {
                foreach (var adaptive in _adaptivePatches.Values)
                    adaptive.Remove(Harmony);
            }
            _adaptivePatches.Clear();
            Harmony?.UnpatchAll(Harmony.Id);
            PatchExceptionGuard.Reset();
            _patchedBindings.Clear();
        }

        private void AddBinding(
            List<(MethodBase Original, MethodInfo PatchMethod)> bindings,
            MethodBase original,
            string owner,
            MethodInfo patchMethod,
            Type declaringType)
        {
            if (owner == Harmony!.Id && patchMethod.DeclaringType == declaringType)
                bindings.Add((original, patchMethod));
        }

        private void UnpatchType(Type patchClass)
        {
            if (Harmony == null) return;

            foreach (var original in Harmony.GetPatchedMethods())
            {
                var info = HarmonyLib.Harmony.GetPatchInfo(original);
                if (info == null) continue;

                foreach (var patch in info.Prefixes)
                    UnpatchIfOwned(original, patch.owner, patch.PatchMethod, patchClass);
                foreach (var patch in info.Postfixes)
                    UnpatchIfOwned(original, patch.owner, patch.PatchMethod, patchClass);
                foreach (var patch in info.Transpilers)
                    UnpatchIfOwned(original, patch.owner, patch.PatchMethod, patchClass);
                foreach (var patch in info.Finalizers)
                    UnpatchIfOwned(original, patch.owner, patch.PatchMethod, patchClass);
            }
        }

        private void UnpatchIfOwned(MethodBase original, string owner, MethodInfo patchMethod, Type patchClass)
        {
            if (Harmony != null && owner == Harmony.Id && patchMethod.DeclaringType == patchClass)
                Harmony.Unpatch(original, patchMethod);
        }

        public void Dispose()
        {
            RemoveAll();
            Harmony = null;
        }
    }
}
