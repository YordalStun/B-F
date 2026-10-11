using BenchForge.Core.Model;

namespace BenchForge.Core.Crash;

public sealed record BugCheckInfo(uint Code, string Name, string Meaning, Component? LikelyComponent, string[] Causes, string[] Fixes);

/// <summary>Plain-English explanations of the Windows stop codes people actually hit.</summary>
public static class BugCheckKnowledgeBase
{
    private static readonly Dictionary<uint, BugCheckInfo> Map = new BugCheckInfo[]
    {
        new(0x124, "WHEA_UNCORRECTABLE_ERROR", "The CPU (or something on the PCIe/memory bus) reported a fatal hardware error.", Component.Cpu,
            new[] { "CPU overclock / undervolt / PBO Curve Optimizer too aggressive", "Not enough CPU or SoC voltage", "Overheating CPU or VRMs", "Unstable RAM (XMP/EXPO) — memory controller errors are reported through the CPU", "Faulty CPU, motherboard or PSU" },
            new[] { "Reset BIOS to defaults (remove all overclocks/undervolts, disable XMP/EXPO) and retest", "Update the BIOS", "Check CPU temperatures under load", "If it only happens with XMP on: lower RAM speed or raise SoC/VDDIO voltage slightly" }),
        new(0x101, "CLOCK_WATCHDOG_TIMEOUT", "A CPU core stopped responding to the others.", Component.Cpu,
            new[] { "Unstable CPU overclock/undervolt", "Too little core voltage at idle/boost transitions", "Faulty CPU", "Outdated BIOS" },
            new[] { "Remove CPU overclock / undervolt", "Update the BIOS and chipset drivers", "Run the CPU stress test for 1 hour" }),
        new(0x1A, "MEMORY_MANAGEMENT", "Windows found corrupted memory-management data.", Component.Memory,
            new[] { "Faulty or unstable RAM (very common with XMP/EXPO)", "RAM not seated properly", "Driver bug corrupting memory", "Failing storage holding the page file" },
            new[] { "Run the RAM error test for at least 1 hour", "Disable XMP/EXPO and retest", "Test one RAM stick at a time", "Reseat RAM in the slots the motherboard manual recommends" }),
        new(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", "Something accessed memory that did not exist.", Component.Memory,
            new[] { "Faulty RAM", "Buggy driver or antivirus", "Corrupt system files", "Failing drive" },
            new[] { "Run the RAM error test", "Update/roll back recently changed drivers", "Run 'sfc /scannow' and 'DISM /Online /Cleanup-Image /RestoreHealth'" }),
        new(0x3B, "SYSTEM_SERVICE_EXCEPTION", "A system call crashed inside the kernel.", null,
            new[] { "Graphics or other driver bug", "Unstable RAM or CPU", "Corrupt system files" },
            new[] { "Update graphics and chipset drivers", "Run RAM and CPU stress tests", "Run 'sfc /scannow'" }),
        new(0x0A, "IRQL_NOT_LESS_OR_EQUAL", "A driver accessed invalid memory at a high priority level.", null,
            new[] { "Faulty driver (network, audio, USB, anti-cheat are common)", "Unstable RAM" },
            new[] { "Update or roll back recently installed drivers", "Run the RAM error test" }),
        new(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver accessed invalid memory at a high priority level.", null,
            new[] { "Faulty driver — network/Wi-Fi drivers are the most common", "Unstable RAM" },
            new[] { "Update network, chipset and storage drivers", "Run the RAM error test" }),
        new(0x116, "VIDEO_TDR_FAILURE", "The graphics card stopped responding and Windows could not reset it.", Component.Gpu,
            new[] { "GPU overclock/undervolt unstable", "GPU overheating", "Graphics driver bug or corrupt install", "PSU can't deliver GPU power spikes / bad PCIe power cables", "Failing graphics card" },
            new[] { "Clean-reinstall the graphics driver (DDU in safe mode)", "Remove GPU overclock / undervolt", "Use separate PCIe power cables (no daisy-chain)", "Check GPU temperatures in the GPU stress test" }),
        new(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", "The graphics driver stopped responding (it was recovered).", Component.Gpu,
            new[] { "Unstable GPU clocks", "Driver issue", "Overheating" },
            new[] { "Update or clean-reinstall the graphics driver", "Remove GPU overclock" }),
        new(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "The GPU scheduler hit an internal error.", Component.Gpu,
            new[] { "Graphics driver bug", "Unstable GPU or VRAM" }, new[] { "Clean-reinstall graphics driver", "Run the GPU VRAM check" }),
        new(0x133, "DPC_WATCHDOG_VIOLATION", "A driver spent too long running at high priority.", Component.Storage,
            new[] { "Storage controller / SSD firmware (very common: old SATA AHCI drivers or SSD firmware)", "Network or audio driver", "Outdated BIOS" },
            new[] { "Update SSD firmware and chipset/storage drivers", "Update the BIOS", "Update network and audio drivers" }),
        new(0x7A, "KERNEL_DATA_INPAGE_ERROR", "Windows could not read data back from the page file/drive.", Component.Storage,
            new[] { "Failing drive or bad sectors", "Loose SATA/power cable", "Faulty RAM" },
            new[] { "Check drive health (SMART) and back up data", "Reseat/replace SATA cables", "Run the storage and RAM tests" }),
        new(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A system thread crashed.", null,
            new[] { "Driver bug (graphics, storage, network)", "Unstable hardware" },
            new[] { "Update drivers", "Run CPU and RAM stress tests" }),
        new(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", "A kernel-mode program crashed.", null,
            new[] { "Driver bug", "Unstable CPU or RAM" }, new[] { "Update drivers", "Run CPU and RAM stress tests" }),
        new(0xEF, "CRITICAL_PROCESS_DIED", "A process Windows needs to run was killed.", Component.Storage,
            new[] { "Corrupt system files", "Failing drive", "Unstable RAM/CPU" },
            new[] { "Run 'sfc /scannow' and DISM repair", "Check drive health", "Run RAM test" }),
        new(0x9F, "DRIVER_POWER_STATE_FAILURE", "A driver did not handle sleep/wake correctly.", null,
            new[] { "Outdated driver (graphics, network, USB)", "Fast Startup issues" },
            new[] { "Update drivers", "Disable Fast Startup", "Update BIOS" }),
        new(0x139, "KERNEL_SECURITY_CHECK_FAILURE", "The kernel detected corrupted data structures.", Component.Memory,
            new[] { "Driver bug", "Unstable RAM", "Corrupt system files" }, new[] { "Run RAM test", "Update drivers", "Run 'sfc /scannow'" }),
        new(0x19, "BAD_POOL_HEADER", "Kernel memory pool was corrupted.", Component.Memory,
            new[] { "Driver bug", "Faulty RAM" }, new[] { "Run RAM test", "Update recently changed drivers" }),
        new(0xC2, "BAD_POOL_CALLER", "A driver made a bad memory pool request.", null,
            new[] { "Driver bug" }, new[] { "Update or remove recently installed drivers" }),
        new(0x154, "UNEXPECTED_STORE_EXCEPTION", "The memory store component hit an unexpected error.", Component.Storage,
            new[] { "Failing SSD/HDD", "Storage driver", "Antivirus" }, new[] { "Check drive health", "Update storage drivers / SSD firmware" }),
        new(0x109, "CRITICAL_STRUCTURE_CORRUPTION", "Critical kernel code or data was modified.", Component.Memory,
            new[] { "Faulty RAM", "Driver bug", "Cheat/anti-cheat software" }, new[] { "Run RAM test", "Remove system-level tools/drivers" }),
        new(0x3D, "INTERRUPT_EXCEPTION_NOT_HANDLED", "An interrupt could not be handled.", null,
            new[] { "Driver or hardware fault" }, new[] { "Update drivers", "Check hardware" }),
        new(0xF4, "CRITICAL_OBJECT_TERMINATION", "A critical process or thread ended.", Component.Storage,
            new[] { "Failing drive or cable", "Corrupt system files" }, new[] { "Check drive health and cables", "Run 'sfc /scannow'" }),
        new(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "Code tried to run from protected memory.", null,
            new[] { "Driver bug", "Unstable RAM" }, new[] { "Update drivers", "Run RAM test" }),
        new(0x18B, "SECURE_KERNEL_ERROR", "The secure kernel hit an error.", Component.Cpu,
            new[] { "Unstable CPU/RAM", "BIOS/virtualisation settings" }, new[] { "Update BIOS", "Remove overclocks" }),
        new(0x1D8, "ATTEMPTED_SWITCH_FROM_DPC", "A driver made an illegal call.", null,
            new[] { "Driver bug" }, new[] { "Update drivers" }),
        new(0x10E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "Graphics memory management failed.", Component.Gpu,
            new[] { "Graphics driver bug", "Faulty VRAM" }, new[] { "Clean-reinstall graphics driver", "Run GPU VRAM check" }),
        new(0x113, "VIDEO_DXGKRNL_FATAL_ERROR", "The DirectX graphics kernel hit a fatal error.", Component.Gpu,
            new[] { "Graphics driver bug" }, new[] { "Clean-reinstall graphics driver" }),
        new(0x12B, "FAULTY_HARDWARE_CORRUPTED_PAGE", "A memory page was corrupted by faulty hardware.", Component.Memory,
            new[] { "Faulty RAM" }, new[] { "Run RAM test one stick at a time", "Replace failing RAM" }),
        new(0x1C7, "STORE_DATA_STRUCTURE_CORRUPTION", "Memory store data structures were corrupted.", Component.Memory,
            new[] { "Faulty RAM", "Storage driver" }, new[] { "Run RAM test", "Update storage drivers" }),
        new(0x9C, "MACHINE_CHECK_EXCEPTION", "The CPU reported a fatal machine check (older Windows).", Component.Cpu,
            new[] { "CPU/RAM instability or overheating" }, new[] { "Remove overclocks", "Check temperatures" }),
        new(0xE2, "MANUALLY_INITIATED_CRASH", "The crash was triggered on purpose (keyboard shortcut or tool).", null,
            Array.Empty<string>(), Array.Empty<string>()),
    }.ToDictionary(b => b.Code);

