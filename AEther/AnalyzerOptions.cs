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
        [Description("Spectrum publication and log-polar tracker update rate.")]
        public int TimeResolution { get; set; } = 1000;

        [Category("DFT")]
        public bool SIMDEnabled { get; set; } = true;

        [Category("DFT")]
        [DisplayName("Spectrum analysis")]
        [DefaultValue(true)]
        [Description("SWIFT: exponential note-centered spectrum. DFT: finite selected window. Switching restarts analysis.")]
        public bool UseSwift { get; set; } = true;

        [Category("DFT")]
        [DisplayName("SWIFT width")]
        [DefaultValue(1.0)]
        [Description("Full -3 dB bandwidth divided by geometric bin width. 1 matches bin width. Larger values are broader and faster, with more overlap; smaller values are narrower and slower.")]
        public double SwiftWidthScale { get; set; } = 1;

        [Category("DFT")]
        [DisplayName("SWIFT imbalance")]
        [DefaultValue(0.0)]
        [Description("0: two equal smoothing stages. 1: single-stage limit. Intermediate values use unequal stages. Duration is retuned to preserve the selected -3 dB width.")]
        public double SwiftImbalance { get; set; } = 0;

        [Category("DFT")]
        [DisplayName("Window function")]
        [DefaultValue(DFTWindow.Hann)]
        [Description("Hann: balanced. Rectangular: narrowest peak, most leakage. Hamming/Blackman: exact coefficients. Nuttall: very low sidelobes, wider peak. FlatTop: accurate off-bin levels, widest peak.")]
        public DFTWindow WindowFunction { get; set; } = DFTWindow.Hann;

        [Category("Visualization")]
        [Description("Seconds of spectral history retained for age-based shader lookups. Longer values allow longer fluid tails and use more memory.")]
        public double HistorySeconds { get; set; } = 2;

        [Category("Input")]
        [DisplayName("Input gain (dB)")]
        [DefaultValue(15.0)]
        [Description("Gain before all analysis, from -60 to +60 dB. 0 dB preserves full-scale input units. For consistent loopback visuals, keep player volume at 100% and adjust listening volume downstream of capture.")]
        public double InputGainDb { get; set; } = 15.0;

        // Retained for historical standalone splitter studies, outside the active UI.
        [Browsable(false)]
        public double TimeWindowSeconds { get; set; } = .2;

        [Browsable(false)]
        public double FrequencyWindowOctaves { get; set; } = 2;

        [Category("Log-polar tracker")]
        [DisplayName("Amplitude response (seconds)")]
        [DefaultValue(.1)]
        [Description("Characteristic amplitude tracking time at amplitude 0.1 and noise RMS 0.001. Actual response depends on signal/noise ratio; this is not a settling-time guarantee.")]
        public double AmplitudeTrackingSeconds { get; set; } = .1;

        [Category("Log-polar tracker")]
        [DisplayName("Amplitude curvature scale (octaves)")]
        [DefaultValue(.1)]
        [Description("Experimental cross-bin log-amplitude prediction and correction. Larger scales penalize broader spectral curvature. 0 disables both; this is not a hard smoothing width.")]
        public double AmplitudeCurvatureOctaves { get; set; } = .1;

        [Category("Log-polar tracker")]
        [DisplayName("Amplitude curvature correction strength")]
        [DefaultValue(1.0)]
        [Description("Confidence-weighted curvature penalty after measurements. Larger values smooth more; 0 keeps prediction coupling only. Strength 1 uses curvature precision 10000 per tick. Measurement confidence still matters.")]
        public double AmplitudeCurvatureCorrectionStrength { get; set; } = 1;

        [Category("Log-polar tracker")]
        [DisplayName("Amplitude coupling time (seconds)")]
        [DefaultValue(.1)]
        [Description("Time scale of the spatial restoring force, separate from measurement tracking. Smaller values couple bins more strongly.")]
        public double AmplitudeCouplingSeconds { get; set; } = .1;

        [Category("Log-polar tracker")]
        [DisplayName("Amplitude spatial damping")]
        [DefaultValue(1.0)]
        [Description("Damping of spatially rough amplitude velocity. 0 allows ringing; 1 is critical damping for a spatial mode with unit normalized curvature eigenvalue.")]
        public double AmplitudeSpatialDamping { get; set; } = 1;

        [Category("Log-polar tracker")]
        [DisplayName("Phase response (seconds)")]
        [DefaultValue(.1)]
        [Description("Characteristic phase tracking time in seconds at the same reference signal/noise ratio as amplitude. Phase is experimental; no phase-jump rejection or frequency snapping is applied.")]
        public double PhaseTrackingSeconds { get; set; } = .1;

        [Category("Log-polar tracker")]
        [DisplayName("Measurement noise RMS")]
        [DefaultValue(.001)]
        [Description("Fixed Cartesian noise RMS per real/imaginary component in coherent amplitude units AFTER input gain. Larger values reduce trust in measurements.")]
        public double TrackerNoiseFloor { get; set; } = .001;

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
            if (!double.IsFinite(SwiftWidthScale) || SwiftWidthScale < .25 || SwiftWidthScale > 8)
                throw new ArgumentOutOfRangeException(nameof(SwiftWidthScale));
            if (!double.IsFinite(SwiftImbalance) || SwiftImbalance < 0 || SwiftImbalance > 1)
                throw new ArgumentOutOfRangeException(nameof(SwiftImbalance));
            var highestFrequency = MinFrequency * Math.Pow(2, (Domain.Length - 1.0) / FrequencyResolution);
            if (UseSwift && SwiftTuning.Bandwidth(highestFrequency, FrequencyResolution, SwiftWidthScale) >= format.SampleRate)
                throw new ArgumentOutOfRangeException(nameof(SwiftWidthScale),
                    "SWIFT bandwidth must be smaller than the sample rate. Reduce width or increase bins per octave.");
            if (!double.IsFinite(HistorySeconds) || HistorySeconds <= 0 || HistorySeconds > 60)
                throw new ArgumentOutOfRangeException(nameof(HistorySeconds));
            ValidateTracker();
        }

        internal void ValidateTracker()
        {
            if (!double.IsFinite(AmplitudeCurvatureCorrectionStrength) || AmplitudeCurvatureCorrectionStrength < 0 || AmplitudeCurvatureCorrectionStrength > 1e8)
                throw new ArgumentOutOfRangeException(nameof(AmplitudeCurvatureCorrectionStrength));
            if (!double.IsFinite(AmplitudeCurvatureOctaves) || AmplitudeCurvatureOctaves < 0 || AmplitudeCurvatureOctaves > 1)
                throw new ArgumentOutOfRangeException(nameof(AmplitudeCurvatureOctaves));
            if (!double.IsFinite(AmplitudeCouplingSeconds) || AmplitudeCouplingSeconds < .01 || AmplitudeCouplingSeconds > 10)
                throw new ArgumentOutOfRangeException(nameof(AmplitudeCouplingSeconds));
            if (!double.IsFinite(AmplitudeSpatialDamping) || AmplitudeSpatialDamping < 0 || AmplitudeSpatialDamping > 4)
                throw new ArgumentOutOfRangeException(nameof(AmplitudeSpatialDamping));
            if (!double.IsFinite(AmplitudeTrackingSeconds) || AmplitudeTrackingSeconds < .001 || AmplitudeTrackingSeconds > 10)
                throw new ArgumentOutOfRangeException(nameof(AmplitudeTrackingSeconds));
            if (!double.IsFinite(PhaseTrackingSeconds) || PhaseTrackingSeconds < .001 || PhaseTrackingSeconds > 10)
                throw new ArgumentOutOfRangeException(nameof(PhaseTrackingSeconds));
            if (!double.IsFinite(TrackerNoiseFloor) || TrackerNoiseFloor < 1e-12 || TrackerNoiseFloor > 1e3)
                throw new ArgumentOutOfRangeException(nameof(TrackerNoiseFloor));
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
