// All controls are normalized to [0,1] for the shader parameter UI.
struct EmitterParameters
{
    float Radius;            // 0 .. .5 simulation units, scaled for aspect.
    float Width;             // 0 .. .1 simulation units (half-width).
    float EdgeSoftness;      // 0 .. .01 units, with pixel antialiasing minimum.
    float Rate;              // 0 .. 200 density units per second, scaled by local S.
    float MaterialGain;      // Local S material sensitivity; .5 = unity gain.
    float Capacity;          // 0 .. 100 density units.
};
struct FluidParameters
{
    float EmissionVelocity;  // 0 .. 20 simulation units per second, scaled by local B.
    float VelocityGain;      // Local B sensitivity; .5 = unity gain.
    float Viscosity;         // 0 .. .1 via .1 * slider^3; no dye diffusion.
    float Curl;              // Local B-driven counter-rotating launch velocity.
    bool PeriodicBoundary;
    bool LowQuality;         // 128x128 simulation instead of the default 512x512.
};

#define RING_EMITTER_DEFAULTS { .667, .1600, .1, .1600, 1, .12 }
#define RING_FLUID_DEFAULTS { .3270, .0690, 0, 0, true, false }
