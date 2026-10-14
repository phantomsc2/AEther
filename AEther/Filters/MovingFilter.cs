using System;
using System.Buffers;
using System.Diagnostics;

namespace AEther
{
    public abstract class MovingFilter<T>
    {

        static ArrayPool<T> Pool => ArrayPool<T>.Shared;

        public T State { get; protected set; }

        public MovingFilter(T state)
        {
            State = state;
        }

        public virtual void Clear(T state)
        {
            State = state;
        }

        public abstract void Filter(T value);

        public void FilterSpan(ReadOnlySpan<T> source, Span<T> destination, Func<T, T, T> op)
        {

            if (source.Length > destination.Length)
                throw new ArgumentException("Destination is too small.", nameof(destination));
            if (source.IsEmpty)
                return;

            var buffer = Pool.Rent(source.Length);

            try
            {
                Clear(buffer[0] = source[0]);
                for (var i = 1; i < source.Length; ++i)
                {
                    Filter(source[i]);
                    buffer[i] = State;
                }

                // Pool arrays can exceed the requested length. Never read their padding.
                Clear(source[^1]);
                destination[source.Length - 1] = op(buffer[source.Length - 1], State);
                for (var i = source.Length - 2; 0 <= i; --i)
                {
                    Filter(source[i]);
                    destination[i] = op(buffer[i], State);
                }
            }
            finally { Pool.Return(buffer); }

        }

    }
}
