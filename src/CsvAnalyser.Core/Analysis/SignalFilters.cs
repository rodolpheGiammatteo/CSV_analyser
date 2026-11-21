namespace CsvAnalyser.Core.Analysis;

/// <summary>
/// Filters over a curve sampled at times <c>t</c> (ascending, possibly irregular). Missing values (NaN) stay
/// missing in the result and are skipped by the filters.
/// </summary>
public static class SignalFilters
{
    /// <summary>Centered moving average over a window of <paramref name="window"/> (same unit as t).</summary>
    public static double[] MovingAverage(double[] t, double[] y, double window)
    {
        int n = Math.Min(t.Length, y.Length);
        var result = new double[n];
        double half = Math.Max(0, window) / 2;
        int lo = 0, hi = 0; // window = [lo, hi)
        // The running sum is kept relative to the first value (a large constant part would eat the precision) and
        // compensated (Neumaier): millions of additions and subtractions would otherwise drift along the file.
        double offset = 0;
        for (int k = 0; k < n; k++)
        {
            if (double.IsFinite(y[k])) { offset = y[k]; break; }
        }
        double sum = 0, compensation = 0;
        void Add(double v)
        {
            double next = sum + v;
            compensation += Math.Abs(sum) >= Math.Abs(v) ? (sum - next) + v : (v - next) + sum;
            sum = next;
        }
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            while (hi < n && t[hi] <= t[i] + half)
            {
                if (double.IsFinite(y[hi])) { Add(y[hi] - offset); count++; }
                hi++;
            }
            while (lo < hi && t[lo] < t[i] - half)
            {
                if (double.IsFinite(y[lo])) { Add(-(y[lo] - offset)); count--; }
                lo++;
            }
            result[i] = double.IsFinite(y[i]) && count > 0 ? offset + (sum + compensation) / count : double.NaN;
        }
        return result;
    }

    /// <summary>
    /// First-order low-pass (RC) with cut-off <paramref name="cutoff"/> (cycles per unit of t, Hz for seconds).
    /// <paramref name="zeroPhase"/> runs it forward then backward: no delay, steeper slope.
    /// </summary>
    public static double[] LowPass(double[] t, double[] y, double cutoff, bool zeroPhase)
    {
        if (!(cutoff > 0))
            throw new ArgumentOutOfRangeException(nameof(cutoff), L.T("La fréquence de coupure doit être positive."));
        double rc = 1 / (2 * Math.PI * cutoff);
        int n = Math.Min(t.Length, y.Length);
        var result = new double[n];
        RunRc(t, y, result, rc, forward: true);
        if (zeroPhase)
            RunRc(t, result, result, rc, forward: false);
        return result;
    }

    /// <summary>First-order high-pass: the signal minus its low-pass.</summary>
    public static double[] HighPass(double[] t, double[] y, double cutoff, bool zeroPhase)
    {
        var low = LowPass(t, y, cutoff, zeroPhase);
        for (int i = 0; i < low.Length; i++)
            low[i] = y[i] - low[i];
        return low;
    }

    private static void RunRc(double[] t, double[] input, double[] output, double rc, bool forward)
    {
        int n = output.Length;
        double state = double.NaN, lastT = double.NaN;
        for (int k = 0; k < n; k++)
        {
            int i = forward ? k : n - 1 - k;
            double v = input[i];
            if (!double.IsFinite(v))
            {
                output[i] = double.NaN;
                continue;
            }
            if (double.IsNaN(state))
            {
                state = v;
            }
            else
            {
                double dt = Math.Abs(t[i] - lastT);
                double alpha = dt / (rc + dt);
                state += alpha * (v - state);
            }
            lastT = t[i];
            output[i] = state;
        }
    }

    /// <summary>dy/dt by central differences (one-sided at the ends and next to missing values).</summary>
    public static double[] Derivative(double[] t, double[] y)
    {
        int n = Math.Min(t.Length, y.Length);
        var result = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(y[i]))
            {
                result[i] = double.NaN;
                continue;
            }
            int a = i > 0 && double.IsFinite(y[i - 1]) ? i - 1 : i;
            int b = i < n - 1 && double.IsFinite(y[i + 1]) ? i + 1 : i;
            double dt = t[b] - t[a];
            result[i] = a == b || dt == 0 ? double.NaN : (y[b] - y[a]) / dt;
        }
        return result;
    }

    /// <summary>Cumulative integral (trapezoids) from the first sample; gaps (NaN) add nothing.</summary>
    public static double[] Integral(double[] t, double[] y)
    {
        int n = Math.Min(t.Length, y.Length);
        var result = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && double.IsFinite(y[i]) && double.IsFinite(y[i - 1]))
                sum += (y[i] + y[i - 1]) / 2 * (t[i] - t[i - 1]);
            result[i] = double.IsFinite(y[i]) ? sum : double.NaN;
        }
        return result;
    }
}
