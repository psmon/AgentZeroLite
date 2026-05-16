using System.IO;
using System.Text;

namespace AgentZeroWpf;

internal sealed class ScrapWriter : IDisposable
{
    private readonly StreamWriter _writer;
    public string FilePath { get; }

    /// <summary>새 텍스트가 파일에 기록될 때 발생. 인자는 새로 추가된 텍스트 청크.</summary>
    public event Action<string>? ChunkWritten;

    public ScrapWriter(string basePath)
    {
        var dir = Path.Combine(basePath, "logs", "scrap");
        Directory.CreateDirectory(dir);
        var fileName = $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-scrap.txt";
        FilePath = Path.Combine(dir, fileName);
        _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>새 줄들을 파일에 기록하고 ChunkWritten 이벤트 발생.</summary>
    public void WriteLines(IReadOnlyList<string> newLines)
    {
        if (newLines.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var line in newLines)
        {
            _writer.WriteLine(line);
            sb.AppendLine(line);
        }
        ChunkWritten?.Invoke(sb.ToString());
    }

    /// <summary>전체 텍스트를 한번에 기록 (TextPattern 등 일괄 결과용).</summary>
    public void WriteAll(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _writer.Write(text);
        ChunkWritten?.Invoke(text);
    }

    public void Dispose() => _writer.Dispose();
}
