#!/usr/bin/env bash
# Compiles src/BenchForge.Gpu/Shaders/scenes.hlsl to SPIR-V for the Vulkan backend (needs glslangValidator).
# Run after editing the shaders; a unit test checks the .spv files match the HLSL source hash.
set -euo pipefail
cd "$(dirname "$0")/../src/BenchForge.Gpu/Shaders"
mkdir -p spv
compile() { glslangValidator -D -V --target-env vulkan1.1 -S "$1" -e main --source-entrypoint "$2" scenes.hlsl -o "spv/$2.spv" >/dev/null; }
compile vert FullVS
compile vert GeoVS;   compile frag GeoPS
compile frag ShadePS
compile comp SimCS;   compile vert PartVS; compile frag PartPS
compile frag FillPS
compile comp VramWriteCS; compile comp VramVerifyCS
compile frag BlitPS
compile vert RectVS;  compile frag RectPS
sha256sum scenes.hlsl | cut -d' ' -f1 > spv/source.sha256
echo "SPIR-V up to date: $(ls spv/*.spv | wc -l) shaders"
