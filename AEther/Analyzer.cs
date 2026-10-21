using System;
using System.Buffers;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AEther
{
    /// <summary>Sequential, bounded audio analysis. Event memory is valid only during the callback.</summary>
    public class Analyzer
    {
        public PipelineProfile Profile { get; } = new();
        public event EventHandler<SampleEvent<double>>? SamplesAnalyzed;
        /// <summary>Signed local-fit effects and standardized scores. Memory is valid only during the callback.</summary>
        public event EventHandler<SampleEvent<JointEvidence>>? JointEvidenceAvailable;
        public event EventHandler<double>? TransientDetected;
        public event EventHandler<AudioFeatures>? FeaturesAvailable;
        public readonly SampleFormat Format;
        public readonly Domain Domain;
        public readonly Task Completion;
        public readonly AnalyzerOptions Options;

        readonly int BatchSize;
        readonly double InputGain;
        readonly byte[] Batch;
        readonly ISpectrumProcessor[] Spectrum;
        readonly Splitter[] Splitters;
        readonly GlobalSplitter Global;
        readonly TransientDetector[] Transients;
        readonly AudioFeatureExtractor Features;
        readonly record struct InputPacket(SampleEvent<byte> Event, long Arrival, AudioPacketTiming? Timing);
        readonly Channel<InputPacket> Input;
        readonly TimeProvider Clock;
        readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long Dropped;
        long droppedFrames, generation;
        volatile AudioTimestampQuality lastQuality;
        long capturedBytes;
        long previousArrival;
        readonly DateTime audioOrigin = DateTime.Now;

        /// <summary>Packets discarded on overflow, age expiry, or closed input.</summary>
        public long DroppedInputCount => Interlocked.Read(ref Dropped);
        public double DroppedAudioMilliseconds => Interlocked.Read(ref droppedFrames) * 1000.0 / Format.SampleRate;
        public long DiscontinuityCount => Interlocked.Read(ref generation);
        public AudioTimestampQuality LastTimestampQuality => lastQuality;

        public Analyzer(SampleFormat format, AnalyzerOptions options, TimeProvider? timeProvider = null)
        {
            options.Validate(format);
            InputGain = Math.Pow(10, options.InputGainDb / 20);
            Domain = options.Domain;
            Format = format;
            Options = options;
            Clock = timeProvider ?? TimeProvider.System;
            BatchSize = Format.SampleRate / options.TimeResolution;
            Profile.Describe("Sample rate", Format.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Input gain dB", options.InputGainDb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Channels", Format.ChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Batch samples", BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Batch budget ms", (BatchSize * 1000.0 / Format.SampleRate).ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Frequency bins", Domain.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("SIMD enabled", options.SIMDEnabled.ToString());
            Profile.Describe("Window function", options.WindowFunction.ToString());
            Profile.Describe("Spectrum backend", options.UseSwift ? "SWIFT" : "DFT");
            Batch = new byte[checked(BatchSize * Format.Size)];
            Profile.Describe("Decomposition", "STBR: rolling Gaussian / directional slopes / residual RMS");
            Profile.Describe("S time slope", options.JointTimeSlopeEnabled.ToString());
            Profile.Describe("T frequency slope", options.JointFrequencySlopeEnabled.ToString());
            Splitters = Enumerable.Range(0, Format.ChannelCount)
                .Select(_ => new Splitter(Domain, options, Format.SampleRate / (double)BatchSize)).ToArray();
            Global = new(Domain, options, Format.SampleRate / (double)BatchSize);
            Spectrum = Enumerable.Range(0, Format.ChannelCount)
                .Select(_ => SpectrumProcessor.Create(Domain, Format.SampleRate, options)).ToArray();
            if (Spectrum[0] is SwiftProcessor swift)
            {
                var bands = swift.Bands.ToArray();
                Profile.Describe("SWIFT width scale", options.SwiftWidthScale.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
                Profile.Describe("SWIFT imbalance", options.SwiftImbalance.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
                Profile.Describe("SWIFT total decay ms", $"{bands.Min(b => b.SlowSeconds + b.FastSeconds) * 1000:F6}–{bands.Max(b => b.SlowSeconds + b.FastSeconds) * 1000:F6}");
                Profile.Describe("SWIFT slow decay ms", $"{bands.Min(b => b.SlowSeconds) * 1000:F6}–{bands.Max(b => b.SlowSeconds) * 1000:F6}");
                Profile.Describe("SWIFT fast decay ms", $"{bands.Min(b => b.FastSeconds) * 1000:F6}–{bands.Max(b => b.FastSeconds) * 1000:F6}");
            }
            Transients = Enumerable.Range(0, Format.ChannelCount)
                .Select(_ => new TransientDetector(Format.SampleRate)).ToArray();
            Features = new AudioFeatureExtractor(format);
            Input = Channel.CreateBounded<InputPacket>(new BoundedChannelOptions(options.BufferCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false,
            }, packet => DropPacket(packet));
            Completion = Task.Run(RunAsync);
        }

        public void Start() => Started.TrySetResult();

        /// <summary>Never blocks capture. Copies memory owned by the capture device.</summary>
        public void PostSamples(ReadOnlyMemory<byte> data) => PostSampleSpan(data.Span);

        /// <summary>Copies a native capture packet into the bounded analysis queue.</summary>
        public void PostSampleSpan(ReadOnlySpan<byte> data) => Post(data, null);

        public void PostSampleSpan(ReadOnlySpan<byte> data, AudioPacketTiming timing) => Post(data, timing);

        void Post(ReadOnlySpan<byte> data, AudioPacketTiming? timing)
        {
            if (data.IsEmpty)
                return;
            if (timing.HasValue && data.Length % Format.Size != 0)
                throw new ArgumentException("Timed capture packets must contain complete interleaved frames.", nameof(data));
            var arrival = AudioClock.Now(Clock);
            var previous = Interlocked.Exchange(ref previousArrival, arrival);
            if (previous != 0)
                Profile.Record("capture.arrival_interval_ms", Stopwatch.GetElapsedTime(previous, arrival).TotalMilliseconds);
            Profile.Record("capture.packet_ms", data.Length * 1000.0 / Format.Size / Format.SampleRate);
            var captureStart = DateTime.Now - TimeSpan.FromSeconds(data.Length / (double)Format.Size / Format.SampleRate);
            var byteIndex = Interlocked.Add(ref capturedBytes, data.Length) - data.Length;
            var evt = RentEvent<byte>(data.Length, 1, captureStart) with { ByteIndex = byteIndex };
            if (timing?.Flags.HasFlag(AudioPacketFlags.Silent) == true)
                evt.Samples.AsSpan(0, data.Length).Clear();
            else data.CopyTo(evt.Samples.AsSpan());
            var packet = new InputPacket(evt, arrival, timing);
            if (!Input.Writer.TryWrite(packet)) DropPacket(packet);
        }

        void DropPacket(InputPacket packet)
        {
            Interlocked.Increment(ref Dropped);
            RecordLoss(packet.Event.SampleCount / Format.Size);
            ReturnEvent(packet.Event);
        }

        void RecordLoss(long frames)
        {
            if (frames <= 0) return;
            Interlocked.Add(ref droppedFrames, frames);
            Profile.Record("capture.dropped_audio_ms", frames * 1000.0 / Format.SampleRate);
        }

        void ResetAnalysis()
        {
            foreach (var processor in Spectrum) processor.Reset();
            foreach (var splitter in Splitters) splitter.Reset();
            foreach (var transient in Transients) transient.Reset();
            Global.Reset();
            Features.Reset();
            Interlocked.Increment(ref generation);
            Profile.Describe("Discontinuities", Interlocked.Read(ref generation).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Closes input and drains accepted samples. Await Completion before releasing subscribers.</summary>
        public void Stop()
        {
            Input.Writer.TryComplete();
            Started.TrySetResult();
        }

        async Task RunAsync()
        {
            var filled = 0;
            var batchTime = DateTime.Now;
            long expectedByte = 0, batchIndex = 0, captureTimestamp = 0;
            long extraBytes = 0;
            long? expectedDevice = null, expectedTime = null, batchStart = null;
            long? lastPacketArrival = null;
            AudioTimestampQuality quality = AudioTimestampQuality.Unspecified;
            try
            {
                await Started.Task;
                await foreach (var packet in Input.Reader.ReadAllAsync())
                {
                    var input = packet.Event;
                    Profile.Record("capture.queue_ms", AudioClock.Seconds(AudioClock.Now(Clock) - packet.Arrival) * 1000);
                    try
                    {
                        var offset = 0;
                        long byteGap = input.ByteIndex - expectedByte;
                        long previousExtraBytes = extraBytes;
                        bool gap = byteGap != 0 || packet.Timing?.Flags.HasFlag(AudioPacketFlags.Discontinuity) == true;
                        var timing = packet.Timing;
                        bool valid = timing.HasValue && !timing.Value.Flags.HasFlag(AudioPacketFlags.TimestampError);
                        long? device = valid ? timing?.FramePosition : null;
                        long? sourceStart = valid ? timing?.Timestamp : null;
                        var packetQuality = sourceStart.HasValue ? timing!.Value.Quality : AudioTimestampQuality.Estimated;
                        if (sourceStart.HasValue && packetQuality == AudioTimestampQuality.Unspecified)
                            packetQuality = AudioTimestampQuality.Estimated;
                        if (device.HasValue && expectedDevice.HasValue && device != expectedDevice)
                        {
                            var missing = device.Value - expectedDevice.Value;
                            gap = true;
                            // Byte indices already account for packets dropped by our own queue.
                            if (missing > 0)
                            {
                                long missingBytes = Math.Max(0, missing * Format.Size - Math.Max(0, byteGap));
                                extraBytes += missingBytes;
                                RecordLoss(missingBytes / Format.Size);
                            }
                        }
                        if (timing.HasValue && !sourceStart.HasValue)
                        {
                            // Untimestamped capture can stop emitting packets during a pause. Extrapolating
                            // forever would leave every resumed packet permanently beyond the age budget.
                            bool paused = byteGap == 0 && lastPacketArrival.HasValue
                                && AudioClock.Seconds(packet.Arrival - lastPacketArrival.Value)
                                    > Math.Max(.05, 2.0 * input.SampleCount / Format.Size / Format.SampleRate);
                            sourceStart = expectedTime.HasValue && !paused && timing?.Flags.HasFlag(AudioPacketFlags.Discontinuity) != true
                                ? expectedTime.Value + AudioClock.Frames((Math.Max(0, byteGap) + extraBytes - previousExtraBytes) / Format.Size, Format.SampleRate)
                                : packet.Arrival - AudioClock.Frames(input.SampleCount / Format.Size, Format.SampleRate);
                        }
                        // Account for source-clock gaps not already represented by missing bytes/device frames.
                        // Device stamps can establish short gaps; arrival estimates need a generous tolerance.
                        var accountedTime = expectedTime + AudioClock.Frames(
                            (Math.Max(0, byteGap) + extraBytes - previousExtraBytes) / Format.Size, Format.SampleRate);
                        if (sourceStart.HasValue && accountedTime.HasValue
                            && AudioClock.Seconds(sourceStart.Value - accountedTime.Value)
                                > (packetQuality == AudioTimestampQuality.Device ? Math.Max(.001, 2.0 / Format.SampleRate) : .05))
                        {
                            extraBytes += (long)Math.Round(AudioClock.Seconds(sourceStart.Value - accountedTime.Value)
                                * Format.SampleRate) * Format.Size;
                            gap = true;
                        }
                        // A malformed timestamp must not rewind the presented timeline or discard valid audio.
                        if (!gap && sourceStart.HasValue && expectedTime.HasValue && sourceStart < expectedTime)
                        {
                            sourceStart = expectedTime;
                            packetQuality = AudioTimestampQuality.Estimated;
                        }
                        if (gap)
                        {
                            RecordLoss(filled / Format.Size);
                            filled = 0; ResetAnalysis();
                        }
                        expectedByte = input.ByteIndex + input.SampleCount;
                        expectedDevice = device + input.SampleCount / Format.Size;
                        expectedTime = sourceStart + AudioClock.Frames(input.SampleCount / Format.Size, Format.SampleRate);
                        lastPacketArrival = packet.Arrival;
                        while (offset < input.SampleCount)
                        {
                            if (sourceStart.HasValue)
                            {
                                var oldest = AudioClock.Now(Clock) - (long)(Options.MaxInputAgeMilliseconds / 1000 * Stopwatch.Frequency);
                                // Expire completed observations, not the early samples needed by a fresh
                                // observation. This also permits tick durations longer than the age budget.
                                var lateFrames = Math.Max(0, (long)Math.Floor(AudioClock.Seconds(oldest - sourceStart.Value) * Format.SampleRate));
                                var packetFrame = (input.ByteIndex + extraBytes) / Format.Size;
                                var firstFreshFrame = (packetFrame + lateFrames) / BatchSize * BatchSize;
                                var skipTo = (int)Math.Min(input.SampleCount, Math.Max(0, firstFreshFrame - packetFrame) * Format.Size);
                                if (skipTo > offset)
                                {
                                    RecordLoss(filled / Format.Size);
                                    RecordLoss((skipTo - offset) / Format.Size);
                                    Interlocked.Increment(ref Dropped);
                                    offset = skipTo;
                                    filled = 0;
                                    ResetAnalysis();
                                    if (offset == input.SampleCount) break;
                                }
                            }
                            if (filled == 0)
                            {
                                var position = input.ByteIndex + extraBytes + offset;
                                var remainder = position % Batch.Length;
                                if (remainder != 0)
                                {
                                    var skipped = (int)Math.Min(Batch.Length - remainder, input.SampleCount - offset);
                                    RecordLoss(skipped / Format.Size);
                                    offset += skipped;
                                    continue;
                                }
                                batchIndex = position / Batch.Length;
                                batchTime = audioOrigin + TimeSpan.FromSeconds(batchIndex * (double)BatchSize / Format.SampleRate);
                                captureTimestamp = packet.Arrival;
                                batchStart = sourceStart + AudioClock.Frames(offset / Format.Size, Format.SampleRate);
                                quality = sourceStart.HasValue ? packetQuality : AudioTimestampQuality.Unspecified;
                            }
                            if (packetQuality < quality) quality = packetQuality;
                            var count = Math.Min(Batch.Length - filled, input.SampleCount - offset);
                            input.Samples.AsSpan(offset, count).CopyTo(Batch.AsSpan(filled));
                            filled += count;
                            offset += count;
                            if (filled == Batch.Length)
                            {
                                var batchEnd = sourceStart + AudioClock.Frames(offset / Format.Size, Format.SampleRate);
                                AnalyzeBatch(batchTime, batchIndex, captureTimestamp, batchStart, batchEnd, quality);
                                filled = 0;
                            }
                        }
                    }
                    finally
                    {
                        ReturnEvent(input);
                    }
                }
                // Discard a trailing incomplete batch rather than inventing padding audio.
            }
            finally
            {
                Input.Writer.TryComplete();
                while (Input.Reader.TryRead(out var pending))
                    ReturnEvent(pending.Event);
            }
        }

        void AnalyzeBatch(DateTime time, long row, long captureTimestamp, long? start, long? end,
            AudioTimestampQuality quality)
        {
            using var totalTiming = Profile.Measure("analysis.total_ms");
            var samples = RentEvent<double>(BatchSize, Format.ChannelCount, time);
            var spectrum = RentEvent<double>(Domain.Length, Format.ChannelCount, time);
            var output = RentEvent<double>(AEther.Splitter.OutputStride * Domain.Length, Format.ChannelCount, time);
            var jointEvidence = JointEvidenceAvailable is not null
                ? RentEvent<JointEvidence>(Domain.Length, Format.ChannelCount, time) : null;
            try
            {
                using (Profile.Measure("analysis.decode_ms"))
                for (var c = 0; c < Format.ChannelCount; ++c)
                {
                    var channel = samples.GetChannel(c).Span;
                    for (var i = 0; i < BatchSize; ++i)
                    {
                        var offset = (i * Format.ChannelCount + c) * Format.Type.Size;
                        var sample = Format.Type.ReadFrom(Batch.AsSpan(offset));
                        channel[i] = double.IsFinite(sample) ? sample * InputGain : 0;
                    }
                }
                using (Profile.Measure("analysis.waveform_features_ms"))
                {
                var attack = 0.0;
                for (var c = 0; c < Format.ChannelCount; ++c)
                    attack = Math.Max(attack, Transients[c].Process(samples.GetChannel(c).Span));
                // Deliver attacks before frequency analysis; low-frequency DFT windows must not delay an impulse.
                if (attack > .02)
                    TransientDetected?.Invoke(this, attack);
                FeaturesAvailable?.Invoke(this, Features.Process(samples));
                }
                for (var c = 0; c < Format.ChannelCount; ++c)
                {
                    using (Profile.Measure("analysis.dft_channel_ms"))
                    {
                    Spectrum[c].Process(samples.GetChannel(c));
                    Spectrum[c].OutputLinear(spectrum.GetChannel(c));
                    }
                    using (Profile.Measure("analysis.split_channel_ms"))
                    {
                        Splitters[c].ProcessLinear(spectrum.GetChannel(c), output.GetChannel(c));
                        if (jointEvidence is not null) Splitters[c].Estimates.CopyTo(jointEvidence.GetChannel(c));
                    }
                }
                GlobalEvidence global;
                using (Profile.Measure("analysis.global_split_ms"))
                    global = Global.Process(spectrum.Samples.AsMemory(0, Domain.Length * Format.ChannelCount), Format.ChannelCount);
                // Publish immediately; render pacing belongs to the renderer.
                output = output with { SampleIndex = row * BatchSize, AnalysisRow = row,
                    CaptureTimestamp = captureTimestamp, AnalysisTimestamp = AudioClock.Now(Clock), GlobalEvidence = global,
                    SampleStartTimestamp = start, SampleEndTimestamp = end, TimestampQuality = quality,
                    DiscontinuityGeneration = Interlocked.Read(ref generation) };
                if (jointEvidence is not null)
                {
                    jointEvidence = jointEvidence with { SampleIndex = output.SampleIndex, AnalysisRow = output.AnalysisRow,
                        CaptureTimestamp = output.CaptureTimestamp, AnalysisTimestamp = output.AnalysisTimestamp, GlobalEvidence = global,
                        SampleStartTimestamp = start, SampleEndTimestamp = end, TimestampQuality = quality,
                        DiscontinuityGeneration = output.DiscontinuityGeneration };
                    JointEvidenceAvailable?.Invoke(this, jointEvidence);
                }
                Profile.Record("analysis.capture_to_completion_ms", AudioClock.Seconds(AudioClock.Now(Clock) - captureTimestamp) * 1000);
                if (end.HasValue) Profile.Record("analysis.source_age_ms", AudioClock.Seconds(AudioClock.Now(Clock) - end.Value) * 1000);
                if (lastQuality != quality) Profile.Describe("Timestamp quality", quality.ToString());
                lastQuality = quality;
                using (Profile.Measure("analysis.subscribers_ms"))
                    SamplesAnalyzed?.Invoke(this, output);
            }
            finally
            {
                ReturnEvent(samples);
                ReturnEvent(spectrum);
                ReturnEvent(output);
                if (jointEvidence is not null) ReturnEvent(jointEvidence);
            }
        }

        static SampleEvent<T> RentEvent<T>(int sampleCount, int channelCount, DateTime time)
            => new(ArrayPool<T>.Shared.Rent(checked(sampleCount * channelCount)), sampleCount, time);
        static void ReturnEvent<T>(SampleEvent<T> evt) => ArrayPool<T>.Shared.Return(evt.Samples);
    }
}
