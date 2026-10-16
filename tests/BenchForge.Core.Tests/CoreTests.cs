using BenchForge.Core.Ai;
using BenchForge.Core.Analysis;
using BenchForge.Core.Cpu;
using BenchForge.Core.Crash;
using BenchForge.Core.Engine;
using BenchForge.Core.Events;
using BenchForge.Core.Memory;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using BenchForge.Core.Scoring;
using Xunit;

namespace BenchForge.Core.Tests;

public class ScoringTests
{
    [Fact]
    public void Scores_are_linear()
    {
        Assert.Equal(1000, Score.Linear(50, 50), 6);
        Assert.Equal(2000, Score.Linear(100, 50), 6);
        Assert.Equal(500, Score.Linear(25, 50), 6);
        // Lower-is-better (latency): half the latency = double the score.
        Assert.Equal(2000, Score.Linear(40, 80, higherIsBetter: false), 6);
    }

    [Fact]
    public void Geometric_mean_scales_linearly()
    {
        var a = Score.GeoMean(new[] { 1000.0, 1000.0, 1000.0 });
        var b = Score.GeoMean(new[] { 2000.0, 2000.0, 2000.0 });
        Assert.Equal(1000, a, 6);
        Assert.Equal(2 * a, b, 6);
        Assert.Equal(0, Score.GeoMean(Array.Empty<double>()));
    }
}

public class PlanTests
{
    [Fact]
    public void Total_duration_includes_loops()
    {
        var p = new TestPlan { Loops = 3, Steps = { new("a", StepKind.Stress, 60, Component.Cpu), new("b", StepKind.Stress, 30, Component.Gpu) } };
        Assert.Equal(270, p.TotalSeconds);
        Assert.Equal(6, p.Expand().Count);
        Assert.Contains("loop 3/3", p.Expand()[^1].Name);
    }

    [Fact]
    public void Burn_in_is_split_into_checkpoint_steps_and_keeps_exact_length()
    {
        var p = PlanPresets.BurnIn(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5), new[] { Component.Cpu, Component.Gpu });
        Assert.Equal(7500, p.TotalSeconds, 3);
        Assert.All(p.Steps, s => Assert.Equal(new[] { Component.Cpu, Component.Gpu }, s.Components));
        Assert.True(p.Steps.All(s => s.DurationSeconds <= 900));
    }

    [Fact]
    public void Rotation_covers_every_component_alone()
    {
        var p = PlanPresets.StabilityRotation(TimeSpan.FromHours(1));
        foreach (var c in Enum.GetValues<Component>())
            Assert.Contains(p.Steps, s => s.Components.Count == 1 && s.Components[0] == c);
        Assert.Equal(3600, p.TotalSeconds, 3);
    }
}

public class CpuKernelTests
{
    [Fact]
    public void Kernels_are_deterministic()
    {
        foreach (var k in CpuKernel.All())
        {
            var s1 = k.CreateState();
            var s2 = k.CreateState();
            for (int seed = 0; seed < 3; seed++)
                Assert.Equal(k.RunUnit(s1, seed), k.RunUnit(s2, seed));
            Assert.NotEqual(k.RunUnit(s1, 0), k.RunUnit(s1, 1));
        }
    }

    [Fact]
    public void Reference_checksums_match_fresh_runs()
    {
        var k = new SieveKernel();
        var refs = CpuModule.ReferenceChecksums(k);
        Assert.Equal(refs[2], k.RunUnit(k.CreateState(), 2));
    }
}

public unsafe class MemoryPatternTests
{
    [Theory]
    [InlineData(MemoryModule.Pattern.AddressInAddress)]
    [InlineData(MemoryModule.Pattern.Checkerboard)]
    [InlineData(MemoryModule.Pattern.WalkingOnes)]
    [InlineData(MemoryModule.Pattern.Random)]
    public void Healthy_memory_has_no_errors(MemoryModule.Pattern pattern)
    {
        var buf = new ulong[4096];
        fixed (ulong* p = buf)
            Assert.Equal(0, MemoryModule.TestBlock(p, buf.Length, pattern, 12345, null));
    }
}

public class MinidumpTests
{
    [Fact]
    public void Parses_64bit_kernel_dump_header()
    {
        var h = new byte[0x1000];
        System.Text.Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(h, 0);
        BitConverter.GetBytes(16u).CopyTo(h, 0x34);
        BitConverter.GetBytes(0x124u).CopyTo(h, 0x38);
        BitConverter.GetBytes(0UL).CopyTo(h, 0x40);
        BitConverter.GetBytes(0xFFFF8000AAAA0000UL).CopyTo(h, 0x48);
        var info = new MinidumpInfo();
        MinidumpReader.ParseHeader(h, info);
        Assert.True(info.Valid);
        Assert.True(info.Is64Bit);
        Assert.Equal(0x124u, info.BugCheckCode);
        Assert.Equal(16, info.Processors);
        Assert.Equal(0xFFFF8000AAAA0000UL, info.Parameters[1]);
        Assert.Equal(Component.Cpu, BugCheckKnowledgeBase.Get(info.BugCheckCode)!.LikelyComponent);
        Assert.Contains("Machine Check", BugCheckKnowledgeBase.DescribeParameters(0x124, info.Parameters));
    }

    [Fact]
    public void Rejects_non_dump_files()
    {
        var info = new MinidumpInfo();
        MinidumpReader.ParseHeader(new byte[0x100], info);
        Assert.False(info.Valid);
    }

    [Fact]
    public void Extended_bugcheck_codes_map_to_base_code()
    {
        Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", BugCheckKnowledgeBase.Get(0x1000007E)!.Name);
    }

    [Fact]
    public void Parses_cdb_output()
    {
        var f = CdbOutput.Parse("blah\nMODULE_NAME: nvlddmkm\nIMAGE_NAME:  nvlddmkm.sys\nFAILURE_BUCKET_ID:  0x116_IMAGE_nvlddmkm.sys\n");
        Assert.Equal("nvlddmkm.sys", f["IMAGE_NAME"]);
        Assert.Equal("nvlddmkm", f["MODULE_NAME"]);
    }
}

