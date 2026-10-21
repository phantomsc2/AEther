"""Render the study CSVs and write its report. Requires numpy and matplotlib."""
from pathlib import Path
import csv
import xml.etree.ElementTree as ET
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'doc' / 'swift-study'
NAMES = ['Hann SIMD', 'SWIFT SIMD', 'SWIFT 5-3']
COLORS = {'Hann SIMD': '#9656ad', 'SWIFT SIMD': '#209779', 'SWIFT 5-3': '#3178c6', 'SWIFT scalar': '#df8b24'}
plt.rcParams.update({'figure.dpi': 140, 'font.size': 10, 'axes.grid': True, 'grid.alpha': .2,
                     'axes.spines.top': False, 'axes.spines.right': False})

def read(name):
    with (OUT / name).open(encoding='utf-8') as f:
        return list(csv.DictReader(f))

def nums(rows, key): return np.array([float(r[key]) for r in rows])
def save(fig, name):
    fig.savefig(OUT / name, bbox_inches='tight')
    plt.close(fig)

response = read('response.csv')
transfer = read('transfer.csv')
traces = read('traces.csv')
scenarios = read('scenarios.csv')
perf = read('performance.csv')
tuning = read('tuning.csv')

fig, axes = plt.subplots(1, 2, figsize=(12, 4), constrained_layout=True)
for name in NAMES:
    rows = [r for r in response if r['backend'] == name]
    for ax, metric, title in zip(axes, ['attack50_ms', 'attack90_ms'], ['Time to 50% coherent amplitude', 'Time to 90% coherent amplitude']):
        ax.loglog(nums(rows, 'frequency'), nums(rows, metric), 'o-', color=COLORS[name], label=name)
        ax.set(xlabel='Frequency (Hz)', ylabel='Time (ms)', title=title)
        ax.legend(fontsize=8)
save(fig, 'response.png')

fig, axes = plt.subplots(1, 3, figsize=(14, 4), constrained_layout=True)
for ax, f in zip(axes, [55, 440, 7000]):
    for name in NAMES:
        rows = [r for r in transfer if r['backend'] == name and float(r['frequency']) == f]
        ax.plot(nums(rows, 'offset_semitones'), nums(rows, 'relative_db'), color=COLORS[name], label=name)
    for x in [-1, 1]: ax.axvline(x, color='gray', lw=.8, ls=':')
    ax.set(title=f'{f} Hz', xlabel='Semitones from center', ylabel='Amplitude relative to center (dB)', ylim=(-80, 2))
    ax.legend(fontsize=8)
save(fig, 'leakage.png')

fig, axes = plt.subplots(3, 3, figsize=(14, 9), constrained_layout=True, sharex=True)
for col, scenario in enumerate(['Tone attack', 'Tone plus noise', 'Note replacement']):
    for name in NAMES:
        rows = [r for r in traces if r['scenario'] == scenario and r['backend'] == name]
        for row, key in enumerate(['center_amplitude', 'local_t', 'local_r']):
            axes[row, col].plot(nums(rows, 'time_s'), nums(rows, key), color=COLORS[name], label=name)
    axes[0, col].set_title(scenario)
    for row, label in enumerate(['Center amplitude', 'Signed local T', 'Local residual RMS']):
        axes[row, col].set_ylabel(label)
        axes[row, col].set_xlim(.2, 1.9)
        axes[row, col].legend(fontsize=7)
    axes[2, col].set_xlabel('Time (s)')
save(fig, 'stbr.png')

fig, axes = plt.subplots(2, 2, figsize=(12, 7), constrained_layout=True, sharex=True)
for name in NAMES:
    rows = [r for r in traces if r['scenario'] == 'Music WAV' and r['backend'] == name]
    for ax, key, title in zip(axes.flat, ['local_s', 'local_t', 'local_b', 'local_r'], ['Signed sustained effect', 'Signed transient', 'Background', 'Residual RMS']):
        ax.plot(nums(rows, 'time_s'), nums(rows, key), color=COLORS[name], label=name, lw=.8)
        ax.set(title=title, xlabel='Time (s)', ylabel='Linear amplitude')
        ax.legend(fontsize=8)
save(fig, 'music.png')

fig, axes = plt.subplots(1, 2, figsize=(12, 4), constrained_layout=True)
for ax, work in zip(axes, ['spectrum', 'spectrum+local']):
    for name in ['Hann SIMD', 'SWIFT scalar', 'SWIFT SIMD']:
        rows = [r for r in perf if r['sample_rate'] == '48000' and r['bins_per_octave'] == '12' and r['backend'] == name and r['workload'] == work]
        ax.plot(nums(rows, 'block'), nums(rows, 'median_us'), 'o-', color=COLORS[name], label=name)
    ax.set(title=work, xlabel='Samples per stereo call', ylabel='Median CPU time (microseconds)', xscale='log')
    ax.legend(fontsize=8)
save(fig, 'performance.png')

