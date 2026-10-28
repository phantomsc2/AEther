using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AEther
{
    public class AnalyzerOptions
    {
        /// <summary>Independent settings for one pipeline generation. All option values are scalars.</summary>
        public AnalyzerOptions Copy() => (AnalyzerOptions)MemberwiseClone();

        public AnalysisOutput GetAnalysisOutput(SampleFormat format)
        {
            Validate(format);
            return new(Domain, format.SampleRate / (double)(format.SampleRate / TimeResolution));
        }

        [Browsable(false)]
        [JsonIgnore]
        public Domain Domain => Domain.FromRange(MinFrequency, MaxFrequency, FrequencyResolution);

        [Category("Pipeline")]
        [Range(1, 64)]
        public int BufferCapacity { get; set; } = 4;

        [Category("Pipeline")]
        [Range(1.0, 1000.0)]
        [Description("Maximum age of completed live analysis ticks. Preserve the samples needed by a fresh tick. Offline input is unaffected.")]
        public double MaxInputAgeMilliseconds { get; set; } = 50;

        [Category("DFT")]
        [Range(1.0, 22049.0)]
        public double MinFrequency { get; set; } = 30;

        [Category("DFT")]
        [Range(2.0, 22050.0)]
        public double MaxFrequency { get; set; } = 8000;

        [Category("DFT")]
        [Range(1, 96)]
        public int FrequencyResolution { get; set; } = 12;

        [Category("DFT")]
        [Range(16, 1024)]
        [Description("Spectrum publication and Kalman smoother update rate.")]
        public int TimeResolution { get; set; } = 1000;

        [Category("DFT")]
        public bool SIMDEnabled { get; set; } = true;

        [Category("DFT")]
        [DisplayName("Spectrum analysis")]
        [Description("SWIFT: exponential note-centered spectrum. DFT: finite selected window. Switching restarts analysis.")]
        public bool UseSwift { get; set; } = true;

        [Category("DFT")]
        [DisplayName("SWIFT width (octaves)")]
        [Range(-2.0, 3.0)]
        [JsonIgnore]
        [Description("Width relative to geometric bin width, in octaves: 0 matches bin width, -2 is one quarter, +3 is eight times. Larger values are broader and faster; smaller values are narrower and slower.")]
        public double SwiftWidthOctaves
        {
            get => Math.Log2(SwiftWidthScale);
            set => SwiftWidthScale = Math.Pow(2, value);
        }

        [Browsable(false)]
        public double SwiftWidthScale { get; set; } = 1;

        [Category("DFT")]
        [DisplayName("SWIFT imbalance")]
        [Range(0.0, 1.0)]
        [Description("0: two equal smoothing stages. 1: single-stage limit. Intermediate values use unequal stages. Duration is retuned to preserve the selected -3 dB width.")]
        public double SwiftImbalance { get; set; } = 0;

        [Category("DFT")]
        [DisplayName("Window function")]
        [Description("Hann: balanced. Rectangular: narrowest peak, most leakage. Hamming/Blackman: exact coefficients. Nuttall: very low sidelobes, wider peak. FlatTop: accurate off-bin levels, widest peak.")]
        public DFTWindow WindowFunction { get; set; } = DFTWindow.Hann;

        [DisplayName("Input gain (dB)")]
        [Range(-60.0, 60.0)]
        [Description("Gain before all analysis, from -60 to +60 dB. 0 dB preserves full-scale input units. For consistent loopback visuals, keep player volume at 100% and adjust listening volume downstream of capture.")]
        public double InputGainDb { get; set; } = 15.0;

        [Category("Splitter")]
        [DisplayName("Frequency tilt (dB/octave)")]
        [Range(0.0, 6.0)]
        [Description("Linear-amplitude frequency weighting after decomposition, anchored at 1 kHz. 0 = None, +3 dB/octave = default, +6 dB/octave = max. Raw/smoother diagnostics remain unweighted.")]
        public double FrequencyTiltDbPerOctave { get; set; } = 3;

        [Category("Splitter")]
        [Range(0.0, 2.0)]
        [Description("Temporal support of the original Gaussian S/T/B/residual decomposition, in seconds.")]
        public double TimeWindowSeconds { get; set; } = .2;

        [Category("Splitter")]
        [Range(0.0, 8.0)]
        [Description("Frequency support of the original splitter, in octaves.")]
        public double FrequencyWindowOctaves { get; set; } = 2;

        [Category("Smoother")]
        [DisplayName("Enabled")]
        [Description("Smooth the spectrum before the splitter. When off, both filters are bypassed and observed linear magnitudes feed the original splitter directly.")]
        public bool SmoothingEnabled { get; set; } = false;

        [Category("Smoother")]
        [DisplayName("Splitter input")]
        [Description("Both smoothers run on identical observations. Select which linear amplitude feeds the original splitter. Spectrum CompareSmoothers shows A left, B right, using the same stereo average.")]
        public SmoothingMode SmoothingMode { get; set; } = SmoothingMode.LogAmplitude;

        [Category("Smoother")]
        [DisplayName("Log acceleration intensity (log2 control)")]
        [Range(0.0, 36.54120904377541)]
        [JsonIgnore]
        [Description("Control x sets Mode A acceleration intensity q = 1e-8 * (2^x - 1), in log-amplitude squared / seconds cubed. Larger permits faster dynamics; 0 is deterministic constant velocity.")]
        public double LogAccelerationControl
        {
            get => Math.Log2(1 + LogAccelerationIntensity / 1e-8);
            set => LogAccelerationIntensity = 1e-8 * (Math.Pow(2, value) - 1);
        }

        [Browsable(false)]
        public double LogAccelerationIntensity { get; set; } = .001;

        [Category("Smoother")]
        [DisplayName("Complex acceleration intensity (log2 control)")]
        [Range(0.0, 36.54120904377541)]
        [JsonIgnore]
        [Description("Control x sets Mode B acceleration intensity q = 1e-8 * (2^x - 1) per Cartesian component, in amplitude squared / seconds cubed. Larger permits faster dynamics.")]
        public double ComplexAccelerationControl
        {
            get => Math.Log2(1 + ComplexAccelerationIntensity / 1e-8);
            set => ComplexAccelerationIntensity = 1e-8 * (Math.Pow(2, value) - 1);
        }

        [Browsable(false)]
        public double ComplexAccelerationIntensity { get; set; } = .00001;

        [Category("Smoother")]
        [DisplayName("Measurement noise RMS (log2)")]
        [Range(-39.86313713864835, 0.0)]
        [JsonIgnore]
        [Description("Base-2 logarithm of fixed Cartesian noise RMS per real/imaginary component AFTER input gain. RMS = 2^x; larger values reduce trust in measurements.")]
        public double SmoothingNoiseLog2
        {
            get => Math.Log2(SmoothingNoiseFloor);
            set => SmoothingNoiseFloor = Math.Pow(2, value);
        }

        [Browsable(false)]
        public double SmoothingNoiseFloor { get; set; } = .001;

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
            if (!double.IsFinite(FrequencyTiltDbPerOctave) || FrequencyTiltDbPerOctave < 0 || FrequencyTiltDbPerOctave > 6)
                throw new ArgumentOutOfRangeException(nameof(FrequencyTiltDbPerOctave));
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
            ValidateSmoother();
            ValidateSplitter(FrequencyResolution, format.SampleRate / (double)(format.SampleRate / TimeResolution));
        }

        internal void ValidateSmoother()
        {
            if (!Enum.IsDefined(SmoothingMode)) throw new ArgumentOutOfRangeException(nameof(SmoothingMode));
            if (!double.IsFinite(LogAccelerationIntensity) || LogAccelerationIntensity < 0 || LogAccelerationIntensity > 1e6)
                throw new ArgumentOutOfRangeException(nameof(LogAccelerationIntensity));
            if (!double.IsFinite(ComplexAccelerationIntensity) || ComplexAccelerationIntensity < 0 || ComplexAccelerationIntensity > 1e6)
                throw new ArgumentOutOfRangeException(nameof(ComplexAccelerationIntensity));
            if (!double.IsFinite(SmoothingNoiseFloor) || SmoothingNoiseFloor < 1e-12 || SmoothingNoiseFloor > 1e3)
                throw new ArgumentOutOfRangeException(nameof(SmoothingNoiseFloor));
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
