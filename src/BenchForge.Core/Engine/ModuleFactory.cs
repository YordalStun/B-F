using BenchForge.Core.Cpu;
using BenchForge.Core.Gpu;
using BenchForge.Core.Memory;
using BenchForge.Core.Model;
using BenchForge.Core.Storage;

namespace BenchForge.Core.Engine;

public sealed class ModuleFactory : IModuleFactory
{
    public ITestModule Create(Component component, StepKind kind) => component switch
    {
        Component.Cpu => new CpuModule(),
        Component.Memory => new MemoryModule(),
        Component.Storage => new StorageModule(),
        Component.Gpu => new GpuModule(),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };
}
