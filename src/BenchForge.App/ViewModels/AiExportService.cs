using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using BenchForge.Core.Ai;
using BenchForge.Core.Analysis;
using BenchForge.Core.Crash;
using BenchForge.Core.Events;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using Microsoft.Win32;

namespace BenchForge.App.ViewModels;

/// <summary>Saves everything an AI needs as one Markdown file (or a zip bundle) to upload to Claude.ai or any AI chat.</summary>
public static class AiExportService
{
    public static async void Export(IReadOnlyList<RunResult> runs, IReadOnlyList<SystemCrash>? crashes = null, IReadOnlyList<EventEntry>? events = null, string? question = null)
    {
        var s = Services.Instance;
        var hw = s.Inventory ?? runs.Select(r => r.Hardware).FirstOrDefault(h => h != null);
        var checks = hw != null ? RecommendationEngine.SystemChecks(hw) : null;
        DiagnosticsExtras extras;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { extras = await Task.Run(() => Platform.DiagnosticsCollector.Collect(s.Events)); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        var md = AiExport.BuildMarkdown(runs, crashes, events, hw, checks, question, extras: extras);
        var dlg = new SaveFileDialog
        {
            Title = "Save the file to upload to an AI",
            Filter = "Markdown text for AI (*.md)|*.md|Full diagnostic bundle with result files (*.zip)|*.zip",
            FileName = AiExport.FileName(DateTime.Now),
            InitialDirectory = DataPaths.Reports,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                WriteBundle(dlg.FileName, md, runs, events, extras, hw);
            else
                File.WriteAllText(dlg.FileName, md);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save the file: " + ex.Message, "BenchForge");
            return;
        }

        try { Clipboard.SetText(AiExport.StarterPrompt); } catch { }
        var answer = MessageBox.Show(
            $"Saved {Path.GetFileName(dlg.FileName)} ({new FileInfo(dlg.FileName).Length / 1024} KB).\n\n" +
            "Upload it to Claude.ai (or any AI chat) and paste the question that is now on your clipboard:\n\n" +
            $"“{AiExport.StarterPrompt}”\n\n" +
            "Nothing is sent anywhere by BenchForge. Open the folder now?",
            "Export for AI", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
            try { Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\""); } catch { }
    }

    /// <summary>The md file + the .bfresult files (re-importable into BenchForge) + BenchForge's log. No crash dumps (binary and private).</summary>
    private static void WriteBundle(string zipPath, string md, IReadOnlyList<RunResult> runs, IReadOnlyList<EventEntry>? events, DiagnosticsExtras extras, HardwareInventory? hw)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Text(string name, string content) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(content); }
        Text(Path.ChangeExtension(Path.GetFileName(zipPath), ".md"), md);
        Text("README.txt", "BenchForge diagnostic bundle\r\n\r\n- The .md file is the summary to upload to an AI (start there).\r\n- sensors/*.csv: every recorded sensor for each run (open in Excel or give to an AI).\r\n- results/*.bfresult: original result files — import them in BenchForge › History › Import.\r\n- events.csv, reliability.csv, drivers.csv, hardware.json: raw system data.\r\n");
        foreach (var r in runs)
        {
            var name = $"{r.StartTime:yyyyMMdd-HHmmss}-{r.Id}";
            using (var es = zip.CreateEntry($"results/{name}.bfresult").Open()) es.Write(Json.SerializeGzip(r));
            if (r.Timeline.Samples.Count > 0) Text($"sensors/{name}-{Sanitize(r.DisplayName)}.csv", AiExport.FullSensorCsv(r.Timeline));
        }
        var allEvents = (events ?? Array.Empty<EventEntry>()).Concat(extras.RecentErrors).Concat(runs.SelectMany(r => r.RelatedEvents))
            .DistinctBy(e => (e.Provider, e.Id, e.Time, e.Log)).OrderByDescending(e => e.Time).ToList();
        if (allEvents.Count > 0) Text("events.csv", AiExport.EventsCsv(allEvents));
        if (extras.Reliability.Count > 0)
            Text("reliability.csv", "time,source,product,event_id,message\r\n" + string.Join("\r\n", extras.Reliability.Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss},\"{e.Source}\",\"{e.Product}\",{e.EventId},\"{e.Message.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ")}\"")));
        if (extras.Drivers.Count > 0)
            Text("drivers.csv", "class,device,provider,version,date\r\n" + string.Join("\r\n", extras.Drivers.Select(d => $"{d.DeviceClass},\"{d.Device}\",\"{d.Provider}\",{d.Version},{d.Date:yyyy-MM-dd}")));
        if (hw != null) Text("hardware.json", Json.Serialize(hw, indented: true));
        if (File.Exists(DataPaths.AppLog))
        {
            try
            {
                using var fs = new FileStream(DataPaths.AppLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var es = zip.CreateEntry("benchforge.log").Open();
                fs.CopyTo(es);
            }
            catch { }
        }
    }

    private static string Sanitize(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

    /// <summary>"Something is wrong with this PC — here's everything": recent runs, 30 days of crashes and hardware/GPU/disk events, system checks.</summary>
    public static async Task ExportEverything()
    {
        var s = Services.Instance;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        List<RunResult> runs; List<SystemCrash> crashes; List<EventEntry> events;
        try
        {
            (runs, crashes, events) = await Task.Run(() =>
            {
                var runList = s.Store.List().Take(10).Select(x => ResultStore.Load(x.FilePath)).Where(r => r != null).Select(r => r!).ToList();
                var since = DateTime.Now.AddDays(-30);
                var spec = new EventQuerySpec { Since = since };
                foreach (var p in new[] { EventPresets.BlueScreens, EventPresets.UnexpectedShutdowns, EventPresets.Whea, EventPresets.GpuDriver, EventPresets.Disk, EventPresets.Thermal, EventPresets.MemoryDiag })
                    spec.Selectors.AddRange(p.Selectors);
                var ev = new List<EventEntry>();
                try { ev = s.Events.Query(spec, 400); } catch { }
                var crashList = CrashAnalyzer.FindSystemCrashes(ev, MinidumpReader.ListDumps(null, since));
                // Crashes already covered by a BenchForge run are shown with that run.
                crashList = crashList.Where(c => !runList.Any(r => r.Crash != null && Math.Abs((r.Crash.LastHeartbeat - c.Time).TotalMinutes) < 30)).ToList();
                return (runList, crashList, ev);
            });
        }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        Export(runs, crashes, events);
    }
}
