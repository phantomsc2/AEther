using System;

namespace AEther;

/// <summary>Experimental mean-only coupling. No cross-bin covariance is retained.</summary>
internal sealed class AmplitudeCurvatureCoupling
{
    readonly double h, stiffness, damping;
    readonly double[] left, center, right, diagonal, lower1, lower2, rhs, scale;
    readonly bool[] correctionRows;

    public AmplitudeCurvatureCoupling(ReadOnlySpan<double> frequencies, double dt,
        double octaves, double seconds, double dampingRatio)
    {
        h = dt; stiffness = 1 / (seconds * seconds); damping = 2 * dampingRatio / seconds;
        int n = frequencies.Length;
        left = new double[n]; center = new double[n]; right = new double[n];
        diagonal = new double[n]; lower1 = new double[n]; lower2 = new double[n]; rhs = new double[n];
        scale = new double[n]; correctionRows = new bool[n];
        for (int j = 1; j < n - 1; j++)
        {
            double l = Math.Log2(frequencies[j]) - Math.Log2(frequencies[j - 1]);
            double r = Math.Log2(frequencies[j + 1]) - Math.Log2(frequencies[j]);
            // Quantized DFT carriers can coincide. Omit that curvature row rather
            // than divide by zero or bridge a degenerate/reversed frequency interval.
            if (l < 1e-6 || r < 1e-6) continue;
            double scale = 2 * octaves * octaves / (l + r);
            left[j] = scale / l; right[j] = scale / r;
            center[j] = -left[j] - right[j];
        }
    }

    /// <summary>Backward Euler: (I+(h*d+h*h*k)*B)*vNext=v-h*k*B*a;
    /// aNext=a+h*vNext, B=C' C. Inactive/currently invalid bins break the stencil.
    /// O(n) work and storage, no per-tick allocations.</summary>
    public void Predict(Span<double> a, Span<double> v, ReadOnlySpan<bool> active)
    {
        Array.Fill(diagonal, 1); Array.Clear(lower1); Array.Clear(lower2);
        v.CopyTo(rhs);
        double beta = h * damping + h * h * stiffness;
        for (int j = 1; j < a.Length - 1; j++)
        {
            if (!active[j - 1] || !active[j] || !active[j + 1]) continue;
            double l = left[j], c = center[j], r = right[j];
            // Difference form preserves constant levels without subtracting large
            // nearly equal products. Nonuniform rows also annihilate log-frequency ramps.
            double curvature = l * (a[j - 1] - a[j]) + r * (a[j + 1] - a[j]);
            double force = h * stiffness * curvature;
            rhs[j - 1] -= l * force; rhs[j] -= c * force; rhs[j + 1] -= r * force;
            diagonal[j - 1] += beta * l * l;
            diagonal[j] += beta * c * c;
            diagonal[j + 1] += beta * r * r;
            lower1[j] += beta * l * c;
            lower1[j + 1] += beta * c * r;
            lower2[j + 1] += beta * l * r;
        }

        // Banded Cholesky, forward and backward substitutions. If pathological
        // spacing/settings lose positive definiteness in roundoff, keep this tick
        // independent. Do not let a failed solve poison neighboring states.
        bool solved = Solve();
        if (solved)
            for (int j = 0; j < a.Length; j++)
                if (!double.IsFinite(rhs[j]) || !double.IsFinite(a[j] + h * rhs[j]))
                { solved = false; break; }
        for (int j = 0; j < a.Length; j++)
        {
            if (solved) v[j] = rhs[j];
            a[j] += h * v[j];
        }
    }

