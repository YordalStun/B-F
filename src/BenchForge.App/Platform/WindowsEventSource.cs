using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using BenchForge.Core.Events;
using BenchForge.Core.Model;

namespace BenchForge.App.Platform;

/// <summary>Reads the Windows event logs (System, Application …) with XPath filters.</summary>
public sealed class WindowsEventSource : IEventSource
{
    public List<EventEntry> Query(EventQuerySpec spec, int max = 500)
    {
        var all = new List<EventEntry>();
        foreach (var log in spec.AllLogs)
        {
            var xpath = spec.BuildXPath(log);
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
                int read = 0;
                for (EventRecord? rec = reader.ReadEvent(); rec != null && read < max * 4; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        read++;
                        var e = Convert(rec, log);
                        if (!spec.MatchesText(e)) continue;
                        all.Add(e);
                        if (all.Count(x => x.Log == log) >= max) break;
                    }
                }
            }
            catch (EventLogNotFoundException) { }
            catch (EventLogException ex) { Core.Persistence.DataPaths.AppLogWrite($"Event query {log} failed: {ex.Message} ({xpath})"); }
            catch (UnauthorizedAccessException) { }
        }
        return all.OrderByDescending(e => e.Time).Take(max).ToList();
    }

    private static EventEntry Convert(EventRecord rec, string log)
    {
        var e = new EventEntry
        {
            Log = log, Time = rec.TimeCreated ?? DateTime.MinValue, Id = rec.Id, Provider = rec.ProviderName ?? "", Level = rec.Level ?? 0,
        };
        try { e.Message = rec.FormatDescription() ?? ""; } catch { }
        try { e.Properties = rec.Properties.Select(p => p.Value?.ToString() ?? "").ToList(); } catch { }
        try
        {
            var x = XDocument.Parse(rec.ToXml());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            foreach (var d in x.Descendants(ns + "Data"))
            {
                var name = d.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name)) e.Data[name] = d.Value;
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(e.Message))
            e.Message = e.Data.Count > 0 ? string.Join("; ", e.Data.Select(kv => $"{kv.Key}={kv.Value}")) : string.Join("; ", e.Properties);
        return e;
    }
}
