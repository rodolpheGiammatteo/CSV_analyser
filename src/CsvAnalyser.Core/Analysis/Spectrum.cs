using System.Numerics;

namespace CsvAnalyser.Core.Analysis;

public enum FftWindow
{
    Hann,
    Rectangular,
    FlatTop,
}

/// <summary>Single-sided amplitude spectrum: a sine of amplitude A shows a peak of height ≈ A at its frequency.</summary>
public sealed record SpectrumResult(double[] Frequencies, double[] Amplitudes, double SampleRate, int SampleCount)
{
    /// <summary>Frequency and amplitude of the highest peak above 0 Hz.</summary>
    public (double Frequency, double Amplitude) Peak
    {
        get
        {
            int best = -1;
            for (int i = 1; i < Amplitudes.Length; i++)
            {
                if (best < 0 || Amplitudes[i] > Amplitudes[best])
                    best = i;
            }
            return best < 0 ? (double.NaN, double.NaN) : (Frequencies[best], Amplitudes[best]);
        }
    }
}

public static class Spectrum
{
    /// <summary>Above this, the samples are spread on a coarser grid (the FFT stays fast and small).</summary>
    public const int MaxPoints = 1 << 21;

    /// <summary>
    /// Spectrum of rows <c>[start, end)</c>. The samples are first put on a regular grid (linear interpolation,
    /// step = median step of t), so irregular sampling and short gaps are handled.
    /// </summary>
    public static SpectrumResult Compute(double[] t, double[] y, int start, int end, FftWindow window, bool removeMean)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, Math.Min(t.Length, y.Length));
        var ts = new List<double>();
        var ys = new List<double>();
        for (int i = start; i < end; i++)
        {
            if (double.IsFinite(t[i]) && double.IsFinite(y[i]) && (ts.Count == 0 || t[i] > ts[^1]))
            {
                ts.Add(t[i]);
                ys.Add(y[i]);
            }
        }
        if (ts.Count < 4)
            throw new InvalidOperationException("Pas assez de points pour calculer un spectre (au moins 4).");

        // Regular grid
        var steps = new double[ts.Count - 1];
        for (int i = 1; i < ts.Count; i++)
            steps[i - 1] = ts[i] - ts[i - 1];
        Array.Sort(steps);
        double dt = steps[steps.Length / 2];
        double span = ts[^1] - ts[0];
        int count = (int)Math.Min(MaxPoints, Math.Floor(span / dt) + 1);
        if (count < 4)
            count = 4;
        dt = span / (count - 1);

        var samples = new double[count];
        int j = 0;
        for (int k = 0; k < count; k++)
        {
            double x = ts[0] + k * dt;
            while (j < ts.Count - 2 && ts[j + 1] < x)
                j++;
            double f = (x - ts[j]) / (ts[j + 1] - ts[j]);
            samples[k] = ys[j] + Math.Clamp(f, 0, 1) * (ys[j + 1] - ys[j]);
        }

        if (removeMean)
        {
            double mean = samples.Average();
            for (int k = 0; k < count; k++)
                samples[k] -= mean;
        }

        // Window, zero padding to a power of two, FFT
        int size = 1;
        while (size < count)
            size <<= 1;
        var data = new Complex[size];
        double windowSum = 0;
        for (int k = 0; k < count; k++)
        {
            double w = WindowValue(window, k, count);
            windowSum += w;
            data[k] = new Complex(samples[k] * w, 0);
        }
        Fft(data);

        int bins = size / 2 + 1;
        var frequencies = new double[bins];
        var amplitudes = new double[bins];
        double sampleRate = 1 / dt;
        for (int k = 0; k < bins; k++)
        {
            frequencies[k] = k * sampleRate / size;
            double scale = (k == 0 || k == size / 2 ? 1 : 2) / windowSum;
            amplitudes[k] = data[k].Magnitude * scale;
        }
        return new SpectrumResult(frequencies, amplitudes, sampleRate, count);
    }

    private static double WindowValue(FftWindow window, int k, int n)
    {
        if (n <= 1)
            return 1;
        double x = 2 * Math.PI * k / (n - 1);
        return window switch
        {
            FftWindow.Hann => 0.5 - 0.5 * Math.Cos(x),
            FftWindow.FlatTop => 0.21557895 - 0.41663158 * Math.Cos(x) + 0.277263158 * Math.Cos(2 * x)
                                      - 0.083578947 * Math.Cos(3 * x) + 0.006947368 * Math.Cos(4 * x),
            _ => 1,
        };
    }

    /// <summary>In-place radix-2 FFT; the length must be a power of two.</summary>
    internal static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }
}
