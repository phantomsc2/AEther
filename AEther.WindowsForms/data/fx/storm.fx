#include "states.fxi"
#include "globals.fxi"

cbuffer StormConstants : register(b1)
{
    float Energy, Bass, Body, Air;
    float Flash, Afterglow, Breath, WeatherTime;
    float Distance, Yaw, Pitch, Roll;
    float Movement, Exposure, UpdraftTime, BackdropOnly;
    float LowSustain, HighTransient, CloudResponse, OrbitResponse;
};

Texture3D<float> CloudNoise : register(t0);
SamplerState NoiseSampler
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = WRAP;
    AddressV = WRAP;
    AddressW = WRAP;
};

float Noise3(float3 p)
{
    return CloudNoise.SampleLevel(NoiseSampler, p / 64, 0);
}

float Density(float3 p)
{
    float envelope = 1 - smoothstep(2.2, 4.8, length(p));
    float3 q = p;
    float rising = smoothstep(.8, 3.5, abs(p.x));
    q.y += lerp(WeatherTime * .55, -UpdraftTime, rising) + Breath * .1;
    q.x += .6 * sin(p.y * .65 + p.z * .2 + WeatherTime * .15);
    q.z += WeatherTime * .16;
    q.xz = mul(Rot2(.24 * sin(p.y * .6 + WeatherTime * .12)), q.xz);
    float n = .62 * Noise3(q * .55 + float3(11, 3, 23))
            + .26 * Noise3(q * 1.27 + float3(17, 9, 31))
            + .12 * Noise3(q * 3.1 + float3(5, 29, 13));
    float shape = smoothstep(.38, .67, n);
    float rain = Noise3(float3(q.x * 3.4, q.y * .09, q.z * 3.4));
    return envelope * (shape * shape * 2.5 + .11 * rain * LowSustain);
}

float3 Backdrop(float3 origin, float3 direction)
{
    // Finite, world-fixed sphere: motifs move only with the camera, never with cloud time.
    float projection = dot(origin, direction);
    float distance = -projection + sqrt(max(0, projection * projection + 3600 - dot(origin, origin)));
    float3 p = normalize(origin + distance * direction);
    float longitude = atan2(p.z, p.x);
    float latitude = asin(clamp(p.y, -1, 1));
    float poleFade = pow(saturate(1 - p.y * p.y), .7);
    float meridian = exp(-22 * abs(sin(longitude * 10 + .8 * sin(latitude * 8)))) * poleFade;
    float parallel = exp(-28 * abs(sin(latitude * 12 + .35 * sin(longitude * 6) * poleFade)));
    float petals = pow(saturate(.5 + .5 * cos(longitude * 20) * cos(latitude * 16)), 9) * poleFade;
    float3 paper = float3(.007, .012, .026);
    paper += float3(.016, .04, .052) * meridian;
    paper += float3(.052, .026, .014) * parallel;
    paper += float3(.025, .036, .063) * petals;
    return paper * (1 + 11 * Flash + 1.2 * Afterglow);
}

float Phase(float cosine, float g)
{
    return (1 - g * g) / pow(max(.02, 1 + g * g - 2 * g * cosine), 1.5);
}

float4 PS(const PSDefaultin IN) : SV_Target
{
    float2 uv = IN.UV * 2 - 1;
    uv.y = -uv.y;
    uv.x *= AspectRatio;
    uv = mul(Rot2(Roll), uv);
    float radius = 11.6 + .18 * Breath * Movement;
    float3 origin = radius * float3(sin(Yaw) * cos(Pitch), sin(Pitch), -cos(Yaw) * cos(Pitch));
    float3 forward = normalize(-origin);
    float3 right = normalize(cross(float3(0, 1, 0), forward));
    float3 up = cross(forward, right);
    float3 direction = normalize(forward + .64 * (right * uv.x + up * uv.y));
    float3 sky = Backdrop(origin, direction);
    if (BackdropOnly > .5) return float4(sky * Exposure, 1);
    float projection = dot(origin, direction);
    float discriminant = projection * projection - dot(origin, origin) + 29.16;
    if (discriminant <= 0) return float4(sky * Exposure, 1);
    float start = max(0, -projection - sqrt(discriminant));
    float end = -projection + sqrt(discriminant);
    if (end <= start) return float4(sky * Exposure, 1);
    float3 sunDirection = normalize(float3(-.5, .48, .9));
    float3 thunderPosition = float3(2.0 * sin(WeatherTime * .17), 1.5, .8);
    float phase = Phase(dot(direction, sunDirection), .38);
    float3 color = 0;
    float transmission = 1;
    const int steps = 64;
    float stride = (end - start) / steps;
    [loop]
    for (int i = 0; i < steps; ++i)
    {
        float3 p = origin + direction * (start + (i + .5) * stride);
        float density = Density(p);
        if (density > .003)
        {
            float sunShadow = exp(-2.2 * (Density(p + sunDirection * 1.2) + Density(p + sunDirection * 3.1)));
            float3 toThunder = thunderPosition - p;
            float lightDistance = length(toThunder);
            float thunderShadow = exp(-Density(p + .5 * toThunder) * lightDistance * .5);
            float internalLight = (Flash * 18 + Afterglow * .8) * thunderShadow / (1 + .22 * lightDistance * lightDistance);
            float sheet = exp(-abs(dot(p - thunderPosition, normalize(float3(.5, .1, .7)))) * 1.4);
            float3 light = float3(.012, .018, .038)
                         + float3(1.0, .5, .18) * sunShadow * (.18 + .06 * phase)
                         + float3(.3, .65, 1.25) * internalLight * (1 + .8 * sheet)
                         + float3(.32, .1, .025) * Bass * .15;
            float synthPlane = exp(-abs(dot(p - float3(0, 1.5, 0),
                normalize(float3(sin(WeatherTime * .13), .18, cos(WeatherTime * .13))))) * 2.2);
            light += float3(.04, .12, .15) * Body * synthPlane * sunShadow;
            float absorption = 1 - exp(-density * stride * .85);
            color += transmission * absorption * light;
            transmission *= 1 - absorption;
            if (transmission < .008) break;
        }
    }
    color += transmission * sky;
    return float4(color * Exposure, 1);
}

technique11 t0
{
    pass p0
    {
        SetRasterizerState(RasterizerDefault);
        SetDepthStencilState(DepthStencilNone, 0);
        SetBlendState(BlendNone, float4(0, 0, 0, 0), 0xFFFFFFFF);
        SetVertexShader(CompileShader(vs_4_0, VSDefault()));
        SetGeometryShader(0);
        SetPixelShader(CompileShader(ps_5_0, PS()));
    }
}
