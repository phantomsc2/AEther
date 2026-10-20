using System;
using System.Numerics;

namespace AEther
{

    public interface IDFTFilter
    {

        int Length { get; }

        void Reset();

        Complex GetOutput();

        void Process(double newSample);

        void Process(ReadOnlyMemory<double> samples);

    }

}
