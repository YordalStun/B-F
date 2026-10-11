using BenchForge.Core.Model;

namespace BenchForge.Core.Persistence;

/// <summary>Saves/loads runs as portable .bfresult files (gzip-compressed JSON) — copy them to any PC and import.</summary>
public sealed class ResultStore
{
    public const string Extension = ".bfresult";
    private readonly string _dir;
    private readonly Dictionary<string, RunSummary> _cache = new();

    public ResultStore(string? dir = null) { _dir = dir ?? DataPaths.Results; Directory.CreateDirectory(_dir); }

    public string Folder => _dir;

    public string Save(RunResult r)
    {
        var path = Path.Combine(_dir, $"{r.StartTime:yyyyMMdd-HHmmss}-{r.Id}{Extension}");
        DataPaths.WriteDurable(path, Json.SerializeGzip(r));
        _cache[path] = RunSummary.From(r, path);
        WriteIndex();
        return path;
    }

    public void Update(RunResult r, string path)
    {
        DataPaths.WriteDurable(path, Json.SerializeGzip(r));
        _cache[path] = RunSummary.From(r, path);
        WriteIndex();
    }

    public static RunResult? Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Json.DeserializeMaybeGzip<RunResult>(fs);
    }

    public List<RunSummary> List()
    {
        var index = LoadIndex();
        var result = new List<RunSummary>();
        foreach (var f in Directory.GetFiles(_dir, "*" + Extension))
        {
            if (index.TryGetValue(f, out var s)) { result.Add(s); continue; }
            try
            {
                var r = Load(f);
                if (r != null) result.Add(_cache[f] = RunSummary.From(r, f));
            }
            catch (Exception ex) { DataPaths.AppLogWrite($"Could not read {f}: {ex.Message}"); }
        }
        foreach (var s in result) _cache[s.FilePath] = s;
        WriteIndex();
        return result.OrderByDescending(s => s.StartTime).ToList();
    }

    /// <summary>Copies an exported result from another PC into this PC's history.</summary>
    public RunResult Import(string sourcePath)
    {
        var r = Load(sourcePath) ?? throw new InvalidDataException("Not a BenchForge result file.");
        if (!r.Imported && r.MachineName != Environment.MachineName)
        {
            r.Imported = true;
            r.ImportedFrom = r.MachineName;
        }
        var existing = Directory.GetFiles(_dir, $"*-{r.Id}{Extension}").FirstOrDefault();
        if (existing != null) return r;
        Save(r);
        return r;
    }

    public void Export(RunResult r, string destination) => File.WriteAllBytes(destination, Json.SerializeGzip(r));

    public void Delete(string path)
    {
        File.Delete(path);
        _cache.Remove(path);
        WriteIndex();
    }

    private string IndexPath => Path.Combine(_dir, "index.json");

    private Dictionary<string, RunSummary> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var list = Json.Deserialize<List<RunSummary>>(File.ReadAllText(IndexPath)) ?? new();
                return list.Where(s => File.Exists(s.FilePath)).GroupBy(s => s.FilePath).ToDictionary(g => g.Key, g => g.First());
            }
        }
        catch { }
        return new();
    }

    private void WriteIndex()
    {
        try { File.WriteAllText(IndexPath, Json.Serialize(_cache.Values.Where(s => File.Exists(s.FilePath)).ToList())); } catch { }
    }
}
