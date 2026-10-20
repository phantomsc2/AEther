using System;

namespace AEther
{
    public class RingBuffer<T>
    {

        public int Length => Data.Length;

        readonly T[] Data;
        int Position;

        public RingBuffer(int size, int? position = default)
            : this(new T[size], position)
        { }

        public RingBuffer(T[] data, int? position = default)
        {
            Data = data;
            Position = position ?? 0;
        }

        public T Add(T value)
        {
            var oldValue = Data[Position];
            Data[Position] = value;
            Position++;
            if (Position == Data.Length)
            {
                Position = 0;
            }
            return oldValue;
        }

        public void Reset() { Array.Clear(Data); Position = 0; }

        public Memory<T> GetMemory(int length)
        {
            var count = Math.Min(Data.Length - Position, length);
            var memory = Data.AsMemory(Position, count);
            Position += count;
            if (Position == Data.Length)
            {
                Position = 0;
            }
            return memory;
        }

    }
}
