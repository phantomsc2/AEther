using System;

namespace AEther;

/// <summary>Exact lower median of a sliding window. Indexed heaps replace expired samples
/// in O(log window) time without allocations or rebuilding the sample population.</summary>
internal sealed class SlidingMedian
{
    readonly double[] Values;
    readonly int[] Lower, Upper, Positions;
    readonly bool[] InLower;
    int LowerCount, UpperCount, Count, Next;

    public SlidingMedian(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Values = new double[capacity];
        Lower = new int[capacity];
        Upper = new int[capacity];
        Positions = new int[capacity];
        InLower = new bool[capacity];
    }

    public double Median => Values[Lower[0]];

    public void Add(double value)
    {
        var key = Next;
        if (Count == Values.Length)
        {
            if (InLower[key]) Remove(Lower, ref LowerCount, Positions[key], true);
            else Remove(Upper, ref UpperCount, Positions[key], false);
        }
        else Count++;
        Values[key] = value;
        if (LowerCount > 0 ? value <= Values[Lower[0]] : UpperCount == 0 || value <= Values[Upper[0]])
            Insert(Lower, ref LowerCount, key, true);
        else Insert(Upper, ref UpperCount, key, false);

        if (LowerCount > UpperCount + 1)
        {
            var root = Lower[0];
            Remove(Lower, ref LowerCount, 0, true);
            Insert(Upper, ref UpperCount, root, false);
        }
        else if (UpperCount > LowerCount)
        {
            var root = Upper[0];
            Remove(Upper, ref UpperCount, 0, false);
            Insert(Lower, ref LowerCount, root, true);
        }
        Next = key + 1 == Values.Length ? 0 : key + 1;
    }

    bool Before(int a, int b, bool lower) => lower ? Values[a] > Values[b] : Values[a] < Values[b];

    void Insert(int[] heap, ref int count, int key, bool lower)
    {
        InLower[key] = lower;
        Up(heap, count++, key, lower);
    }

    void Remove(int[] heap, ref int count, int position, bool lower)
    {
        var key = heap[--count];
        if (position == count) return;
        if (position > 0 && Before(key, heap[(position - 1) / 2], lower))
            Up(heap, position, key, lower);
        else
        {
            while (2 * position + 1 < count)
            {
                var child = 2 * position + 1;
                if (child + 1 < count && Before(heap[child + 1], heap[child], lower)) child++;
                if (!Before(heap[child], key, lower)) break;
                heap[position] = heap[child];
                Positions[heap[position]] = position;
                position = child;
            }
            heap[position] = key;
            Positions[key] = position;
        }
    }

    void Up(int[] heap, int position, int key, bool lower)
    {
        while (position > 0)
        {
            var parent = (position - 1) / 2;
            if (!Before(key, heap[parent], lower)) break;
            heap[position] = heap[parent];
            Positions[heap[position]] = position;
            position = parent;
        }
        heap[position] = key;
        Positions[key] = position;
    }
}
