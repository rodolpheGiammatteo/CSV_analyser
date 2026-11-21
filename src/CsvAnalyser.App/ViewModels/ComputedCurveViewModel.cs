#pragma warning disable MVVMTK0034 // fields set on purpose without change notification (initial values, suggestions)
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvAnalyser.Core.Analysis;
using CsvAnalyser.Core.Configuration;

namespace CsvAnalyser.App.ViewModels;

/// <summary>The computed-curve window: a formula, or a filter applied to one curve.</summary>
public sealed partial class ComputedCurveViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _nameTouched;
    private bool _unitTouched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormula), nameof(IsFilter), nameof(HasParameter), nameof(HasZeroPhase), nameof(ParameterLabel), nameof(KindHelp))]
    private string _kind = ComputedCurveConfig.Formula;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _unit = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormulaCheck))]
    private string _expression = "";

    [ObservableProperty]
    private ChannelItemViewModel? _source;

    [ObservableProperty]
    private string _parameterText = "";

    [ObservableProperty]
    private bool _zeroPhase = true;

    [ObservableProperty]
    private string? _error;

    public ComputedCurveViewModel(MainViewModel main, ComputedCurveRequest request)
    {
        _main = main;
        Editing = request.Editing;
        Curves = main.Channels.Where(c => c != request.Editing).ToList();

        if (request.Editing?.Definition is { } def)
        {
            _kind = def.Kind;
            _name = def.Name;
            _unit = def.Unit;
            _expression = def.Expression ?? "";
            _source = def.Source is { } s ? main.FindCurve(s) : null;
            _parameterText = def.Parameter is { } p ? p.ToString("G6", CultureInfo.CurrentCulture) : "";
            _zeroPhase = def.ZeroPhase;
            _nameTouched = _unitTouched = true;
        }
        else
        {
            _source = request.Source ?? main.PlottedChannels.FirstOrDefault() ?? Curves.FirstOrDefault();
            if (request.Source is not null)
                _kind = ComputedCurveConfig.MovingAverage;
            _parameterText = DefaultParameter(_kind);
            SuggestNameAndUnit();
        }
    }

    public ChannelItemViewModel? Editing { get; }

    public string Title => Editing is null ? L.T("Nouvelle courbe calculée") : L.T("Modifier la courbe calculée");

    public string OkText => Editing is null ? L.T("Créer") : L.T("Enregistrer");

    public IReadOnlyList<ChannelItemViewModel> Curves { get; }

    public static IReadOnlyList<StyleChoice<string>> KindChoices { get; } =
    [
        new(ComputedCurveConfig.Formula, "Formule"),
        new(ComputedCurveConfig.MovingAverage, "Moyenne glissante"),
        new(ComputedCurveConfig.LowPass, "Filtre passe-bas"),
        new(ComputedCurveConfig.HighPass, "Filtre passe-haut"),
        new(ComputedCurveConfig.Derivative, "Dérivée"),
        new(ComputedCurveConfig.Integral, "Intégrale"),
    ];

    public bool IsFormula => Kind == ComputedCurveConfig.Formula;

    public bool IsFilter => !IsFormula;

    public bool HasParameter => Kind is ComputedCurveConfig.MovingAverage or ComputedCurveConfig.LowPass or ComputedCurveConfig.HighPass;

    public bool HasZeroPhase => Kind is ComputedCurveConfig.LowPass or ComputedCurveConfig.HighPass;

    private string TimeUnit => _main.HasTimeBase ? "s" : L.T("lignes");

    public string ParameterLabel => Kind == ComputedCurveConfig.MovingAverage
        ? L.F("Largeur de la fenêtre ({0})", TimeUnit)
        : _main.HasTimeBase ? L.T("Fréquence de coupure (Hz)") : L.T("Fréquence de coupure (cycles par ligne)");

    public string KindHelp => Kind switch
    {
        ComputedCurveConfig.MovingAverage => L.T("Moyenne des points dans une fenêtre centrée sur chaque point : lisse le bruit sans décaler la courbe."),
        ComputedCurveConfig.LowPass => L.T("Garde les variations lentes, enlève celles plus rapides que la fréquence de coupure (filtre RC du 1er ordre)."),
        ComputedCurveConfig.HighPass => L.T("Garde les variations rapides : la courbe moins sa version filtrée passe-bas (enlève la valeur moyenne et la dérive)."),
        ComputedCurveConfig.Derivative => L.F("Pente de la courbe, par {0}.", TimeUnit),
        ComputedCurveConfig.Integral => L.F("Aire sous la courbe depuis le début du fichier (méthode des trapèzes, en unité × {0}).", TimeUnit),
        _ => "",
    };

    /// <summary>Live check of the formula: an error, or the curves it uses.</summary>
    public string FormulaCheck
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Expression))
                return "";
            try
            {
                var f = Formula.Parse(Expression);
                var unknown = f.Variables.Where(v => _main.FindCurve(v) is null && v is not ("t" or "T")).ToList();
                if (unknown.Count > 0)
                    return L.T("Courbe inconnue : ") + string.Join(", ", unknown.Select(u => $"« {u} »"));
                return f.Variables.Count == 0 ? L.T("Formule correcte (constante).") : L.T("Formule correcte. Courbes : ") + string.Join(", ", f.Variables);
            }
            catch (FormulaException ex)
            {
                return L.F("{0} (position {1})", ex.Message, ex.Position + 1);
            }
        }
    }

    /// <summary>Puts "[name]" of a curve into the formula (the window inserts it at the caret).</summary>
    public static string Reference(ChannelItemViewModel item) => $"[{item.Channel.SourceName}]";

    partial void OnKindChanged(string value)
    {
        ParameterText = DefaultParameter(value);
        SuggestNameAndUnit();
    }

    partial void OnSourceChanged(ChannelItemViewModel? value) => SuggestNameAndUnit();

    partial void OnNameChanged(string value) => _nameTouched = true;

    partial void OnUnitChanged(string value) => _unitTouched = true;

    private string DefaultParameter(string kind) => kind switch
    {
        ComputedCurveConfig.MovingAverage => _main.HasTimeBase ? "1" : "10",
        // In the user's number format: "0,05" could not be read back on an English Windows.
        ComputedCurveConfig.LowPass or ComputedCurveConfig.HighPass => _main.HasTimeBase ? "1" : 0.05.ToString(CultureInfo.CurrentCulture),
        _ => "",
    };

    /// <summary>Suggests a name and unit until the user types their own.</summary>
    private void SuggestNameAndUnit()
    {
        string source = Source?.Name ?? "";
        string unit = Source?.Unit ?? "";
        string t = TimeUnit == "s" ? "s" : "";
        (string name, string u) = Kind switch
        {
            ComputedCurveConfig.MovingAverage => (L.F("{0} moyenne", source), unit),
            ComputedCurveConfig.LowPass => (L.F("{0} passe-bas", source), unit),
            ComputedCurveConfig.HighPass => (L.F("{0} passe-haut", source), unit),
            ComputedCurveConfig.Derivative => ($"d({source})/dt", unit.Length > 0 && t.Length > 0 ? $"{unit}/{t}" : unit),
            ComputedCurveConfig.Integral => ($"∫ {source}", unit.Length > 0 && t.Length > 0 ? $"{unit}·{t}" : unit),
            _ => (L.T("Calcul"), ""),
        };
        if (!_nameTouched || string.IsNullOrWhiteSpace(Name))
        {
            SetProperty(ref _name, UniqueName(name), nameof(Name));
            _nameTouched = false;
        }
        if (!_unitTouched)
        {
            SetProperty(ref _unit, u, nameof(Unit));
            _unitTouched = false;
        }
    }

    private string UniqueName(string name)
    {
        string candidate = name;
        for (int i = 2; _main.Channels.Any(c => c != Editing && (c.Name == candidate || c.Channel.SourceName == candidate)); i++)
            candidate = $"{name} ({i})";
        return candidate;
    }

    /// <summary>The definition, or null with <see cref="Error"/> set.</summary>
    public ComputedCurveConfig? BuildDefinition()
    {
        var def = new ComputedCurveConfig { Kind = Kind, Name = Name.Trim(), Unit = Unit.Trim(), ZeroPhase = ZeroPhase };
        if (IsFormula)
        {
            def.Expression = Expression.Trim();
        }
        else
        {
            if (Source is null)
            {
                Error = L.T("Choisissez la courbe à traiter.");
                return null;
            }
            def.Source = Source.Channel.SourceName;
            if (HasParameter)
            {
                if (!double.TryParse(ParameterText, NumberStyles.Float, CultureInfo.CurrentCulture, out double p)
                    && !double.TryParse(ParameterText, NumberStyles.Float, CultureInfo.InvariantCulture, out p) || !(p > 0))
                {
                    Error = L.F("{0} : entrez un nombre positif.", ParameterLabel);
                    return null;
                }
                def.Parameter = p;
            }
        }
        Error = null;
        return def;
    }
}
