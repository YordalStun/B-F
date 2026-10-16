using System.Diagnostics;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Platform;
using BenchForge.Core.Scoring;

namespace BenchForge.Core.Cpu;

/// <summary>CPU benchmark (single + multi thread, six workloads) and CPU stress / error-detection test.</summary>
public sealed class CpuModule : ITestModule
{
    private const int Seeds = 8;
    public Component Component => Component.Cpu;

    public Task<ModuleResult> RunAsync(StepContext ctx) => Task.FromResult(
        ctx.Kind == StepKind.Benchmark ? Benchmark(ctx)
        : ctx.Step.Variant switch
        {
            StepVariants.CoreCycle => CpuStability.CoreCycle(ctx),
            StepVariants.LightLoad => CpuStability.LightLoad(ctx),
            StepVariants.PsuSpike => CpuStability.PsuSpike(ctx),
            _ => Stress(ctx),
        });

    /// <summary>Known-good checksums, computed twice and confirmed before testing starts.</summary>
    public static ulong[] ReferenceChecksums(CpuKernel k)
    {
        var state = k.CreateState();
        var r = new ulong[Seeds];
        for (int s = 0; s < Seeds; s++)
        {
            ulong a = k.RunUnit(state, s), b = k.RunUnit(state, s);
            if (a != b) { ulong c = k.RunUnit(state, s); a = c == a ? a : c == b ? b : c; }
            r[s] = a;
        }
        return r;
    }

    private static int ThreadCount(StepContext ctx) => ctx.Settings.CpuThreads > 0 ? Math.Min(ctx.Settings.CpuThreads, Environment.ProcessorCount) : Environment.ProcessorCount;

    private ModuleResult Benchmark(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Cpu, Kind = StepKind.Benchmark };
        var kernels = CpuKernel.All();
        int threads = ThreadCount(ctx);
        // When other parts are stressed at the same time, leave one thread so results are not starved.
        double phaseSeconds = Math.Max(0.5, (ctx.DurationSeconds - 2) / (kernels.Length * 2));
        result.Info["Threads"] = threads.ToString();
        result.Info["Vector width"] = $"{System.Numerics.Vector<float>.Count * 32}-bit";
        result.Info["Phase length"] = $"{phaseSeconds:0.0} s";