def one(rows, **query):
    return next(r for r in rows if all(r[k] == str(v) for k, v in query.items()))

def table(headers, rows):
    return '\n'.join(['| ' + ' | '.join(headers) + ' |', '| ' + ' | '.join(['---'] * len(headers)) + ' |']
                     + ['| ' + ' | '.join(str(x) for x in r) + ' |' for r in rows])

f55 = [one(response, frequency=55, backend=n) for n in NAMES]
f55_table = table(['Backend', 'Slow / fast cycles', '10% attack', '50% attack', '90% attack / release to 10%', 'Noise bandwidth'],
    [[r['backend'], 'finite Hann' if r['backend'] == 'Hann SIMD' else f"{float(r['slow_cycles']):.2f} / {float(r['fast_cycles']):.2f}",
      f"{float(r['attack10_ms']):.1f} ms", f"{float(r['attack50_ms']):.1f} ms", f"{float(r['attack90_ms']):.1f} ms", f"{float(r['enbw_hz']):.2f} Hz"] for r in f55])

cpu_rows = []
for block in [48, 187, 1024]:
    for work in ['spectrum', 'spectrum+local']:
        rows = [one(perf, sample_rate=48000, bins_per_octave=12, block=block, workload=work, backend=n)
                for n in ['Hann SIMD', 'SWIFT scalar', 'SWIFT SIMD']]
        cpu_rows.append([block, work] + [f"{float(r['median_us']):.2f} / {float(r['p95_us']):.2f}" for r in rows]
                       + [f"{float(rows[0]['median_us'])/float(rows[2]['median_us']):.2f}×"])
cpu_table = table(['Block', 'Workload', 'Hann SIMD', 'SWIFT scalar', 'SWIFT SIMD', 'Median speedup vs Hann'], cpu_rows)

tone_rows = [one(scenarios, scenario='Tone attack', backend=n) for n in NAMES]
tone_table = table(['Backend', 'Local T peak after onset', 'Local R peak after onset', 'Peak R'],
    [[r['backend'], f"{1000*(float(r['time_peak_t'])-.3):.0f} ms", f"{1000*(float(r['time_peak_r'])-.3):.0f} ms", f"{float(r['peak_r']):.5f}"] for r in tone_rows])

calibration_error = max(abs(float(r['hann_neighbor_db'])-float(r['swift_neighbor_db'])) for r in tuning)
alloc = max(float(r['allocated_bytes_per_call']) for r in perf)
music_row = one(scenarios, scenario='Music WAV', backend='Hann SIMD')
first_hann = one(perf, sample_rate=48000, bins_per_octave=12, block=48, workload='spectrum', backend='Hann SIMD')
first_swift = one(perf, sample_rate=48000, bins_per_octave=12, block=48, workload='spectrum', backend='SWIFT SIMD')
validation = []
for filename, label in [('swift-nongpu.trx', 'Non-GPU regression suite'),
                        ('swift-gpu.trx', 'Real SWIFT/STBR GPU silence'),
                        ('swift-existing-gpu.trx', 'Existing all-scene GPU silence')]:
    result_path = ROOT / 'AEther.Tests' / 'TestResults' / filename
    if result_path.exists():
        counters = ET.parse(result_path).find('.//{*}Counters')
        validation.append(f"- {label}: {counters.get('passed')} passed, {counters.get('failed')} failed.")
    else:
        validation.append(f"- {label}: no local TRX found; see the README to run this suite.")
validation_text = '\n'.join(validation)

