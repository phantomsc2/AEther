# AEther

## Description

Audio analysis and audio-reactive DirectX visualization on Windows using NAudio.

## Project Layout

- **AEther**: Core library to read and analyze sample data
- **AEther.Benchmarks**: using [BenchmarkDotNet](https://benchmarkdotnet.org/)
- **AEther.NAudio**: audio input with [NAudio](https://github.com/naudio/NAudio)
- **AEther.Tests**: using [NUnit](https://nunit.org/)
- **AEther.WindowsForms**: outputs DirectX visualization with [Windows Forms](https://github.com/dotnet/winforms) and [SharpDX](http://sharpdx.org/)

- **tools/AEther.CaptureProbe**: measures NAudio loopback packet timing
- **tools/AEther.Smoke**: exercises analysis and rendering with synthetic audio
