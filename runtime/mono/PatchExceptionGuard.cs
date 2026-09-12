using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace Iridium.Runtime
{
    /// <summary>
    /// Safety net for patched methods. Patches are compiled against a specific
    /// game version; a runtime API change (e.g. an added optional parameter)
    /// compiles fine but throws MissingMethodException when the patch runs on a
    /// newer game build. The finalizer installed on every patched method catches
    /// such exceptions, logs them and lets the game continue instead of
    /// breaking the patched hot path.
    ///
    /// Only exceptions whose stack trace contains an Iridium patch frame are
    /// swallowed — exceptions raised by the original game method propagate
    /// unchanged.
    /// </summary>
    public static class PatchExceptionGuard
    {
        /// <summary>Set by the mod to route guard messages to its own logger.</summary>
        public static Action<string>? ErrorLogger;

        private static readonly HashSet<MethodBase> _guarded = new();
        private static readonly MethodInfo? _finalizerMethod =
            AccessTools.Method(typeof(PatchExceptionGuard), nameof(Finalizer));

        internal static void Attach(Harmony harmony, MethodBase? original)
        {
            if (harmony == null || original == null || _finalizerMethod == null) return;

            lock (_guarded)
            {
                if (!_guarded.Add(original)) return;
            }

            try
            {
                harmony.Patch(original, finalizer: new HarmonyMethod(_finalizerMethod));
            }
            catch (Exception error)
            {
                lock (_guarded) _guarded.Remove(original);
                Log($"[PatchGuard] Failed to attach finalizer to {original.DeclaringType?.Name}.{original.Name}: {error.Message}");
            }
        }

        internal static void Reset()
        {
            lock (_guarded) _guarded.Clear();
        }

        // ReSharper disable once UnusedMember.Global — invoked by Harmony.
        public static Exception? Finalizer(Exception? __exception)
        {
            if (__exception == null) return null;

            string trace = __exception.StackTrace ?? string.Empty;
            if (trace.IndexOf("Iridium", StringComparison.Ordinal) < 0 &&
                trace.IndexOf("Iris.", StringComparison.Ordinal) < 0)
            {
                return __exception; // 游戏自身异常，原样抛出
            }

            Log($"[PatchGuard] Suppressed exception from patch: {__exception}");
            return null;
        }

        private static void Log(string message)
        {
            if (ErrorLogger != null)
                ErrorLogger(message);
            else
                Debug.WriteLine(message);
        }
    }
}