text = f'''# SWIFT versus windowed SIMD DFT

## Using the visual comparison

Open **Settings → Analyzer → DFT → Spectrum analysis**. **SWIFT** is the default on state;
**DFT** is the off state and uses the existing selected window. Switching follows the existing
session restart path, resetting analysis and its histories. SWIFT defaults to fast/slow ratio **0.6**
and duration multiplier **1**. Only the selected backend runs. Window controls are disabled in SWIFT;
SWIFT controls are disabled in DFT. The SIMD setting applies to either backend.

Duration 1 matches Hann's worst adjacent-note rejection for each bin, including Hann's rounded
length and center frequency. It always uses Hann as the reference, independently of the DFT
window currently selected. A lower multiplier responds sooner with more leakage. A higher one
narrows the response. Changing the ratio recalibrates the matched duration before applying the multiplier.

## Response and spectral quality

At 55 Hz and 48 kHz, the matched cascade uses about 3.30 / 1.98 carrier cycles rather than the
notebook's fixed 5 / 3. Both preserve unit coherent center gain. Here “attack” measures a complex
tone switched on at the requested center; it does not measure CPU latency or noisy note decisions.
Hann uses the actual existing periodic window and rounded center. Its tiny detuning means amplitude
attack and release need not be exact complements; their sampled crossing times coincide here.

{f55_table}

![Coherent response time](response.png)

The matched cascade reaches half amplitude sooner and also reaches 90% sooner in this comparison.
Its infinite tail lasts longer than Hann's finite support. Matching one neighboring frequency does
not match the whole response: Hann has much stronger rejection at many more distant offsets and
exact nulls, while SWIFT has smooth leakage tails. The fixed 5/3 pair is more selective and slower.

![Leakage curves](leakage.png)

The tuning sweep covers 44.1/48 kHz and 12/24 bins per octave over the default 30–8000 Hz domain.
The largest analytic matching error was **{calibration_error:.3g} dB**. The reference tests separately
compare the analytic Hann calculation with direct finite sums, including near-Nyquist/coarse grids.
Noise bandwidth is two-sided ENBW at coherent center gain. Equal tone gain does not imply equal
mean noise magnitude. `response.csv` also includes center gain, half-note detuning gain, and the
analytic real-tone minimum/maximum caused by the negative-frequency image.

## Downstream STBR behavior

Synthetic signals are analyzed at 48 kHz with the default 97-bin domain, the actual 1 kHz update
rate, a 0.2 s time window, and a two-octave local frequency window. Input gain is unity for
the comparison. Local additive production fits consume
each backend's linear magnitudes. Traces and summaries use the selected bin in the first channel.
Trace CSVs keep every fifth observation; summary peaks use every row.
The centered tone is the first domain bin above 55 Hz (about 56.63 Hz), starting at 0.3 s.

{tone_table}

![Tone attacks, noise and replacement](stbr.png)

The matched backend makes T respond earlier, but its clean-tone onset also produces a different,
larger peak residual. This is evidence of changed time/frequency interaction, not proof of more
physical noise. Residual RMS remains the existing patch statistic. No STBR weights or component
gains were changed to make either backend appear better. Unequal-neighbor fixtures include a
20 dB amplitude difference; matching leakage does not guarantee recovery of the quieter note.

The music example uses the first {float(music_row['seconds']):g} seconds of `TestFiles/test_input.wav`
({music_row['sample_rate']} Hz, {music_row['channels']} channels), without input gain. It has no labeled
STBR ground truth; these traces demonstrate behavioral differences rather than objective component accuracy.

![Music STBR comparison](music.png)

## CPU performance

Numbers below are **median / p95 microseconds per stereo call**, measured in Release on the machine
in [benchmark-machine.txt](benchmark-machine.txt). The benchmark thread is pinned to logical CPU 0;
tiered compilation is disabled. Each case warms for at least 200 ms and enough samples to populate
the Hann and STBR histories. Five measured rounds target approximately 200 ms each. Every timed
call includes linear magnitude readout. The combined workload additionally includes both local fits
at the actual block update rate. Audio synthesis, construction, and I/O are excluded.

{cpu_table}

![CPU cost](performance.png)

The full CSV additionally covers 44.1 kHz and 24 bins/octave. The largest measured processing
allocation was **{alloc:g} bytes per call**. Construction allocations include transient tuning objects,
so `construction_bytes_stereo` is not retained memory. In the default stereo 48 kHz/97-bin setup,
construction allocated {int(first_hann['construction_bytes_stereo']):,} bytes for Hann and
{int(first_swift['construction_bytes_stereo']):,} bytes for SIMD SWIFT. SWIFT retains no audio sample
history; its array payload is approximately {2*(15*100*8+97*40):,} bytes for this domain, excluding
array/object headers. STBR retains its existing separate histories in either case.

These are CPU timings, not audio-to-display measurements. Maximum timings include OS scheduling
outliers and are preserved in the raw CSV. No rendering performance claim is made; the low-resolution
GPU tests are correctness tests. Any future rendering timing must use at least 1280×720.

## Silence, checks, and limits

SWIFT decays naturally after zero input and clears each bin after 48 slow time constants plus two
samples of exact zeros. This truncates a negligible tail without gating quiet nonzero audio. A new
nonzero input restarts the silence clock, including within a bulk call. Discontinuities reset state.

Checks cover independent weighted sums; scalar/SIMD/chunk parity; equal and nearly equal poles;
direct Hann calibration; real-tone gain and image; quiet input; allocation; backend routing; analyzer
channel wiring; and active-input-to-silence GPU rendering. The existing finite-window implementation
and its direct-reference regressions remain available. Recorded validation:

{validation_text}

The implementation's WinUI application build completed in Release/x64 with zero warnings or errors.
Re-running the study alone does not rebuild WinUI or rerun regressions.

This is a bank of note-centered amplitude measurements. It adds no discrete note events or
polyphonic inference, and it does not fuse the nonlinear magnitude operation with the STBR fit.

## Reproduction and raw data

See [the study README](../../tools/AEther.SwiftStudy/README.md) for commands.
Raw results: [response](response.csv), [transfer curves](transfer.csv), [tuning](tuning.csv),
[scenario summaries](scenarios.csv), [STBR traces](traces.csv), and [CPU performance](performance.csv).
'''
(OUT / 'report.md').write_text(text, encoding='utf-8')
print(OUT / 'report.md')
