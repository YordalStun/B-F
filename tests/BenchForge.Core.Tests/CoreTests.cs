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
