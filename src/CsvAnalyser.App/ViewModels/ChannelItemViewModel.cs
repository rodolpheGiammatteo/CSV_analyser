using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvAnalyser.Core.Data;

namespace CsvAnalyser.App.ViewModels;

public partial class ChannelItemViewModel : ObservableObject
{
    private readonly Action<ChannelItemViewModel> _onPlottedChanged;

    [ObservableProperty]
    private bool _isPlotted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    private ScottPlot.Color _color = ScottPlot.Colors.Gray;

    [ObservableProperty]
    private string _cursorText = "";

    public ChannelItemViewModel(Channel channel, Action<ChannelItemViewModel> onPlottedChanged)
    {
        Channel = channel;
        _onPlottedChanged = onPlottedChanged;
    }

    public Channel Channel { get; }

    public string Name => Channel.DisplayName;

    public string Unit => Channel.Unit;

    public string UnitLabel => string.IsNullOrEmpty(Unit) ? "" : $"[{Unit}]";

    public string LegendText => string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]";

    public SolidColorBrush Brush
    {
        get
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));
            brush.Freeze();
            return brush;
        }
    }

    partial void OnIsPlottedChanged(bool value) => _onPlottedChanged(this);
}
