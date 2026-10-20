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

        [Category("Pipeline")]
        [Description("Maximum age of completed live analysis ticks. Preserve the samples needed by a fresh tick. Offline input is unaffected.")]
        public double MaxInputAgeMilliseconds { get; set; } = 50;

        [Category("Domain")]
        public double MinFrequency { get; set; } = 30;

        [Category("Domain")]
        public double MaxFrequency { get; set; } = 8000;

        [Category("Domain")]
        public int FrequencyResolution { get; set; } = 12;

        [Category("DFT")]
        [Description("Spectrum publication and STBR Gaussian fit update rate.")]
        public int TimeResolution { get; set; } = 1000;

        [Category("DFT")]
        public bool SIMDEnabled { get; set; } = true;

        [Category("DFT")]
        [DisplayName("Window function")]
        [DefaultValue(DFTWindow.Hann)]
        [Description("Hann: balanced. Rectangular: narrowest peak, most leakage. Hamming/Blackman: exact coefficients. Nuttall: very low sidelobes, wider peak. FlatTop: accurate off-bin levels, widest peak.")]
        public DFTWindow WindowFunction { get; set; } = DFTWindow.Hann;

        [Category("Visualization")]
        [Description("Seconds of spectral history retained for age-based shader lookups. Longer values allow longer fluid tails and use more memory.")]
        public double HistorySeconds { get; set; } = 2;

        [Category("STBR fit")]
        [DefaultValue(false)]
        [DisplayName("S time slope")]
        [Description("Allow each frequency's S evidence to rise or fall linearly through the time window.")]
        public bool JointTimeSlopeEnabled { get; set; } = false;

        [Category("STBR fit")]
        [DefaultValue(true)]
        [DisplayName("T frequency slope")]
        [Description("Allow each frame's T evidence to tilt linearly across log frequency.")]
        public bool JointFrequencySlopeEnabled { get; set; } = true;

        [Category("Input")]
        [DisplayName("Input gain (dB)")]
        [DefaultValue(15.0)]
        [Description("Gain before all analysis, from -60 to +60 dB. 0 dB preserves full-scale input units. For consistent loopback visuals, keep player volume at 100% and adjust listening volume downstream of capture.")]
        public double InputGainDb { get; set; } = 15.0;

        [Category("STBR fit")]
        [DisplayName("Time window (seconds)")]
        [DefaultValue(.2)]
        [Description("Causal time extent of the Gaussian STBR fit in seconds.")]
        public double TimeWindowSeconds { get; set; } = .2;

        [Category("STBR fit")]
        [DisplayName("Frequency window (octaves)")]
        [DefaultValue(2.0)]
        [Description("Centered log-frequency extent of the Gaussian STBR fit in octaves.")]
        public double FrequencyWindowOctaves { get; set; } = 2;

        public void Validate(SampleFormat format)
        {
            if (format.SampleRate <= 0 || format.ChannelCount <= 0 || format.Type.Size <= 0)
                throw new ArgumentOutOfRangeException(nameof(format));
            if (BufferCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(BufferCapacity), "Use a positive, bounded input capacity.");
            if (!double.IsFinite(MaxInputAgeMilliseconds) || MaxInputAgeMilliseconds <= 0 || MaxInputAgeMilliseconds > 1000)
                throw new ArgumentOutOfRangeException(nameof(MaxInputAgeMilliseconds));
            if (!double.IsFinite(InputGainDb) || InputGainDb < -60 || InputGainDb > 60)
                throw new ArgumentOutOfRangeException(nameof(InputGainDb), "Input gain must be between -60 and +60 dB.");
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
            if (!double.IsFinite(FrequencyWindowOctaves) || FrequencyWindowOctaves * resolution < 2)
                throw new ArgumentOutOfRangeException(nameof(FrequencyWindowOctaves), "Frequency window needs at least two frequency samples.");
            if (!double.IsFinite(TimeWindowSeconds) || TimeWindowSeconds * rate < 2)
                throw new ArgumentOutOfRangeException(nameof(TimeWindowSeconds), "Time window needs at least two time samples.");
        }

    }
}
