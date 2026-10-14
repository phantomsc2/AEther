using System;

namespace AEther
{
    /// <summary>Shared .01/.99 quantiles of an exponentially weighted spectrum distribution.</summary>
    public sealed class SpectralRange
    {
        const int MinimumDb = -200, MaximumDb = 20;
        readonly double[] Histogram = new double[MaximumDb - MinimumDb + 1];
        readonly double Decay, Follow, AudibleFloor;
        public double FloorDb { get; private set; }
        public double CeilingDb { get; private set; }
        public double SpanDb => Math.Max(24, CeilingDb - FloorDb);

        public SpectralRange(double historySeconds, double frameSeconds, double audibleFloor)
        {
            if (!double.IsFinite(historySeconds) || historySeconds <= 0
                || !double.IsFinite(frameSeconds) || frameSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(historySeconds));
            AudibleFloor = audibleFloor;
            Decay = Math.Exp(-frameSeconds / historySeconds);
            Follow = 1 - Math.Exp(-frameSeconds / 20);
            FloorDb = audibleFloor;
            CeilingDb = 0;
            // Broad prior avoids aggressive gain during startup or one quiet note.
            var first = Math.Clamp((int)Math.Floor(audibleFloor) - MinimumDb, 0, Histogram.Length - 1);
            for (var i = first; i <= -MinimumDb; ++i)
                Histogram[i] = 1.0 / (-MinimumDb - first + 1);
        }

        public void Process(ReadOnlySpan<double> spectrumDb)
        {
            var audible = false;
            foreach (var value in spectrumDb) audible |= double.IsFinite(value) && value > AudibleFloor;
            if (!audible || spectrumDb.IsEmpty) return;
            for (var i = 0; i < Histogram.Length; ++i) Histogram[i] *= Decay;
            var weight = (1 - Decay) / spectrumDb.Length;
            foreach (var value in spectrumDb)
            {
                var db = double.IsFinite(value) ? Math.Clamp(value, AudibleFloor, MaximumDb) : AudibleFloor;
                var bin = Math.Clamp((int)Math.Round(db) - MinimumDb, 0, Histogram.Length - 1);
                Histogram[bin] += weight;
            }
            double sum = 0, total = 0;
            foreach (var mass in Histogram) total += mass;
            var floor = (double)MinimumDb;
            var ceiling = (double)MaximumDb;
            var foundFloor = false;
            for (var i = 0; i < Histogram.Length; ++i)
            {
                sum += Histogram[i];
                if (!foundFloor && sum >= .01 * total) { floor = i + MinimumDb; foundFloor = true; }
                if (sum >= .99 * total) { ceiling = i + MinimumDb; break; }
            }
            FloorDb += Follow * (floor - FloorDb);
            CeilingDb += Follow * (ceiling - CeilingDb);
        }
    }
}