public class EventTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0);

    [Theory]
    [InlineData("all bluescreens", "bsod")]
    [InlineData("show me BSODs from last week", "bsod")]
    [InlineData("gpu driver crashes today", "gpu")]
    [InlineData("whea errors", "whea")]
    [InlineData("unexpected shutdowns last month", "shutdown")]
    [InlineData("disk errors", "disk")]
    [InlineData("is my cpu throttling", "thermal")]
    public void Parser_maps_phrases_to_presets(string text, string presetKey)
    {
        var spec = EventQueryParser.Parse(text, Now);
        var preset = EventPresets.All.First(p => p.Key == presetKey);
        Assert.Contains(preset.Selectors[0], spec.Selectors);
    }

    [Fact]
    public void Parser_understands_time()
    {
        Assert.Equal(Now.AddDays(-7), EventQueryParser.Parse("crashes last week", Now).Since);
        Assert.Equal(Now.Date, EventQueryParser.Parse("bluescreens today", Now).Since);
        Assert.Equal(Now.AddDays(-3), EventQueryParser.Parse("whea last 3 days", Now).Since);
        Assert.Null(EventQueryParser.Parse("all bluescreens", Now).Since);
    }

    [Fact]
    public void Plain_crash_means_system_crashes()
    {
        var spec = EventQueryParser.Parse("crashes", Now);
        Assert.Contains(EventPresets.BlueScreens.Selectors[0], spec.Selectors);
        Assert.Contains(EventPresets.UnexpectedShutdowns.Selectors[0], spec.Selectors);
    }

    [Fact]
    public void Event_ids_are_recognised()
    {
        var spec = EventQueryParser.Parse("event 41", Now);
        Assert.Contains(spec.Selectors, s => s.Ids.Contains(41));
    }

    [Fact]
    public void Builds_valid_xpath()
    {
        var spec = EventPresets.BlueScreens.ToSpec(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var x = spec.BuildXPath("System");
        Assert.StartsWith("*[System[", x);
        Assert.Contains("Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting']", x);
        Assert.Contains("EventID=1001", x);
        Assert.Contains("TimeCreated[@SystemTime>='2026-01-01T00:00:00.000Z']", x);
        Assert.Equal("*", new EventQuerySpec().BuildXPath("System"));
    }

    [Fact]
    public void Interprets_bugcheck_event()
    {
        var e = new EventEntry
        {
            Provider = "Microsoft-Windows-WER-SystemErrorReporting", Id = 1001, Level = 2, Time = Now,
            Message = "The computer has rebooted from a bugcheck.  The bugcheck was: 0x00000116 (0xffffa50c1d0c4460, 0xfffff80354a2e1c0, 0xffffffffc000009a, 0x0000000000000004). A dump was saved in: C:\\Windows\\MEMORY.DMP.",
        };
        var s = EventInterpreter.Interpret(e)!;
        Assert.Equal(SignalKind.BugCheck, s.Kind);
        Assert.Equal(0x116u, s.BugCheckCode);
        Assert.Equal(Component.Gpu, s.Component);
    }

    [Fact]
    public void Kernel_power_41_without_bugcheck_is_power_loss()
    {
        var e = new EventEntry { Provider = "Microsoft-Windows-Kernel-Power", Id = 41, Level = 1, Time = Now, Data = { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "0" } };
        Assert.Equal(SignalKind.UnexpectedShutdown, EventInterpreter.Interpret(e)!.Kind);
        e.Data["BugcheckCode"] = "292"; // 0x124
        var s = EventInterpreter.Interpret(e)!;
        Assert.Equal(SignalKind.BugCheck, s.Kind);
        Assert.Equal(0x124u, s.BugCheckCode);
    }

    [Fact]
    public void Interprets_whea_and_tdr()
    {
        var whea = new EventEntry
        {
            Provider = "Microsoft-Windows-WHEA-Logger", Id = 18, Level = 1, Time = Now,
            Message = "A fatal hardware error has occurred.\r\n\r\nReported by component: Processor Core\r\nError Source: Machine Check Exception\r\nError Type: Cache Hierarchy Error\r\nProcessor APIC ID: 6\r\n",
        };
        var s = EventInterpreter.Interpret(whea)!;
        Assert.Equal(SignalKind.WheaFatal, s.Kind);
        Assert.Equal(Component.Cpu, s.Component);
        Assert.Contains("APIC ID 6", s.HardwareSource);

        var tdr = new EventEntry { Provider = "Display", Id = 4101, Level = 3, Time = Now, Message = "Display driver nvlddmkm stopped responding and has successfully recovered." };
        var t = EventInterpreter.Interpret(tdr)!;
        Assert.Equal(SignalKind.GpuDriverReset, t.Kind);
        Assert.Equal("nvlddmkm", t.Module);
    }

    [Fact]
    public void Groups_crash_events_into_crashes()
    {
        var events = new List<EventEntry>
        {
            new() { Provider = "Microsoft-Windows-Kernel-Power", Id = 41, Level = 1, Time = Now, Data = { ["BugcheckCode"] = "0" } },
            new() { Provider = "EventLog", Id = 6008, Level = 2, Time = Now.AddSeconds(30), Message = "The previous system shutdown at 11:50 was unexpected." },
            new() { Provider = "Microsoft-Windows-WHEA-Logger", Id = 19, Level = 3, Time = Now.AddMinutes(-5), Message = "Component: Processor Core\nError Source: Corrected Machine Check" },
            new() { Provider = "Microsoft-Windows-Kernel-Power", Id = 41, Level = 1, Time = Now.AddDays(-2), Data = { ["BugcheckCode"] = "278" } },
        };
        var crashes = CrashAnalyzer.FindSystemCrashes(events);
        Assert.Equal(2, crashes.Count);
        Assert.Equal(CrashKind.PowerLossOrHang, crashes[0].Info.Kind);
        Assert.Contains(crashes[0].Info.Signals, s => s.Kind == SignalKind.WheaCorrected);
        Assert.Equal(CrashKind.BlueScreen, crashes[1].Info.Kind);
    }
}

public class PatternTests
{
    private static RunResult Crashed(uint code, Component during, double cpuTemp, double minutes)
    {
        var r = new RunResult { Status = RunStatus.Crashed, StartTime = DateTime.Now.AddHours(-minutes) };
        r.Crash = new CrashInfo
        {
            Kind = CrashKind.BlueScreen, ActiveComponents = { during }, StepName = $"{during} stress", RunElapsedSeconds = minutes * 60,
            PeakMetrics = { [KeyMetrics.CpuTemp] = cpuTemp },
            Signals = { new CrashSignal { Kind = SignalKind.BugCheck, BugCheckCode = code } },
        };
        return r;
    }

    [Fact]
    public void Finds_common_stop_code_component_and_heat()
    {
        var runs = new[] { Crashed(0x124, Component.Cpu, 97, 21), Crashed(0x124, Component.Cpu, 96, 23) };
        var rep = PatternAnalyzer.Analyze(runs.Select(FailureCase.FromRun).ToList());
        Assert.Equal(Component.Cpu, rep.Suspected);
        Assert.Contains(rep.Findings, f => f.Title.Contains("same stop code"));
        Assert.Contains(rep.Findings, f => f.Title.Contains("CPU was under load"));
        Assert.Contains(rep.Findings, f => f.Title.Contains("CPU temperature"));
        Assert.Contains(rep.Findings, f => f.Title.Contains("similar time"));
    }

    [Fact]
    public void Different_stop_codes_point_at_memory()
    {
        var runs = new[] { Crashed(0x1A, Component.Cpu, 60, 5), Crashed(0x50, Component.Gpu, 60, 40), Crashed(0x3B, Component.Memory, 60, 80) };
        var rep = PatternAnalyzer.Analyze(runs.Select(FailureCase.FromRun).ToList());
        Assert.Contains(rep.Findings, f => f.Title.Contains("different stop codes"));
        Assert.Equal(Component.Memory, rep.Suspected);
    }

    [Fact]
    public void Comparison_rows_cover_scores()
    {
        var a = new RunResult { OverallScore = 1000, ComponentScores = { [Component.Cpu] = 1000 } };
        var b = new RunResult { OverallScore = 1100, ComponentScores = { [Component.Cpu] = 1200 } };
        var rows = RunComparison.Build(new[] { a, b });
        Assert.Equal(1200, rows.First(r => r.Name == "CPU score").Values[1]);
    }
}

public class RecommendationTests
{
    [Fact]
    public void Cpu_errors_produce_critical_fix()
    {
        var r = new RunResult { Status = RunStatus.CompletedWithErrors };
        r.Steps.Add(new StepResult { Modules = { new ModuleResult { Component = Component.Cpu, Kind = StepKind.Stress, ErrorCount = 3 } } });
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Severity == Severity.Critical && f.Component == Component.Cpu);
        Assert.Contains(r.Recommendations, x => x.Title == "Make the CPU stable");
    }

    [Fact]
    public void Hot_cpu_recommends_cooling()
    {
        var r = new RunResult { Status = RunStatus.Completed, KeyStats = { [KeyMetrics.CpuTemp] = new ChannelStats { Max = 98 } } };
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Recommendations, x => x.Title == "Improve cooling");
    }

    [Fact]
    public void Xmp_off_is_detected()
    {
        var r = new RunResult { Status = RunStatus.Completed, Hardware = new HardwareInventory { MemoryModules = { new MemoryModuleInfo { SpeedMTs = 3600, ConfiguredSpeedMTs = 2133 }, new MemoryModuleInfo { SpeedMTs = 3600, ConfiguredSpeedMTs = 2133 } } } };
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Recommendations, x => x.Title == "Enable XMP / EXPO");
    }
}

