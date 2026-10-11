using System.Globalization;
using System.Net;
using System.Text;
using BenchForge.Core.Analysis;
using BenchForge.Core.Crash;
using BenchForge.Core.Model;
using BenchForge.Core.Scoring;

namespace BenchForge.Core.Reporting;

/// <summary>Self-contained HTML report (inline SVG charts, no internet needed) for sharing a run.</summary>
public static class HtmlReport
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(double v, string f = "0.#") => v.ToString(f, CultureInfo.InvariantCulture);

    public static string Build(RunResult r)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>BenchForge — {E(r.DisplayName)} {r.StartTime:yyyy-MM-dd HH:mm}</title><style>{Css}</style></head><body><div class=wrap>");
        sb.Append($"<header><div class=eyebrow>BenchForge report · {E(r.ImportedFrom ?? r.MachineName)}</div><h1>{E(r.DisplayName)}</h1>");
        sb.Append($"<div class=meta>{r.StartTime:dddd d MMMM yyyy HH:mm} · {TimeFormat.Short(r.Duration)} · <span class=\"st st-{r.Status}\">{StatusText(r.Status)}</span>{(r.TotalErrorCount > 0 ? $" · <b class=bad>{r.TotalErrorCount} errors</b>" : "")}</div></header>");

        // Scores
        if (r.OverallScore != null || r.ComponentScores.Count > 0)
        {
            sb.Append("<section class=scores>");
            if (r.OverallScore is double o) sb.Append($"<div class=\"score big\"><span>Overall</span><b>{Score.Format(o)}</b></div>");
            foreach (var (c, v) in r.ComponentScores) sb.Append($"<div class=score><span>{c.Display()}</span><b>{Score.Format(v)}</b></div>");
            sb.Append("</section><p class=hint>Scores are linear: 1000 = reference machine, 2000 = twice as fast.</p>");
        }

        if (r.Crash is CrashInfo crash)
        {
            sb.Append($"<section class=crash><h2>Crash: {E(crash.Summary)}</h2><ul>");
            foreach (var e in crash.Explanation) sb.Append($"<li>{E(e)}</li>");
            sb.Append("</ul>");
            if (crash.Signals.Count > 0)
            {
                sb.Append("<table><tr><th>Time</th><th>What</th><th>Detail</th></tr>");
                foreach (var s in crash.Signals.Take(30)) sb.Append($"<tr><td>{s.Time:HH:mm:ss}</td><td>{E(s.Title)}</td><td>{E(s.Detail)}</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("</section>");
        }

        if (r.Recommendations.Count > 0)
        {
            sb.Append("<section><h2>Recommended fixes</h2>");
            foreach (var rec in r.Recommendations)
            {
                sb.Append($"<div class=\"rec sev-{rec.Severity}\"><h3>{E(rec.Title)}</h3><p>{E(rec.Detail)}</p>");
                if (rec.Actions.Count > 0) sb.Append("<ol>" + string.Concat(rec.Actions.Select(a => $"<li>{E(a)}</li>")) + "</ol>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        if (r.Findings.Count > 0)
        {
            sb.Append("<section><h2>Findings</h2><ul class=findings>");
            foreach (var f in r.Findings) sb.Append($"<li class=\"sev-{f.Severity}\"><b>{E(f.Title)}</b>{(f.Detail.Length > 0 ? " — " + E(f.Detail) : "")}</li>");
            sb.Append("</ul></section>");
        }

        // Charts of key metrics.
        var charts = new (string Title, string Unit, string[] Keys)[]
        {
            ("Temperatures", "°C", new[] { KeyMetrics.CpuTemp, KeyMetrics.GpuTemp, KeyMetrics.GpuHotspot, KeyMetrics.DiskTemp }),
            ("Usage", "%", new[] { KeyMetrics.CpuLoad, KeyMetrics.GpuLoad, KeyMetrics.RamLoad }),
            ("Clocks", "MHz", new[] { KeyMetrics.CpuClock, KeyMetrics.GpuClock }),
            ("Power", "W", new[] { KeyMetrics.CpuPower, KeyMetrics.GpuPower }),
        };
        var chartHtml = new StringBuilder();
        foreach (var (title, unit, keys) in charts)
        {
            var series = keys.Select(k => (Label: KeyMetrics.Label(k), Points: r.Timeline.Series(KeyMetrics.ChannelId(k)).ToList())).Where(s => s.Points.Count > 1).ToList();
            if (series.Count > 0) chartHtml.Append($"<div class=chart><h3>{title}</h3>{Svg(series, unit)}</div>");
        }
        var fps = r.Timeline.Series("test/gpu.fps").ToList();
        if (fps.Count > 1) chartHtml.Append($"<div class=chart><h3>3D test FPS</h3>{Svg(new() { ("FPS", fps) }, "FPS")}</div>");
        if (chartHtml.Length > 0) sb.Append($"<section><h2>Sensors over time</h2><div class=charts>{chartHtml}</div></section>");

        // Detailed results per step.
        sb.Append("<section><h2>Test details</h2>");
        foreach (var s in r.Steps)
        {
            sb.Append($"<details open><summary><b>{E(s.Name)}</b> · {s.Kind} · {E(s.Status)}</summary>");
            foreach (var m in s.Modules)
            {
                sb.Append($"<h4>{m.Component.Display()}{(m.Score is double sc ? $" — {Score.Format(sc)} pts" : "")}{(m.Failed ? " <span class=bad>FAILED</span>" : "")}</h4>");
                if (m.FailureReason != null && m.Failed) sb.Append($"<p class=bad>{E(m.FailureReason)}</p>");
                if (m.SubScores.Count > 0)
                {
                    double maxScore = m.SubScores.Select(x => x.Score).DefaultIfEmpty(1).Max();
                    sb.Append("<table><tr><th>Test</th><th>Result</th><th>Score</th><th></th></tr>");
                    foreach (var x in m.SubScores)
                        sb.Append($"<tr><td>{E(x.Group)} · {E(x.Name)}{(x.Detail != null ? $"<br><small>{E(x.Detail)}</small>" : "")}</td><td class=num>{N(x.Value, "#,0.##")} {E(x.Unit)}</td><td class=num>{(x.Score > 0 ? Score.Format(x.Score) : "")}</td><td class=barcell>{(x.Score > 0 ? $"<i style=\"width:{N(Math.Min(100, x.Score / Math.Max(1, maxScore) * 100), "0")}%\"></i>" : "")}</td></tr>");
                    sb.Append("</table>");
                }
                if (m.Info.Count > 0) sb.Append("<dl>" + string.Concat(m.Info.Select(kv => $"<dt>{E(kv.Key)}</dt><dd>{E(kv.Value)}</dd>")) + "</dl>");
            }
            sb.Append("</details>");
        }
        sb.Append("</section>");

        if (r.Errors.Count > 0)
        {
            sb.Append("<section><h2>Errors</h2><table><tr><th>Time</th><th>Part</th><th>Test</th><th>Message</th></tr>");
            foreach (var e in r.Errors.Take(200)) sb.Append($"<tr><td>{e.Time:HH:mm:ss}</td><td>{e.Component.Display()}</td><td>{E(e.Test)}</td><td>{E(e.Message)} <small>{E(e.Detail)}</small></td></tr>");
            sb.Append("</table></section>");
        }

        if (r.SensorStats.Count > 0 || r.KeyStats.Count > 0)
        {
            sb.Append("<section><h2>All sensors (min / average / max)</h2><div class=scroll><table><tr><th>Hardware</th><th>Sensor</th><th>Min</th><th>Avg</th><th>Max</th></tr>");
            var stats = r.SensorStats.Count > 0 ? r.SensorStats.Where(s => s.HwType != HwType.Test && !s.Id.StartsWith("key/")) : r.KeyStats.Values;
            foreach (var s in stats.OrderBy(s => s.HwType).ThenBy(s => s.Hardware).ThenBy(s => s.Kind))
                sb.Append($"<tr><td>{E(s.Hardware)}</td><td>{E(s.Name)}</td><td class=num>{N(s.Min, "#,0.##")} {E(s.Unit)}</td><td class=num>{N(s.Avg, "#,0.##")}</td><td class=num>{N(s.Max, "#,0.##")}</td></tr>");
            sb.Append("</table></div></section>");
        }

        if (r.Hardware is HardwareInventory hw)
        {
            sb.Append("<section><h2>System</h2><dl>");
            sb.Append($"<dt>CPU</dt><dd>{E(hw.Cpu.Name)} — {hw.Cpu.Cores} cores / {hw.Cpu.Threads} threads</dd>");
            foreach (var g in hw.Gpus) sb.Append($"<dt>GPU</dt><dd>{E(g.Name)} {(g.VramGB > 0 ? N(g.VramGB) + " GB" : "")} · driver {E(g.DriverVersion)} {(g.DriverDate is DateTime d ? d.ToString("yyyy-MM-dd") : "")}</dd>");
            sb.Append($"<dt>RAM</dt><dd>{N(hw.TotalMemoryGB)} GB — {string.Join(", ", hw.MemoryModules.Select(m => $"{E(m.Slot)} {N(m.CapacityGB)} GB {m.ConfiguredSpeedMTs}/{m.SpeedMTs} MT/s {E(m.PartNumber)}"))}</dd>");
            foreach (var d in hw.Disks) sb.Append($"<dt>Drive</dt><dd>{E(d.Model)} {N(d.SizeGB, "0")} GB {E(d.BusType)} {E(d.MediaType)} · health {E(d.Health)}</dd>");
            sb.Append($"<dt>Board</dt><dd>{E(hw.Motherboard)} · BIOS {E(hw.BiosVersion)} {(hw.BiosDate is DateTime bd ? bd.ToString("yyyy-MM-dd") : "")}</dd><dt>OS</dt><dd>{E(hw.OsName)} {E(hw.OsVersion)}</dd></dl></section>");
        }

        if (r.RelatedEvents.Count > 0)
        {
            sb.Append("<section><h2>Windows events around the crash</h2><table><tr><th>Time</th><th>Source</th><th>ID</th><th>Message</th></tr>");
            foreach (var e in r.RelatedEvents.Take(100)) sb.Append($"<tr><td>{e.Time:yyyy-MM-dd HH:mm:ss}</td><td>{E(e.Provider)}</td><td>{e.Id}</td><td>{E(e.FirstLine)}</td></tr>");
            sb.Append("</table></section>");
        }
        sb.Append("<footer>Generated by BenchForge</footer></div></body></html>");
        return sb.ToString();
    }

    public static string BuildComparison(IReadOnlyList<RunResult> runs, PatternReport? pattern)
    {
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>BenchForge comparison</title><style>{Css}</style></head><body><div class=wrap>");
        sb.Append($"<header><div class=eyebrow>BenchForge comparison</div><h1>{runs.Count} runs compared</h1></header>");
        if (pattern != null)
        {
            sb.Append($"<section class=crash><h2>{E(pattern.Verdict)}</h2><ul>");
            foreach (var f in pattern.Findings) sb.Append($"<li class=\"sev-{f.Severity}\"><b>{E(f.Title)}</b> — {E(f.Detail)}</li>");
            sb.Append("</ul>");
            if (pattern.NextSteps.Count > 0) sb.Append("<h3>Next steps</h3><ol>" + string.Concat(pattern.NextSteps.Select(s => $"<li>{E(s)}</li>")) + "</ol>");
            sb.Append("</section>");
        }
        sb.Append("<section><div class=scroll><table><tr><th>Test</th>");
        foreach (var r in runs) sb.Append($"<th>{E(r.DisplayName)}<br><small>{r.StartTime:yyyy-MM-dd HH:mm} · {StatusText(r.Status)}</small></th>");
        sb.Append("</tr>");
        foreach (var row in RunComparison.Build(runs))
        {
            sb.Append($"<tr><td>{E(row.Group)} · {E(row.Name)}</td>");
            double? first = row.Values.FirstOrDefault(v => v != null);
            int firstIndex = Array.FindIndex(row.Values, x => x != null);
            for (int vi = 0; vi < row.Values.Length; vi++)
            {
                var v = row.Values[vi];
                string delta = "";
                if (v is double x && first is double f0 && f0 != 0 && vi != firstIndex)
                {
                    double pct = (x / f0 - 1) * 100;
                    bool better = row.HigherIsBetter ? pct > 0 : pct < 0;
                    if (Math.Abs(pct) >= 1) delta = $" <small class={(better ? "good" : "bad")}>{(pct > 0 ? "+" : "")}{N(pct, "0")}%</small>";
                }
                sb.Append($"<td class=num>{(v is double vv ? N(vv, "#,0.#") + " " + E(row.Unit) : "—")}{delta}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table></div></section><footer>Generated by BenchForge</footer></div></body></html>");
        return sb.ToString();
    }

    public static string StatusText(RunStatus s) => s switch
    {
        RunStatus.Completed => "Passed",
        RunStatus.CompletedWithErrors => "Errors found",
        RunStatus.Aborted => "Stopped (safety)",
        RunStatus.Cancelled => "Stopped by user",
        RunStatus.Crashed => "Crashed",
        _ => "Running",
    };

    private static readonly string[] Colors = { "#ff7a3d", "#3fa7ff", "#3cc47a", "#c77dff", "#f0b429" };

    private static string Svg(List<(string Label, List<(double T, double V)> Points)> series, string unit)
    {
        const int W = 640, H = 220, L = 46, R = 10, T = 10, B = 26;
        double tMin = series.Min(s => s.Points.First().T), tMax = Math.Max(tMin + 1, series.Max(s => s.Points.Last().T));
        double vMin = Math.Min(0, series.Min(s => s.Points.Min(p => p.V))), vMax = series.Max(s => s.Points.Max(p => p.V));
        if (unit == "%") vMax = 100;
        vMax = NiceCeil(vMax <= vMin ? vMin + 1 : vMax);
        double X(double t) => L + (t - tMin) / (tMax - tMin) * (W - L - R);
        double Y(double v) => T + (1 - (v - vMin) / (vMax - vMin)) * (H - T - B);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=img><g class=grid>");
        for (int i = 0; i <= 4; i++)
        {
            double v = vMin + (vMax - vMin) * i / 4;
            sb.Append($"<line x1={L} x2={W - R} y1={N(Y(v), "0.0")} y2={N(Y(v), "0.0")} /><text x={L - 6} y={N(Y(v) + 4, "0.0")} text-anchor=end>{N(v, "0")}</text>");
        }
        for (int i = 0; i <= 4; i++)
        {
            double t = tMin + (tMax - tMin) * i / 4;
            sb.Append($"<text x={N(X(t), "0.0")} y={H - 6} text-anchor=middle>{TimeFormat.Short(TimeSpan.FromSeconds(t))}</text>");
        }
        sb.Append("</g>");
        int ci = 0;
        foreach (var s in series)
        {
            var pts = Downsample(s.Points, 600);
            sb.Append($"<polyline fill=none stroke=\"{Colors[ci % Colors.Length]}\" stroke-width=1.6 points=\"{string.Join(" ", pts.Select(p => $"{N(X(p.T), "0.0")},{N(Y(p.V), "0.0")}"))}\" />");
            ci++;
        }
        sb.Append("</svg><div class=legend>");
        ci = 0;
        foreach (var s in series) sb.Append($"<span><i style=\"background:{Colors[ci++ % Colors.Length]}\"></i>{E(s.Label)} (max {N(s.Points.Max(p => p.V), "0.#")} {E(unit)})</span>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static List<(double T, double V)> Downsample(List<(double T, double V)> p, int max)
    {
        if (p.Count <= max) return p;
        int step = (int)Math.Ceiling(p.Count / (double)max);
        return p.Where((_, i) => i % step == 0).ToList();
    }

    private static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (m * mag >= v) return m * mag;
        return 10 * mag;
    }

    private const string Css = """
    :root{--bg:#f2f4f7;--panel:#fff;--fg:#14181d;--muted:#5b6672;--line:#d8dee5;--accent:#e2561b;--ok:#1f8a4c;--warn:#b7791f;--bad:#c53030}
    @media (prefers-color-scheme:dark){:root{--bg:#0f1317;--panel:#171c22;--fg:#e6eaee;--muted:#93a0ad;--line:#2a323b;--accent:#ff7a3d;--ok:#3cc47a;--warn:#f0b429;--bad:#ff6b6b;color-scheme:dark}}
    body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.55 "Segoe UI",system-ui,sans-serif}
    .wrap{max-width:1000px;margin:0 auto;padding:24px 16px 48px}
    h1{font-size:32px;margin:4px 0}h2{font-size:20px;margin:0 0 10px}h3{font-size:16px;margin:8px 0 4px}h4{margin:14px 0 6px}
    .eyebrow{text-transform:uppercase;letter-spacing:.08em;font-size:12px;color:var(--muted)}
    .meta{color:var(--muted)}section{margin-top:28px}
    .scores{display:flex;flex-wrap:wrap;gap:12px}.score{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 18px;min-width:120px}
    .score span{display:block;color:var(--muted);font-size:13px}.score b{font-size:28px;font-variant-numeric:tabular-nums}.score.big b{color:var(--accent);font-size:36px}
    .hint{color:var(--muted);font-size:13px}
    .crash{background:var(--panel);border:1px solid var(--bad);border-radius:10px;padding:16px}
    .rec{background:var(--panel);border:1px solid var(--line);border-left:4px solid var(--muted);border-radius:8px;padding:10px 14px;margin-bottom:10px}
    .sev-Critical{border-left-color:var(--bad)}.sev-Warning{border-left-color:var(--warn)}.sev-Info{border-left-color:var(--ok)}
    .findings li{margin:4px 0}.findings .sev-Critical b{color:var(--bad)}.findings .sev-Warning b{color:var(--warn)}
    .charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px}.chart{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:10px}
    svg{width:100%;height:auto}.grid line{stroke:var(--line)}.grid text{fill:var(--muted);font-size:11px}
    .legend{display:flex;flex-wrap:wrap;gap:10px;font-size:12px;color:var(--muted)}.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:4px}
    table{border-collapse:collapse;width:100%;background:var(--panel);font-size:14px}th,td{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}
    th{color:var(--muted);font-weight:600}.num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
    .barcell{width:22%}.barcell i{display:block;height:8px;background:var(--accent);border-radius:4px}
    .scroll{overflow-x:auto}details{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:10px 14px;margin-bottom:10px}
    dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 14px}dt{color:var(--muted)}dd{margin:0}
    .bad{color:var(--bad)}.good{color:var(--ok)}small{color:var(--muted)}
    .st{font-weight:600}.st-Completed{color:var(--ok)}.st-Crashed,.st-CompletedWithErrors,.st-Aborted{color:var(--bad)}
    footer{margin-top:40px;color:var(--muted);font-size:12px}
    """;
}
