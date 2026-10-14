using System;

namespace AEther
{
    /// <summary>A continuous history playhead in analysis-row units, independent of packet arrival cadence.</summary>
    public sealed class HistoryScroll
    {
        readonly double RowsPerSecond;
        readonly double DelayRows;
        long ReceivedRows;
        bool Started;
        public double Position { get; private set; }

        public HistoryScroll(double rowsPerSecond, double presentationDelay = 0)
        {
            if (!double.IsFinite(rowsPerSecond) || rowsPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(rowsPerSecond));
            if (!double.IsFinite(presentationDelay) || presentationDelay < 0)
                throw new ArgumentOutOfRangeException(nameof(presentationDelay));
            RowsPerSecond = rowsPerSecond;
            DelayRows = rowsPerSecond * presentationDelay;
        }

        public void AddRow() => ReceivedRows++;

        public void Advance(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (ReceivedRows == 0)
                return;
            if (!Started)
            {
                Position = ReceivedRows - 1 - DelayRows;
                Started = true;
            }
            else
                Position = Math.Min(ReceivedRows - 1, Position + RowsPerSecond * seconds);
        }

        public double TextureCoordinate(int height)
        {
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            // Row centers, not the next unwritten row. Wrap remains continuous across the ring boundary.
            var coordinate = (Position + .5) / height;
            return coordinate - Math.Floor(coordinate);
        }

        public double AvailableSpan(int height)
        {
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            return Math.Clamp(1 - (ReceivedRows - 1 - Position) / height, 0, 1);
        }
    }
}
