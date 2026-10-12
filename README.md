# BenchForge

Windows hardware benchmark, stress test and crash-diagnosis tool. One portable `BenchForge.exe` — put it on a USB stick and run it on any Windows 10/11 64-bit PC.

## What it does

| | |
|---|---|
| **Benchmarks** | CPU (6 workloads, single + multi-thread), RAM (bandwidth + latency), drive (CrystalDiskMark-style queue depths: SEQ1M Q8, 4K Q1/Q32/Q32×4, latency percentiles + stalls, 70/30 mixed, small files; cache bypassed; compared with what that kind of drive should do), GPU (real OpenGL 3D scenes at a fixed 1920×1080). |
| **Stress / burn tests** | Every part at 100% load, alone or all together, for minutes or hours. CPU results are verified (finds silent calculation errors and which core), RAM is pattern-tested, VRAM is pattern-tested, drive data is verified. |
| **Scores** | Linear: 1000 = reference machine, 2000 = twice as fast. Per-test sub-scores in real units (MB/s, FPS, ns, GFLOP/s). |
| **Live view** | Time left + finish time, every step, live charts of temps/usage/clocks/power/FPS, and an always-on-top overlay that sits over the 3D test. |
| **Drive health check** | Read-only surface scan of the whole disk (bad + slow areas, speed map like HD Tune), sustained-write test that finds where the SLC write cache runs out, SATA SMART attributes (reallocated/pending sectors, CRC cable errors), NVMe health log (% life used, spare, media errors), PCIe link check (SSD in the wrong slot), TRIM and partition alignment — with one-click fixes for TRIM. Pick several drives at once. |
| **Results** | Recommended fixes, findings (throttling, overheating, single-channel RAM, XMP off, slow drive…), graphs of every sensor, min/avg/max of every sensor, errors, system info. Export HTML report. |
| **Crashes** | If the PC crashes mid-test, BenchForge reopens after restart and shows what was running, the last temps/power, Windows events (blue screens, WHEA, GPU resets, disk errors) and crash-dump stop codes — with a plain-English explanation. |
| **Compare** | Pick 2+ runs or crashes → it looks for what they have in common (same stop code, same part under load, temperature, timing, same core, same driver…). |
| **Event Viewer** | Type “all bluescreens” or “gpu crashes last week”, or click a category. |
| **Export for AI** | One click saves a single `.md` file with everything — tests, scores, every sensor (min/avg/max + history CSV), crash details with the last seconds of sensor data, Windows crash/WHEA/GPU/disk events, Reliability Monitor, drivers, system info, BenchForge's own analysis. Upload it to Claude.ai or any AI — no API key needed. Optional `.zip` bundle adds full-resolution sensor CSVs and the original result files. |
| **Fixes** | One-click software fixes (system file repair, chkdsk, memory diagnostic, power plan, Fast Startup, crash dumps, GPU timeout, temp cleanup) — always asks first. |
| **Laptop mode** | Auto-detects laptops: running at the thermal limit is treated as normal (safety limits 100 °C / 95 °C held for 10 s, hard stop at 105 °C); instead BenchForge scores the **cooling** — how much performance the CPU keeps under sustained load. Warns when on battery or if the charger is pulled mid-test, and reports battery health. |
| **Small screens** | Works on laptops: icon-only sidebar below ~1150 px, panels stack on narrow windows, pages scroll instead of squashing. |
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
