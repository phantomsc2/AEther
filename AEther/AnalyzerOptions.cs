using System;
using System.ComponentModel;

namespace AEther
{
    public class AnalyzerOptions
    {

        [Browsable(false)]
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
        public bool SIMDEnabled { get; set; } = true;

        [Category("DFT")]
        [DisplayName("Window function")]
        [DefaultValue(DFTWindow.Hann)]
        [Description("Hann: balanced. Rectangular: narrowest peak, most leakage. Hamming/Blackman: exact coefficients. Nuttall: very low sidelobes, wider peak. FlatTop: accurate off-bin levels, widest peak.")]
        public DFTWindow WindowFunction { get; set; } = DFTWindow.Hann;

        [Category("Visualization")]
        public double HistorySeconds { get; set; } = .3f;

        [Category("Splitter")]
        [DefaultValue(3.0)]
        [Description("Linear gain for independent time-median evidence. Does not scale magnitude.")]
        public double SineGain { get; set; } = 3.0;

        [Category("Splitter")]
        [DefaultValue(3.0)]
        [Description("Linear gain for independent frequency-median evidence. Does not scale magnitude.")]
        public double TransientGain { get; set; } = 3.0;

        [Category("Splitter")]
        [DefaultValue(3.0)]
        [Description("Linear gain for independent time-frequency median evidence. Does not scale magnitude.")]
        public double NoiseGain { get; set; } = 1.0;

        [Category("Splitter")]
        [Description("Causal time-median window in seconds; also the time extent of 2D evidence.")]
        public double MedianLength { get; set; } = .2;

        [Category("Splitter")]
        [Description("Centered frequency-median width in octaves; also the frequency extent of 2D evidence.")]
        public double MedianWidth { get; set; } = 3;

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
            if (!Enum.IsDefined(WindowFunction))
                throw new ArgumentOutOfRangeException(nameof(WindowFunction));
            if (!double.IsFinite(HistorySeconds) || HistorySeconds <= 0 || HistorySeconds > 60)
                throw new ArgumentOutOfRangeException(nameof(HistorySeconds));
            ValidateSplitter(FrequencyResolution, TimeResolution);
        }

        internal void ValidateSplitter(int resolution, double rate)
        {
            if (!double.IsFinite(SineGain) || SineGain < 0)
                throw new ArgumentOutOfRangeException(nameof(SineGain));
            if (!double.IsFinite(TransientGain) || TransientGain < 0)
                throw new ArgumentOutOfRangeException(nameof(TransientGain));
            if (!double.IsFinite(NoiseGain) || NoiseGain < 0)
                throw new ArgumentOutOfRangeException(nameof(NoiseGain));
            if (!double.IsFinite(MedianWidth) || MedianWidth * resolution < 2)
                throw new ArgumentOutOfRangeException(nameof(MedianWidth), "Median width needs at least two frequency samples.");
            if (!double.IsFinite(MedianLength) || MedianLength * rate < 2)
                throw new ArgumentOutOfRangeException(nameof(MedianLength), "Median history needs at least two time samples.");
        }

    }
}
