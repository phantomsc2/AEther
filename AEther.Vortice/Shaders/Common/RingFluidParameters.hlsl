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
    float EmissionVelocity;  // 0 .. 40 simulation units per second, scaled by local B.
    float VelocityGain;      // Local B sensitivity; .5 matches the former .236 setting.
    float Viscosity;         // 0 .. .1 via .1 * slider^3; no dye diffusion.
    float Curl;              // Local B-driven counter-rotating launch velocity.
    bool PeriodicBoundary;
};

#define RING_EMITTER_DEFAULTS { .667, .1600, .1, .1600, 1, .12 }
#define RING_FLUID_DEFAULTS { .5, .5, 0, 0, true }