public class PersistenceTests
{
    [Fact]
    public void Result_round_trips_through_bfresult()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new ResultStore(dir);
        var r = new RunResult { Name = "t", StartTime = DateTime.Now, Status = RunStatus.Completed, OverallScore = 1234, ComponentScores = { [Component.Gpu] = 999 } };
        r.Timeline.Channels.Add(new SensorChannel { Id = "x", Name = "X" });
        r.Timeline.Samples.Add(new TimelineSample { T = 1, V = new float?[] { 42 } });
        var path = store.Save(r);
        var back = ResultStore.Load(path)!;
        Assert.Equal(1234, back.OverallScore);
        Assert.Equal(999, back.ComponentScores[Component.Gpu]);
        Assert.Equal(42f, back.Timeline.Samples[0].V[0]);
        Assert.Single(store.List());
    }

    [Fact]
    public void Imported_results_are_marked()
    {
        var src = Directory.CreateTempSubdirectory().FullName;
        var dst = Directory.CreateTempSubdirectory().FullName;
        var r = new RunResult { Name = "other pc", MachineName = "OTHER-PC", StartTime = DateTime.Now };
        var file = Path.Combine(src, "x.bfresult");
        new ResultStore(src).Export(r, file);
        var imported = new ResultStore(dst).Import(file);
        Assert.True(imported.Imported);
        Assert.Equal("OTHER-PC", imported.ImportedFrom);
    }

    [Fact]
    public void Journal_survives_and_is_found()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var j = SessionJournal.Start(new JournalHeader { RunId = "abc", Name = "burn", StartTime = DateTime.Now, BootTime = DateTime.Now.AddHours(-1) }, dir);
        j.Log("Step 1 started");
        j.Heartbeat(new HeartbeatState { Time = DateTime.Now, StepName = "Burn 1", Components = { Component.Cpu }, Tail = { new HeartbeatSample { Time = DateTime.Now, Metrics = { [KeyMetrics.CpuTemp] = 91 } } } });
        j.Heartbeat(new HeartbeatState { Time = DateTime.Now, StepName = "Burn 2", Components = { Component.Cpu } });
        var found = SessionJournal.FindUnfinished(dir);
        var rec = Assert.Single(found);
        Assert.Equal("Burn 2", rec.LastHeartbeat!.StepName);
        Assert.Contains(rec.LogLines, l => l.Contains("Step 1 started"));
        j.Complete();
        Assert.Empty(SessionJournal.FindUnfinished(dir));
    }
}

public class FixTests
{
    [Fact]
    public void Stop_codes_map_to_software_fixes()
    {
        Assert.Contains(FixId.MemoryDiagnostic, RecommendationEngine.FixesForBugCheck(0x1A));
        Assert.Contains(FixId.ScanDisk, RecommendationEngine.FixesForBugCheck(0x7A));
        Assert.Contains(FixId.DisableFastStartup, RecommendationEngine.FixesForBugCheck(0x9F));
        Assert.Empty(RecommendationEngine.FixesForBugCheck(0x124)); // hardware: guidance only
    }

    [Fact]
    public void System_checks_offer_crash_dump_and_power_fixes()
    {
        var checks = RecommendationEngine.SystemChecks(new HardwareInventory { CrashDumpSetting = 0, PowerPlan = "Power saver" });
        Assert.Contains(checks, r => r.Fixes.Contains(FixId.EnableCrashDumps));
        Assert.Contains(checks, r => r.Fixes.Contains(FixId.PowerPlanBalanced));
        Assert.DoesNotContain(checks, r => r.Title == "No problems found");
    }
}

public class AiExportTests
{
    private static RunResult CrashedRun()
    {
        var start = new DateTime(2026, 10, 1, 20, 0, 0);
        var r = new RunResult { Name = "Burn test", StartTime = start, EndTime = start.AddMinutes(22), Status = RunStatus.Crashed, MachineName = "TESTPC" };
        r.Hardware = new HardwareInventory { ComputerName = "TESTPC", Cpu = { Name = "Test CPU 9000" }, CrashDumpSetting = 3 };
        r.Timeline.Channels.Add(new SensorChannel { Id = KeyMetrics.ChannelId(KeyMetrics.CpuTemp), Name = "CPU temperature", Unit = "°C" });
        r.Timeline.Channels.Add(new SensorChannel { Id = "test/cpu.stress", Name = "CPU stress throughput", Unit = "pts/s", HwType = HwType.Test });
        for (int i = 0; i < 1000; i++) r.Timeline.Samples.Add(new TimelineSample { T = i, V = new float?[] { 60 + i % 40, 1000 } });
        r.Crash = new CrashInfo
        {
            Kind = CrashKind.BlueScreen, Summary = "Blue screen 0x124 WHEA_UNCORRECTABLE_ERROR", StepName = "Burn 2", ActiveComponents = { Component.Cpu },
            Signals = { new CrashSignal { Kind = SignalKind.BugCheck, BugCheckCode = 0x124, Title = "Blue screen: 0x124" } },
            Tail = { new HeartbeatSample { Time = start.AddMinutes(22), Metrics = { [KeyMetrics.CpuTemp] = 97 } } },
        };
        return r;
    }

    [Fact]
    public void Export_contains_everything_an_ai_needs()
    {
        var md = AiExport.BuildMarkdown(new[] { CrashedRun() }, now: new DateTime(2026, 10, 2));
        Assert.StartsWith("# BenchForge diagnostics for AI analysis", md);
        Assert.Contains("## Instructions for the AI", md);
        Assert.Contains("Test CPU 9000", md);
        Assert.Contains("0x124", md);
        Assert.Contains("```csv", md);
        Assert.Contains("t,cpu.temp", md);
        Assert.Contains("BenchForge's own analysis", md);
        Assert.Contains("crash dumps small (minidump)", md);
    }

    [Fact]
    public void Sensor_table_is_downsampled_and_keeps_peaks()
    {
        var table = AiExport.SensorTable(CrashedRun().Timeline, 150)!;
        var rows = table.Trim().Split('\n');
        Assert.True(rows.Length <= 151);
        Assert.Contains("99", table); // peak temperature of each bucket survives
    }

    [Fact]
    public void Export_respects_size_limit()
    {
        var runs = Enumerable.Range(0, 60).Select(_ => CrashedRun()).ToList();
        var events = Enumerable.Range(0, 400).Select(i => new EventEntry { Provider = "disk", Id = 7, Level = 2, Time = DateTime.Now, Message = new string('x', 600) }).ToList();
        var md = AiExport.BuildMarkdown(runs, events: events);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(md) <= AiExport.MaxBytes);
        Assert.Contains("## Instructions for the AI", md);
    }
}

public class AiExportEverythingTests
{
    [Fact]
    public void Extras_and_all_sensor_stats_are_included()
    {
        var r = new RunResult { Name = "Quick benchmark", StartTime = DateTime.Now, Status = RunStatus.Completed };
        r.SensorStats.Add(new ChannelStats { Id = "/amdcpu/0/temperature/2", Hardware = "AMD Ryzen 7", Name = "Core (Tctl/Tdie)", Kind = SensorKind.Temperature, HwType = HwType.Cpu, Min = 40, Avg = 70, Max = 88, Unit = "°C" });
        var extras = new DiagnosticsExtras
        {
            Drivers = { new DriverEntry("DISPLAY", "NVIDIA GeForce RTX 3070", "NVIDIA", "32.0.15.6094", new DateTime(2024, 8, 1)) },
            Reliability = { new ReliabilityEntry(DateTime.Now, "Windows Error Reporting", "game.exe", "Stopped working", 1001) },
            RecentErrors = { new EventEntry { Log = "System", Provider = "disk", Id = 7, Level = 2, Time = DateTime.Now, Message = "bad block" } },
            AppLogTail = { "2026-10-11 sensor started" },
        };
        var md = AiExport.BuildMarkdown(new[] { r }, extras: extras);
        Assert.Contains("Every sensor during this run", md);
        Assert.Contains("Core (Tctl/Tdie)", md);
        Assert.Contains("RTX 3070", md);
        Assert.Contains("Reliability Monitor", md);
        Assert.Contains("bad block", md);
        Assert.Contains("BenchForge app log", md);
    }