    /// <summary>Minimize sum((a-aLocal)^2 / variance) + precision*||C*a||^2.
    /// Solve in curvature-observation space, preserving the local estimate
    /// if the solve fails. Conditional local velocity shifts with amplitude;
    /// covariance is deliberately left unchanged in this mean-only prototype.</summary>
    public void Correct(Span<double> a, Span<double> v, ReadOnlySpan<double> variance,
        ReadOnlySpan<double> velocityPerAmplitude, ReadOnlySpan<bool> active, double precision)
    {
        Array.Fill(diagonal, 1); Array.Clear(lower1); Array.Clear(lower2); Array.Clear(rhs);
        Array.Clear(correctionRows);
        // Equivalent to (P^-1 + precision*C' C)*aNew=P^-1*aLocal,
        // but avoids inverse variances and the poorly constrained level/slope
        // null space of C when the curvature penalty becomes very strong.
        // Solve (I/precision + C*P*C')*t=C*a, then deltaA=-P*C'*t.
        for (int j = 1; j < a.Length - 1; j++)
        {
            if (!active[j - 1] || !active[j] || !active[j + 1] || left[j] == 0) continue;
            correctionRows[j] = true;
            double l = left[j], c = center[j], r = right[j];
            rhs[j] = l * (a[j - 1] - a[j]) + r * (a[j + 1] - a[j]);
            diagonal[j] = 1 / precision + l * l * variance[j - 1]
                + c * c * variance[j] + r * r * variance[j + 1];
            if (correctionRows[j - 1])
                lower1[j] = l * center[j - 1] * variance[j - 1] + c * right[j - 1] * variance[j];
            if (j >= 2 && correctionRows[j - 2])
                lower2[j] = l * right[j - 2] * variance[j - 1];
        }
        // Symmetric diagonal scaling improves conditioning when bin confidence
        // varies greatly. Inactive rows are independent unit equations with rhs=0.
        for (int j = 0; j < a.Length; j++)
        {
            scale[j] = Math.Sqrt(diagonal[j]);
            if (!double.IsFinite(scale[j]) || scale[j] <= 0) return;
        }
        for (int j = 0; j < a.Length; j++)
        {
            diagonal[j] = 1; rhs[j] /= scale[j];
            if (j >= 1) lower1[j] = lower1[j] / scale[j] / scale[j - 1];
            if (j >= 2) lower2[j] = lower2[j] / scale[j] / scale[j - 2];
        }
        if (!Solve()) return;
        for (int j = 0; j < a.Length; j++) rhs[j] /= scale[j];
        // Validate the entire result before committing any bin.
        for (int j = 0; j < a.Length; j++)
        {
            double value = center[j] * rhs[j];
            if (j + 1 < a.Length) value += left[j + 1] * rhs[j + 1];
            if (j >= 1) value += right[j - 1] * rhs[j - 1];
            scale[j] = -variance[j] * value;
            if (!double.IsFinite(a[j] + scale[j])
                || !double.IsFinite(v[j] + velocityPerAmplitude[j] * scale[j])) return;
        }
        for (int j = 0; j < a.Length; j++)
        {
            a[j] += scale[j];
            v[j] += velocityPerAmplitude[j] * scale[j];
        }
    }

    bool Solve()
    {
        for (int j = 0; j < diagonal.Length; j++)
        {
            if (j >= 2) lower2[j] /= diagonal[j - 2];
            if (j >= 1)
                lower1[j] = (lower1[j] - lower2[j] * lower1[j - 1]) / diagonal[j - 1];
            double pivot = diagonal[j] - lower1[j] * lower1[j] - lower2[j] * lower2[j];
            if (!double.IsFinite(pivot) || pivot <= 0) return false;
            diagonal[j] = Math.Sqrt(pivot);
            double value = rhs[j];
            if (j >= 1) value -= lower1[j] * rhs[j - 1];
            if (j >= 2) value -= lower2[j] * rhs[j - 2];
            rhs[j] = value / diagonal[j];
        }
        for (int j = diagonal.Length - 1; j >= 0; j--)
        {
            double value = rhs[j];
            if (j + 1 < diagonal.Length) value -= lower1[j + 1] * rhs[j + 1];
            if (j + 2 < diagonal.Length) value -= lower2[j + 2] * rhs[j + 2];
            rhs[j] = value / diagonal[j];
        }
        return true;
    }
}
