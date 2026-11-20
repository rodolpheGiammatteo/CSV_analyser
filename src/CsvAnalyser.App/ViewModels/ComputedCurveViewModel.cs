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

    public string Title => Editing is null ? "Nouvelle courbe calculée" : "Modifier la courbe calculée";

    public string OkText => Editing is null ? "Créer" : "Enregistrer";

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

    private string TimeUnit => _main.HasTimeBase ? "s" : "lignes";

    public string ParameterLabel => Kind == ComputedCurveConfig.MovingAverage
        ? $"Largeur de la fenêtre ({TimeUnit})"
        : _main.HasTimeBase ? "Fréquence de coupure (Hz)" : "Fréquence de coupure (cycles par ligne)";

    public string KindHelp => Kind switch
    {
        ComputedCurveConfig.MovingAverage => "Moyenne des points dans une fenêtre centrée sur chaque point : lisse le bruit sans décaler la courbe.",
        ComputedCurveConfig.LowPass => "Garde les variations lentes, enlève celles plus rapides que la fréquence de coupure (filtre RC du 1er ordre).",
        ComputedCurveConfig.HighPass => "Garde les variations rapides : la courbe moins sa version filtrée passe-bas (enlève la valeur moyenne et la dérive).",
        ComputedCurveConfig.Derivative => $"Pente de la courbe, par {TimeUnit}.",
        ComputedCurveConfig.Integral => $"Aire sous la courbe depuis le début du fichier (méthode des trapèzes, en unité × {TimeUnit}).",
        _ => "",
    };

    public string FormulaHelp =>
        "Courbes entre crochets, par exemple [Courant] * [Tension] / 1000. Opérateurs + − * / ^, comparaisons < > <= >= == != "
        + "(1 si vrai, 0 sinon), t = temps. Fonctions : " + string.Join(", ", Formula.FunctionNames) + " ; if(condition, si vrai, si faux). "
        + "Décimales avec un point, arguments séparés par , ou ;.";

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
                    return "Courbe inconnue : " + string.Join(", ", unknown.Select(u => $"« {u} »"));
                return f.Variables.Count == 0 ? "Formule correcte (constante)." : "Formule correcte. Courbes : " + string.Join(", ", f.Variables);
            }
            catch (FormulaException ex)
            {
                return $"{ex.Message} (position {ex.Position + 1})";
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
        ComputedCurveConfig.LowPass or ComputedCurveConfig.HighPass => _main.HasTimeBase ? "1" : "0,05",
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
            ComputedCurveConfig.MovingAverage => ($"{source} moyenne", unit),
            ComputedCurveConfig.LowPass => ($"{source} passe-bas", unit),
            ComputedCurveConfig.HighPass => ($"{source} passe-haut", unit),
            ComputedCurveConfig.Derivative => ($"d({source})/dt", unit.Length > 0 && t.Length > 0 ? $"{unit}/{t}" : unit),
            ComputedCurveConfig.Integral => ($"∫ {source}", unit.Length > 0 && t.Length > 0 ? $"{unit}·{t}" : unit),
            _ => ("Calcul", ""),
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
                Error = "Choisissez la courbe à traiter.";
                return null;
            }
            def.Source = Source.Channel.SourceName;
            if (HasParameter)
            {
                if (!double.TryParse(ParameterText, NumberStyles.Float, CultureInfo.CurrentCulture, out double p)
                    && !double.TryParse(ParameterText, NumberStyles.Float, CultureInfo.InvariantCulture, out p) || !(p > 0))
                {
                    Error = $"{ParameterLabel} : entrez un nombre positif.";
                    return null;
                }
                def.Parameter = p;
            }
        }
        Error = null;
        return def;
    }
}
