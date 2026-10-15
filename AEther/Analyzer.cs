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
        public event EventHandler<double>? TransientDetected;
        public event EventHandler<AudioFeatures>? FeaturesAvailable;
        public readonly SampleFormat Format;
        public readonly Domain Domain;
        public readonly Task Completion;
        public readonly AnalyzerOptions Options;

        readonly int BatchSize;
        readonly byte[] Batch;
        readonly DFTProcessor[] DFT;
        readonly Splitter[] Splitter;
        readonly TransientDetector[] Transients;
        readonly AudioFeatureExtractor Features;
        readonly Channel<(SampleEvent<byte> Event, long Arrival)> Input;
        readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long Dropped;
        long capturedBytes;
        long previousArrival;
        readonly DateTime audioOrigin = DateTime.Now;

        /// <summary>Capture callbacks rejected because the input queue was full or closed.</summary>
        public long DroppedInputCount => Interlocked.Read(ref Dropped);

        public Analyzer(SampleFormat format, AnalyzerOptions options)
        {
            options.Validate(format);
            Domain = options.Domain;
            Format = format;
            Options = options;
            BatchSize = Format.SampleRate / options.TimeResolution;
            Profile.Describe("Sample rate", Format.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Channels", Format.ChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Batch samples", BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Batch budget ms", (BatchSize * 1000.0 / Format.SampleRate).ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("Frequency bins", Domain.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Profile.Describe("SIMD enabled", options.SIMDEnabled.ToString());
            Profile.Describe("Window function", options.WindowFunction.ToString());
            Batch = new byte[checked(BatchSize * Format.Size)];
            DFT = Enumerable.Range(0, Format.ChannelCount)
                .Select(_ => new DFTProcessor(Domain, Format.SampleRate, options.SIMDEnabled,
                    options.MaxParallelization, options.WindowFunction)).ToArray();
            Splitter = Enumerable.Range(0, Format.ChannelCount)
                .Select(_ => new Splitter(Domain, options, Format.SampleRate / (double)BatchSize)).ToArray();
            Transients = Enumerable.Range(0, Format.ChannelCount)
                .Select(_ => new TransientDetector(Format.SampleRate)).ToArray();
            Features = new AudioFeatureExtractor(format);
            Input = Channel.CreateBounded<(SampleEvent<byte> Event, long Arrival)>(new BoundedChannelOptions(options.BufferCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            Completion = Task.Run(RunAsync);
        }

        public void Start() => Started.TrySetResult();

        /// <summary>Never blocks capture. Copies memory owned by the capture device.</summary>
        public void PostSamples(ReadOnlyMemory<byte> data) => PostSampleSpan(data.Span);

        /// <summary>Copies a native capture packet into the bounded analysis queue.</summary>
        public void PostSampleSpan(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;
            var arrival = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref previousArrival, arrival);
            if (previous != 0)
                Profile.Record("capture.arrival_interval_ms", Stopwatch.GetElapsedTime(previous, arrival).TotalMilliseconds);
            Profile.Record("capture.packet_ms", data.Length * 1000.0 / Format.Size / Format.SampleRate);
            var captureStart = DateTime.Now - TimeSpan.FromSeconds(data.Length / (double)Format.Size / Format.SampleRate);
            var byteIndex = Interlocked.Add(ref capturedBytes, data.Length) - data.Length;
            var evt = RentEvent<byte>(data.Length, 1, captureStart) with { ByteIndex = byteIndex };
            data.CopyTo(evt.Samples.AsSpan());
            if (!Input.Writer.TryWrite((evt, arrival)))
            {
                ReturnEvent(evt);
                Interlocked.Increment(ref Dropped);
            }
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
            try
            {
                await Started.Task;
                await foreach (var packet in Input.Reader.ReadAllAsync())
                {
                    var input = packet.Event;
                    Profile.Elapsed("capture.queue_ms", packet.Arrival);
                    try
                    {
                        var offset = 0;
                        if (input.ByteIndex != expectedByte) filled = 0;
                        expectedByte = input.ByteIndex + input.SampleCount;
                        while (offset < input.SampleCount)
                        {
                            if (filled == 0)
                            {
                                var position = input.ByteIndex + offset;
                                var remainder = position % Batch.Length;
                                if (remainder != 0)
                                {
                                    offset += (int)Math.Min(Batch.Length - remainder, input.SampleCount - offset);
                                    continue;
                                }
                                batchIndex = position / Batch.Length;
                                batchTime = audioOrigin + TimeSpan.FromSeconds(batchIndex * (double)BatchSize / Format.SampleRate);
                                captureTimestamp = packet.Arrival;
                            }
                            var count = Math.Min(Batch.Length - filled, input.SampleCount - offset);
                            input.Samples.AsSpan(offset, count).CopyTo(Batch.AsSpan(filled));
                            filled += count;
                            offset += count;
                            if (filled == Batch.Length)
                            {
                                AnalyzeBatch(batchTime, batchIndex, captureTimestamp);
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

        void AnalyzeBatch(DateTime time, long row, long captureTimestamp)
        {
            using var totalTiming = Profile.Measure("analysis.total_ms");
            var samples = RentEvent<double>(BatchSize, Format.ChannelCount, time);
            var spectrum = RentEvent<double>(Domain.Length, Format.ChannelCount, time);
            var output = RentEvent<double>(4 * Domain.Length, Format.ChannelCount, time);
            try
            {
                using (Profile.Measure("analysis.decode_ms"))
                for (var c = 0; c < Format.ChannelCount; ++c)
                {
                    var channel = samples.GetChannel(c).Span;
                    for (var i = 0; i < BatchSize; ++i)
                    {
                        var offset = (i * Format.ChannelCount + c) * Format.Type.Size;
                        channel[i] = Format.Type.ReadFrom(Batch.AsSpan(offset));
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
                    DFT[c].Process(samples.GetChannel(c));
                    DFT[c].Output(spectrum.GetChannel(c));
                    }
                    using (Profile.Measure("analysis.split_channel_ms"))
                    Splitter[c].Process(spectrum.GetChannel(c), output.GetChannel(c));
                }
                // Publish immediately; render pacing belongs to the renderer.
                output = output with { SampleIndex = row * BatchSize, AnalysisRow = row,
                    CaptureTimestamp = captureTimestamp, AnalysisTimestamp = Stopwatch.GetTimestamp() };
                Profile.Elapsed("analysis.capture_to_completion_ms", captureTimestamp);
                using (Profile.Measure("analysis.subscribers_ms"))
                    SamplesAnalyzed?.Invoke(this, output);
            }
            finally
            {
                ReturnEvent(samples);
                ReturnEvent(spectrum);
                ReturnEvent(output);
            }
        }

        static SampleEvent<T> RentEvent<T>(int sampleCount, int channelCount, DateTime time)
            => new(ArrayPool<T>.Shared.Rent(checked(sampleCount * channelCount)), sampleCount, time);
        static void ReturnEvent<T>(SampleEvent<T> evt) => ArrayPool<T>.Shared.Return(evt.Samples);
    }
}