        var st = new List<double>(); var mt = new List<double>();
        int phase = 0, totalPhases = kernels.Length * 2;
        foreach (var k in kernels)
        {
            if (ctx.Token.IsCancellationRequested) break;
            var refs = ReferenceChecksums(k);
            foreach (bool multi in new[] { false, true })
            {
                if (ctx.Token.IsCancellationRequested) break;
                ctx.Progress($"{k.Name} — {(multi ? $"{threads} threads" : "1 thread")}", (double)phase / totalPhases);
                var (rate, errors) = RunPhase(ctx, k, refs, multi ? threads : 1, phaseSeconds, pin: false, report: (r) => ctx.Metric("cpu.rate", "CPU throughput (" + k.Unit + ")", r, k.Unit));
                result.ErrorCount += errors;
                double reference = ScoreReferences.CpuSingle[k.Name] * (multi ? ScoreReferences.CpuMultiFactor : 1);
                double score = Score.Linear(rate, reference);
                (multi ? mt : st).Add(score);
                result.SubScores.Add(new SubScore
                {
                    Name = k.Name, Group = multi ? "Multi-thread" : "Single-thread",
                    Value = Math.Round(rate, 2), Unit = k.Unit, Score = Math.Round(score),
                });
                phase++;
            }
        }
        double stScore = Score.GeoMean(st), mtScore = Score.GeoMean(mt);
        result.SubScores.Insert(0, new SubScore { Name = "Single-thread score", Group = "Summary", Value = Math.Round(stScore), Unit = "pts", Score = Math.Round(stScore) });
        result.SubScores.Insert(1, new SubScore { Name = "Multi-thread score", Group = "Summary", Value = Math.Round(mtScore), Unit = "pts", Score = Math.Round(mtScore) });
        if (st.Count > 0 && mt.Count > 0)
        {
            result.Score = Math.Round(Score.GeoMean(new[] { stScore, mtScore }));
            result.Info["Multi-thread scaling"] = $"{mtScore * ScoreReferences.CpuMultiFactor / Math.Max(1, stScore):0.0}× of one thread";
        }
        if (result.ErrorCount > 0) result.Info["Calculation errors"] = result.ErrorCount.ToString();
        ctx.Progress("Done", 1);
        return result;
    }

    /// <summary>Runs one kernel on N threads for a fixed time, verifying every result. Returns throughput in kernel units/s.</summary>
    private static (double Rate, int Errors) RunPhase(StepContext ctx, CpuKernel k, ulong[] refs, int threads, double seconds, bool pin, Action<double> report)
    {
        long[] units = new long[threads];
        int errors = 0;
        var startSignal = new ManualResetEventSlim(false);
        var stopAt = 0L;
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            workers[t] = new Thread(() =>
            {
                if (pin) ThreadPinning.Pin(tid);
                var state = k.CreateState();
                k.RunUnit(state, 0); // warm caches/JIT
                startSignal.Wait();
                long n = 0; int seed = tid % Seeds;
                while (Stopwatch.GetTimestamp() < Volatile.Read(ref stopAt) && !ctx.Token.IsCancellationRequested)
                {
                    var r = k.RunUnit(state, seed);
                    if (r != refs[seed])
                    {
                        Interlocked.Increment(ref errors);
                        ctx.Error(new TestError { Test = k.Name, Thread = tid, Message = $"Wrong result on thread {tid} (seed {seed})", Detail = $"expected {refs[seed]:X16} got {r:X16}" });
                    }
                    n++;
                    seed = (seed + 1) % Seeds;
                    Volatile.Write(ref units[tid], n);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"CPU worker {t}" };
            workers[t].Start();
        }
        Thread.Sleep(50);
        long start = Stopwatch.GetTimestamp();
        Volatile.Write(ref stopAt, start + (long)(seconds * Stopwatch.Frequency));
        startSignal.Set();
        // Live rate reporting.
        long lastUnits = 0; long lastT = start;
        while (workers.Any(w => w.IsAlive))
        {
            Thread.Sleep(250);
            long now = Stopwatch.GetTimestamp(); long u = units.Sum();
            double dt = (now - lastT) / (double)Stopwatch.Frequency;
            if (dt > 0.2) { report((u - lastUnits) * k.WorkPerUnit / dt); lastUnits = u; lastT = now; }
        }
        double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        return (units.Sum() * k.WorkPerUnit / Math.Max(1e-6, elapsed), errors);
    }

    private ModuleResult Stress(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Cpu, Kind = StepKind.Stress };
        var all = CpuKernel.All();
        var kernels = ctx.Settings.CpuStressMode switch
        {
            CpuStressMode.MaxHeat => all.Where(k => k.IsHeavyVector).ToArray(),
            CpuStressMode.Integer => all.Where(k => k.IsInteger).ToArray(),
            CpuStressMode.FloatingPoint => all.Where(k => !k.IsInteger).ToArray(),
            _ => all,
        };
        int threads = ThreadCount(ctx);
        // Leave a little room for the GPU feeder thread when stressing everything together.
        if (ctx.Concurrent && ctx.Step.Components.Contains(Component.Gpu) && threads > 2) threads--;
        result.Info["Threads"] = threads.ToString();
        result.Info["Mode"] = ctx.Settings.CpuStressMode.ToString();
        ctx.Progress("Preparing known-good results", 0);
        var refs = kernels.ToDictionary(k => k, ReferenceChecksums);

        long[] work = new long[threads];          // normalised work units (points×1000)
        int[] threadErrors = new int[threads];
        var errorsByKernel = new int[kernels.Length];
        var deadline = ctx.Deadline;
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            workers[t] = new Thread(() =>
            {
                bool pinned = ThreadPinning.Pin(tid);
                var states = kernels.Select(k => k.CreateState()).ToArray();
                int ki = tid % kernels.Length, seed = tid % Seeds;
                var switchAt = DateTime.Now.AddSeconds(3);
                while (DateTime.Now < deadline && !ctx.Token.IsCancellationRequested)
                {
                    var k = kernels[ki];
                    var r = k.RunUnit(states[ki], seed);
                    if (r != refs[k][seed])
                    {
                        Interlocked.Increment(ref threadErrors[tid]);
                        Interlocked.Increment(ref errorsByKernel[ki]);
                        ctx.Error(new TestError
                        {
                            Test = k.Name, Thread = tid,
                            Message = pinned ? $"Calculation error on logical CPU {tid}" : $"Calculation error on worker {tid}",
                            Detail = $"seed {seed}: expected {refs[k][seed]:X16}, got {r:X16}",
                        });
                    }
                    Interlocked.Add(ref work[tid], (long)(k.WorkPerUnit / ScoreReferences.CpuSingle[k.Name] * 1_000_000));
                    seed = (seed + 1) % Seeds;
                    if (kernels.Length > 1 && DateTime.Now >= switchAt) { ki = (ki + 1) % kernels.Length; switchAt = DateTime.Now.AddSeconds(3); }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"CPU stress {t}" };
            workers[t].Start();
        }

        long last = 0; var lastT = DateTime.Now;
        var samples = new List<double>();
        while (workers.Any(w => w.IsAlive))
        {
            Thread.Sleep(500);
            var now = DateTime.Now;
            long w = work.Sum();
            double dt = (now - lastT).TotalSeconds;
            if (dt >= 0.9)
            {
                double rate = (w - last) / 1000.0 / dt; // points per second
                ctx.Metric("cpu.stress", "CPU stress throughput", Math.Round(rate, 1), "pts/s");
                samples.Add(rate);
                last = w; lastT = now;
            }
            ctx.Progress($"Stressing {threads} threads ({ctx.Settings.CpuStressMode})", ctx.Elapsed.TotalSeconds / ctx.DurationSeconds);
        }
        result.ErrorCount = threadErrors.Sum();
        if (samples.Count > 4)
        {
            var firstQ = samples.Take(Math.Max(1, samples.Count / 5)).Average();
            var lastQ = samples.Skip(samples.Count - Math.Max(1, samples.Count / 5)).Average();
            result.Info["Throughput start → end"] = $"{firstQ:0} → {lastQ:0} pts/s ({(lastQ / Math.Max(1, firstQ) - 1) * 100:+0;-0}%)";
            result.SubScores.Add(new SubScore { Name = "Sustained throughput", Group = "Stress", Value = Math.Round(lastQ, 1), Unit = "pts/s", Score = Math.Round(lastQ / ScoreReferences.CpuMultiFactor) });
            result.SubScores.Add(new SubScore { Name = "Throughput drop over test", Group = "Stress", Value = Math.Round((1 - lastQ / Math.Max(1, firstQ)) * 100, 1), Unit = "%", HigherIsBetter = false });
        }
        if (result.ErrorCount > 0)
        {
            result.Info["Errors by thread"] = string.Join(", ", threadErrors.Select((e, i) => (e, i)).Where(x => x.e > 0).Select(x => $"CPU{x.i}: {x.e}"));
            result.Info["Errors by workload"] = string.Join(", ", errorsByKernel.Select((e, i) => (e, i)).Where(x => x.e > 0).Select(x => $"{kernels[x.i].Name}: {x.e}"));
        }
        ctx.Progress("Done", 1);
        return result;
    }
}
