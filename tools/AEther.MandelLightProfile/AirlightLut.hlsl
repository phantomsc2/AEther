// Experimental entry point replacements, appended to the expanded v4 source by
// --airlight-lut. Production scene shaders and host transport are unchanged.
// StudyConstants and resource registers are supplied by the profiling harness.

float3 StudyTail(Source source, float t, float angle, bool infinity, float3 extinction, float lutX)
{
    if (infinity) return 0;
    uint width,height; StudyLut.GetDimensions(width,height);
    // Cluster angular samples near +/- pi/2 so almost-collinear lights behind
    // the camera do not interpolate across the entire slowly decaying tail.
    float angleCoordinate=.5+.5*sign(angle)*(1-sqrt(max(0,1-2*abs(angle)/Pi)));
    float2 uv=(float2(lutX,angleCoordinate)*float2(width-1,height-1)+.5)/float2(width,height);
    float3 tail=StudyLut.SampleLevel(StudySampler,uv,0).rgb;
    if (StudyScaled==0) return tail;
    float x=t-source.Center;
    float r=sqrt(x*x+source.Perpendicular2);
    return tail*exp(-extinction*(t+r));
}

float4 IntegrateAirlight(int2 pixel)
{
    if (StudyMode==0) return SampledAirlight(pixel);
    float3 origin,forward,right,up; CameraFrame(origin,forward,right,up);
    float2 screen=(2*(pixel+.5)/Resolution-1)*float2(max(1,AspectRatio),-max(1,1/AspectRatio));
    float3 direction=normalize(2*forward+screen.x*right+screen.y*up);
    float z=RasterDepth.Load(int3(pixel,0)); bool hit=z<1;
    float end=hit ? LinearDepth(z)/dot(direction,forward) : 0;
    float guide=hit ? LinearDepth(z) : 0;
    float3 scatter=AirScatter(),extinction=AirExtinction(),radiance=0;
    int count=(int)RasterLightInfo.y;
    if (count==0 || !any(scatter>0)) return float4(0,0,0,guide);
    [loop] for (int slot=0;slot<count;++slot)
    {
        int i=ActiveLight(slot);
        Source source=DescribeLight(i,origin,direction,0,hit ? end : 1e30);
        // The study covers b <= 128, wider than this scene's camera/light bounds.
        // Do not silently clamp an unsupported ray to the edge of the table.
        if (source.Perpendicular2>StudyBmax*StudyBmax) return SampledAirlight(pixel);
        if (StudyMode==3)
        {
            // Matched numerical control: exactly the same angular bins and PCF
            // sample positions as mode 2, but midpoint quadrature for the weight.
            float3 sum=0;
            [loop] for (int s=0;s<StudySamples;++s)
            {
                float t=max(0,source.Center+source.Height*tan(source.Angle+(s+.5)*source.Span/StudySamples));
                if (hit) t=min(t,end);
                float x=t-source.Center,r2=x*x+source.Perpendicular2;
                if (r2<1e-12) continue;
                float phase=3*(2-source.Perpendicular2/r2)/(16*Pi);
                sum+=phase*exp(-extinction*(t+sqrt(r2)))*Visibility(i,origin+t*direction,0);
            }
            radiance+=scatter*source.Intensity*sum*(source.Span/(source.Height*StudySamples));
            continue;
        }
        float b=sqrt(source.Perpendicular2),h=StudyRadius;
        float lutX=log(b/h+sqrt(1+b*b/(h*h)))/StudyBwarp;
        float3 previous=StudyTail(source,0,source.Angle,false,extinction,lutX),total=0;
        if (StudyMode==1) total=max(0,previous-StudyTail(source,end,source.Angle+source.Span,!hit,extinction,lutX));
        else
        {
            // Piecewise-constant PCF visibility in equiangular bins. Adjacent
            // bins share a tail lookup; this does not discover exact boundaries.
            [loop] for (int s=0;s<StudySamples;++s)
            {
                float u=(s+1.0)/StudySamples;
                float angle=source.Angle+u*source.Span;
                float t=(s==StudySamples-1 && hit) ? end
                    : max(0,source.Center+source.Height*tan(angle));
                float3 next=StudyTail(source,t,angle,s==StudySamples-1 && !hit,extinction,lutX);
                float middle=max(0,source.Center+source.Height*tan(source.Angle+(s+.5)*source.Span/StudySamples));
                if (hit) middle=min(middle,end);
                float visibility=Visibility(i,origin+middle*direction,0);
                total+=visibility*max(0,previous-next);
                previous=next;
            }
        }
        radiance+=scatter*source.Intensity*total*(StudyScaled==0 ? exp(-extinction*source.Center) : 1);
    }
    return float4(radiance,guide);
}