    [Fact]
    public void Full_sensor_csv_has_every_channel()
    {
        var tl = new SensorTimeline();
        tl.Channels.Add(new SensorChannel { Id = "a", Hardware = "CPU", Name = "Temp", Unit = "°C" });
        tl.Channels.Add(new SensorChannel { Id = "b", Hardware = "GPU", Name = "Load", Unit = "%" });
        tl.Samples.Add(new TimelineSample { T = 0, V = new float?[] { 50, null } });
        tl.Samples.Add(new TimelineSample { T = 1, V = new float?[] { 51.5f, 99 } });
        var csv = AiExport.FullSensorCsv(tl).Trim().Split('\n');
        Assert.Equal(3, csv.Length);
        Assert.Contains("CPU / Temp", csv[0]);
        Assert.Equal("1,51.5,99", csv[2].Trim());
    }
}

public class LaptopModeTests
{
    private static RunResult HotRun(bool laptop, double cpuMax)
    {
        var r = new RunResult { Status = RunStatus.Completed, StartTime = DateTime.Now };
        r.Plan.Settings.LaptopRules = laptop;
        r.KeyStats[KeyMetrics.CpuTemp] = new ChannelStats { Max = cpuMax };
        return r;
    }

    [Fact]
    public void Ninety_eight_degrees_is_normal_on_a_laptop_but_critical_on_a_desktop()
    {
        var laptop = HotRun(true, 98); RunAnalyzer.Analyze(laptop);
        var desktop = HotRun(false, 98); RunAnalyzer.Analyze(desktop);
        Assert.DoesNotContain(laptop.Findings, f => f.Severity == Severity.Critical);
        Assert.Contains(laptop.Findings, f => f.Title.Contains("normal for laptops"));
        Assert.Contains(desktop.Findings, f => f.Severity == Severity.Critical && f.Title.Contains("overheating"));
        Assert.DoesNotContain(laptop.Recommendations, r => r.Title == "Improve cooling");
    }

    [Fact]
    public void Above_105_is_critical_even_on_a_laptop()
    {
        var r = HotRun(true, 106); RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Severity == Severity.Critical);
    }

    [Theory]
    [InlineData(0.95, "Excellent", Severity.Info)]
    [InlineData(0.85, "Good (typical laptop)", Severity.Info)]
    [InlineData(0.70, "Weak — cooling is struggling", Severity.Warning)]
    [InlineData(0.50, "Poor — heavy thermal throttling", Severity.Critical)]
    public void Cooling_bands(double ratio, string verdict, Severity sev)
    {
        var (s, v, _) = RunAnalyzer.CoolingVerdict(ratio, laptop: true);
        Assert.Equal(verdict, v);
        Assert.Equal(sev, s);
    }

    [Fact]
    public void Cooling_score_from_stress_throughput_drives_laptop_advice()
    {
        var start = DateTime.Now.AddMinutes(-5);
        var r = new RunResult { Status = RunStatus.Completed, StartTime = start };
        r.Plan.Settings.LaptopRules = true;
        r.Hardware = new HardwareInventory { IsLaptop = true };
        var step = new StepResult { Kind = StepKind.Stress, Components = { Component.Cpu }, Start = start, End = start.AddSeconds(240), Modules = { new ModuleResult { Component = Component.Cpu, Kind = StepKind.Stress } } };
        r.Steps.Add(step);
        r.Timeline.Channels.Add(new SensorChannel { Id = "test/cpu.stress", HwType = HwType.Test, Name = "CPU stress throughput" });
        for (int t = 0; t < 240; t++) r.Timeline.Samples.Add(new TimelineSample { T = t, V = new float?[] { t < 20 ? 1000 : 600 } });
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Title.StartsWith("Cooling: Poor"));
        Assert.Contains(r.Recommendations, x => x.Title == "Help the laptop cool down");
        Assert.Contains(step.Modules[0].SubScores, s => s.Name.StartsWith("Sustained performance") && s.Value == 60);
    }

    [Fact]
    public void Charger_and_battery_findings()
    {
        var r = new RunResult { Status = RunStatus.Completed, StartTime = DateTime.Now, Hardware = new HardwareInventory { IsLaptop = true, BatteryWearPercent = 55, BatteryDesignCapacityWh = 80, BatteryFullChargeWh = 36 } };
        r.Plan.Settings.LaptopRules = true;
        r.Timeline.Channels.Add(new SensorChannel { Id = KeyMetrics.ChannelId(KeyMetrics.OnAc) });
        for (int t = 0; t < 100; t++) r.Timeline.Samples.Add(new TimelineSample { T = t, V = new float?[] { t < 50 ? 1 : 0 } });
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Title.StartsWith("Charger was disconnected"));
        Assert.Contains(r.Findings, f => f.Title.Contains("45% of its original charge"));
        Assert.Contains(r.Recommendations, x => x.Title == "Test with the charger plugged in");
        Assert.Contains(r.Recommendations, x => x.Title == "Consider replacing the battery");
    }
}

public class DriveTests
{
    private static byte[] AtaBlock(params (int Id, int Cur, int Worst, long Raw)[] attrs)
    {
        var b = new byte[512];
        b[0] = 0x10; // revision
        for (int i = 0; i < attrs.Length; i++)
        {
            int o = 2 + i * 12;
            b[o] = (byte)attrs[i].Id; b[o + 1] = 0x33; b[o + 2] = 0x00;
            b[o + 3] = (byte)attrs[i].Cur; b[o + 4] = (byte)attrs[i].Worst;
            for (int k = 0; k < 6; k++) b[o + 5 + k] = (byte)(attrs[i].Raw >> (8 * k));
        }
        return b;
    }

    [Fact]
    public void Ata_smart_attributes_parse_from_raw_bytes()
    {
        // Temperature 194 packs min/max in the upper raw bytes; only the low byte is the current value.
        var data = AtaBlock((5, 100, 100, 8), (9, 95, 95, 12345), (194, 64, 50, 0x2D_0014_0024), (199, 200, 200, 0x01_0203));
        var th = new byte[512];
        th[2] = 5; th[3] = 10; th[14] = 9; th[15] = 0;
        var a = Storage.SmartParser.ParseAta(data, th);
        Assert.Equal(4, a.Count);
        Assert.Equal("Reallocated sectors", a[0].Name);
        Assert.Equal(8, a[0].Raw);
        Assert.Equal(10, a[0].Threshold);
        Assert.False(a[0].Failing);
        Assert.Equal(12345, a[1].Raw);
        Assert.Equal(0x24, a[2].Raw);
        Assert.Equal(0x010203, a[3].Raw);
        Assert.Null(a[3].Threshold);
    }

    [Fact]
    public void Smart_attribute_fails_at_threshold()
    {
        var a = Storage.SmartParser.ParseAta(AtaBlock((5, 9, 9, 900)), new byte[] { 0, 0, 5, 10 });
        Assert.True(a[0].Failing);
    }