    public static BugCheckInfo? Get(uint code)
    {
        // 0x1000007E style codes are the same as 0x7E with extended params.
        if (Map.TryGetValue(code, out var info)) return info;
        if ((code & 0x10000000) != 0 && Map.TryGetValue(code & 0x0FFFFFFF, out info)) return info;
        return null;
    }

    public static string Describe(uint code)
    {
        var i = Get(code);
        return i != null ? $"0x{code:X} {i.Name}" : $"0x{code:X}";
    }

    /// <summary>Extra interpretation of parameters for a few codes where it is useful.</summary>
    public static string? DescribeParameters(uint code, ulong[] p)
    {
        if (p.Length < 4) return null;
        switch (code & 0x0FFFFFFF)
        {
            case 0x124:
                return p[0] switch
                {
                    0 => "Error source: Machine Check Exception (CPU core, cache or memory controller)",
                    1 => "Error source: Corrected Machine Check",
                    2 => "Error source: Corrected Platform Error",
                    4 => "Error source: NMI (non-maskable interrupt, often motherboard/PCIe)",
                    5 => "Error source: PCI Express error (graphics card, NVMe or slot)",
                    6 => "Error source: Generic hardware error",
                    7 => "Error source: Initialization error",
                    _ => $"Error source type {p[0]}",
                };
            case 0x1A:
                return p[0] switch
                {
                    0x41790 => "A page table page has been corrupted (typical for bad/unstable RAM)",
                    0x41792 => "A corrupted PTE was found (often RAM)",
                    0x5003 => "The working set free list is corrupt (often RAM)",
                    0x61941 => "Paging hierarchy corrupted (often RAM)",
                    _ => null,
                };
            case 0x101:
                return $"Stuck processor index: {p[3]}";
            case 0x133:
                return p[0] == 0 ? "A single DPC ran too long" : p[0] == 1 ? "The system spent too long at high IRQL (cumulative)" : null;
            case 0x7A:
                return p[1] switch
                {
                    0xC000009C => "Status: bad sectors on the drive (STATUS_DEVICE_DATA_ERROR)",
                    0xC000009D => "Status: drive disconnected / cable problem (STATUS_DEVICE_NOT_CONNECTED)",
                    0xC000016A => "Status: bad blocks (STATUS_DISK_OPERATION_FAILED)",
                    0xC0000185 => "Status: I/O device error — check cables and drive",
                    _ => null,
                };
            default:
                return null;
        }
    }
}
