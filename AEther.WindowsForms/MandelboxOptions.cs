using System.ComponentModel;
using AEther.Vortice;

namespace AEther.WindowsForms;

public sealed class MandelboxOptions
{
    AntialiasingQuality antialiasing = AntialiasingQuality.None;
    int maximumIterations = 10;
    float movementSpeed = 3;
    float scale = -1.5f;
    float juliaBlend = .3f, foldRotation = .35f, anisotropy = .22f;
    float bilateralStrength = 1, bilateralFrequencySigma = 1, bilateralTimeSigma = 1, bilateralRangeSigma = .1f;
    bool filteredHistogramView;
    float fluidFlow = 1, fluidHalfLife = 1.4f, fluidDiffusion = .001f, fluidNoise = 1, fluidCurl = .45f;

    public event EventHandler? Changed;

    [DisplayName("Julia blend"), DefaultValue(.3f)]
    [Description("0 to 1. Musical time explores offset Julia-like chambers. Zero restores the original point injection. Set all three variation controls to zero for the original geometry.")]
    public float JuliaBlend { get => juliaBlend; set => SetFilterValue(ref juliaBlend, value, 0, 1); }

    [DisplayName("Internal fold rotation"), DefaultValue(.35f)]
    [Description("0 to 1 radians per axis. Slowly rotates the orbit inside each iteration, creating interlocking structures. Follows musical time and movement speed.")]
    public float FoldRotation { get => foldRotation; set => SetFilterValue(ref foldRotation, value, 0, 1); }

    [DisplayName("Anisotropy"), DefaultValue(.22f)]
    [Description("0 to 0.5. Musical time stretches different axes while preserving volume, creating ribs and elongated chambers. Zero restores uniform scale.")]
    public float Anisotropy { get => anisotropy; set => SetFilterValue(ref anisotropy, value, 0, .5f); }

    [DisplayName("Fluid outward flow"), DefaultValue(1f)]
    [Description("MandelFluid expansion speed, 0 to 3. Tones and mist travel from the center through the rhythmic structure.")]
    public float FluidFlow { get => fluidFlow; set => SetFilterValue(ref fluidFlow, value, 0, 3); }

    [DisplayName("Fluid half-life (seconds)"), DefaultValue(1.4f)]
    [Description("MandelFluid pigment lifetime, 0.1 to 10 seconds. Longer values leave lingering ambient plumes.")]
    public float FluidHalfLife { get => fluidHalfLife; set => SetFilterValue(ref fluidHalfLife, value, .1f, 10); }

    [DisplayName("Fluid diffusion"), DefaultValue(.001f)]
    [Description("Maximum noise-driven MandelFluid mixing, 0 to 0.01. Pure tones remain crystalline; noise increases diffusion toward this level.")]
    public float FluidDiffusion { get => fluidDiffusion; set => SetFilterValue(ref fluidDiffusion, value, 0, .01f); }

    [DisplayName("Fluid noise"), DefaultValue(1f)]
    [Description("MandelFluid noise-driven mist and turbulence, 0 to 3. Noise and distortion drive overall movement and mixing.")]
    public float FluidNoise { get => fluidNoise; set => SetFilterValue(ref fluidNoise, value, 0, 3); }

    [DisplayName("Fluid curl"), DefaultValue(.45f)]
    [Description("MandelFluid vorticity confinement, 0 to 2. Restores small curls as the fluid spreads.")]
    public float FluidCurl { get => fluidCurl; set => SetFilterValue(ref fluidCurl, value, 0, 2); }

    [DisplayName("Filtered histogram view"), DefaultValue(false)]
    [Description("Show bilateral-filtered STN amplitudes in the histogram analyzer view. False shows raw history. The combined magnitude view remains raw.")]
    public bool FilteredHistogramView
    {
        get => filteredHistogramView;
        set
        {
            if (filteredHistogramView == value) return;
            filteredHistogramView = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [DisplayName("Bilateral strength"), DefaultValue(.6f)]
    [Description("History smoothing blend, 0 to 1. Zero bypasses the filter. Applies to the fractal and optionally the histogram analyzer view.")]
    public float BilateralStrength
    {
        get => bilateralStrength;
        set => SetFilterValue(ref bilateralStrength, value, 0, 1);
    }

    [DisplayName("Bilateral frequency sigma"), DefaultValue(1f)]
    [Description("Gaussian neighborhood width in frequency bins, 0.1 to 2. Kernel extends to twice sigma. Larger values smooth across more frequencies.")]
    public float BilateralFrequencySigma
    {
        get => bilateralFrequencySigma;
        set => SetFilterValue(ref bilateralFrequencySigma, value, .1f, 2);
    }

    [DisplayName("Bilateral time sigma"), DefaultValue(1f)]
    [Description("Gaussian neighborhood width in analysis rows, 0.1 to 2. Larger values smooth across more history samples.")]
    public float BilateralTimeSigma
    {
        get => bilateralTimeSigma;
        set => SetFilterValue(ref bilateralTimeSigma, value, .1f, 2);
    }

    [DisplayName("Bilateral range sigma"), DefaultValue(.1f)]
    [Description("Edge tolerance in linear amplitude after class gains, 0.0001 to 10. Smaller values protect amplitude edges; larger values allow stronger smoothing.")]
    public float BilateralRangeSigma
    {
        get => bilateralRangeSigma;
        set => SetFilterValue(ref bilateralRangeSigma, value, .0001f, 10);
    }

    void SetFilterValue(ref float field, float value, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), $"Use a finite value from {minimum} to {maximum}.");
        if (field == value) return;
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [DisplayName("Scale"), DefaultValue(-1.5f)]
    [Description("Signed scale applied inside every Mandelbox iteration. Default: -1.5. Positive values produce a different family of shapes.")]
    public float Scale
    {
        get => scale;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Use a finite scale.");
            if (scale == value) return;
            scale = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [DisplayName("Antialiasing"), DefaultValue(AntialiasingQuality.Medium)]
    [Description("None: one sample. Medium: 2 × 2 samples. High: 4 × 4 samples.")]
    public AntialiasingQuality Antialiasing
    {
        get => antialiasing;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (antialiasing == value) return;
            antialiasing = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [DisplayName("Maximum iterations"), DefaultValue(10)]
    [Description("Maximum depth reached by audio-driven depth travel. Any positive integer.")]
    public int MaximumIterations
    {
        get => maximumIterations;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Use a positive iteration count.");
            if (maximumIterations == value) return;
            maximumIterations = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [DisplayName("Movement speed"), DefaultValue(1f)]
    [Description("Slice, rotation, zoom and structural variation speed. 1 is the original speed; 3 is three times faster; 0 freezes movement. Depth travel and history timing are unaffected.")]
    public float MovementSpeed
    {
        get => movementSpeed;
        set
        {
            if (!float.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a finite, nonnegative speed.");
            if (movementSpeed == value) return;
            movementSpeed = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
