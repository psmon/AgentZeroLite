using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Agent.Common.Services;

namespace Agent.Common.Platform;

/// <summary>Where a WebView2 runtime was found, or that none was.</summary>
/// <param name="Installed">True when a usable Evergreen runtime is registered.</param>
/// <param name="Version">The registered version (<c>pv</c>), or null.</param>
/// <param name="Scope"><c>machine</c>, <c>user</c>, <c>forced-missing</c> or <c>none</c>.</param>
public sealed record WebView2RuntimeStatus(bool Installed, string? Version, string Scope)
{
    public static readonly WebView2RuntimeStatus NotApplicable = new(true, null, "not-windows");

    public override string ToString() => Installed ? $"WebView2 {Version ?? "?"} ({Scope})" : $"WebView2 missing ({Scope})";
}

/// <summary>
/// The Edge WebView2 runtime the Windows terminal renderer (xterm.js in <c>NativeWebView</c>)
/// runs on. Windows 11 ships it and Windows Update brought it to most Windows 10 machines, but
/// LTSC/Server editions, WSUS-managed or offline images and a removed install do not have it —
/// and then every terminal tab stays blank. An MSIX cannot declare it as a dependency, so the
/// Store edition cannot get it at install time; the app has to notice and offer it.
///
/// <para>Detection is Microsoft's documented one: the <c>pv</c> value under
/// <c>EdgeUpdate\Clients\{F3017226-…}</c> in HKLM (WOW6432Node on 64-bit) or HKCU; missing,
/// empty or <c>0.0.0.0</c> means not installed. A packaged process reads the real registry
/// here (reads are merged, only new writes are virtualized).</para>
///
/// <para>Install is the Evergreen bootstrapper from Microsoft's fwlink, Authenticode-checked
/// (valid chain, Microsoft signer) before it runs. Inside a package it is started with the
/// desktop-app breakaway policy, or its registry and file writes would land in the package's
/// private store and the runtime would be installed only for us — and deleted with us.</para>
///
/// <para>macOS (WKWebView) and Linux (WebKitGTK, a <c>.deb</c> dependency) have no such
/// runtime: <see cref="Probe"/> answers <see cref="WebView2RuntimeStatus.NotApplicable"/>.</para>
/// </summary>
public static class WebView2Runtime
{
    /// <summary>The Evergreen runtime's client id in EdgeUpdate.</summary>
    public const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>Microsoft's permanent link to the Evergreen bootstrapper (~2 MB).</summary>
    public const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>The download page, for when the in-app install cannot run.</summary>
    public const string DownloadPageUrl = "https://developer.microsoft.com/microsoft-edge/webview2/#download";

    /// <summary>
    /// Set to <c>1</c> to make <see cref="Probe"/> report the runtime missing — how the banner
    /// and the install flow are checked on a machine that has it.
    /// </summary>
    public const string ForceMissingVariable = "AGENTZERO_FAKE_NO_WEBVIEW2";

    private const string MachineKey64 = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientId;
    private const string MachineKey32 = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + ClientId;
    private const string UserKey = @"Software\Microsoft\EdgeUpdate\Clients\" + ClientId;

    /// <summary>
    /// Reads one string value: (hive is HKLM, subkey, value name) → value or null. Tests inject
    /// one; the default reads the registry.
    /// </summary>
    public delegate string? RegistryReader(bool localMachine, string subKey, string valueName);

    /// <summary>Is the runtime there? Cheap (three registry reads); safe on any thread.</summary>
    public static WebView2RuntimeStatus Probe(RegistryReader? reader = null, Func<string, string?>? env = null)
    {
        if (reader is null && !OperatingSystem.IsWindows()) return WebView2RuntimeStatus.NotApplicable;
        env ??= Environment.GetEnvironmentVariable;
        if (env(ForceMissingVariable) == "1") return new WebView2RuntimeStatus(false, null, "forced-missing");

        reader ??= ReadRegistry;
        foreach (var (machine, key, scope) in new[]
                 {
                     (true, MachineKey64, "machine"),
                     (true, MachineKey32, "machine"),
                     (false, UserKey, "user"),
                 })
        {
            string? pv;
            try { pv = reader(machine, key, "pv"); }
            catch { pv = null; }
            if (IsUsableVersion(pv)) return new WebView2RuntimeStatus(true, pv!.Trim(), scope);
        }
        return new WebView2RuntimeStatus(false, null, "none");
    }

