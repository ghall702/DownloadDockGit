namespace DownloadDock.Core.Tests;

/// <summary>A temporary directory deleted after the test.</summary>
public sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dd-tests-" + Guid.NewGuid().ToString("N"));

    public TestFolder() => Directory.CreateDirectory(Path);

    public string File(string name, string content = "x", DateTime? writeUtc = null, DateTime? createUtc = null)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        if (createUtc is { } c) System.IO.File.SetCreationTimeUtc(p, c);
        if (writeUtc is { } w) System.IO.File.SetLastWriteTimeUtc(p, w);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
