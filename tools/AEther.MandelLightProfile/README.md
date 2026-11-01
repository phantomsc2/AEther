# Graphics profiling

Run point-light profiles with `dotnet run --project tools/AEther.MandelLightProfile -- --point-lights`.
The profile uses 1280x720, GPU-generated STB light rings, a stationary camera and
128 cubes. The GPU builds the shared medium LUT before shading; the profile measures passes after
warmup. Captures and timing JSON go to artifacts/mandellight-v5.

`--projection` captures the current perspective scenes in landscape, portrait and
square viewports. These are visual checks, not timing comparisons.
`--fluid` exercises the shared fluid solver through its ring material scene.
Use `--vc1` and/or `--vc2` to profile the corresponding confinement methods at
their default strengths; output resolution must be at least 1280x720.
The fluid cases use the application's default 1024x512 simulation grid (`low`
uses 128x128). Override it with `--grid-width=512 --grid-height=256`.
For example, `--fluid --case=default --width=3200 --height=2000 --frames=240`
profiles high-resolution output. `--show` briefly displays the profiling window.
Console and JSON results include the output format and detected HDR state.
The harness uses synthetic audio at 60 Hz by default and synchronizes GPU queries
each frame; it excludes audio analysis, compositor/present waits and scanout.
Use `--capture-every=120` to save matched visual checkpoints outside the GPU
timing interval. The final velocity and material attachments are also exported
as raw float32 files for exact state comparisons.
The default solver now uses two warm-started V-cycles, retaining three cycles
for cold starts and boundary-mode changes. `--reference-pressure` selects the
former three-cycle, zero-initialized solver from startup for comparisons.
Checkpoints record pressure residual RMS before recording final velocity
divergence (which includes the scene's boundary forcing), outside timed work.
The default mode profiles the MandelLightv2 volume prototype; `--reference`
increases its sample budget. V4 raster shadow, spot/filament and LUT experiments
have been removed with that scene.
