using System;

namespace AEther
{
    /// <summary>A critically damped spring. Attacks change velocity; position remains continuous.</summary>
    public sealed class ImpulseMotion
    {
        readonly double Frequency;
        public double Position { get; private set; }
        public double Velocity { get; private set; }

        public ImpulseMotion(double frequency = 5)
        {
            if (!double.IsFinite(frequency) || frequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(frequency));
            Frequency = frequency;
        }

        public void AddImpulse(double strength)
        {
            if (!double.IsFinite(strength))
                throw new ArgumentOutOfRangeException(nameof(strength));
            Velocity += strength;
        }

        public void Advance(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            var momentum = Velocity + Frequency * Position;
            var decay = Math.Exp(-Frequency * seconds);
            Position = (Position + momentum * seconds) * decay;
            Velocity = (Velocity - Frequency * momentum * seconds) * decay;
        }
    }
}
