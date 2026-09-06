using System;
using System.IO;
using System.Runtime.InteropServices;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// Brings SQLite up with nothing asked of the user (#17). The native library ships inside the mod under
    /// <c>Natives/&lt;rid&gt;/</c>; at startup this picks the folder for the running OS and architecture,
    /// loads it by full path, and hands the driver a function-pointer resolver over that handle. Nothing
    /// depends on the process search path, the assembly's location, or anything installed on the machine.
    ///
    /// <para>If any step fails — unsupported platform, missing file, a managed dependency that would not
    /// load — <see cref="Available"/> stays false, one warning is logged, and the mod runs without a
    /// database: the in-memory census keeps working and the .rws keeps its packed overlay copy.</para>
    /// </summary>
    public static class SqliteRuntime
    {
        public const string NativesFolder = "Natives";
        public const string LibraryName = "e_sqlite3";

        public static bool Available { get; private set; }
        public static string Version { get; private set; }
        public static string LoadedFrom { get; private set; }
        public static string Error { get; private set; }

        private static bool attempted;

        /// <summary>Load and register SQLite once. Safe to call repeatedly; only the first call does work.</summary>
        public static bool Initialize(string modRoot)
        {
            if (attempted) return Available;
            attempted = true;
            try
            {
                string path = NativePath(modRoot, out string rid);
                if (path == null) { Error = "no native SQLite for this platform (" + rid + ")"; Warn(); return false; }
                if (!File.Exists(path)) { Error = "native SQLite missing: " + path; Warn(); return false; }

                IntPtr handle = NativeLoader.Load(path, out string loadError);
                if (handle == IntPtr.Zero) { Error = "could not load " + path + ": " + loadError; Warn(); return false; }

                Version = Register(handle);   // SQLite-typed code lives in its own method (see below)
                LoadedFrom = path;
                Available = true;
                Log.Message($"[R&S PersistentWorld] SQLite {Version} loaded from {rid}.");
                return true;
            }
            catch (Exception e)
            {
                // Includes TypeLoadException / FileNotFoundException when a managed dependency is absent —
                // thrown here, at the call into Register, not in the caller.
                Error = e.GetType().Name + ": " + e.Message;
                Warn();
                return false;
            }
        }

        // Kept separate so the JIT only touches the SQLite assemblies when we actually get this far.
        private static string Register(IntPtr handle)
        {
            SQLitePCL.SQLite3Provider_dynamic_cdecl.Setup(LibraryName, new HandleResolver(handle));
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_dynamic_cdecl());
            SQLitePCL.raw.FreezeProvider();
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "select sqlite_version()";
                    return (string)cmd.ExecuteScalar();
                }
            }
        }

        private static void Warn()
        {
            Log.Warning("[R&S PersistentWorld] SQLite unavailable; running without a database (" + Error + ").");
        }

        /// <summary>The native library path for this machine, or null when the platform is unsupported.</summary>
        public static string NativePath(string modRoot, out string rid)
        {
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string file;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { rid = "win-" + arch; file = LibraryName + ".dll"; }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { rid = "osx-" + arch; file = "lib" + LibraryName + ".dylib"; }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) { rid = "linux-" + arch; file = "lib" + LibraryName + ".so"; }
            else { rid = "unknown"; return null; }
            if (string.IsNullOrEmpty(modRoot)) return null;
            return Path.Combine(modRoot, NativesFolder, rid, file);
        }

        /// <summary>The root folder of this mod as loaded, or null when it cannot be found.</summary>
        public static string ModRoot()
        {
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                if (mod != null && string.Equals(mod.PackageId, "RegionsAndSocieties.PersistentWorld", StringComparison.OrdinalIgnoreCase))
                    return mod.RootDir;
            return null;
        }

        /// <summary>Resolves SQLite entry points against the handle we loaded ourselves.</summary>
        private sealed class HandleResolver : SQLitePCL.IGetFunctionPointer
        {
            private readonly IntPtr handle;
            public HandleResolver(IntPtr handle) { this.handle = handle; }
            public IntPtr GetFunctionPointer(string name) => NativeLoader.Symbol(handle, name);
        }
    }

    /// <summary>The three platforms' dynamic loaders, chosen at runtime. Each import is resolved lazily by
    /// the runtime on first call, so declaring all three is harmless on any one OS.</summary>
    internal static class NativeLoader
    {
        public static IntPtr Load(string path, out string error)
        {
            error = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                IntPtr h = Win.LoadLibraryW(path);
                if (h == IntPtr.Zero) error = "Win32 error " + Marshal.GetLastWin32Error();
                return h;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                IntPtr h = Mac.dlopen(path, RtldNow | RtldGlobal);
                if (h == IntPtr.Zero) error = Marshal.PtrToStringAnsi(Mac.dlerror());
                return h;
            }
            IntPtr l = Linux.dlopen(path, RtldNow | RtldGlobal);
            if (l == IntPtr.Zero) error = Marshal.PtrToStringAnsi(Linux.dlerror());
            return l;
        }

        public static IntPtr Symbol(IntPtr handle, string name)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Win.GetProcAddress(handle, name);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return Mac.dlsym(handle, name);
            return Linux.dlsym(handle, name);
        }

        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;   // glibc value; macOS uses 8 but accepts the flag being absent

        private static class Win
        {
            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibraryW(string path);
            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)] public static extern IntPtr GetProcAddress(IntPtr h, string name);
        }

        private static class Linux
        {
            [DllImport("libdl.so.2")] public static extern IntPtr dlopen(string path, int flags);
            [DllImport("libdl.so.2")] public static extern IntPtr dlsym(IntPtr h, string name);
            [DllImport("libdl.so.2")] public static extern IntPtr dlerror();
        }

        private static class Mac
        {
            [DllImport("libdl.dylib")] public static extern IntPtr dlopen(string path, int flags);
            [DllImport("libdl.dylib")] public static extern IntPtr dlsym(IntPtr h, string name);
            [DllImport("libdl.dylib")] public static extern IntPtr dlerror();
        }
    }
}
