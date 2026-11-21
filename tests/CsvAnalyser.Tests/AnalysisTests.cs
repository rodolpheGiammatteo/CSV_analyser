using CsvAnalyser.Core.Analysis;

namespace CsvAnalyser.Tests;

public class FormulaTests
{
    private static double[] Eval(string text, Dictionary<string, double[]> curves, int rows = 3) =>
        Formula.Parse(text).Evaluate(rows, name => curves.TryGetValue(name, out var v) ? v : null);

    [Fact]
    public void Arithmetic_PrecedenceAndPower()
    {
        Assert.Equal([7.0], Eval("1 + 2 * 3", [], rows: 1));
        Assert.Equal([-4.0], Eval("-2^2", [], rows: 1));
        Assert.Equal([512.0], Eval("2^3^2", [], rows: 1));
        Assert.Equal([2.5], Eval("(1 + 4) / 2", [], rows: 1));
    }

    [Fact]
    public void Curves_InBracketsWithNestedBrackets_OrPlainNames()
    {
        var curves = new Dictionary<string, double[]>
        {
            ["Débit [g/s]"] = [1, 2, 3],
            ["U"] = [10, 20, 30],
        };
        var f = Formula.Parse("[Débit [g/s]] * U + 1");
        Assert.Equal(["Débit [g/s]", "U"], f.Variables);
        Assert.Equal([11.0, 41, 91], Eval("[Débit [g/s]] * U + 1", curves));
    }

    [Fact]
    public void Functions_AndArgumentSeparators()
    {
        Assert.Equal([5.0], Eval("sqrt(3^2 + 4^2)", [], rows: 1));
        Assert.Equal([3.0], Eval("max(1; 3)", [], rows: 1));
        Assert.Equal([1.0], Eval("min(1, 3)", [], rows: 1));
        Assert.Equal(Math.PI, Eval("pi", [], rows: 1)[0], 12);
    }

    [Fact]
    public void ComparisonsAndIf()
    {
        var curves = new Dictionary<string, double[]> { ["x"] = [-1, 0, 2] };
        Assert.Equal([0.0, 0, 1], Eval("x > 0", curves));
        Assert.Equal([1.0, 0, 2], Eval("if(x < 0, -x, x)", curves));
    }

    [Fact]
    public void NaNAndDivisionByZero_GiveMissingValues()
    {
        var curves = new Dictionary<string, double[]> { ["x"] = [double.NaN, 0, 2] };
        var r = Eval("1 / x", curves);
        Assert.True(double.IsNaN(r[0]));
        Assert.True(double.IsNaN(r[1]));
        Assert.Equal(0.5, r[2]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("[abc")]
    [InlineData("foo(1)")]
    [InlineData("sqrt(1, 2)")]
    [InlineData("1,5 + 2")]
    public void InvalidFormulas_Throw(string text)
    {
        Assert.Throws<FormulaException>(() => Formula.Parse(text));
    }

    [Fact]
    public void UnknownCurve_Throws()
    {
        Assert.Throws<FormulaException>(() => Eval("[inconnue] + 1", []));
    }
}

public class SignalFiltersTests
{
    private static double[] Range(int n, double step = 1) => Enumerable.Range(0, n).Select(i => i * step).ToArray();

    [Fact]
    public void MovingAverage_CenteredWindow()
    {
        double[] t = Range(5);
        double[] y = [0, 0, 3, 0, 0];
        var r = SignalFilters.MovingAverage(t, y, window: 2); // [t-1, t+1]
        Assert.Equal([0.0, 1, 1, 1, 0], r);
    }

    [Fact]
    public void MovingAverage_SkipsNaN()
    {
        double[] t = Range(3);
        double[] y = [1, double.NaN, 3];
        var r = SignalFilters.MovingAverage(t, y, window: 2);
        Assert.Equal(1, r[0]);
        Assert.True(double.IsNaN(r[1]));
        Assert.Equal(3, r[2]);
    }

