using System.Net;
using System.Net.NetworkInformation;
using BenchForge.Core.Model;

namespace BenchForge.Core.Platform;

/// <summary>
/// Light background ping to the router and the internet while tests run (one ping per target every 2 s).
/// Network drivers that fail under heavy CPU/GPU load show up as lost pings or dropouts.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<NetworkTargetStats> _targets = new();
    private Task? _task;
    /// <summary>A dropout = this many lost pings in a row (10 s).</summary>
    public const int DropoutPings = 5;

    public event Action<string>? DropoutStarted;
    public event Action<string, double>? Sample;

    public IReadOnlyList<NetworkTargetStats> Targets => _targets;

    public void Start()
    {
        var gw = DefaultGateway();
        if (gw != null) _targets.Add(new NetworkTargetStats { Name = "Router", Address = gw.ToString() });
        _targets.Add(new NetworkTargetStats { Name = "Internet", Address = "1.1.1.1" });
        _task = Task.Run(Loop);
    }

    private async Task Loop()
    {
        using var ping = new Ping();
        var lostInRow = new int[_targets.Count];
        var everReplied = new bool[_targets.Count];
        while (!_cts.IsCancellationRequested)
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (t.Unavailable) continue;
                double? ms = null;
                try
                {
                    var r = await ping.SendPingAsync(IPAddress.Parse(t.Address), TimeSpan.FromSeconds(1), cancellationToken: _cts.Token);
                    if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
                }
                catch (OperationCanceledException) { return; }
                catch { }
                lock (t)
                {
                    t.Sent++;
                    if (ms is double v)
                    {
                        everReplied[i] = true;
                        t.Received++; t.SumMs += v; t.MaxMs = Math.Max(t.MaxMs, v);
                        lostInRow[i] = 0;
                    }
                    else if (++lostInRow[i] == DropoutPings) { t.Dropouts++; DropoutStarted?.Invoke(t.Name); }
                }
                // A target that never answers at all is blocked (firewall / VM), not dropping out: stop judging it.
                if (!everReplied[i] && t.Sent >= 5) t.Unavailable = true;
                Sample?.Invoke(t.Name, ms ?? -1);
            }
            if (_targets.All(t => t.Unavailable)) return;
            try { await Task.Delay(2000, _cts.Token); } catch { return; }
        }
    }

    public static IPAddress? DefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task?.Wait(3000); } catch { }
    }
}
