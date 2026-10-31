#ifndef AETHER_ATMOSPHERE_TYPES
#define AETHER_ATMOSPHERE_TYPES

// Distances in km, scattering/extinction in inverse km. No aerosols or absorption.
static const float AtmospherePi = 3.141592653589793;
static const float AtmosphereRadius = 6360.0;
struct AtmosphereSettings
{
    float3 RayleighScattering;
    float ScaleHeight;
    float Density;
};
struct AtmosphereSunSettings { float Azimuth, Altitude, Intensity; };
struct AtmosphereViewSettings { float Azimuth, Altitude; };
struct AtmospherePhaseSettings { float Anisotropy, Shape; };
#endif