    [Fact]
    public void MovingAverage_NoDriftOnLongFileWithLargeOffset()
    {
        // 2 million samples around 1e6, values of very different sizes: the end must match a direct mean.
        int n = 2_000_000;
        double[] t = Range(n);
        double[] y = new double[n];
        for (int i = 0; i < n; i++)
            y[i] = 1e6 + (i % 7) * 0.123456789 + (i % 1000 == 0 ? 5e4 : 0);
        var r = SignalFilters.MovingAverage(t, y, window: 100);
        int last = n - 60;
        double direct = 0;
        int count = 0;
        for (int k = last - 50; k <= last + 50; k++) { direct += y[k] - 1e6; count++; }
        Assert.Equal(1e6 + direct / count, r[last], 1e-9);
    }

    [Fact]
    public void LowPass_KeepsSlowSignal_RemovesFastOne()
    {
        const int n = 20000;
        double[] t = Range(n, 0.001); // 1 kHz, 20 s
        var slow = t.Select(x => Math.Sin(2 * Math.PI * 0.5 * x)).ToArray();
        var fast = t.Select(x => Math.Sin(2 * Math.PI * 200 * x)).ToArray();

        var slowOut = SignalFilters.LowPass(t, slow, cutoff: 5, zeroPhase: true);
        var fastOut = SignalFilters.LowPass(t, fast, cutoff: 5, zeroPhase: true);

        double Amplitude(double[] v) => v.Skip(n / 4).Take(n / 2).Max();
        Assert.InRange(Amplitude(slowOut), 0.95, 1.0);
        Assert.InRange(Amplitude(fastOut), 0, 0.01);
    }

    [Fact]
    public void HighPass_RemovesOffset()
    {
        double[] t = Range(10000, 0.001);
        var y = t.Select(_ => 5.0).ToArray();
        var r = SignalFilters.HighPass(t, y, cutoff: 1, zeroPhase: true);
        Assert.All(r, v => Assert.Equal(0, v, 9));
    }

    [Fact]
    public void DerivativeAndIntegral_OfARamp()
    {
        double[] t = Range(5, 0.5);
        var y = t.Select(x => 3 * x).ToArray();
        Assert.All(SignalFilters.Derivative(t, y), v => Assert.Equal(3, v, 9));
        var integral = SignalFilters.Integral(t, y);
        Assert.Equal(1.5 * 2 * 2, integral[^1], 9); // ∫0..2 3x dx = 6
    }
}

public class SpectrumTests
{
    [Fact]
    public void Sine_PeakAtItsFrequencyWithItsAmplitude()
    {
        const int n = 1024;
        const double fs = 1024, f = 64, amplitude = 2;
        var t = Enumerable.Range(0, n).Select(i => i / fs).ToArray();
        var y = t.Select(x => 1 + amplitude * Math.Sin(2 * Math.PI * f * x)).ToArray();

        var s = Spectrum.Compute(t, y, 0, n, FftWindow.Rectangular, removeMean: true);

        var (peakF, peakA) = s.Peak;
        Assert.Equal(f, peakF, 0);
        Assert.Equal(amplitude, peakA, 1);
        Assert.Equal(fs, s.SampleRate, 6);
    }

    [Fact]
    public void FlatTopWindow_AmplitudeAccurateBetweenBins()
    {
        const int n = 4000;
        const double fs = 1000, f = 123.4, amplitude = 1.5;
        var t = Enumerable.Range(0, n).Select(i => i / fs).ToArray();
        var y = t.Select(x => amplitude * Math.Sin(2 * Math.PI * f * x)).ToArray();

        var s = Spectrum.Compute(t, y, 0, n, FftWindow.FlatTop, removeMean: true);

        var (peakF, peakA) = s.Peak;
        Assert.InRange(peakF, f - 0.5, f + 0.5);
        Assert.InRange(peakA, amplitude * 0.98, amplitude * 1.02);
    }

