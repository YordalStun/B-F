using System.Text;
using BenchForge.Core.Model;

namespace BenchForge.Core.Persistence;

public sealed class JournalHeader
{
    public string RunId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime BootTime { get; set; }
    public int ProcessId { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;
    public TestPlan Plan { get; set; } = new();
    public HardwareInventory? Hardware { get; set; }
}

public sealed class HeartbeatState
{
    public DateTime Time { get; set; }
    public int Sequence { get; set; }
    public int StepIndex { get; set; }
    public string StepName { get; set; } = "";
    public StepKind StepKind { get; set; }
    public List<Component> Components { get; set; } = new();
    public double StepElapsedSeconds { get; set; }
    public double RunElapsedSeconds { get; set; }
    public int ErrorCount { get; set; }
    public List<TestError> RecentErrors { get; set; } = new();
    public List<HeartbeatSample> Tail { get; set; } = new();
}

public sealed class JournalRecovery
{
    public string Folder { get; set; } = "";
    public JournalHeader Header { get; set; } = new();
    public HeartbeatState? LastHeartbeat { get; set; }
    public List<StepResult> CompletedSteps { get; set; } = new();
    public List<string> LogLines { get; set; } = new();
    /// <summary>Best guess of when the session died (last heartbeat or last file write).</summary>
    public DateTime LastAlive { get; set; }
}

/// <summary>
/// Crash-proof record of a running test session. Every couple of seconds the latest state is forced to disk
/// (alternating between two files so a half-written file never loses everything). If BenchForge starts and finds a
/// journal without a "done" marker, the previous session ended unexpectedly.
/// </summary>
public sealed class SessionJournal
{
    private readonly string _folder;
    private int _seq;
    private readonly object _lock = new();

    private SessionJournal(string folder) { _folder = folder; }

    public string Folder => _folder;

    public static SessionJournal Start(JournalHeader header, string? root = null)
    {
        var folder = Path.Combine(root ?? DataPaths.Journal, "session-" + header.RunId);
        Directory.CreateDirectory(folder);
        var j = new SessionJournal(folder);
        DataPaths.WriteDurable(Path.Combine(folder, "header.json"), Encoding.UTF8.GetBytes(Json.Serialize(header)));
        j.Log($"Session started: {header.Name}");
        return j;
    }

    public void Heartbeat(HeartbeatState state)
    {
        lock (_lock)
        {
            state.Sequence = ++_seq;
            var file = Path.Combine(_folder, (_seq % 2 == 0) ? "hb-a.json" : "hb-b.json");
            try { DataPaths.WriteDurable(file, Encoding.UTF8.GetBytes(Json.Serialize(state))); } catch { }
        }
    }

    public void Log(string line)
    {
        lock (_lock)
        {
            try { DataPaths.AppendDurable(Path.Combine(_folder, "log.txt"), $"{DateTime.Now:O} {line}"); } catch { }
        }
    }

    public void SaveCompletedSteps(List<StepResult> steps)
    {
        lock (_lock)
        {
            try { DataPaths.WriteDurable(Path.Combine(_folder, "steps.json"), Encoding.UTF8.GetBytes(Json.Serialize(steps))); } catch { }
        }
    }

    /// <summary>Marks the session as finished cleanly and removes the journal.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            try
            {
                File.WriteAllText(Path.Combine(_folder, "done"), DateTime.Now.ToString("O"));
                Directory.Delete(_folder, true);
            }
            catch { }
        }
    }

    public static List<JournalRecovery> FindUnfinished(string? root = null)
    {
        var list = new List<JournalRecovery>();
        var dir = root ?? DataPaths.Journal;
        if (!Directory.Exists(dir)) return list;
        foreach (var folder in Directory.GetDirectories(dir, "session-*"))
        {
            if (File.Exists(Path.Combine(folder, "done"))) { TryDelete(folder); continue; }
            var rec = Load(folder);
            if (rec != null) list.Add(rec);
        }
        return list;
    }

    public static JournalRecovery? Load(string folder)
    {
        try
        {
            var headerPath = Path.Combine(folder, "header.json");
            if (!File.Exists(headerPath)) return null;
            var header = Json.Deserialize<JournalHeader>(File.ReadAllText(headerPath));
            if (header == null) return null;
            var rec = new JournalRecovery { Folder = folder, Header = header, LastAlive = header.StartTime };
            foreach (var name in new[] { "hb-a.json", "hb-b.json" })
            {
                var p = Path.Combine(folder, name);
                if (!File.Exists(p)) continue;
                try
                {
                    var hb = Json.Deserialize<HeartbeatState>(File.ReadAllText(p));
                    if (hb != null && (rec.LastHeartbeat == null || hb.Sequence > rec.LastHeartbeat.Sequence)) rec.LastHeartbeat = hb;
                }
                catch { /* half-written file: the other one is used */ }
            }
            var steps = Path.Combine(folder, "steps.json");
            if (File.Exists(steps))
            {
                try { rec.CompletedSteps = Json.Deserialize<List<StepResult>>(File.ReadAllText(steps)) ?? new(); } catch { }
            }
            var log = Path.Combine(folder, "log.txt");
            if (File.Exists(log))
            {
                try { rec.LogLines = File.ReadAllLines(log).ToList(); } catch { }
            }
            if (rec.LastHeartbeat != null) rec.LastAlive = rec.LastHeartbeat.Time;
            var lastWrite = Directory.GetFiles(folder).Select(File.GetLastWriteTime).DefaultIfEmpty(header.StartTime).Max();
            if (lastWrite > rec.LastAlive) rec.LastAlive = lastWrite;
            return rec;
        }
        catch
        {
            return null;
        }
    }

    public static void Discard(JournalRecovery rec) => TryDelete(rec.Folder);

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); } catch { }
    }
}
