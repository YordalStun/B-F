using System.Diagnostics;
using BenchForge.Core.Gpu;

namespace BenchForge.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The same exe doubles as the 3D test worker so the whole app is one portable file.
        if (args.Length > 0 && args[0] == "--gpu-worker")
            return BenchForge.Gpu.GpuProgram.Run(args[1..]);
        // CI-only: end-to-end test of the partition-alignment fix on throw-away virtual disks.
        if (args.Length > 1 && args[0] == "--align-selftest")
            return Platform.AlignSelfTest.Run(args[1]);

        GpuModule.LaunchFactory = workerArgs => new ProcessStartInfo(Environment.ProcessPath!, "--gpu-worker " + workerArgs);
        var app = new App { StartupArgs = args };
        app.InitializeComponent();
        return app.Run();
    }
}
