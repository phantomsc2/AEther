using System;

namespace AEther
{
    /// <summary>Independent signal-response profiles drive light, cloud evolution and orbital momentum.</summary>
    public sealed class StormResponse
    {
        readonly ImpulseMotion Bob = new(3.2);
        readonly ImpulseMotion Turn;
        readonly ResponseChannel Lightning;
        readonly ResponseChannel Glow;
        readonly ResponseChannel Cloud;
        readonly ResponseChannel Build;
        readonly double OrbitInertia;
        double Precession, RollAngle, RollVelocity;
        double PitchAngle = .18;
        double Gain = 4;
        double TargetMovement = 1;

        public StormResponse()
        {
            OrbitInertia = 2.8;
            Turn = new(2 / OrbitInertia);
            Lightning = new(new(0, .09, 0));
            Glow = new(new(0, 1.2, .5));
            Cloud = new(new(.12, 2.8, .9));
            Build = new(new(.35, 5, OrbitInertia));
        }

        public double InputGain
        {
            get => Gain;
            set
            {
                if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
                Gain = value;
            }
        }
        public double CameraMovement
        {
            get => TargetMovement;
            set
            {
                if (!double.IsFinite(value) || value < 0 || value > 2) throw new ArgumentOutOfRangeException(nameof(value));
                TargetMovement = value;
            }
        }
        public double MovementScale { get; private set; } = 1;
        public double Energy { get; private set; }
        public double Bass { get; private set; }
        public double Body { get; private set; }
        public double Air { get; private set; }
        public double LowSustain { get; private set; }
        public double HighTransient { get; private set; }
        public double Flash => Lightning.Envelope;
        public double Afterglow => Glow.Envelope;
        public double CloudResponse => Cloud.Inertial;
        public double OrbitResponse => Build.Inertial;
        public double WeatherTime { get; private set; }
        public double UpdraftTime { get; private set; }
        public double Distance { get; private set; }
        public double Breath => Bob.Position;
        public double Yaw => .13 * Distance;
        public double Pitch => PitchAngle;
        public double Roll => RollAngle;
        public double Speed => 2.8 * Build.Inertial + .25 * Math.Max(0, Bob.Velocity);

        static double Level(double amplitude, double gain)
            => 1 - Math.Exp(-Math.Clamp(amplitude, 0, 4) * gain);

        static double Follow(double state, double target, double seconds, double attack, double release)
            => target + (state - target) * Math.Exp(-seconds / (target > state ? attack : release));

        public void Advance(double seconds, AudioFeatures features, double transient, SpectralControls? separated = null)
        {
            if (!double.IsFinite(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            MovementScale = Follow(MovementScale, TargetMovement, seconds, .4, .4);
            Energy = Follow(Energy, Level(features.Level, InputGain), seconds, .035, 1.35);
            var low = separated?.LowSustain ?? features.Bass;
            var high = separated?.HighTransient ?? transient;
            LowSustain = Follow(LowSustain, Level(low, InputGain * 1.4), seconds, .1, 1.8);
            HighTransient = Math.Clamp(high * InputGain * 1.6, 0, 1);
            Bass = Follow(Bass, Level(Math.Max(features.Bass * .4, low), InputGain * 1.4), seconds, .08, 1.2);
            Body = Follow(Body, Level(features.Body, InputGain), seconds, .06, .75);
            Air = Follow(Air, Level(features.Air, InputGain * 1.5), seconds, .035, .65);
            var flashInput = separated.HasValue ? Math.Max(HighTransient, transient * InputGain * .3) : HighTransient;
            Lightning.Advance(seconds, flashInput);
            Glow.Advance(seconds, flashInput);
            Cloud.Advance(seconds, Math.Max(Energy * .65 + LowSustain * .35, flashInput * .6));
            Build.Advance(seconds, Energy * .55 + LowSustain * .45);
            if (transient > .02)
            {
                var impulse = Math.Clamp(transient * InputGain * 1.6, 0, 1);
                Bob.AddImpulse(.4 * impulse);
                Turn.AddImpulse(.18 * impulse * (Math.Sin(Precession) + .35 * features.Stereo));
            }
            Bob.Advance(seconds);
            Turn.Advance(seconds);
            // Precession changes the orbit plane continuously; no random direction changes or positional reset.
            Precession += seconds * (.035 + .12 * Build.Inertial) * MovementScale;
            var pitchTarget = .18 + .32 * Build.Inertial * Math.Sin(Precession);
            PitchAngle = Follow(PitchAngle, pitchTarget, seconds * MovementScale, OrbitInertia, OrbitInertia);
            var rollTarget = .36 * Build.Inertial * Build.Inertial
                + .045 * Math.Sin(Precession * .71) * Build.Inertial;
            RollVelocity = Follow(RollVelocity, rollTarget, seconds, OrbitInertia, OrbitInertia);
            RollAngle += seconds * RollVelocity * MovementScale;
            WeatherTime += seconds * (.08 + .28 * Cloud.Inertial + .15 * Bass);
            UpdraftTime += seconds * (.1 + .3 * Cloud.Inertial + .35 * Air);
            Distance += seconds * (Speed + .1 * Turn.Position) * MovementScale;
        }
    }
}