    /// <summary>Microsoft: a missing, empty or <c>0.0.0.0</c> <c>pv</c> means the runtime is not installed.</summary>
    public static bool IsUsableVersion(string? pv)
    {
        if (string.IsNullOrWhiteSpace(pv)) return false;
        if (!Version.TryParse(pv.Trim(), out var v)) return false;
        return v > new Version(0, 0, 0, 0);
    }

    private static string? ReadRegistry(bool localMachine, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var root = localMachine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
        using var key = root.OpenSubKey(subKey);
        return key?.GetValue(valueName) as string;
    }

    // ── install ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Download the bootstrapper, check its signature, run it silently (it elevates itself when
    /// it can, and installs per-user when it cannot), then probe again.
    /// </summary>
    /// <param name="progress">One human-readable line per step.</param>
    /// <returns>The status after the attempt — <see cref="WebView2RuntimeStatus.Installed"/> is the verdict.</returns>
    public static async Task<WebView2RuntimeStatus> InstallAsync(
        Action<string>? progress = null, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WebView2 is a Windows runtime.");

        var folder = Path.Combine(Path.GetTempPath(), "AgentZeroLite", "webview2");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "MicrosoftEdgeWebview2Setup.exe");

        progress?.Invoke("Downloading the WebView2 runtime installer from Microsoft…");
        var ownsHttp = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        try
        {
            using var response = await http.GetAsync(BootstrapperUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(exe))
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsHttp) http.Dispose();
        }

        var signer = VerifyMicrosoftSignature(exe);
        progress?.Invoke($"Installer signature ok ({signer}). Installing — Windows may ask for permission…");

        var command = $"\"{exe}\" /silent /install";
        int exitCode;
        if (DesktopAppBreakaway.IsPackagedProcess())
        {
            exitCode = await DesktopAppBreakaway.RunCapturedAsync(command, line => progress?.Invoke(line), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "/silent /install")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("The installer did not start.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }

        try { File.Delete(exe); } catch { /* still running its own child, or locked by AV — temp folder */ }

        var after = Probe(env: _ => null);   // the real registry, whatever the test switch says
        progress?.Invoke(after.Installed
            ? $"Installed {after}. Restart AgentZero to open terminals."
            : $"The installer finished (exit {exitCode}) but no runtime is registered. Use the download page instead.");
        return after;
    }

    /// <summary>
    /// Authenticode: the chain must verify and the signer must be Microsoft. Throws otherwise.
    /// Returns the signer's subject for the progress line.
    /// </summary>
    public static string VerifyMicrosoftSignature(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var trust = WinTrust.Verify(path);
        if (trust != 0) throw new InvalidOperationException($"The downloaded installer is not validly signed (WinVerifyTrust 0x{trust:X8}).");

#pragma warning disable SYSLIB0057 // the signer of an Authenticode file, not a certificate file
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        var org = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal))
            throw new InvalidOperationException($"The downloaded installer is signed by '{cert.Subject}', not Microsoft.");
        return org;
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static uint Verify(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
            };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, filePtr, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,            // WTD_UI_NONE
                    fdwRevocationChecks = 0,   // WTD_REVOKE_NONE — works offline-ish; the chain still verifies
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = filePtr,
                    dwStateAction = 0,
                    dwProvFlags = 0x00000080,  // WTD_REVOCATION_CHECK_NONE
                };
                var action = GenericVerifyV2;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
                Marshal.FreeHGlobal(filePtr);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
    }
}
