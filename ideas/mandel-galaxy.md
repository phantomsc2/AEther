# MandelGalaxy: procedural spectral swarm

## Structure

- One instanced draw: 65,536 available emitters, initially 33,280 active.
- A stable permutation distributes any active population across the orbital hierarchy.
- Large stars follow broad galactic orbits; smaller companions orbit deterministic stellar anchors, with moons and dust around shared analytic planet orbits.
- Each emitter samples a log-frequency analyzer band from a weighted combination of galactic radius, distance along its spiral arm, orbital phase and stable scatter. The defaults emphasize bass inside and treble outside.
- Positions are analytic functions of time and seeded orbital data. No N-body solver, respawning, particle state upload or fluid simulation.
- Perspective projection, inclined orbital planes, spiral arms, thickness and eccentricity create the 3D structure. Materials are additive billboards, not opaque sphere meshes.

## Controls (all numbers have normalized sliders)

| Group | Controls |
|---|---|
| Galaxy | Population (1,024–65,536), Radius, Spacing, Arms (1–12), Winding, Thickness, Inclination, Eccentricity |
| Orbits | Speed, Differential rotation, Phase, AudioGain, StereoWidth |
| Camera | Distance, Tilt, Heading, Speed; Orbit toggle |
| Appearance | Size, Gas, Rays, Fractal, Exposure |
| Trails | Length, Width, Falloff, AetherSpeed, AetherHeading, AetherElevation |
| Spectrum | Radial, Arm, Orbit (relative coordinate weights), Scatter (stable frequency offset) |
| Layers | Cores, Gas, NoteRays, Mandelstars, HistogramTrails, Shells, Starfield |

## Spectrum and stereo

The final sample coordinate combines three normalized coordinates using the relative Spectrum weights: world-space distance from the galactic center, cumulative arc length along the host's logarithmic spiral arm, and a smooth sinusoidal coordinate around the body's own orbit. Arm distance uses a Simpson approximation of the spiral's arc length; companions inherit their host's progress along the arm. Scatter adds a fixed signed offset per emitter, then Spacing redistributes the combined coordinate across the analyzer domain. With all three weights at zero, the mapping falls back to radial distance.

Radius sets the spatial extent, including companion orbits. Sampling coordinates are evaluated before camera projection or trail drift, so camera movement does not change frequency assignments. The underlying analyzer domain determines actual hertz. Body scale and orbital role retain their seeded hierarchy independently of the audio mapping.

Each analyzer band has a matching palette hue. Its tonal and noise energy increase color saturation, and its transients brighten the color toward white. These modulations use that same band's current or historical audio and stereo coordinate. They add no light without the existing audio-driven emission.

Each emitter has a stable pseudorandom stereo coordinate independent of screen position. StereoWidth zero uses the channel average; one spans left-only through right-only responses. The same coordinate selects current and historical audio, so orbiting the camera does not reverse an emitter's audio pan. This controls visual response only; it does not alter audio playback.

## Time travels outward

Each star has an eight-segment perspective ribbon tracing its past hierarchical orbit and past camera projection, plus a shared world-space aether drift. The head samples the current spectrum; increasing distance along the ribbon samples progressively older histogram rows. The tip represents the end of the retained analyzer history. Unavailable history contributes zero. Length selects 5–100% of the retained history; geometry and note sampling use the same age. AetherSpeed sets one shared drift velocity for all bodies, with AetherHeading and AetherElevation setting its direction. Drift is zero at the head and accumulates with age. HistorySeconds sets the retained temporal span.

Noise drives gas, tones illuminate rays multiplicatively through that gas, and transients excite the local Mandelbox slice. Each trail evaluates the combined sampling coordinates at the star's historical position and orbital phase, retaining the same stable scatter. The resulting coordinate interpolates along the ribbon; audio selects an exact frequency column with interpolation only through time. Noise and transients can modulate an existing trail's color but cannot illuminate it without tonal energy. Emitters assigned to the same analyzer band share that band's hue and history; no note tracking is inferred between bands.

## Cost and limits

- One draw, no vertex/index buffers, no per-frame CPU particle loop.
- Small projected stars use integrated glow; nearby stars resolve six Mandelbox folds.
- Subpixel ribbons are suppressed. Hidden population pieces collapse before rasterization.
- Additive overlap can still become expensive at large Size, Length and Population settings. No performance benchmark has been run.
- The renderer submits the maximum instance count; Population suppresses unused instances on the GPU, so it reduces pixel work but not the maximum vertex workload.
- Restart after rebuilding for the instancing renderer change; subsequent shader edits can reload normally.

Rider runs the x64 build (bin/x64). Build with /p:Platform=x64, then restart; shader reload cannot add instancing to an older renderer. Launch with --scene=MandelGalaxy to select this scene on startup.

