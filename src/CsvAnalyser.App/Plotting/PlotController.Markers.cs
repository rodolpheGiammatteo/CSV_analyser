using CsvAnalyser.Core.Analysis;

namespace CsvAnalyser.App.Plotting;

/// <summary>Spaced markers: on a dense curve, a marker about every N pixels instead of one per sample.</summary>
public sealed partial class PlotController
{
    /// <summary>At most this many spaced markers per curve, whatever the zoom.</summary>
    private const int MaxSpacedMarkers = 2000;

    /// <summary>
    /// Before each render: for the curves with spaced markers, a marker every N samples, N chosen from the samples
    /// in the visible X range so they are about <c>MarkerSpacing</c> pixels apart. Counted in samples (rows that are
    /// multiples of N), not in X: markers stay on the same samples while panning, and a round X step would fall on
    /// the same phase of a periodic signal every time.
    /// </summary>
    private void PlaceSpacedMarkers()
    {
        foreach (var s in _series)
        {
            if (s.Spaced is not { IsVisible: true } || !s.XSorted || s.Xs is not { Length: > 0 } xs)
                continue;
            s.SpacedXs.Clear();
            s.SpacedYs.Clear();

            var plot = s.Graph.Plot;
            var range = plot.Axes.Bottom.Range;
            int lo = SortedSearch.LowerBound(xs, range.Min);
            int hi = Math.Min(SortedSearch.UpperBound(xs, range.Max), s.Ys.Length);
            if (hi <= lo)
                continue;
            double width = plot.LastRender.DataRect.Width;
            if (!(width > 1))
                width = 800 * _scale; // first render: the data area is not known yet
            double perMarker = (hi - lo) * s.Item.MarkerSpacing * _scale / width;
            int every = (int)Math.Max(1, Math.Ceiling(perMarker));
            every = Math.Max(every, (hi - lo) / MaxSpacedMarkers + 1);

            for (long i = (lo + every - 1) / every * (long)every; i < hi; i += every)
            {
                double y = s.Ys[i];
                if (!double.IsFinite(y))
                    continue;
                s.SpacedXs.Add(xs[i]);
                s.SpacedYs.Add(y);
            }
        }
    }
}
