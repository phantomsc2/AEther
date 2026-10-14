using System;

namespace AEther
{
    public sealed record ResponseProfile(double AttackSeconds, double ReleaseSeconds, double InertiaSeconds, double Gain = 1);

    /// <summary>One signal can produce a sharp envelope and a separate inertial response.</summary>
    public sealed class ResponseChannel
    {
        public ResponseProfile Profile { get; }
        public double Envelope { get; private set; }
        public double Inertial { get; private set; }
        public double Velocity { get; private set; }

        public ResponseChannel(ResponseProfile profile)
        {
            if (!double.IsFinite(profile.AttackSeconds) || profile.AttackSeconds < 0
                || !double.IsFinite(profile.ReleaseSeconds) || profile.ReleaseSeconds <= 0
                || !double.IsFinite(profile.InertiaSeconds) || profile.InertiaSeconds < 0
                || !double.IsFinite(profile.Gain) || profile.Gain < 0)
                throw new ArgumentOutOfRangeException(nameof(profile));
            Profile = profile;
        }

        public void Advance(double seconds, double input)
        {
            if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(input))
                throw new ArgumentOutOfRangeException(nameof(seconds));
            var target = Math.Clamp(input * Profile.Gain, 0, 1);
            var time = target > Envelope ? Profile.AttackSeconds : Profile.ReleaseSeconds;
            var previousEnvelope = Envelope;
            Envelope = time == 0 ? target : target + (Envelope - target) * Math.Exp(-seconds / time);
            if (Profile.InertiaSeconds == 0)
            {
                Inertial = Envelope;
                Velocity = 0;
                return;
            }
            // Integrate the exponential envelope and critically damped spring together, not by
            // holding the changing envelope at its end-of-frame value.
            var frequency = 2 / Profile.InertiaSeconds;
            var offset = Inertial - target;
            var momentum = Velocity + frequency * offset;
            var decay = Math.Exp(-frequency * seconds);
            var position = target + (offset + momentum * seconds) * decay;
            var velocity = (Velocity - frequency * momentum * seconds) * decay;
            if (time > 0)
            {
                var delta = frequency - 1 / time;
                var z = delta * seconds;
                double integral, derivative;
                if (Math.Abs(z) < .001)
                {
                    // Stable limiting series, including coincident envelope/spring decay rates.
                    integral = decay * seconds * seconds * (.5 + z / 6 + z * z / 24 + z * z * z / 120);
                    derivative = decay * seconds * (1 + z / 2 + z * z / 6 + z * z * z / 24)
                        - frequency * integral;
                }
                else
                {
                    var envelopeDecay = Math.Exp(-seconds / time);
                    integral = (envelopeDecay - decay * (1 + z)) / (delta * delta);
                    derivative = (envelopeDecay - decay) / delta - frequency * integral;
                }
                var force = frequency * frequency * (previousEnvelope - target);
                position += force * integral;
                velocity += force * derivative;
            }
            Inertial = position;
            Velocity = velocity;
        }
    }
}
