using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Iridium.Native
{
    /// <summary>
    /// Loader for the optional native hot-path library (iridium_core).
    ///
    /// The library ships next to the mod assembly — the only location that is
    /// identical across all loaders (UMM/MelonLoader/BepInEx/MiniModLoader);
    /// MelonLoader's ../UserLibs is a secondary fallback only.
    ///
    /// Binding relies on Mono's DllImport resolution, which probes the
    /// assembly directory on every platform (iridium_core.dll /
    /// libiridium_core.so / libiridium_core.dylib). No manual dlopen — the
    /// libdl handle is glibc-Linux-only and would break macOS.
    ///
    /// Absent or ABI-incompatible library means the pure C# path is used;
    /// native is an enhancement, never a requirement.
    /// </summary>
    public static class IridiumNative
    {
        private const int RequiredAbiVersion = 1;

        private static bool _probeDone;
        private static bool _available;
        private static string _version = "";

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

        // Existence check only, for a precise log line; actual binding below
        // goes through Mono's DllImport resolution.
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
                    Main.Logger?.Log("[IridiumNative] not present, using pure C# path");
                    return;
                }

                uint abi = iridium_core_abi_version();
                if (abi != RequiredAbiVersion)
                {
                    Main.Logger?.Log($"[IridiumNative] ABI mismatch: have {abi}, need {RequiredAbiVersion} — falling back");
                    return;
                }

                _version = Marshal.PtrToStringAnsi(iridium_core_lib_version()) ?? "";
                _available = true;
                Main.Logger?.Log($"[IridiumNative] iridium_core v{_version} (ABI {abi}) loaded");
            }
            catch (DllNotFoundException)
            {
                Main.Logger?.Log("[IridiumNative] binding unavailable — using pure C# path");
            }
            catch (EntryPointNotFoundException ex)
            {
                Main.Logger?.Log("[IridiumNative] symbol missing: " + ex.Message);
            }
            catch (Exception ex)
            {
                Main.Logger?.Log("[IridiumNative] probe failed: " + ex.Message);
            }
        }

        // ── Native entry points (resolved by Mono against the mod dir) ──

        [DllImport("iridium_core", EntryPoint = "iridium_core_abi_version")]
        private static extern uint iridium_core_abi_version();

        [DllImport("iridium_core", EntryPoint = "iridium_core_lib_version")]
        private static extern IntPtr iridium_core_lib_version();
    }
}