    [Fact]
    public void Nvme_health_log_parses()
    {
        var log = new byte[512];
        log[0] = 0x04;
        BitConverter.GetBytes((ushort)(273 + 41)).CopyTo(log, 1);
        log[3] = 98; log[4] = 10; log[5] = 7;
        BitConverter.GetBytes(20_000_000UL).CopyTo(log, 32); // data units read
        BitConverter.GetBytes(40_000_000UL).CopyTo(log, 48); // data units written
        BitConverter.GetBytes(1500UL).CopyTo(log, 112);
        BitConverter.GetBytes(8760UL).CopyTo(log, 128);
        BitConverter.GetBytes(77UL).CopyTo(log, 144);
        BitConverter.GetBytes(3UL).CopyTo(log, 160);
        var n = Storage.SmartParser.ParseNvme(log);
        Assert.Equal(4, n.CriticalWarning);
        Assert.Equal(41, n.TemperatureC);
        Assert.Equal(98, n.AvailableSparePercent);
        Assert.Equal(10, n.SpareThresholdPercent);
        Assert.Equal(7, n.PercentageUsed);
        Assert.Equal(10.24, n.DataReadTB, 2);
        Assert.Equal(20.48, n.DataWrittenTB, 2);
        Assert.Equal(1500, n.PowerCycles);
        Assert.Equal(8760, n.PowerOnHours);
        Assert.Equal(77, n.UnsafeShutdowns);
        Assert.Equal(3, n.MediaErrors);
        Assert.Contains("reliability degraded", Storage.SmartParser.DescribeCriticalWarning(n.CriticalWarning));
    }

    [Fact]
    public void Slc_cache_knee_is_found()
    {
        var pts = new List<double[]>();
        for (int i = 1; i <= 60; i++) pts.Add(new[] { i * 0.5, i * 0.5 <= 12 ? 3000 + (i % 3) * 20 : 450 + (i % 4) * 10 });
        var r = Storage.DriveAnalysis.DetectSlc(pts);
        Assert.True(r.Detected);
        Assert.InRange(r.CacheGB!.Value, 11.5, 13);
        Assert.InRange(r.CachedMBs, 2990, 3050);
        Assert.InRange(r.AfterMBs, 440, 490);
    }

    [Fact]
    public void Flat_write_curve_has_no_slc_knee()
    {
        var pts = Enumerable.Range(1, 40).Select(i => new[] { i * 1.0, 520 + (i % 5) * 5.0 }).ToList();
        Assert.False(Storage.DriveAnalysis.DetectSlc(pts).Detected);
    }

    [Fact]
    public void Spread_order_visits_every_block_once_and_spreads_early()
    {
        var order = Storage.DriveAnalysis.SpreadOrder(1000).ToList();
        Assert.Equal(1000, order.Count);
        Assert.Equal(1000, order.Distinct().Count());
        // The first 16 reads already touch every 1/8 of the disk.
        Assert.Equal(8, order.Take(16).Select(x => x * 8 / 1000).Distinct().Count());
    }

    [Fact]
    public void Expected_speed_depends_on_drive_kind()
    {
        Assert.Equal(3000, Storage.SmartParser.Expected(new DiskInfo { BusType = "NVMe", MediaType = "SSD" }, new PcieLink("x", "NVMe", 4, 4, 4, 4)).ExpectedSeqRead);
        Assert.Equal(420, Storage.SmartParser.Expected(new DiskInfo { BusType = "SATA", MediaType = "SSD" }, null).ExpectedSeqRead);
        Assert.Equal(90, Storage.SmartParser.Expected(new DiskInfo { BusType = "SATA", MediaType = "HDD" }, null).ExpectedSeqRead);
    }

    private static List<Recommendation> Checks(Action<HardwareInventory> setup)
    {
        var hw = new HardwareInventory();
        setup(hw);
        return RecommendationEngine.SystemChecks(hw);
    }

    [Fact]
    public void Damaged_sectors_and_cable_errors_are_reported()
    {
        var hw = new HardwareInventory();
        hw.Disks.Add(new DiskInfo { Model = "OldDisk", MediaType = "HDD", BusType = "SATA", Smart = { new(5, "Reallocated sectors", 100, 100, 10, 24), new(199, "Interface CRC errors (cable)", 200, 200, 0, 31) } });
        var r = new RunResult { Status = RunStatus.Running, Hardware = hw };
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Severity == Severity.Critical && f.Title.Contains("damaged sectors"));
        Assert.Contains(r.Findings, f => f.Title.Contains("CRC errors"));
        Assert.Contains(r.Recommendations, x => x.Title == "Replace the SATA cable");
    }

    [Fact]
    public void Worn_nvme_and_slow_link_are_reported()
    {
        var hw = new HardwareInventory();
        hw.Disks.Add(new DiskInfo { Model = "FastSSD", MediaType = "SSD", BusType = "NVMe", Nvme = new NvmeHealthInfo { PercentageUsed = 104, AvailableSparePercent = 100, SpareThresholdPercent = 10 } });
        hw.PcieLinks.Add(new PcieLink("FastSSD", "NVMe", 3, 2, 4, 4));
        hw.PcieLinks.Add(new PcieLink("GPU", "GPU", 1, 16, 4, 16));
        var r = new RunResult { Status = RunStatus.Running, Hardware = hw };
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Severity == Severity.Critical && f.Title.Contains("past its rated lifetime"));
        Assert.Contains(r.Findings, f => f.Severity == Severity.Warning && f.Title.StartsWith("FastSSD is running at PCIe 3.0 ×2"));
        Assert.Contains(r.Findings, f => f.Severity == Severity.Info && f.Component == Component.Gpu);
        Assert.Contains(r.Recommendations, x => x.Title == "Give the SSD its full PCIe speed");
    }

    [Fact]
    public void Trim_off_offers_one_click_fix()
    {
        var recs = Checks(hw => { hw.TrimEnabled = false; hw.Disks.Add(new DiskInfo { Model = "SSD", MediaType = "SSD", BusType = "SATA" }); });
        var trim = Assert.Single(recs, x => x.Title == "Turn TRIM back on");
        Assert.Contains(FixId.EnableTrim, trim.Fixes);
    }

    [Fact]
    public void Healthy_drive_gives_no_drive_warnings()
    {
        var hw = new HardwareInventory { TrimEnabled = true };
        hw.Disks.Add(new DiskInfo { Model = "Good", MediaType = "SSD", BusType = "NVMe", Nvme = new NvmeHealthInfo { PercentageUsed = 3, AvailableSparePercent = 100, SpareThresholdPercent = 10, PowerCycles = 900, UnsafeShutdowns = 20 } });
        hw.PcieLinks.Add(new PcieLink("Good", "NVMe", 4, 4, 4, 4));
        var r = new RunResult { Status = RunStatus.Running, Hardware = hw };
        RunAnalyzer.Analyze(r);
        Assert.DoesNotContain(r.Findings, f => f.Component == Component.Storage && f.Severity >= Severity.Warning);
    }

    [Fact]
    public void Misaligned_ssd_names_the_partition_and_gives_steps()
    {
        var hw = new HardwareInventory();
        hw.Disks.Add(new DiskInfo { Model = "ClonedSSD", MediaType = "SSD", BusType = "SATA", Misaligned = true, MisalignedPartitions = { "C: (partition 2) starts at 32,256 bytes — not a multiple of 4,096" } });
        hw.Disks.Add(new DiskInfo { Model = "OldHDD", MediaType = "HDD", BusType = "SATA", Misaligned = true });
        var r = new RunResult { Status = RunStatus.Running, Hardware = hw };
        RunAnalyzer.Analyze(r);
        var f = Assert.Single(r.Findings, x => x.Title.Contains("not aligned"));
        Assert.Equal("ClonedSSD: partition C: not aligned to 4 KB", f.Title);
        Assert.Contains("32,256 bytes", f.Detail);
        var rec = Assert.Single(r.Recommendations, x => x.Title == "Align the SSD partition");
        Assert.Contains(rec.Actions, a => a.Contains("msinfo32"));
        Assert.Contains(FixId.AlignPartitions, rec.Fixes);
    }
}

