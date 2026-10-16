using System.Diagnostics;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Platform;
using BenchForge.Core.Scoring;

namespace BenchForge.Core.Cpu;

/// <summary>
/// Stability tests that full-load stress misses: single cores at maximum boost, idle/light-load crashes, and load spikes
/// that trip weak power supplies. Every result is still checked against known-good checksums.
/// </summary>
public static class CpuStability
{
    /// <summary>Minimum time per core in one round of the core cycler.</summary>
    public const double MinSlotSeconds = 6;

    /// <summary>How long each core is tested and how many rounds fit in the step.</summary>
    public static (int Rounds, double SlotSeconds) CyclePlan(double seconds, int cores)
    {
        cores = Math.Max(1, cores);
        // Prefer ≥ 60 s per core per round (curve-optimizer errors often need a while); several rounds when time allows.
        int rounds = Math.Max(1, (int)(seconds / (cores * 60.0)));
        double slot = Math.Max(MinSlotSeconds, seconds / (cores * rounds));
        return (rounds, slot);
    }

    /// <summary>
    /// One heavy, verified thread pinned to each logical CPU in turn. A single busy core boosts to its highest clock and lowest
    /// voltage margin — where undervolts / Curve Optimizer settings and weak cores fail, even if an all-core test passes.
    /// </summary>
    public static ModuleResult CoreCycle(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Cpu, Kind = StepKind.Stress };
        int cores = Math.Min(Environment.ProcessorCount, 64);
        // Heavy vector maths (highest current), integer (highest clock) and crypto (AES units) — different parts of each core.
        var all = CpuKernel.All();
        var kernels = new[] { all.First(k => k.IsHeavyVector), all.First(k => k is SieveKernel), all.First(k => k is CryptoKernel) };
        ctx.Progress("Preparing known-good results", 0);
        var refs = kernels.ToDictionary(k => k, CpuModule.ReferenceChecksums);
        var (rounds, slot) = CyclePlan(Math.Max(10, ctx.DurationSeconds - 3), cores);
        result.Info["Mode"] = "Core cycler (one heavy thread per core, in turn)";
        result.Info["Time per core"] = $"{slot:0} s × {rounds} round{(rounds == 1 ? "" : "s")}";
        result.Info["Workloads"] = string.Join(", ", kernels.Select(k => k.Name));

        var errors = new int[cores];
        var rates = new List<double>[cores];
        for (int i = 0; i < cores; i++) rates[i] = new();
        bool pinOk = true;
        int done = 0, total = rounds * cores;
        for (int round = 0; round < rounds && !ctx.Token.IsCancellationRequested; round++)
            for (int core = 0; core < cores && !ctx.Token.IsCancellationRequested && DateTime.Now < ctx.Deadline; core++)
            {
                double secs = Math.Min(slot, (ctx.Deadline - DateTime.Now).TotalSeconds);
                if (secs < 1) break;
                ctx.Progress($"Round {round + 1}/{rounds} — core {core} of {cores}", (double)done / total);
                ctx.Metric("cpu.cycle.core", "Core under test", core, "");
                var (rate, errs, pinned) = RunPinned(ctx, kernels, refs, core, secs);
                pinOk &= pinned;
                errors[core] += errs;
                rates[core].Add(rate);
                ctx.Metric("cpu.cycle.rate", "Single-core throughput", Math.Round(rate, 1), "pts/s");
                done++;
            }

