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
        internal delegate float EvaluateFn(int ease, float time, float duration, float amplitude, float period);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int EvaluateBatchFn(
            IntPtr easeIds, IntPtr times, IntPtr durations,
            IntPtr amplitudes, IntPtr periods, uint len, IntPtr outProgress);

        [StructLayout(LayoutKind.Sequential)]
        internal struct FloorPathInput
        {
            public IntPtr Angles;
            public IntPtr LengthMults;
            public double StartAngle;
            public float TileSize;
            public uint Steps;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FloorPathOutput
        {
            public IntPtr EntryAngles;
            public IntPtr ExitAngles;
            public IntPtr PositionsX;
            public IntPtr PositionsY;
            public IntPtr Count;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int RemakePathFn(ref FloorPathInput input, ref FloorPathOutput output);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int RemakePathFromFn(ref FloorPathInput input, float originX, float originY, ref FloorPathOutput output);

        /// <summary>
        /// Flat JSON DOM mirrored from Rust `ffi::JsonView`: 8 pointers,
        /// then 5 scalars. Field order/type must match exactly.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct JsonView
        {
            public IntPtr Kinds;
            public IntPtr A;
            public IntPtr B;
            public IntPtr Values;
            public IntPtr Children;
            public IntPtr Strings;
            public IntPtr StrOff;
            public IntPtr StrLen;
            public uint NodeCount;
            public uint ChildCount;
            public uint StringCount;
            public uint StringPoolLen;
            public uint Root;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int JsonParseFn(IntPtr text, uint len, IntPtr section, uint sectionLen);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int JsonViewFn(out JsonView view);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int JsonReleaseFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int SpatialRebuildFn(uint count, IntPtr rects);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int SpatialQueryFn(
            float minX, float minY, float maxX, float maxY, IntPtr outIds, uint cap);

        private static AbiVersionFn _abiVersion;
        private static LibVersionFn _libVersion;
        private static EvaluateFn _evaluate;
        private static EvaluateBatchFn _evaluateBatch;
        private static RemakePathFn _remakePath;
        private static RemakePathFromFn _remakePathFrom;
        private static JsonParseFn _jsonParse;
        private static JsonViewFn _jsonView;
        private static JsonReleaseFn _jsonRelease;
        private static SpatialRebuildFn _spatialRebuild;
        private static SpatialQueryFn _spatialQuery;

        /// <summary>
        /// Batch path rebuild on the native side. Null unless
        /// <see cref="Available"/> — callers must gate on that first.
        /// </summary>
        internal static RemakePathFn RemakePath => _remakePath;

        /// <summary>
        /// Incremental path rebuild from an anchor tile (optional export;
        /// null on older libraries or when not <see cref="Available"/>).
        /// </summary>
        internal static RemakePathFromFn RemakePathFrom => _available ? _remakePathFrom : null;

        /// <summary>
        /// Pinned RemakePath call. <paramref name="angleOffset"/> &gt; 0 with
        /// <paramref name="fromAnchor"/> rebuilds from an anchor tile (slot 0 of
        /// every output = the anchor). Output arrays need <c>steps + 1</c> slots.
        /// Returns false when the export is missing or the call failed.
        /// </summary>
        internal static bool RemakePathPinned(
            float[] angles, int angleOffset, float[] lengthMults, int steps,
            double startAngle, float tileSize, bool fromAnchor, float originX, float originY,
            double[] entry, double[] exit, float[] px, float[] py)
        {
            if (!_available || steps < 0) return false;
            var full = _remakePath;
            var from = _remakePathFrom;
            if (fromAnchor ? from == null : full == null) return false;
            if (angleOffset < 0 || angleOffset + steps > angles.Length || steps > lengthMults.Length ||
                entry.Length < steps + 1 || exit.Length < steps + 1 || px.Length < steps + 1 || py.Length < steps + 1)
                return false;

            var count = new uint[1];
            GCHandle hA = default, hM = default, hE = default, hX = default, hPx = default, hPy = default, hC = default;
            try
            {
                hA = GCHandle.Alloc(angles, GCHandleType.Pinned);
                hM = GCHandle.Alloc(lengthMults, GCHandleType.Pinned);
                hE = GCHandle.Alloc(entry, GCHandleType.Pinned);
                hX = GCHandle.Alloc(exit, GCHandleType.Pinned);
                hPx = GCHandle.Alloc(px, GCHandleType.Pinned);
                hPy = GCHandle.Alloc(py, GCHandleType.Pinned);
                hC = GCHandle.Alloc(count, GCHandleType.Pinned);

                var input = new FloorPathInput
                {
                    Angles = hA.AddrOfPinnedObject() + angleOffset * sizeof(float),
                    LengthMults = hM.AddrOfPinnedObject(),
                    StartAngle = startAngle,
                    TileSize = tileSize,
                    Steps = (uint)steps,
                };
                var output = new FloorPathOutput
                {
                    EntryAngles = hE.AddrOfPinnedObject(),
                    ExitAngles = hX.AddrOfPinnedObject(),
                    PositionsX = hPx.AddrOfPinnedObject(),
                    PositionsY = hPy.AddrOfPinnedObject(),
                    Count = hC.AddrOfPinnedObject(),
                };

                int rc = fromAnchor
                    ? from(ref input, originX, originY, ref output)
                    : full(ref input, ref output);
                return rc == 0 && count[0] == (uint)(steps + 1);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hA.IsAllocated) hA.Free();
                if (hM.IsAllocated) hM.Free();
                if (hE.IsAllocated) hE.Free();
                if (hX.IsAllocated) hX.Free();
                if (hPx.IsAllocated) hPx.Free();
                if (hPy.IsAllocated) hPy.Free();
                if (hC.IsAllocated) hC.Free();
            }
        }

        /// <summary>True when the batch easing export is present.</summary>
        internal static bool HasEvaluateBatch
        {
            get { EnsureProbed(); return _available && _evaluateBatch != null; }
        }

        /// <summary>True when the JSON parse/view/release exports are present.</summary>
        internal static bool HasJson
        {
            get { EnsureProbed(); return _available && _jsonParse != null && _jsonView != null && _jsonRelease != null; }
        }

        /// <summary>True when the spatial index exports are present.</summary>
        internal static bool HasSpatial
        {
            get { EnsureProbed(); return _available && _spatialRebuild != null && _spatialQuery != null; }
        }

        /// <summary>
        /// Single easing evaluation on the native side. Returns 0 when the
        /// library is absent (features gate on <see cref="Available"/> first,
        /// so this is a safety net, not the control path).
        /// </summary>
        internal static float Evaluate(int ease, float time, float duration, float amplitude, float period)
        {
            var fn = _evaluate;
            if (fn == null) return time >= duration ? 1f : 0f;
            return fn(ease, time, duration, amplitude, period);
        }

        /// <summary>
        /// Batch easing evaluation (SoA). Returns false when the export is
        /// unavailable or the call failed; the caller falls back to
        /// per-tween <see cref="Evaluate"/>.
        /// </summary>
        internal static bool EvaluateBatch(
            int[] easeIds, float[] times, float[] durations,
            float[] amplitudes, float[] periods, float[] outProgress, int count)
        {
            var fn = _evaluateBatch;
            if (fn == null || count <= 0) return false;
            if (count > easeIds.Length || count > times.Length || count > durations.Length ||
                count > amplitudes.Length || count > periods.Length || count > outProgress.Length)
                return false;

            GCHandle hEase = default, hTime = default, hDur = default,
                hAmp = default, hPer = default, hOut = default;
            try
            {
                hEase = GCHandle.Alloc(easeIds, GCHandleType.Pinned);
                hTime = GCHandle.Alloc(times, GCHandleType.Pinned);
                hDur = GCHandle.Alloc(durations, GCHandleType.Pinned);
                hAmp = GCHandle.Alloc(amplitudes, GCHandleType.Pinned);
                hPer = GCHandle.Alloc(periods, GCHandleType.Pinned);
                hOut = GCHandle.Alloc(outProgress, GCHandleType.Pinned);
                int rc = fn(
                    hEase.AddrOfPinnedObject(), hTime.AddrOfPinnedObject(), hDur.AddrOfPinnedObject(),
                    hAmp.AddrOfPinnedObject(), hPer.AddrOfPinnedObject(), (uint)count,
                    hOut.AddrOfPinnedObject());
                return rc == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hEase.IsAllocated) hEase.Free();
                if (hTime.IsAllocated) hTime.Free();
                if (hDur.IsAllocated) hDur.Free();
                if (hAmp.IsAllocated) hAmp.Free();
                if (hPer.IsAllocated) hPer.Free();
                if (hOut.IsAllocated) hOut.Free();
            }
        }

        /// <summary>
        /// Parses JSON with the native parser into the thread-local DOM.
        /// `section` non-null mirrors GDMiniJSON DeserializePartially.
        /// Returns false when the library/export is absent or input malformed
        /// (caller must then use the managed parser).
        /// </summary>
        internal static bool JsonParse(string text, string? section)
        {
            var fn = _jsonParse;
            if (fn == null) return false;

            GCHandle hText = default, hSection = default;
            try
            {
                hText = GCHandle.Alloc(text, GCHandleType.Pinned);
                IntPtr sectionPtr = IntPtr.Zero;
                uint sectionLen = 0;
                if (section != null)
                {
                    hSection = GCHandle.Alloc(section, GCHandleType.Pinned);
                    sectionPtr = hSection.AddrOfPinnedObject();
                    sectionLen = (uint)section.Length;
                }
                int rc = fn(hText.AddrOfPinnedObject(), (uint)text.Length, sectionPtr, sectionLen);
                return rc == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hText.IsAllocated) hText.Free();
                if (hSection.IsAllocated) hSection.Free();
            }
        }

        /// <summary>Fetches the DOM view after a successful <see cref="JsonParse"/>.</summary>
        internal static bool JsonGetView(out JsonView view)
        {
            view = default;
            var fn = _jsonView;
            if (fn == null) return false;
            try
            {
                return fn(out view) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Releases the thread-local JSON DOM buffers.</summary>
        internal static void JsonRelease()
        {
            try { _jsonRelease?.Invoke(); }
            catch { /* release is best-effort */ }
        }

        /// <summary>Rebuilds the spatial index from packed AABBs (4 f32 each).</summary>
        internal static bool SpatialRebuild(float[] packed, int count)
        {
            var fn = _spatialRebuild;
            if (fn == null) return false;
            GCHandle h = default;
            try
            {
                h = GCHandle.Alloc(packed, GCHandleType.Pinned);
                return fn((uint)count, h.AddrOfPinnedObject()) == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (h.IsAllocated) h.Free();
            }
        }

        /// <summary>
        /// Queries the spatial index with a pre-allocated unmanaged output
        /// buffer (avoids a GCHandle per call on hot frames).
        /// Returns candidate count (may exceed <paramref name="cap"/>). -1 on error.
        /// </summary>
        internal static int SpatialQueryRaw(
            float minX, float minY, float maxX, float maxY, IntPtr outIds, int cap)
        {
            var fn = _spatialQuery;
            if (fn == null) return -1;
            try
            {
                return fn(minX, minY, maxX, maxY, outIds, (uint)cap);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Queries the spatial index. Returns candidate count (may exceed
        /// <paramref name="ids"/>.Length; only that many are written). -1 on error.
        /// </summary>
        internal static int SpatialQuery(
            float minX, float minY, float maxX, float maxY, int[] ids, int count)
        {
            var fn = _spatialQuery;
            if (fn == null) return -1;
            GCHandle h = default;
            try
            {
                h = GCHandle.Alloc(ids, GCHandleType.Pinned);
                return fn(minX, minY, maxX, maxY, h.AddrOfPinnedObject(), (uint)count);
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (h.IsAllocated) h.Free();
            }
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
                _remakePath = GetExport<RemakePathFn>("iridium_core_remake_path");
                if (_abiVersion == null || _libVersion == null || _evaluate == null || _remakePath == null)
                {
                    Main.Logger?.Log("[IridiumNative] exports missing — native-backed features DISABLED");
                    return;
                }

                // Optional exports: loaded best-effort so a core-compatible
                // older library still enables the base features.
                _evaluateBatch = GetExport<EvaluateBatchFn>("iridium_core_evaluate_batch");
                _remakePathFrom = GetExport<RemakePathFromFn>("iridium_core_remake_path_from");
                _jsonParse = GetExport<JsonParseFn>("iridium_core_json_parse");
                _jsonView = GetExport<JsonViewFn>("iridium_core_json_view");
                _jsonRelease = GetExport<JsonReleaseFn>("iridium_core_json_release");
                _spatialRebuild = GetExport<SpatialRebuildFn>("iridium_core_spatial_rebuild");
                _spatialQuery = GetExport<SpatialQueryFn>("iridium_core_spatial_query");

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
