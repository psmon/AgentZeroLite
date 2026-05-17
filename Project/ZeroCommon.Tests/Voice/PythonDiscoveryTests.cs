using Agent.Common.Voice;

namespace ZeroCommon.Tests.Voice;

/// <summary>
/// Coverage for the py-launcher + filesystem-fallback Python discovery.
/// All probes are mocked so the test never depends on what's actually
/// installed on the CI machine.
/// </summary>
public sealed class PythonDiscoveryTests
{
    [Theory]
    [InlineData(@" -V:3.12 *        C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe",
                "3.12", @"C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe", true)]
    [InlineData(@" -V:3.11          C:\Users\me\AppData\Local\Programs\Python\Python311\python.exe",
                "3.11", @"C:\Users\me\AppData\Local\Programs\Python\Python311\python.exe", false)]
    [InlineData(@" -V:3.13/PythonCore *  C:\Python313\python.exe",
                "3.13", @"C:\Python313\python.exe", true)]
    [InlineData(@" -V:3.14.0          C:\Users\me\AppData\Local\Python\pythoncore-3.14-64\python.exe",
                "3.14.0", @"C:\Users\me\AppData\Local\Python\pythoncore-3.14-64\python.exe", false)]
    public void Parses_py_launcher_output_lines(string line, string expectedVersion, string expectedPath, bool expectedDefault)
    {
        var p = PythonDiscovery.ParsePyLauncherLine(line);
        Assert.NotNull(p);
        Assert.Equal(expectedVersion, p!.Version);
        Assert.Equal(expectedPath, p.ExecutablePath);
        Assert.Equal(expectedDefault, p.IsDefault);
        Assert.Equal("py-launcher", p.Source);
    }

    [Theory]
    [InlineData("Installed Pythons found by C:\\WINDOWS\\py.exe Launcher for Windows")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage line")]
    public void Skips_header_and_blank_lines(string line)
    {
        Assert.Null(PythonDiscovery.ParsePyLauncherLine(line));
    }

    [Fact]
    public async Task Enumerates_pythons_from_py_launcher_output()
    {
        var runner = new FakeRunner
        {
            ExitCode = 0,
            StdOut = " -V:3.14 *        C:\\Users\\me\\AppData\\Local\\Python\\pythoncore-3.14-64\\python.exe\n" +
                     " -V:3.12          C:\\Users\\me\\AppData\\Local\\Programs\\Python\\Python312\\python.exe\n",
        };
        var files = new FakeFiles(); // empty — fallback finds nothing

        var pythons = await PythonDiscovery.EnumerateAsync(runner, files, _ => "");

        Assert.Equal(2, pythons.Count);
        Assert.True(pythons[0].IsDefault);
        Assert.Equal("3.14", pythons[0].Version);
        Assert.Contains("pythoncore-3.14-64", pythons[0].ExecutablePath);
        Assert.False(pythons[1].IsDefault);
        Assert.Equal("3.12", pythons[1].Version);
    }

    [Fact]
    public async Task Falls_back_to_filesystem_when_py_launcher_missing()
    {
        var runner = new FakeRunner { ThrowOnRun = new System.ComponentModel.Win32Exception("py not found") };
        var existingExe = @"C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe";
        var files = new FakeFiles(existingExe);
        string SpecialFolder(Environment.SpecialFolder f) =>
            f == Environment.SpecialFolder.LocalApplicationData ? @"C:\Users\me\AppData\Local" : "";

        var pythons = await PythonDiscovery.EnumerateAsync(runner, files, SpecialFolder);

        Assert.Single(pythons);
        Assert.Equal(existingExe, pythons[0].ExecutablePath);
        Assert.Equal("filesystem", pythons[0].Source);
    }

    [Fact]
    public async Task Deduplicates_when_py_launcher_and_filesystem_overlap()
    {
        var shared = @"C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe";
        var runner = new FakeRunner
        {
            ExitCode = 0,
            StdOut = $" -V:3.12          {shared}\n",
        };
        var files = new FakeFiles(shared);
        string SpecialFolder(Environment.SpecialFolder f) =>
            f == Environment.SpecialFolder.LocalApplicationData ? @"C:\Users\me\AppData\Local" : "";

        var pythons = await PythonDiscovery.EnumerateAsync(runner, files, SpecialFolder);

        // The same exe appears in both sources, but the result must list it once.
        Assert.Single(pythons);
        Assert.Equal("py-launcher", pythons[0].Source); // py launcher wins (added first)
    }

    [Fact]
    public void DisplayLabel_includes_version_and_path_and_default_marker()
    {
        var p = new PythonInstallation("3.12", @"C:\Python312\python.exe", IsDefault: true, Source: "py-launcher");
        Assert.Equal(@"Python 3.12 (default) — C:\Python312\python.exe", p.DisplayLabel);

        var q = new PythonInstallation("3.11", @"C:\Python311\python.exe", IsDefault: false, Source: "py-launcher");
        Assert.Equal(@"Python 3.11 — C:\Python311\python.exe", q.DisplayLabel);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        public Exception? ThrowOnRun { get; set; }

        public Task<ProcessRunResult> RunAsync(
            string fileName, IReadOnlyList<string> args, string? stdin, string? workingDir, CancellationToken ct)
        {
            if (ThrowOnRun is not null) throw ThrowOnRun;
            return Task.FromResult(new ProcessRunResult(ExitCode, StdOut, StdErr));
        }
    }

    private sealed class FakeFiles : IFileExistenceProbe
    {
        private readonly HashSet<string> _existing;
        public FakeFiles(params string[] existing)
            => _existing = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => _existing.Contains(path);
    }
}
