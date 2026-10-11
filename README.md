# BenchForge

Windows hardware benchmark, stress test and crash-diagnosis tool. One portable `BenchForge.exe` — put it on a USB stick and run it on any Windows 10/11 64-bit PC.

## What it does

| | |
|---|---|
| **Benchmarks** | CPU (6 workloads, single + multi-thread), RAM (bandwidth + latency), drive (sequential + 4K random, cache bypassed), GPU (real OpenGL 3D scenes at a fixed 1920×1080). |
| **Stress / burn tests** | Every part at 100% load, alone or all together, for minutes or hours. CPU results are verified (finds silent calculation errors and which core), RAM is pattern-tested, VRAM is pattern-tested, drive data is verified. |
| **Scores** | Linear: 1000 = reference machine, 2000 = twice as fast. Per-test sub-scores in real units (MB/s, FPS, ns, GFLOP/s). |
| **Live view** | Time left + finish time, every step, live charts of temps/usage/clocks/power/FPS, and an always-on-top overlay that sits over the 3D test. |
| **Results** | Recommended fixes, findings (throttling, overheating, single-channel RAM, XMP off, slow drive…), graphs of every sensor, min/avg/max of every sensor, errors, system info. Export HTML report. |
| **Crashes** | If the PC crashes mid-test, BenchForge reopens after restart and shows what was running, the last temps/power, Windows events (blue screens, WHEA, GPU resets, disk errors) and crash-dump stop codes — with a plain-English explanation. |
| **Compare** | Pick 2+ runs or crashes → it looks for what they have in common (same stop code, same part under load, temperature, timing, same core, same driver…). |
| **Event Viewer** | Type “all bluescreens” or “gpu crashes last week”, or click a category. |
| **AI (optional)** | Built-in analysis always works offline. Optionally: a local AI model (downloads on selection, runs offline), Claude with your API key, or your own Ollama/LM Studio server. Results files (`.bfresult`) can be taken to another PC to analyse there. |

## Running

1. Download `BenchForge-portable-win-x64.zip` from the **Releases** (latest build) or the **Actions** tab.
2. Extract it anywhere (USB stick is fine) and run `BenchForge.exe` — it asks for administrator rights (needed for sensors, event logs and crash dumps).
3. Everything it saves goes into a `BenchForgeData` folder next to the exe.

For CPU temperature/clock/power sensors, install the free [PawnIO](https://pawnio.eu) driver once per PC (GPU/drive/RAM sensors work without it).

## Building

```
dotnet test tests/BenchForge.Core.Tests
dotnet publish src/BenchForge.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Projects: `BenchForge.Core` (all test/analysis logic, cross-platform), `BenchForge.Gpu` (OpenGL scenes), `BenchForge.App` (WPF UI), `BenchForge.Cli` (headless runner), tests.

![3D test scenes](docs/screenshots/2-Shading.png)
