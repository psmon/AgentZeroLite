using Agent.Common.Web;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Where the in-process toolbelt finds the Browser page (M0032). MainWindow registers its
/// <c>BrowserPagePanel</c> once it exists; <c>WorkspaceTerminalToolHost</c> reads it per call
/// so AgentBot's <c>web_*</c> tools land in the same tabs the watch's do. Null until the main
/// window is up, which the toolbelt reports as "not available" rather than failing.
/// </summary>
public static class BrowserToolSurfaceRegistry
{
    public static IWebToolSurface? Current { get; set; }
}
