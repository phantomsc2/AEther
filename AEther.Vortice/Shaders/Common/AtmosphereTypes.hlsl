#ifndef AETHER_ATMOSPHERE_TYPES
#define AETHER_ATMOSPHERE_TYPES

// Distances in km, scattering/extinction in inverse km. No aerosols or absorption.
static const float AtmospherePi = 3.141592653589793;
static const float AtmosphereRadius = 6360.0;
// L0 is reserved for surface reflectance (currently unimplemented).
struct TransportSettings { bool L0, L1, L2Plus; };
struct AtmosphereSettings
{
    float3 RayleighScattering;
    float ScaleHeight;
    float Density;
};
struct AtmosphereSunSettings { float Azimuth, Altitude, Intensity; };
struct AtmosphereViewSettings { float Azimuth, Altitude; };
struct AtmospherePhaseSettings { float Anisotropy, Strength; bool UseForMultipleScattering; };
#endif
