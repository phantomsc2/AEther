// Four equal strips, left to right: S contrast, signed T, global B, interaction RMS.
// Negative T is cyan; positive T is orange. Black represents zero in every strip.
float3 GlobalDiagnosticColor(float4 evidence, float strip)
{
    if (strip < 1) return float3(.4, 1, .3) * GlobalAmplitudeResponse(evidence.r, .05);
    if (strip < 2) return (evidence.g < 0 ? float3(.1, .65, 1) : float3(1, .4, .1))
        * GlobalAmplitudeResponse(abs(evidence.g), .02);
    if (strip < 3) return float3(.35, .5, 1) * GlobalAmplitudeResponse(evidence.b, .01);
    return float3(1, .2, .7) * GlobalAmplitudeResponse(evidence.a, .02);
}
