using System.Runtime.InteropServices;
using System.Text.Json;

namespace Agent.Common.Platform;

/// <summary>
/// Folder access for the Mac App Store build (App Sandbox). App Review refused the home-wide
/// temporary file exception (guideline 2.4.5(i), 2026-10-28), so a sandboxed app reaches a
/// project only through the folder the person picks — and keeps it through a security-scoped
/// bookmark. Measured with a sandboxed probe: a picked folder, and the same folder resolved from
/// its bookmark on the next launch after <c>startAccessingSecurityScopedResource</c>, are both
/// readable and writable by a child <c>/bin/zsh</c> — which is what a terminal tab is.
/// <para>Avalonia's own bookmark API opens the scope only around each of its calls, so the
/// access here is started once and held for the life of the process. Outside the sandbox every
/// call is a no-op.</para>
/// </summary>
public static class SandboxFolderAccess
{
    private const string StoreFile = "folder-access.json";

    /// <summary>True in a macOS App Sandbox (the system sets the container id for every sandboxed process).</summary>
    public static bool IsSandboxed =>
        OperatingSystem.IsMacOS() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_SANDBOX_CONTAINER_ID"));

    /// <summary>
    /// Store a bookmark for <paramref name="path"/>, just picked in the folder dialog (the dialog's
    /// grant is what lets the bookmark be made). Returns false when not sandboxed or it failed.
    /// </summary>
    public static bool Remember(string path)
    {
        if (!IsSandboxed || string.IsNullOrEmpty(path)) return false;
        try
        {
            var bookmark = MacBookmarks.Create(path);
            if (bookmark is null)
            {
                AppLogger.Log($"[FolderAccess] bookmark failed: {path}");
                return false;
            }
            var store = Load();
            store[path] = bookmark;
            Save(store);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[FolderAccess] remember failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Start access to every remembered folder. Call once at startup, before terminals start.</summary>
    public static int RestoreAll()
    {
        if (!IsSandboxed) return 0;
        var opened = 0;
        try
        {
            var store = Load();
            var changed = false;
            foreach (var (path, bookmark) in store.ToList())
            {
                var resolved = MacBookmarks.ResolveAndAccess(bookmark, out var stale);
                if (resolved is null)
                {
                    AppLogger.Log($"[FolderAccess] could not reopen {path} — add the folder again");
                    continue;
                }
                opened++;
                // A moved or renamed folder resolves to its new path; a stale bookmark is re-made
                // while access is held, so the next launch does not depend on the old one.
                if (stale || resolved != path)
                {
                    if (MacBookmarks.Create(resolved) is { } fresh)
                    {
                        store.Remove(path);
                        store[resolved] = fresh;
                        changed = true;
                    }
                }
            }
            if (changed) Save(store);
            AppLogger.Log($"[FolderAccess] reopened {opened}/{store.Count} folder(s)");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[FolderAccess] restore failed: {ex.GetType().Name}: {ex.Message}");
        }
        return opened;
    }

    private static Dictionary<string, string> Load()
    {
        var file = AppPaths.File(StoreFile);
        if (!File.Exists(file)) return new(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
               ?? new(StringComparer.Ordinal);
    }

    private static void Save(Dictionary<string, string> store)
        => File.WriteAllText(AppPaths.File(StoreFile), JsonSerializer.Serialize(store));

    /// <summary>NSURL security-scoped bookmarks through the Objective-C runtime (Foundation is loaded in any AppKit process).</summary>
    private static class MacBookmarks
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const ulong CreationWithSecurityScope = 1UL << 11;   // NSURLBookmarkCreationWithSecurityScope
        private const ulong ResolutionWithSecurityScope = 1UL << 10; // NSURLBookmarkResolutionWithSecurityScope

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, byte b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, ulong length);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, ulong options, IntPtr keys, IntPtr relativeTo, out IntPtr error);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr data, ulong options, IntPtr relativeTo, out byte stale, out IntPtr error);
        // ObjC BOOL is one byte on arm64; a C# bool would be marshalled as a 4-byte Win32 BOOL.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendULong(IntPtr receiver, IntPtr selector);

        private static IntPtr NSString(string s)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(s);
            try { return Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
        }

        private static string? FromNSString(IntPtr s)
            => s == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(s, sel_registerName("UTF8String")));

        public static string? Create(string path)
        {
            var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:isDirectory:"), NSString(path), 1);
            if (url == IntPtr.Zero) return null;
            var data = Send(url, sel_registerName("bookmarkDataWithOptions:includingResourceValuesForKeys:relativeToURL:error:"),
                CreationWithSecurityScope, IntPtr.Zero, IntPtr.Zero, out _);
            if (data == IntPtr.Zero) return null;
            var length = (int)SendULong(data, sel_registerName("length"));
            var bytes = new byte[length];
            Marshal.Copy(Send(data, sel_registerName("bytes")), bytes, 0, length);
            return Convert.ToBase64String(bytes);
        }

        public static string? ResolveAndAccess(string bookmark, out bool stale)
        {
            stale = false;
            var raw = Convert.FromBase64String(bookmark);
            var buffer = Marshal.AllocHGlobal(raw.Length);
            try
            {
                Marshal.Copy(raw, 0, buffer, raw.Length);
                var data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), buffer, (ulong)raw.Length);
                var url = Send(objc_getClass("NSURL"),
                    sel_registerName("URLByResolvingBookmarkData:options:relativeToURL:bookmarkDataIsStale:error:"),
                    data, ResolutionWithSecurityScope, IntPtr.Zero, out var staleByte, out _);
                stale = staleByte != 0;
                if (url == IntPtr.Zero) return null;
                // Held for the life of the process on purpose: terminal tabs started later inherit it.
                if (SendBool(url, sel_registerName("startAccessingSecurityScopedResource")) == 0) return null;
                return FromNSString(Send(url, sel_registerName("path")));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
