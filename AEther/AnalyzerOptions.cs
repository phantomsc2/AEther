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
        public int FrequencyResolution { get; set; } = 12;

        [Category("DFT")]
        [Description("Spectrum publication and Gaussian local-fit update rate. Legacy exact filters observe at this rate; experimental per-sample tracking updates at the audio sample rate.")]
        public int TimeResolution { get; set; } = 1 << 8;

        [Category("DFT")]
        public bool SIMDEnabled { get; set; } = true;

        [Category("DFT")]
        [DisplayName("Window function")]
        [DefaultValue(DFTWindow.Blackman)]
        [Description("Hann: balanced. Rectangular: narrowest peak, most leakage. Hamming/Blackman: exact coefficients. Nuttall: very low sidelobes, wider peak. FlatTop: accurate off-bin levels, widest peak.")]
        public DFTWindow WindowFunction { get; set; } = DFTWindow.Hann;

        [Category("Visualization")]
        public double HistorySeconds { get; set; } = .5f;

        [Category("Alternative evidence modes")]
        [DefaultValue(true)]
        [Description("Subtract the raw-amplitude rectangular background before estimating S/T. Deviations remain signed through filtering; only displayed S/T are clamped. Disable for direct evidence. LocalJointFit always fits the background jointly.")]
        public bool SubtractNoiseBackground { get; set; } = true;

        [Category("Splitter")]
        [DefaultValue(EvidenceMode.LocalJointFit)]
        [Description("Default: rolling Gaussian local fit with time and log-frequency slopes. Exact medians are a legacy alternative; per-sample rolling sign tracking is experimental.")]
        public EvidenceMode EvidenceMode { get; set; } = EvidenceMode.LocalJointFit;

        [Category("Joint fit")]
        [DefaultValue(true)]
        [Description("LocalJointFit: allow each frequency's S evidence to rise or fall linearly through the time window.")]
        public bool JointTimeSlopeEnabled { get; set; } = true;

        [Category("Joint fit")]
        [DefaultValue(true)]
        [Description("LocalJointFit: allow each frame's T evidence to tilt linearly across log frequency.")]
        public bool JointFrequencySlopeEnabled { get; set; } = true;

        [Category("Alternative evidence modes")]
        [DefaultValue(1.0)]
        [Description("Experimental per-sample mode only: maximum tracker slew in linear amplitude per second. Does not affect the Gaussian fit.")]
        public double SignSlewRate { get; set; } = 1.0;

        [Category("Splitter")]
        [DefaultValue(10.0)]
        [Description("Display gain for time-directional S evidence. Does not scale magnitude.")]
        public double SineGain { get; set; } = 10.0;

        [Category("Splitter")]
        [DefaultValue(10.0)]
        [Description("Display gain for frequency-directional T evidence. Does not scale magnitude.")]
        public double TransientGain { get; set; } = 10.0;

        [Category("Splitter")]
        [DefaultValue(10.0)]
        [Description("Display gain for background N evidence. Does not scale magnitude.")]
        public double NoiseGain { get; set; } = 10.0;

        [Category("Splitter")]
        [DisplayName("Time window (seconds)")]
        [DefaultValue(.1)]
        [Description("Gaussian local-fit time extent in seconds; also used by legacy exact filters. Unused by experimental per-sample tracking.")]
        public double MedianLength { get; set; } = .1;

        [Category("Splitter")]
        [DisplayName("Frequency window (octaves)")]
        [DefaultValue(3.0)]
        [Description("Centered log-frequency extent of the Gaussian local fit. Also defines neighborhoods for the alternative modes.")]
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
            if (!Enum.IsDefined(EvidenceMode))
                throw new ArgumentOutOfRangeException(nameof(EvidenceMode));
            if (!double.IsFinite(SignSlewRate) || SignSlewRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(SignSlewRate));
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
