using System;
using System.Numerics;

namespace AEther
{
    public readonly struct ComplexVector<T>
        where T : struct, IAdditionOperators<T, T, T>
    {

        public static readonly ComplexVector<T> Zero = new(Vector<T>.Zero, Vector<T>.Zero);
        public static readonly ComplexVector<T> One = new(Vector<T>.One, Vector<T>.Zero);
        public static readonly ComplexVector<T> ImaginaryOne = new(Vector<T>.Zero, Vector<T>.One);
        public static int Count => Vector<T>.Count;

        public readonly Vector<T> Real;
        public readonly Vector<T> Imaginary;

        public (T, T) this[int i] => (Real[i], Imaginary[i]);

        public T LengthSquared()
        {
            var re2 = Vector.Dot(Real, Real);
            var im2 = Vector.Dot(Imaginary, Imaginary);
            return re2 + im2;
        }

        public ComplexVector(Vector<T> real, Vector<T> imaginary)
        {
            Real = real;
            Imaginary = imaginary;
        }

        public override string ToString()
            => $"({Real}, {Imaginary}i)";

        public static ComplexVector<T> operator +(ComplexVector<T> a, ComplexVector<T> b)
            => new(a.Real + b.Real, a.Imaginary + b.Imaginary);

        public static ComplexVector<T> operator +(Vector<T> a, ComplexVector<T> b)
            => new(a + b.Real, b.Imaginary);

        public static ComplexVector<T> operator +(ComplexVector<T> a, Vector<T> b)
            => new(a.Real + b, a.Imaginary);

        public static ComplexVector<T> operator -(ComplexVector<T> a, ComplexVector<T> b)
            => new(a.Real - b.Real, a.Imaginary - b.Imaginary);

        public static ComplexVector<T> operator *(ComplexVector<T> a, ComplexVector<T> b)
            => new(a.Real * b.Real - a.Imaginary * b.Imaginary, a.Real * b.Imaginary + a.Imaginary * b.Real);

        public static ComplexVector<T> operator *(Vector<T> a, ComplexVector<T> b)
            => new(a * b.Real, a * b.Imaginary);

        public static ComplexVector<T> operator *(ComplexVector<T> a, Vector<T> b)
            => new(a.Real * b, a.Imaginary * b);

        public static ComplexVector<T> operator *(T a, ComplexVector<T> b)
            => new(a * b.Real, a * b.Imaginary);

        public static ComplexVector<T> operator *(ComplexVector<T> a, T b)
            => new(a.Real * b, a.Imaginary * b);

    }
}
