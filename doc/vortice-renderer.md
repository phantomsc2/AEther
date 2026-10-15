# Vortice renderer milestone

The current application has three D3D11 fullscreen passes: Spectrum, Histogram and
Mandelbox. The former SharpDX Effects renderer, asset importer, geometry scenes and
fluid simulations were removed. Earlier renderer documents describe historical work.

## Projects and ownership

- AEther: analysis and shared signal types.
- AEther.NAudio: Windows audio capture.
- AEther.Vortice: native graphics resources, shader compilation, audio texture history.
- AEther.WindowsForms: application startup, plain canvas, owned settings window,
  capture/session lifecycle, keyboard shortcuts and borderless fullscreen.

Forms do not handle Direct3D objects. The host passes a native window handle and pixel
dimensions to Renderer. All native graphics operations happen on the UI thread.
Application.Idle renders while the message queue is empty; Present uses vsync. Messages
are serviced between frames. Minimizing pauses rendering, and restore consumes recent
audio from the bounded mailbox. Settings edits rebuild the complete session.

## Audio handoff

Capture copies into the analyzer's existing bounded queue. The sequential analysis worker
publishes borrowed sample events. AudioHistory copies the retained RGBA descriptors into
owned stereo arrays and posts them to a bounded channel, dropping oldest queued rows on
overload. The render thread drains the channel, builds history and uploads textures.
No graphics operation holds an audio/analysis lock. DroppedRows reports discarded visual
updates separately from Analyzer.DroppedInputCount. This is a live display pipeline, not
a lossless offline consumer. Spectrum takes component maxima between rendered frames.

History uses timestamped slots, with a continuous monotonic playhead and inferred capture
times mapped to it at session creation. Rows expire even if loopback sends no silence
packets. The default one-second history occupies each half of Histogram. Frequency stays
discrete while time is interpolated. The current timing still inherits the analyzer's
estimated capture timestamps; hardware QPC timestamps are not used yet.

## Shaders

Common.hlsl contains the fullscreen triangle vertex shader, frame constants at b0, scene
constants at b1, four audio textures at t0–t3 and a sampler at s0. Scene source is appended
to this common header and compiled with D3DCompiler into Shader Model 5 vertex/pixel
programs. There are no Effects11 techniques or native Effects DLLs.

Mandelbox carries forward the finite-iteration box/sphere folds and history illumination
from mandelbox-slice.fx. Retained spectral descriptors drive its illumination and musical
time. The desktop does not need model meshes or a 3D scene graph for these fractals.

During development source shaders are found relative to the repository; deployed apps use
the copied Shaders directory. The host debounces file changes. Reload constructs all new
GPU shaders before replacing any working program. Failed reload preserves the old programs.

## Verification

Run:

```powershell
dotnet build AEther.sln -c Release
dotnet test AEther.Tests
dotnet run --project tools/AEther.Smoke -- .
```

The smoke tool uses synthetic stereo descriptors, without recording system audio. It
checks all three scenes render visible, distinct images, recreates the renderer, resizes,
reloads valid shaders, rejects an invalid shader while retaining working programs, and
checks history expires during silence. PNGs go into its ignored artifacts directory.
It also exercises invisible host windows with a synthetic source: settings ownership,
close/reopen, Space, F11 entry/restore, scene selection with full restart, and shutdown.
CPU tests check mailbox bounds, borrowed-memory copying, mono mirroring and finite values.

Manual checks: real-audio response, settings close/reopen, repeated setting changes,
input failures, minimize/restore, display selection and F11 transitions on each monitor.
