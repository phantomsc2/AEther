using System.Collections.Generic;

namespace AEther
{

    public class MovingMedianHeap<T> : WindowedFilter<T>
    {

        int Position;

        readonly FixedHeap<T> Below;
        readonly FixedHeap<T> Above;
        readonly Comparer<T> Comparer;
        readonly bool[] Flags;

        public MovingMedianHeap(T state, int windowSize, Comparer<T> comparer)
            : base(state, windowSize)
        {
            Flags = new bool[WindowSize];
            Comparer = comparer;
            Below = new(windowSize, Comparer<T>.Create((x, y) => -Comparer.Compare(x, y)));
            Above = new(windowSize, Comparer);
            Clear(state);
        }

        public override void Clear(T state)
        {
            base.Clear(state);
            Below.Clear();
            Above.Clear();
            Above.Insert((state, 0));
            Flags[0] = true; // the seed belongs to Above, including after every Clear
            Position = WindowSize == 1 ? 0 : 1;
        }

        public override void Filter(T value)
        {

            if (WindowSize <= Size)
            {
                if (Flags[Position])
                {
                    Above.Delete(Position);
                }
                else
                {
                    Below.Delete(Position);
                }
            }

            if (0 < Size)
            {
                Flags[Position] = 0 < Comparer.Compare(value, State);
            }

            if (Flags[Position])
            {
                Above.Insert((value, Position));
            }
            else
            {
                Below.Insert((value, Position));
            }

            if (Below.Size + 1 <= Above.Size - 1)
            {
                Balance(Above, Below);
            }
            else if (Above.Size + 1 <= Below.Size - 1)
            {
                Balance(Below, Above);
            }

            Position += 1;
            if (Position == WindowSize)
            {
                Position = 0;
            }

            State = Below.Size < Above.Size
                ? Above.Root.Item1
                : Below.Root.Item1;

        }

        int Size => Below.Size + Above.Size;

        void Balance(FixedHeap<T> from, FixedHeap<T> to)
        {
            var root = from.Root;
            from.DeleteRoot();
            to.Insert(root);
            Flags[root.Item2] = to == Above;
        }

    }

}
