using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Iridium.Native
{
    /// <summary>
    /// Loader for the optional native hot-path library (iridium_core).
    ///
    /// Fully manual binding (load + dlsym/GetProcAddress) — Mono's DllImport
    /// resolution follows the OS search path and does NOT probe the mod
    /// directory, so manual binding gives identical, deterministic behavior
    /// on Windows / Linux / macOS.
    ///
    /// Hard binding: features backed by native exports are DISABLED when the
    /// library is missing or ABI-incompatible (no silent C# duplicates —
    /// historical fallback code lives in git history). Gate new native
    /// features on <see cref="Available"/>.
    /// </summary>
    public static class IridiumNative
    {
        private const int RequiredAbiVersion = 1;

        private static bool _probeDone;
        private static bool _available;
        private static string _version = "";
        private static IntPtr _handle;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint AbiVersionFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LibVersionFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float EvaluateFn(int ease, float time, float duration, float amplitude, float period);

        private static AbiVersionFn _abiVersion;
        private static LibVersionFn _libVersion;
        private static EvaluateFn _evaluate;

        /// <summary>
        /// Single easing evaluation on the native side. Returns 0 when the
        /// library is absent (features gate on <see cref="Available"/> first,
        /// so this is a safety net, not the control path).
        /// </summary>
        internal static float Evaluate(int ease, float time, float duration, float amplitude, float period)
        {
            var fn = _evaluate;
            if (fn == null) return time >= duration ? 1f : 0f;
            return fn((int)ease, time, duration, amplitude, period);
        }

        /// <summary>True when the library is loaded and ABI-compatible.</summary>
        public static bool Available
        {
            get
            {
                EnsureProbed();
                return _available;
            }
        }

        public static string LibraryVersion => _version;

        private static string LibName
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return "iridium_core.dll";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return "libiridium_core.dylib";
                return "libiridium_core.so";
            }
        }

        /// <summary>
        /// Probes for the library and logs the outcome. Called once from
        /// Main.Initialize so the result is visible in Player.log.
        /// </summary>
        public static void Probe() => EnsureProbed();

        /// <summary>
        /// Resolves a native export to a delegate. Fails (null) when the
        /// library is not loaded; throws only on delegate type mismatch.
        /// </summary>
        public static T GetExport<T>(string entry) where T : class
        {
            if (_handle == IntPtr.Zero) return null;
            IntPtr ptr = GetSymbol(entry);
            if (ptr == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }

        // Existence check only, for a precise log line.
        private static string ExpectedPath()
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (modDir == null) return null;

            try
            {
                string primary = Path.Combine(modDir, LibName);
                if (File.Exists(primary)) return primary;

                string ml = Path.GetFullPath(Path.Combine(modDir, "..", "UserLibs", LibName));
                if (File.Exists(ml)) return ml;
            }
            catch { }
            return null;
        }

        private static void EnsureProbed()
        {
            if (_probeDone) return;
            _probeDone = true;

            try
            {
                string path = ExpectedPath();
                if (path == null)
                {
                    Main.Logger?.Log("[IridiumNative] not present — native-backed features DISABLED");
                    return;
                }

                if (!LoadNative(path))
                {
                    Main.Logger?.Log("[IridiumNative] load failed: " + path + " — native-backed features DISABLED");
                    return;
                }

                _abiVersion = GetExport<AbiVersionFn>("iridium_core_abi_version");
                _libVersion = GetExport<LibVersionFn>("iridium_core_lib_version");
                _evaluate = GetExport<EvaluateFn>("iridium_core_evaluate");
                if (_abiVersion == null || _libVersion == null || _evaluate == null)
                {
                    Main.Logger?.Log("[IridiumNative] exports missing — native-backed features DISABLED");
                    return;
                }

                uint abi = _abiVersion();
                if (abi != RequiredAbiVersion)
                {
                    Main.Logger?.Log($"[IridiumNative] ABI mismatch: have {abi}, need {RequiredAbiVersion} — native-backed features DISABLED");
                    return;
                }

                _version = Marshal.PtrToStringAnsi(_libVersion()) ?? "";
                _available = true;
                Main.Logger?.Log($"[IridiumNative] iridium_core v{_version} (ABI {abi}) loaded");
            }
            catch (Exception ex)
            {
                Main.Logger?.Log("[IridiumNative] probe failed: " + ex.Message);
            }
        }

        // ── Per-OS loader APIs (lazy-bound; only the current OS's is used) ──

        private static bool LoadNative(string path)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    _handle = LoadLibraryExW(path, IntPtr.Zero, 0x8 /*LOAD_WITH_ALTERED_SEARCH_PATH*/);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    _handle = MacDlopen(path, 2 /*RTLD_NOW*/);
                else
                    _handle = LinuxDlopen(path, 2 /*RTLD_NOW*/);
                return _handle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr GetSymbol(string name)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetProcAddressW(_handle, name);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacDlsym(_handle, name);
            return LinuxDlsym(_handle, name);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32", CharSet = CharSet.Ansi, EntryPoint = "GetProcAddress", SetLastError = true)]
        private static extern IntPtr GetProcAddressW(IntPtr handle, string name);

        // glibc Linux (Steam/Proton distros); TINA uses the same binding here.
        [DllImport("libdl.so.2", EntryPoint = "dlopen", SetLastError = true)]
        private static extern IntPtr LinuxDlopen(string path, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlsym")]
        private static extern IntPtr LinuxDlsym(IntPtr handle, string name);

        // macOS: dlopen lives in libSystem (no libdl on modern macOS).
        [DllImport("libSystem", EntryPoint = "dlopen", SetLastError = true)]
        private static extern IntPtr MacDlopen(string path, int flags);

        [DllImport("libSystem", EntryPoint = "dlsym")]
        private static extern IntPtr MacDlsym(IntPtr handle, string name);
    }
}