public class PartitionAlignTests
{
    private static Storage.PartitionEntry DataPart(long offset = 32256, long size = 100L << 30) => new()
    {
        DiskNumber = 1, PartitionNumber = 2, DriveLetter = 'D', Offset = offset, Size = size, UsedBytes = 10L << 30,
        FileSystem = "NTFS", Label = "Games", PartitionStyle = "GPT", GptType = Storage.PartitionAlign.GptBasicData, Encrypted = false, MediaType = "SSD",
    };

    [Fact]
    public void New_layout_is_aligned_and_fits_inside_the_old_partition()
    {
        foreach (var (off, size) in new[] { (32256L, 100L << 30), (1048576L + 512, 5L << 30), (65536L * 3 + 7, (1L << 30) + 12345) })
        {
            var (o, s) = Storage.PartitionAlign.NewLayout(off, size);
            Assert.Equal(0, o % (1 << 20));
            Assert.True(o >= off);
            Assert.True(o + s <= off + size);
            Assert.True(off + size - (o + s) < 2L << 20); // loses less than 2 MB
        }
    }

    [Fact]
    public void Only_safe_partitions_are_fixed_automatically()
    {
        Assert.True(Storage.PartitionAlign.Check(DataPart(), @"E:\BenchForge\", @"C:\Windows").CanAutoFix);
        Assert.False(Storage.PartitionAlign.Check(DataPart(1048576)).CanAutoFix);                                   // already aligned
        var c = DataPart(); c.DriveLetter = 'C';
        Assert.Contains("Windows partition", Storage.PartitionAlign.Check(c, null, @"C:\Windows").Reason);
        var boot = DataPart(); boot.IsBoot = true;
        Assert.False(Storage.PartitionAlign.Check(boot).CanAutoFix);
        var app = DataPart();
        Assert.Contains("runs from this drive", Storage.PartitionAlign.Check(app, @"D:\Tools\BenchForge\").Reason);
        var bl = DataPart(); bl.Encrypted = true;
        Assert.Contains("BitLocker", Storage.PartitionAlign.Check(bl).Reason);
        var unknown = DataPart(); unknown.Encrypted = null;
        Assert.False(Storage.PartitionAlign.Check(unknown).CanAutoFix);
        var pf = DataPart(); pf.HasPageFile = true;
        Assert.Contains("page file", Storage.PartitionAlign.Check(pf).Reason);
        var recovery = DataPart(); recovery.GptType = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
        Assert.False(Storage.PartitionAlign.Check(recovery).CanAutoFix);
        var logical = DataPart(); logical.PartitionStyle = "MBR"; logical.MbrType = 7; logical.IsLogical = true;
        Assert.False(Storage.PartitionAlign.Check(logical).CanAutoFix);
        var refs = DataPart(); refs.FileSystem = "ReFS";
        Assert.False(Storage.PartitionAlign.Check(refs).CanAutoFix);
        var bigFat = DataPart(); bigFat.FileSystem = "FAT32";
        Assert.Contains("32 GB", Storage.PartitionAlign.Check(bigFat).Reason);
        var full = DataPart(size: 10L << 30); full.UsedBytes = (10L << 30) - (100L << 20);
        Assert.Contains("too full", Storage.PartitionAlign.Check(full).Reason);
    }

    [Fact]
    public void Backup_target_needs_room_and_prefers_another_disk()
    {
        var p = DataPart();
        var vols = new[]
        {
            new Storage.BackupVolume('D', "NTFS", 900L << 30, 1, "self"),
            new Storage.BackupVolume('E', "NTFS", 500L << 30, 1, "same disk"),
            new Storage.BackupVolume('F', "NTFS", 200L << 30, 2, "other disk"),
            new Storage.BackupVolume('G', "FAT32", 900L << 30, 3, "fat"),
            new Storage.BackupVolume('H', "exFAT", 5L << 30, 4, "too small"),
        };
        var t = Storage.PartitionAlign.BackupTargets(p, vols);
        Assert.Equal(new[] { 'F', 'E' }, t.Select(x => x.Letter));
    }

    [Fact]
    public void Recreate_script_keeps_letter_type_and_label()
    {
        var p = DataPart(); p.Label = "Bob's games";
        var s = Storage.PartitionAlign.RecreateScript(p);
        var (o, size) = Storage.PartitionAlign.NewLayout(p.Offset, p.Size);
        Assert.Contains($"-Offset {o} -Size {size} -DriveLetter D -GptType '{Storage.PartitionAlign.GptBasicData}'", s);
        Assert.Contains("-FileSystem NTFS -NewFileSystemLabel 'Bob''s games'", s);
        var mbr = DataPart(); mbr.PartitionStyle = "MBR"; mbr.MbrType = 0x0B; mbr.FileSystem = "FAT32";
        Assert.Contains("-MbrType 12", Storage.PartitionAlign.RecreateScript(mbr));
        Assert.Contains("-FileSystem FAT32", Storage.PartitionAlign.RecreateScript(mbr));
    }

    private sealed class FakeOps : Storage.IAlignOps
    {
        public string Drive = "";
        public bool FailRecreate, CorruptCopy;
        public int Recreated;
        public bool DropLinks;
        public Task<bool> CopyTree(string from, string to, Action<string> log, CancellationToken ct)
        {
            CopyLinks(from, to);
            foreach (var f in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var dst = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, true);
                if (CorruptCopy) { File.AppendAllText(dst, "x"); CorruptCopy = false; }
            }
            return Task.FromResult(true);
        }
        /// <summary>Like robocopy /SJ /SL: links are recreated as links, never followed.</summary>
        private void CopyLinks(string from, string to)
        {
            if (DropLinks) return;
            foreach (var d in Directory.EnumerateDirectories(from, "*", new EnumerationOptions { RecurseSubdirectories = true }))
            {
                var info = new DirectoryInfo(d);
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is null) continue;
                var dst = Path.Combine(to, Path.GetRelativePath(from, d));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!Directory.Exists(dst)) Directory.CreateSymbolicLink(dst, info.LinkTarget);
            }
        }

        public string? LockError;
        public bool GuardFails;
        public int Locked, RecreatedMissing;
        public Task<Storage.RecreateResult> Recreate(Storage.PartitionEntry p, Action<string> log)
        {
            if (GuardFails) return Task.FromResult(new Storage.RecreateResult(null, false, "The partition changed since it was checked"));
            if (FailRecreate) { Directory.Delete(Drive, true); return Task.FromResult(new Storage.RecreateResult(null, true, "format failed")); }
            Recreated++;
            Directory.Delete(Drive, true); Directory.CreateDirectory(Drive); // formatted = empty
            return Task.FromResult(new Storage.RecreateResult(Storage.PartitionAlign.NewLayout(p.Offset, p.Size).Offset, true));
        }
        public Task<Storage.RecreateResult> RecreateMissing(Storage.PartitionEntry p, Action<string> log)
        {
            if (Directory.Exists(Drive)) return Task.FromResult(new Storage.RecreateResult(null, true, "space not free"));
            RecreatedMissing++;
            Directory.CreateDirectory(Drive);
            return Task.FromResult(new Storage.RecreateResult(Storage.PartitionAlign.NewLayout(p.Offset, p.Size).Offset, true));
        }
        public Task<string?> LockAndDismount(char letter, Action<string> log) { Locked++; return Task.FromResult(LockError); }
        public Task<bool> WaitForDrive(char letter, TimeSpan timeout) => Task.FromResult(Directory.Exists(Drive));
    }

    private static (string Root, string Drive, string Backup) Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "bf-align-" + Guid.NewGuid().ToString("N"));
        var drive = Path.Combine(root, "drive");
        Directory.CreateDirectory(Path.Combine(drive, "Photos", "2024"));
        File.WriteAllText(Path.Combine(drive, "notes.txt"), "hello");
        File.WriteAllBytes(Path.Combine(drive, "Photos", "2024", "big.bin"), Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 31)).ToArray());
        Directory.CreateDirectory(Path.Combine(drive, "System Volume Information"));
        // Like a game-library junction. (Targets exist: on Linux a dangling link isn't listed as a folder, unlike Windows.)
        Directory.CreateDirectory(Path.Combine(root, "elsewhere", "SteamLibrary"));
        Directory.CreateSymbolicLink(Path.Combine(drive, "Library"), Path.Combine(root, "elsewhere", "SteamLibrary"));
        File.WriteAllText(Path.Combine(drive, "System Volume Information", "tracking.log"), "windows");
        Persistence.DataPaths.Override(Path.Combine(root, "data"));
        return (root, drive, Path.Combine(root, "backup"));
    }

    private static Task<bool> Yes(string _) => Task.FromResult(true);

    [Fact]
    public async Task Full_fix_restores_identical_files_and_removes_the_copy()
    {
        var (root, drive, backup) = Setup();
        var before = Storage.FileManifest.Build(drive);
        Assert.Equal(3, before.Count); // 2 files + the link; System Volume Information is skipped
        Assert.Equal(Storage.FileManifest.LinkSize, before["Library"].Size);
        var ops = new FakeOps { Drive = drive };
        var log = new List<string>();
        bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None);
        Assert.True(ok, string.Join("\n", log));
        Assert.Equal(1, ops.Recreated);
        Assert.Empty(Storage.FileManifest.Compare(before, Storage.FileManifest.Build(drive)));
        Assert.True(new DirectoryInfo(Path.Combine(drive, "Library")).LinkTarget is not null);
        Assert.False(Directory.Exists(backup));
        Assert.Null(Storage.AlignJob.Load());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Bad_copy_never_touches_the_partition()
    {
        var (root, drive, backup) = Setup();
        var ops = new FakeOps { Drive = drive, CorruptCopy = true };
        var log = new List<string>();
        bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(0, ops.Recreated);
        Assert.True(File.Exists(Path.Combine(drive, "notes.txt")));
        Assert.Contains(log, l => l.Contains("not identical"));
        Assert.Null(Storage.AlignJob.Load());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Saying_no_at_the_last_question_changes_nothing()
    {
        var (root, drive, backup) = Setup();
        var ops = new FakeOps { Drive = drive };
        bool ok = await new Storage.PartitionAligner(ops, _ => { }, (_, _) => { }).RunAsync(DataPart(), drive, backup, _ => Task.FromResult(false), Yes, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(0, ops.Recreated);
        Assert.False(Directory.Exists(backup));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Failed_recreate_keeps_the_copy_and_can_be_finished_later()
    {
        var (root, drive, backup) = Setup();
        var before = Storage.FileManifest.Build(drive);
        var ops = new FakeOps { Drive = drive, FailRecreate = true };
        var log = new List<string>();
        bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains(log, l => l.Contains("YOUR FILES ARE SAFE"));
        var job = Storage.AlignJob.Load();
        Assert.NotNull(job);
        Assert.True(job!.DataOnlyInBackup);
        Assert.Empty(Storage.FileManifest.Compare(before, Storage.FileManifest.Build(backup)));

        // The user recreates the volume in Disk Management, then presses "Finish restoring".
        Directory.CreateDirectory(drive);
        ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RestoreAsync(job, null, drive, _ => Task.FromResult(false));
        Assert.True(ok);
        Assert.Empty(Storage.FileManifest.Compare(before, Storage.FileManifest.Build(drive)));
        Assert.True(Directory.Exists(backup)); // user chose to keep it
        Assert.Null(Storage.AlignJob.Load());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Dropped_links_are_caught_before_anything_is_deleted()
    {
        var (root, drive, backup) = Setup();
        var ops = new FakeOps { Drive = drive, DropLinks = true };
        var log = new List<string>();
        bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(0, ops.Recreated);
        Assert.Contains(log, l => l.Contains("missing link: Library"));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Manifest_does_not_follow_links_and_spots_retargeted_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "bf-links-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "x");
        var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        Directory.CreateSymbolicLink(Path.Combine(a, "link"), target);
        Directory.CreateDirectory(Path.Combine(root, "other"));
        Directory.CreateSymbolicLink(Path.Combine(b, "link"), Path.Combine(root, "other"));
        var ma = Storage.FileManifest.Build(a);
        Assert.Single(ma); // the link only — inside.txt is not reached through it
        Assert.Contains("link points somewhere else: link", Storage.FileManifest.Compare(ma, Storage.FileManifest.Build(b)));
        Directory.Delete(Path.Combine(b, "link"));
        File.WriteAllText(Path.Combine(b, "link"), "a plain file where the link was");
        Assert.Contains("link became a normal file/folder: link", Storage.FileManifest.Compare(ma, Storage.FileManifest.Build(b)));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Interrupted_fix_recreates_the_missing_drive_and_restores()
    {
        var (root, drive, backup) = Setup();
        var before = Storage.FileManifest.Build(drive);
        var ops = new FakeOps { Drive = drive, FailRecreate = true };
        var log = new List<string>();
        Assert.False(await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None));
        var job = Storage.AlignJob.Load()!;
        Assert.NotNull(job.Partition);
        Assert.False(Directory.Exists(drive)); // the "drive" is gone, like after a power cut

        ops.FailRecreate = false;
        bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).ResumeAsync(job, drive, driveExists: false, Yes);
        Assert.True(ok, string.Join("\n", log));
        Assert.Equal(1, ops.RecreatedMissing);
        Assert.Empty(Storage.FileManifest.Compare(before, Storage.FileManifest.Build(drive)));
        Assert.Null(Storage.AlignJob.Load());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Practice_run_never_touches_the_drive()
    {
        var (root, drive, backup) = Setup();
        var before = Storage.FileManifest.Build(drive);
        var ops = new FakeOps { Drive = drive };
        bool ok = await new Storage.PartitionAligner(ops, _ => { }, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None, practice: true);
        Assert.True(ok);
        Assert.Equal(0, ops.Recreated);
        Assert.Equal(0, ops.Locked);
        Assert.False(Directory.Exists(backup));
        Assert.Empty(Storage.FileManifest.Compare(before, Storage.FileManifest.Build(drive)));
        Assert.Null(Storage.AlignJob.Load());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Files_changed_after_the_check_stop_the_fix()
    {
        var (root, drive, backup) = Setup();
        var ops = new FakeOps { Drive = drive };
        var log = new List<string>();
        Task<bool> ChangeThenYes(string _) { File.AppendAllText(Path.Combine(drive, "notes.txt"), " more"); return Task.FromResult(true); }
        bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(DataPart(), drive, backup, ChangeThenYes, Yes, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(0, ops.Recreated);
        Assert.Contains(log, l => l.Contains("changed after they were copied"));
        Assert.Equal("hello more", File.ReadAllText(Path.Combine(drive, "notes.txt")));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Open_files_or_a_changed_partition_mean_nothing_is_deleted()
    {
        foreach (var setup in new Action<FakeOps>[] { o => o.LockError = "Some program still has files open on D:.", o => o.GuardFails = true })
        {
            var (root, drive, backup) = Setup();
            var before = Storage.FileManifest.Build(drive);
            var ops = new FakeOps { Drive = drive };
            setup(ops);
            var log = new List<string>();
            bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None);
            Assert.False(ok);
            Assert.Equal(0, ops.Recreated);
            Assert.Contains(log, l => l.Contains("Nothing was changed") || l.Contains("was not changed"));
            Assert.Empty(Storage.FileManifest.Compare(before, Storage.FileManifest.Build(drive)));
            Assert.Null(Storage.AlignJob.Load());       // nothing to resume
            Assert.False(Directory.Exists(backup));     // our own temporary copy is cleaned up
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Files_that_would_not_fit_back_stop_the_fix()
    {
        var (root, drive, backup) = Setup();
        var ops = new FakeOps { Drive = drive };
        var log = new List<string>();
        var tiny = DataPart(size: 200L << 20); // new partition < files + 256 MB margin
        bool ok = await new Storage.PartitionAligner(ops, log.Add, (_, _) => { }).RunAsync(tiny, drive, backup, Yes, Yes, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(0, ops.Recreated);
        Assert.Contains(log, l => l.Contains("new partition holds"));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task An_existing_folder_is_never_used_or_deleted()
    {
        var (root, drive, backup) = Setup();
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "my old kept copy.txt"), "keep me");
        var ops = new FakeOps { Drive = drive };
        bool ok = await new Storage.PartitionAligner(ops, _ => { }, (_, _) => { }).RunAsync(DataPart(), drive, backup, Yes, Yes, CancellationToken.None);
        Assert.False(ok);
        Assert.True(File.Exists(Path.Combine(backup, "my old kept copy.txt")));
        var p = DataPart();
        var a = Storage.PartitionAligner.BackupFolderFor(p, root);
        Directory.CreateDirectory(a);
        Assert.NotEqual(a, Storage.PartitionAligner.BackupFolderFor(p, root));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Safe_delete_removes_links_but_never_their_targets()
    {
        var root = Path.Combine(Path.GetTempPath(), "bf-del-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "do not delete");
        var tree = Path.Combine(root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllText(Path.Combine(tree, "sub", "ro.txt"), "x");
        File.SetAttributes(Path.Combine(tree, "sub", "ro.txt"), FileAttributes.ReadOnly);
        Directory.CreateSymbolicLink(Path.Combine(tree, "sub", "link"), outside);
        Storage.SafeDelete.Tree(tree);
        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Drive_tied_things_block_the_automatic_fix()
    {
        var p = DataPart();
        p.Blockers.Add("Microsoft Store / Xbox apps are installed on D:.");
        var v = Storage.PartitionAlign.Check(p);
        Assert.False(v.CanAutoFix);
        Assert.Contains("Store", v.Reason);
    }

    [Fact]
    public void Recreate_script_re_finds_the_partition_by_letter_and_checks_it()
    {
        var p = DataPart();
        var s = Storage.PartitionAlign.RecreateScript(p);
        Assert.Contains("Get-Partition -DriveLetter D", s);
        Assert.Contains($"$old.Offset -ne {p.Offset}", s);
        Assert.Contains($"$old.Size -ne {p.Size}", s);
        Assert.Contains("$old.DiskNumber -ne 1", s);
        Assert.DoesNotContain("-PartitionNumber", s); // numbers can change; never delete by number
        Assert.True(s.IndexOf("throw", StringComparison.Ordinal) < s.IndexOf("Remove-Partition", StringComparison.Ordinal));
        var m = Storage.PartitionAlign.RecreateMissingScript(p);
        Assert.DoesNotContain("Remove-Partition", m);
        Assert.Contains("not free any more", m);
    }
}

public class StabilityTestTests
{
    [Fact]
    public void Spike_schedule_alternates_and_counts_jumps()
    {
        Assert.True(PsuSpikeSchedule.IsOn(0.5));
        Assert.False(PsuSpikeSchedule.IsOn(1.5));
        Assert.True(PsuSpikeSchedule.IsOn(2.5));
        Assert.False(PsuSpikeSchedule.IsOn(-1));
        double cycle = PsuSpikeSchedule.Pattern.Sum();
        Assert.Equal(PsuSpikeSchedule.Pattern.Length / 2, PsuSpikeSchedule.Spikes(cycle - 0.01));
        Assert.Equal(PsuSpikeSchedule.IsOn(3.3), PsuSpikeSchedule.IsOn(3.3 + cycle)); // repeats
    }

    [Fact]
    public void Core_cycler_gives_every_core_time_and_more_rounds_when_long()
    {
        var (r1, s1) = Cpu.CpuStability.CyclePlan(600, 16);
        Assert.Equal(1, r1);
        Assert.Equal(37.5, s1, 3);
        var (r2, s2) = Cpu.CpuStability.CyclePlan(4 * 3600, 16);
        Assert.Equal(15, r2);
        Assert.True(s2 >= 60);
        var (_, s3) = Cpu.CpuStability.CyclePlan(30, 64);
        Assert.Equal(Cpu.CpuStability.MinSlotSeconds, s3);
    }

    [Fact]
    public void Crash_finder_plan_shares_time_and_tags_steps()
    {
        var plan = PlanPresets.CrashFinder(TimeSpan.FromHours(2), true, true, true);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal(3600, plan.Steps.Single(s => s.Variant == StepVariants.CoreCycle).DurationSeconds, 0);
        var spike = plan.Steps.Single(s => s.Variant == StepVariants.PsuSpike);
        Assert.Contains(Component.Gpu, spike.Components);
        Assert.Single(PlanPresets.CrashFinder(TimeSpan.FromHours(1), false, true, false).Steps);
    }

    private static RunResult WithStep(string variant, string status, ModuleResult cpu) => new()
    {
        Status = RunStatus.Completed,
        Steps = { new StepResult { Name = "x", Variant = variant, Status = status, Components = { Component.Cpu }, Modules = { cpu } } },
    };

    [Fact]
    public void Core_errors_become_a_critical_finding_with_advice()
    {
        var cpu = new ModuleResult { Component = Component.Cpu, Kind = StepKind.Stress, ErrorCount = 3 };
        cpu.SubScores.Add(new SubScore { Name = "Core 0", Group = "Per core", Value = 400 });
        cpu.SubScores.Add(new SubScore { Name = "Core 5", Group = "Per core", Value = 410, Detail = "3 calculation errors" });
        var r = WithStep(StepVariants.CoreCycle, "Passed", cpu);
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Severity == Severity.Critical && f.Title.StartsWith("Core 5 made calculation errors"));
        Assert.Contains(r.Recommendations, x => x.Title == "Fix single-core / light-load instability");
    }

    [Fact]
    public void Shutdown_during_spike_test_points_at_the_power_supply()
    {
        var r = new RunResult { Status = RunStatus.Crashed, Crash = new CrashInfo { StepName = StepVariants.PsuSpikeName, Summary = "PC restarted" } };
        RunAnalyzer.Analyze(r);
        Assert.Contains(r.Findings, f => f.Title.Contains("load-spike test"));
        Assert.Contains(r.Recommendations, x => x.Title == "Check the power supply");
    }
}

public class CacheTests
{
    [Fact]
    public void Cache_levels_are_found_from_the_latency_ladder()
    {
        // Typical desktop: L1 48 KB @ 1 ns, L2 1.25 MB @ 3.5 ns, L3 32 MB @ 11 ns, RAM ~80 ns.
        double Ns(long s) => s <= 48 << 10 ? 1.0 : s <= 1280 << 10 ? 3.5 : s <= 32 << 20 ? 11 : 80;
        var pts = Memory.CacheAnalysis.LadderSizes(1L << 30).Select(s => (s, Ns(s) * (1 + (s % 7) / 100.0))).ToList();
        var levels = Memory.CacheAnalysis.DetectLevels(pts);
        Assert.Equal(new[] { "L1", "L2", "L3", "RAM" }, levels.Select(l => l.Name));
        Assert.InRange(levels[0].SizeBytes, 32 << 10, 48 << 10);
        Assert.InRange(levels[1].SizeBytes, 900 << 10, 1280 << 10);
        Assert.InRange(levels[2].SizeBytes, 22 << 20, 32 << 20);
        Assert.InRange(levels[3].LatencyNs, 75, 90);
    }
}
