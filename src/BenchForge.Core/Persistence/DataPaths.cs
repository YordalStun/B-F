namespace BenchForge.Core.Persistence;

/// <summary>
/// Where BenchForge keeps its data. Portable mode: a "BenchForgeData" folder next to the exe (works from a USB stick).
/// Falls back to %LocalAppData%\BenchForge when the exe folder is read-only.
/// </summary>
public static class DataPaths
{
    private static string? _root;

    public static string Root => _root ??= Resolve();
    public static bool IsPortable { get; private set; }

    public static string Results => Ensure(Path.Combine(Root, "Results"));
    public static string Journal => Ensure(Path.Combine(Root, "Journal"));
    public static string Reports => Ensure(Path.Combine(Root, "Reports"));
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string AppLog => Path.Combine(Ensure(Path.Combine(Root, "Logs")), "benchforge.log");

    public static void Override(string root)
    {
        _root = Ensure(root);
    }

    private static string Resolve()
    {
        try
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "BenchForgeData");
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            IsPortable = true;
            return portable;
        }
        catch
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge");
            Directory.CreateDirectory(local);
            return local;
        }
    }

    private static string Ensure(string p)
    {
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Writes a file and forces it to physical disk so it survives a hard crash.</summary>
    public static void WriteDurable(string path, ReadOnlySpan<byte> data)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        fs.Write(data);
        fs.Flush(true);
    }

    public static void AppendDurable(string path, string line)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + Environment.NewLine);
        fs.Write(bytes);
        fs.Flush(true);
    }

    public static void AppLogWrite(string message)
    {
        try { File.AppendAllText(AppLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); } catch { }
    }
}
