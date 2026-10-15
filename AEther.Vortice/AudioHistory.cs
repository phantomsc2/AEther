using System.Diagnostics;
using System.Threading.Channels;

namespace AEther.Vortice;

/// <summary>Owned, bounded handoff. Only the render thread touches history or graphics resources.</summary>
public sealed class AudioHistory
{
    readonly Channel<(double Time, float[] Samples)> pending;
    readonly DateTime origin = DateTime.Now;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly double rate;
    readonly float[] peak;
    long latestRow = -1;
    double lastAudio = double.NegativeInfinity;
    public int Bands { get; }
    public int Rows { get; }
    public double Seconds { get; }
    public float[][] History { get; }
    public float[][] Spectrum { get; }
    public double MusicalTime { get; private set; }
    public float Flash { get; private set; }
    public float Energy { get; private set; }
    public double Now => clock.Elapsed.TotalSeconds;
    public float Shift => (float)((Now * rate % Rows + .5) / Rows);
    public float TimeScale => (float)(Seconds * rate / Rows);
    public long DroppedRows => Interlocked.Read(ref dropped);
    long dropped;

    public AudioHistory(int bands, double analysisRate, double seconds = 1)
    {
        if (bands <= 0 || analysisRate <= 0 || !double.IsFinite(analysisRate)
            || seconds <= 0 || !double.IsFinite(seconds)) throw new ArgumentOutOfRangeException();
        Bands = bands; rate = analysisRate; Seconds = seconds;
        Rows = (int)Math.Clamp(Math.Ceiling(seconds * rate) + 2, 4, 16384);
        Spectrum = [new float[4 * bands], new float[4 * bands]];
        History = [new float[4 * bands * Rows], new float[4 * bands * Rows]];
        peak = new float[8 * bands];
        pending = Channel.CreateBounded<(double, float[])>(new BoundedChannelOptions(Math.Min(4096, Rows * 2))
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        }, _ => Interlocked.Increment(ref dropped));
    }

    public void Post(SampleEvent<double> input, int channelCount)
    {
        if (input.SampleCount != Bands * 4 || channelCount < 1)
            throw new ArgumentException("Analysis dimensions do not match the renderer.");
        var owned = new float[Bands * 8];
        for (var c = 0; c < 2; c++)
        {
            var src = input.GetChannel(Math.Min(c, channelCount - 1)).Span;
            for (var i = 0; i < src.Length; i++)
                owned[c * Bands * 4 + i] = double.IsFinite(src[i]) ? (float)Math.Clamp(src[i], 0, 1) : 0;
        }
        // The event time identifies the first sample of the analysis block.
        pending.Writer.TryWrite(((input.Time - origin).TotalSeconds + 1 / rate, owned));
    }

    public void Advance(double dt)
    {
        Flash *= (float)Math.Exp(-dt / .09);
        Array.Clear(peak);
        var received = false;
        var now = Now;
        var target = (long)Math.Floor(now * rate);
        // Clear elapsed slots even when loopback stops delivering silence packets.
        for (var row = Math.Max(latestRow + 1, target - Rows + 1); row <= target; row++)
            for (var c = 0; c < 2; c++)
                Array.Clear(History[c], (int)((row % Rows + Rows) % Rows) * Bands * 4, Bands * 4);
        latestRow = target;
        while (pending.Reader.TryRead(out var packet))
        {
            var row = (long)Math.Floor(packet.Time * rate);
            if (row < target - Rows + 1 || row > target + 1) continue;
            row = Math.Min(row, target);
            var offset = (int)((row % Rows + Rows) % Rows) * Bands * 4;
            for (var c = 0; c < 2; c++)
                packet.Samples.AsSpan(c * Bands * 4, Bands * 4).CopyTo(History[c].AsSpan(offset));
            for (var i = 0; i < peak.Length; i++) peak[i] = Math.Max(peak[i], packet.Samples[i]);
            var power = 0.0;
            for (var i = 0; i < packet.Samples.Length; i += 4)
            {
                power += packet.Samples[i] * packet.Samples[i] + packet.Samples[i + 1] * packet.Samples[i + 1]
                    + packet.Samples[i + 2] * packet.Samples[i + 2];
                Flash = Math.Max(Flash, packet.Samples[i + 1]);
            }
            Energy = (float)Math.Sqrt(power / (2 * Bands));
            MusicalTime += power / (2 * Bands * rate);
            lastAudio = packet.Time;
            received = true;
        }
        if (received)
            for (var c = 0; c < 2; c++) peak.AsSpan(c * Bands * 4, Bands * 4).CopyTo(Spectrum[c]);
        if (now - lastAudio > .15)
        {
            foreach (var spectrum in Spectrum) Array.Clear(spectrum);
            Energy = 0;
        }
    }
}
