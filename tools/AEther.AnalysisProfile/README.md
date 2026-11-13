# Analyzer steady-state CPU probe

Run `dotnet run --project tools/AEther.AnalysisProfile -c Release -- artifacts/analysis-profile/smoother-20261113`.

This exercises the production Analyzer with precomputed 48 kHz stereo tones,
transients and low-level broadband noise, at 1000 analysis ticks/second. Each case
processes 4000 warmup ticks and retains 6000 ticks, repeated three times with rotated
case order. Input is offline and unpaced; there is no capture device, renderer or
UI. Default options are used except backend, frequency resolution and smoothing
selection. Results include both SWIFT (97 and 387 bins) and finite DFT (97 bins).

Timings come from the production PipelineProfile scopes, with bounded histories
enabled for the five measured stages. They measure synchronous elapsed time, so
OS scheduling, JIT and CPU frequency changes can affect them. Warmup is discarded
by exact sample count. Packet drops and missing timings fail the run.

Smoother, DFT and splitter metrics are per channel. Multiply their means by two
before comparing with the stereo processing/total means. Prefer summed smoother
durations divided by summed analyzer durations to ratios of percentile values.
`analysis.processing_ms` ends before subscriber callbacks; `analysis.total_ms`
also includes callbacks, progress publication and buffer returns. This probe's
subscriber only counts completed ticks. Queue waiting and input packet copying
are outside those analysis scopes. Construction and audio generation are excluded.

Both smoother filters execute whenever smoothing is enabled. The mode selector
only chooses the output passed to the splitter. This probe compares the existing
Off/A/B pipeline paths; it does not isolate hypothetical A-only/B-only filters.

On 2026-11-13, .NET 10.0.13, local Intel x64 host, averaged across three runs:

| Backend / bins | Mode | Analyzer total, us/stereo tick | Smoother A+B, us/stereo tick | Smoother share |
|----------------|------|-------------------------------:|-----------------------------:|---------------:|
| SWIFT / 97     | Off  |                          23.77 |                            0 |             0% |
| SWIFT / 97     | A    |                          38.62 |                        15.80 |          40.9% |
| SWIFT / 97     | B    |                          40.00 |                        16.35 |          40.9% |
| SWIFT / 387    | Off  |                          71.54 |                            0 |             0% |
| SWIFT / 387    | A    |                         137.82 |                        63.92 |          46.4% |
| SWIFT / 387    | B    |                         142.99 |                        65.72 |          46.0% |
| DFT / 97       | Off  |                          40.51 |                            0 |             0% |
| DFT / 97       | A    |                          59.65 |                        17.21 |          28.9% |
| DFT / 97       | B    |                          58.48 |                        16.84 |          28.8% |

Raw output: `artifacts/analysis-profile/smoother-20261113/results.csv`.
A/B timing differences should not be interpreted as different smoother work;
separate runs vary in scheduling, frequency and downstream numeric inputs.