        if (!pinOk) result.Info["Note"] = "Windows did not let BenchForge pin the thread to every core — some results may be from a different core.";
        var avg = Enumerable.Range(0, cores).Select(c => rates[c].Count > 0 ? rates[c].Average() : 0).ToArray();
        double best = avg.DefaultIfEmpty(0).Max();
        for (int c = 0; c < cores; c++)
        {
            if (rates[c].Count == 0) continue;
            result.SubScores.Add(new SubScore
            {
                Name = $"Core {c}", Group = "Per core", Value = Math.Round(avg[c], 1), Unit = "pts/s",
                Score = Math.Round(avg[c]), Detail = errors[c] > 0 ? $"{errors[c]} calculation errors" : null,
            });
        }
        result.Charts.Add(new ChartData
        {
            Title = "Single-core throughput per core", XLabel = "Core (#)", YLabel = "pts/s", Unit = "pts/s",
            Points = Enumerable.Range(0, cores).Where(c => rates[c].Count > 0).Select(c => new[] { (double)c, Math.Round(avg[c], 1) }).ToList(),
            Note = errors.Any(e => e > 0) ? "Cores with errors: " + string.Join(", ", errors.Select((e, i) => (e, i)).Where(x => x.e > 0).Select(x => x.i)) : "No calculation errors on any core",
        });
        result.ErrorCount = errors.Sum();
        var bad = errors.Select((e, i) => (e, i)).Where(x => x.e > 0).ToList();
        if (bad.Count > 0) result.Info["Errors by core"] = string.Join(", ", bad.Select(x => $"core {x.i}: {x.e}"));
        var tested = avg.Where(a => a > 0).ToArray();
        if (tested.Length > 1)
        {
            double worst = tested.Min();
            result.Info["Slowest vs fastest core"] = $"{(1 - worst / best) * 100:0}% slower (core {Array.IndexOf(avg, worst)} vs core {Array.IndexOf(avg, best)})";
        }
        ctx.Progress("Done", 1);
        return result;
    }

    private static (double Rate, int Errors, bool Pinned) RunPinned(StepContext ctx, CpuKernel[] kernels, Dictionary<CpuKernel, ulong[]> refs, int core, double seconds)
    {
        double rate = 0; int errs = 0; bool pinned = false;
        var t = new Thread(() =>
        {
            pinned = ThreadPinning.Pin(core);
            var states = kernels.Select(k => k.CreateState()).ToArray();
            long work = 0; int ki = 0, seed = core % 8;
            var sw = Stopwatch.StartNew();
            var switchAt = 2.0;
            while (sw.Elapsed.TotalSeconds < seconds && !ctx.Token.IsCancellationRequested)
            {
                var k = kernels[ki];
                var r = k.RunUnit(states[ki], seed);
                var expected = refs[k][seed % refs[k].Length];
                if (r != expected)
                {
                    errs++;
                    ctx.Error(new TestError { Test = k.Name, Thread = core, Message = $"Calculation error on core {core} at single-core boost", Detail = $"seed {seed}: expected {expected:X16}, got {r:X16}" });
                }
                work += (long)(k.WorkPerUnit / ScoreReferences.CpuSingle[k.Name] * 1_000_000);
                seed = (seed + 1) % refs[k].Length;
                if (sw.Elapsed.TotalSeconds >= switchAt) { ki = (ki + 1) % kernels.Length; switchAt += 2; }
            }
            rate = work / 1000.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        }) { IsBackground = true, Priority = ThreadPriority.Normal, Name = $"Core cycler {core}" };
        t.Start();
        t.Join();
        return (rate, errs, pinned);
    }

    /// <summary>
    /// Mostly idle with short, verified bursts on random cores, like browsing or a desktop sitting idle. Unstable undervolts and
    /// some power-saving (C-state) problems only crash here, never under full load.
    /// </summary>
    public static ModuleResult LightLoad(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Cpu, Kind = StepKind.Stress };
        var kernels = CpuKernel.All();
        ctx.Progress("Preparing known-good results", 0);
        var refs = kernels.ToDictionary(k => k, CpuModule.ReferenceChecksums);
        int cores = Math.Min(Environment.ProcessorCount, 64);
        var rnd = new Random();
        int bursts = 0, errors = 0, wakeups = 0;
        var errorCores = new Dictionary<int, int>();
        result.Info["Mode"] = "Idle / light load (short bursts on random cores, idle in between)";
        while (DateTime.Now < ctx.Deadline && !ctx.Token.IsCancellationRequested)
        {
            // Idle: long sleeps, sometimes a storm of tiny timer wake-ups (cores jump in and out of sleep states).
            double idle = rnd.NextDouble() < 0.3 ? 0.2 + rnd.NextDouble() : 1 + rnd.NextDouble() * 5;
            var idleEnd = DateTime.Now.AddSeconds(idle);
            bool storm = rnd.NextDouble() < 0.25;
            while (DateTime.Now < idleEnd && DateTime.Now < ctx.Deadline && !ctx.Token.IsCancellationRequested)
            {
                Thread.Sleep(storm ? 1 : 50);
                if (storm) wakeups++;
            }
            if (DateTime.Now >= ctx.Deadline || ctx.Token.IsCancellationRequested) break;

            // Burst: 1 thread most of the time, sometimes a few, for 20–600 ms.
            int threads = rnd.NextDouble() < 0.8 ? 1 : rnd.Next(2, Math.Max(3, Math.Min(5, cores + 1)));
            double ms = 20 + rnd.NextDouble() * 580;
            var workers = Enumerable.Range(0, threads).Select(_ =>
            {
                int core = rnd.Next(cores);
                var k = kernels[rnd.Next(kernels.Length)];
                int seed = rnd.Next(refs[k].Length);
                var t = new Thread(() =>
                {
                    ThreadPinning.Pin(core);
                    var state = k.CreateState();
                    var sw = Stopwatch.StartNew();
                    int s = seed;
                    do
                    {
                        var r = k.RunUnit(state, s);
                        if (r != refs[k][s])
                        {
                            Interlocked.Increment(ref errors);
                            lock (errorCores) errorCores[core] = errorCores.GetValueOrDefault(core) + 1;
                            ctx.Error(new TestError { Test = k.Name, Thread = core, Message = $"Calculation error on core {core} during a light-load burst", Detail = $"seed {s}: expected {refs[k][s]:X16}, got {r:X16}" });
                        }
                        s = (s + 1) % refs[k].Length;
                    } while (sw.Elapsed.TotalMilliseconds < ms);
                }) { IsBackground = true, Name = "Light load burst" };
                t.Start();
                return t;
            }).ToList();
            foreach (var w in workers) w.Join();
            bursts++;
            if (bursts % 10 == 0) ctx.Metric("cpu.light.bursts", "Light-load bursts", bursts, "");
            ctx.Progress($"Light load — {bursts} bursts, {errors} errors", ctx.Elapsed.TotalSeconds / ctx.DurationSeconds);
        }
        result.ErrorCount = errors;
        result.SubScores.Add(new SubScore { Name = "Load bursts", Group = "Light load", Value = bursts, Unit = "bursts" });
        result.SubScores.Add(new SubScore { Name = "Timer wake-ups", Group = "Light load", Value = wakeups, Unit = "wake-ups" });
        if (errorCores.Count > 0) result.Info["Errors by core"] = string.Join(", ", errorCores.OrderBy(x => x.Key).Select(x => $"core {x.Key}: {x.Value}"));
        ctx.Progress("Done", 1);
        return result;
    }

    /// <summary>
    /// All threads switch between idle and maximum power together (the GPU does the same in step), following
    /// <see cref="PsuSpikeSchedule"/>. Weak or failing power supplies switch the PC off on these jumps.
    /// </summary>
    public static ModuleResult PsuSpike(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Cpu, Kind = StepKind.Stress };
        var all = CpuKernel.All();
        var heavy = all.FirstOrDefault(k => k.IsHeavyVector) ?? all[0];
        // Short work units keep the on/off edges sharp.
        var kernel = UnitMs(heavy) <= 30 ? heavy : all.OrderBy(UnitMs).First();
        var refs = CpuModule.ReferenceChecksums(kernel);
        int threads = Environment.ProcessorCount;
        int errors = 0;
        result.Info["Mode"] = "Load spikes (all threads on/off together)";
        result.Info["Workload"] = kernel.Name;
        result.Info["Pattern"] = string.Join(" / ", PsuSpikeSchedule.Pattern.Select(p => $"{p:0.#}s")) + " (on / off, repeating)";
        var workers = Enumerable.Range(0, threads).Select(tid =>
        {
            var t = new Thread(() =>
            {
                var state = kernel.CreateState();
                int seed = tid % refs.Length;
                while (DateTime.Now < ctx.Deadline && !ctx.Token.IsCancellationRequested)
                {
                    if (!PsuSpikeSchedule.IsOn(ctx.Elapsed.TotalSeconds)) { Thread.Sleep(2); continue; }
                    var r = kernel.RunUnit(state, seed);
                    if (r != refs[seed])
                    {
                        Interlocked.Increment(ref errors);
                        ctx.Error(new TestError { Test = kernel.Name, Thread = tid, Message = $"Calculation error on worker {tid} during a load spike", Detail = $"seed {seed}: expected {refs[seed]:X16}, got {r:X16}" });
                    }
                    seed = (seed + 1) % refs.Length;
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"PSU spike {tid}" };
            t.Start();
            return t;
        }).ToList();
        while (workers.Any(w => w.IsAlive))
        {
            Thread.Sleep(200);
            bool on = PsuSpikeSchedule.IsOn(ctx.Elapsed.TotalSeconds);
            ctx.Metric("cpu.spike", "Load spike (1 = full load)", on ? 1 : 0, "");
            ctx.Progress($"Load spikes — {(on ? "FULL LOAD" : "idle")} — {PsuSpikeSchedule.Spikes(ctx.Elapsed.TotalSeconds)} spikes so far", ctx.Elapsed.TotalSeconds / ctx.DurationSeconds);
        }
        int spikes = PsuSpikeSchedule.Spikes(Math.Min(ctx.Elapsed.TotalSeconds, ctx.DurationSeconds));
        result.ErrorCount = errors;
        result.SubScores.Add(new SubScore { Name = "Load spikes survived", Group = "Power supply", Value = spikes, Unit = "spikes" });
        ctx.Progress("Done", 1);
        return result;
    }

    private static readonly Dictionary<string, double> UnitMsCache = new();
    /// <summary>Milliseconds one work unit takes on this CPU.</summary>
    private static double UnitMs(CpuKernel k)
    {
        lock (UnitMsCache) if (UnitMsCache.TryGetValue(k.Name, out var v)) return v;
        var s = k.CreateState();
        k.RunUnit(s, 0);
        var sw = Stopwatch.StartNew();
        k.RunUnit(s, 1);
        double ms = sw.Elapsed.TotalMilliseconds;
        lock (UnitMsCache) UnitMsCache[k.Name] = ms;
        return ms;
    }
}