    [Fact]
    public void Peaks_FindsEachSineOnce_HighestFirst()
    {
        const int n = 2048;
        const double fs = 1000;
        var t = Enumerable.Range(0, n).Select(i => i / fs).ToArray();
        (double F, double A)[] sines = [(50, 1), (120, 3), (200, 0.5), (310, 2), (420, 0.8), (450, 0.1)];
        var y = t.Select(x => sines.Sum(s => s.A * Math.Sin(2 * Math.PI * s.F * x))).ToArray();

        var s = Spectrum.Compute(t, y, 0, n, FftWindow.Hann, removeMean: true);
        var peaks = s.Peaks(5);

        Assert.Equal(5, peaks.Count);
        double[] expected = [120, 310, 50, 420, 200];
        double[] amplitudes = [3, 2, 1, 0.8, 0.5];
        for (int i = 0; i < 5; i++)
        {
            Assert.InRange(peaks[i].Frequency, expected[i] - 1, expected[i] + 1);
            // Hann loses at most ~15 % between two bins; high frequencies must not be smoothed away.
            Assert.InRange(peaks[i].Amplitude, amplitudes[i] * 0.84, amplitudes[i] * 1.01);
        }
    }

    [Fact]
    public void Peaks_PureSine_OnlyOnePeak()
    {
        const int n = 10_000;
        const double fs = 1000;
        var t = Enumerable.Range(0, n).Select(i => i / fs).ToArray();
        var y = t.Select(x => 0.5 * Math.Sin(2 * Math.PI * 25 * x)).ToArray();

        var peaks = Spectrum.Compute(t, y, 0, n, FftWindow.Hann, removeMean: true).Peaks(5);

        var peak = Assert.Single(peaks); // the window leakage around it is not listed
        Assert.InRange(peak.Frequency, 24.9, 25.1);
    }

    /// <summary>
    /// A square wave ±1 at 10 Hz has only odd harmonics, of amplitude 4/(π·n): 1.273, 0.424, 0.255, 0.182, 0.141
    /// at 10, 30, 50, 70, 90 Hz, and nothing at the even ones.
    /// </summary>
    [Theory]
    [InlineData(FftWindow.FlatTop, 0.02)]
    [InlineData(FftWindow.Hann, 0.16)]
    public void SquareWave_OddHarmonicsAtFourOverPiN(FftWindow window, double tolerance)
    {
        const int n = 10_000; // 10 s at 1 kHz, 100 samples per period
        const double fs = 1000;
        var t = Enumerable.Range(0, n).Select(i => i / fs).ToArray();
        var y = Enumerable.Range(0, n).Select(i => i % 100 < 50 ? 1.0 : -1.0).ToArray();

        var s = Spectrum.Compute(t, y, 0, n, window, removeMean: true);
        var peaks = s.Peaks(5);

        Assert.Equal(5, peaks.Count);
        for (int k = 0; k < 5; k++)
        {
            int harmonic = 2 * k + 1;
            Assert.InRange(peaks[k].Frequency, 10 * harmonic - 0.2, 10 * harmonic + 0.2);
            double expected = 4 / (Math.PI * harmonic);
            Assert.InRange(peaks[k].Amplitude, expected * (1 - tolerance), expected * (1 + tolerance));
        }
        // Even harmonics: nothing (well under 1 % of the fundamental).
        double At(double f) => s.Amplitudes[(int)Math.Round(f / s.Frequencies[1])];
        foreach (double even in new[] { 20.0, 40.0, 60.0 })
            Assert.True(At(even) < 0.01 * peaks[0].Amplitude, $"{even} Hz: {At(even)}");
    }

    [Fact]
    public void TooFewPoints_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Spectrum.Compute([0, 1], [1, 2], 0, 2, FftWindow.Hann, true));
    }
}
