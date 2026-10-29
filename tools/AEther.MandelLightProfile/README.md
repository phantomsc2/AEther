# Graphics profiling

Run point-light profiles with `dotnet run --project tools/AEther.MandelLightProfile -- --point-lights`.
The profile uses 1280x720, GPU-generated STB light rings, a stationary camera and
128 cubes. It waits for the shared medium LUT and measures GPU passes after
warmup. Captures and timing JSON go to artifacts/mandellight-v5.

`--projection` captures the current perspective scenes in landscape, portrait and
square viewports. These are visual checks, not timing comparisons.
`--fluid-v4` exercises the shared fluid solver through its ring material scene.
The default mode profiles the MandelLightv2 volume prototype; `--reference`
increases its sample budget. V4 raster shadow, spot/filament and LUT experiments
have been removed with that scene.
