using System;
using System.Reflection;

namespace Iridium
{
    /// <summary>
    /// Open-instance delegate without HarmonyLib's AccessTools.MethodDelegate: the build
    /// references HarmonyX 2.3.6 (4-parameter overload) while MelonLoader ships 2.10 (3
    /// parameters), so the call throws MissingMethodException at runtime.
    /// Call sites pass the same optional arguments as MethodDelegate.
    /// </summary>
    internal static class FastDelegate
    {
        public static T Open<T>(
            MethodInfo method,
            object instance = null,
            bool virtualCall = true,
            params Type[] delegateArgs) where T : Delegate
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return (T)Delegate.CreateDelegate(typeof(T), instance, method, true);
        }
    }
}
