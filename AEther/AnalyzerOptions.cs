using System;
using System.ComponentModel;
using System.IO;
using System.Xml.Serialization;

namespace AEther
{
    public enum SplitterOrder { TimeThenFrequency, FrequencyThenTime }
    public class AnalyzerOptions
    {

        public Domain Domain => Domain.FromRange(MinFrequency, MaxFrequency, FrequencyResolution);

        [Category("Pipeline")]
        public int BufferCapacity { get; set; } = 4;

        [Category("Domain")]
        public double MinFrequency { get; set; } = 27.5;

        [Category("Domain")]
        public double MaxFrequency { get; set; } = 8000;

        [Category("Domain")]
        public int FrequencyResolution { get; set; } = 8;

        [Category("DFT")]
        public int TimeResolution { get; set; } = 1 << 8;

        [Category("DFT")]
        public int MaxParallelization { get; set; } = 0;

        [Category("DFT")]
        public bool SIMDEnabled { get; set; } = true;

        [Category("Visualization")]
        public double HistorySeconds { get; set; } = 1;

        [Category("Visualization")]
        public double HistoryPresentationDelayMilliseconds { get; set; } = 0;

        [Category("Storm responses")]
        public double FlashDecaySeconds { get; set; } = .09;

        [Category("Storm responses")]
        public double AfterglowSeconds { get; set; } = 1.2;

        [Category("Storm responses")]
        public double OrbitInertiaSeconds { get; set; } = 2.8;

        [Category("Storm responses")]
        public double BuildReleaseSeconds { get; set; } = 5;

        [Category("Splitter")]
        public double SeparationFloorDb { get; set; } = -80;

        [Category("Splitter")]
        public double SinuoidWidth { get; set; } = .3;

        [Category("Splitter")]
        public double SinuoidLength { get; set; } = .3;

        [Category("Splitter")]
        public double TransientWidth { get; set; } = 3;

        [Category("Splitter")]
        public double TransientLength { get; set; } = .03;

        [Category("Splitter")]
        public double NoiseWidth { get; set; } = 1;

        [Category("Splitter")]
        public double NoiseLength { get; set; } = .1;

        [Category("Splitter")]
        [Description("Shared post-filter EMA time constant in seconds; zero disables it. Transients attack immediately and use this time for release.")]
        public double JitterLength { get; set; } = .01;

        [Category("Splitter")]
        public SplitterOrder SplitOrder { get; set; } = SplitterOrder.TimeThenFrequency;

        [Category("Splitter")]
        public bool AdaptiveSpectrumRange { get; set; } = true;

        [Category("Splitter")]
        public double QuantileHistorySeconds { get; set; } = 60;

        public void Validate(SampleFormat format)
        {
            if (format.SampleRate <= 0 || format.ChannelCount <= 0 || format.Type.Size <= 0)
                throw new ArgumentOutOfRangeException(nameof(format));
            if (BufferCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(BufferCapacity), "Use a positive, bounded input capacity.");
            if (TimeResolution <= 0 || TimeResolution > format.SampleRate)
                throw new ArgumentOutOfRangeException(nameof(TimeResolution));
            if (!double.IsFinite(MinFrequency) || !double.IsFinite(MaxFrequency)
                || MinFrequency <= 0 || MaxFrequency <= MinFrequency || MaxFrequency > format.SampleRate / 2.0)
                throw new ArgumentOutOfRangeException(nameof(MaxFrequency), "Frequency range must be positive and below Nyquist.");
            if (FrequencyResolution <= 0 || Domain.Length <= 0)
                throw new ArgumentOutOfRangeException(nameof(FrequencyResolution));
            if (MaxParallelization < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxParallelization));
            if (!double.IsFinite(HistorySeconds) || HistorySeconds <= 0 || HistorySeconds > 60)
                throw new ArgumentOutOfRangeException(nameof(HistorySeconds));
            if (!double.IsFinite(HistoryPresentationDelayMilliseconds)
                || HistoryPresentationDelayMilliseconds < 0 || HistoryPresentationDelayMilliseconds > 250)
                throw new ArgumentOutOfRangeException(nameof(HistoryPresentationDelayMilliseconds));
            foreach (var width in new[] { SinuoidWidth, TransientWidth, NoiseWidth })
                if (!double.IsFinite(width) || width * FrequencyResolution < 2)
                    throw new ArgumentOutOfRangeException(nameof(width), "Filter widths in octaves need at least two frequency samples.");
            foreach (var length in new[] { SinuoidLength, TransientLength, NoiseLength })
                if (!double.IsFinite(length) || length * TimeResolution < 2)
                    throw new ArgumentOutOfRangeException(nameof(length), "Filter lengths in seconds need at least two time samples.");
            if (!double.IsFinite(SeparationFloorDb) || SeparationFloorDb > 0 || SeparationFloorDb < -200)
                throw new ArgumentOutOfRangeException(nameof(SeparationFloorDb));
            if (!double.IsFinite(JitterLength) || JitterLength < 0)
                throw new ArgumentOutOfRangeException(nameof(JitterLength));
            if (!double.IsFinite(QuantileHistorySeconds) || QuantileHistorySeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(QuantileHistorySeconds));
            if (!Enum.IsDefined(SplitOrder)) throw new ArgumentOutOfRangeException(nameof(SplitOrder));
            if (!double.IsFinite(FlashDecaySeconds) || FlashDecaySeconds <= 0
                || !double.IsFinite(AfterglowSeconds) || AfterglowSeconds <= 0
                || !double.IsFinite(OrbitInertiaSeconds) || OrbitInertiaSeconds <= 0
                || !double.IsFinite(BuildReleaseSeconds) || BuildReleaseSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(OrbitInertiaSeconds));
        }

        public static AnalyzerOptions ReadFromFile(string path)
        {

            var file = new FileInfo(path);
            var serializer = new XmlSerializer(typeof(AnalyzerOptions));
            using var stream = file.Open(FileMode.Open, FileAccess.Read);
            var result = serializer.Deserialize(stream);
            if (result is not AnalyzerOptions options)
                throw new InvalidCastException();
            return options;

        }

        public void WriteToFile(string path)
        {

            var file = new FileInfo(path);
            var serializer = new XmlSerializer(typeof(AnalyzerOptions));
            using var stream = file.Open(FileMode.Create, FileAccess.Write);
            serializer.Serialize(stream, this);

        }

    }
}
