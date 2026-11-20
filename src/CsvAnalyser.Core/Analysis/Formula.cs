using System.Globalization;
using System.Text;

namespace CsvAnalyser.Core.Analysis;

/// <summary>A formula error, with the position (0-based) of the faulty text.</summary>
public sealed class FormulaException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>
/// A formula computed row by row over curves, e.g. <c>[Courant] * [Tension] / 1000</c> or <c>sqrt(x^2 + y^2)</c>.
/// Curves are written between brackets (nested brackets allowed: <c>[Débit [g/s]]</c>) or as a plain name when it
/// has only letters, digits and <c>_</c>. Decimals use a point; arguments are separated by <c>,</c> or <c>;</c>.
/// Operators: <c>+ - * / ^</c>, comparisons <c>&lt; &lt;= &gt; &gt;= == !=</c> (1 or 0); constants <c>pi</c>, <c>e</c>;
/// functions: see <see cref="FunctionNames"/>.
/// </summary>
public sealed class Formula
{
    private readonly Node _root;

    private Formula(Node root, IReadOnlyList<string> variables)
    {
        _root = root;
        Variables = variables;
    }

    /// <summary>The curve names used by the formula, in order of appearance.</summary>
    public IReadOnlyList<string> Variables { get; }

    public static IReadOnlyList<string> FunctionNames { get; } =
    [
        "abs", "sqrt", "exp", "ln", "log10", "sin", "cos", "tan", "asin", "acos", "atan", "atan2",
        "pow", "min", "max", "round", "floor", "ceil", "sign", "if",
    ];

    public static Formula Parse(string text)
    {
        var parser = new Parser(text);
        var root = parser.ParseAll();
        return new Formula(root, parser.Variables);
    }

    /// <summary>
    /// Computes the formula for every row. <paramref name="resolve"/> gives the values of a curve name
    /// (null = unknown, reported as an error).
    /// </summary>
    public double[] Evaluate(int rowCount, Func<string, double[]?> resolve)
    {
        var arrays = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var name in Variables)
        {
            var values = resolve(name) ?? throw new FormulaException($"Courbe inconnue : « {name} ».", 0);
            arrays[name] = values;
        }
        var compiled = _root.Compile(arrays);
        var result = new double[rowCount];
        Parallel.For(0, rowCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            double v = compiled(i);
            result[i] = double.IsFinite(v) ? v : double.NaN;
        });
        return result;
    }

    // ---------------------------------------------------------------- syntax tree

    private abstract class Node
    {
        public abstract Func<int, double> Compile(IReadOnlyDictionary<string, double[]> arrays);
    }

    private sealed class Constant(double value) : Node
    {
        public override Func<int, double> Compile(IReadOnlyDictionary<string, double[]> arrays) => _ => value;
    }

    private sealed class Variable(string name) : Node
    {
        public override Func<int, double> Compile(IReadOnlyDictionary<string, double[]> arrays)
        {
            var values = arrays[name];
            return i => i < values.Length ? values[i] : double.NaN;
        }
    }

    private sealed class Unary(Func<double, double> op, Node operand) : Node
    {
        public override Func<int, double> Compile(IReadOnlyDictionary<string, double[]> arrays)
        {
            var a = operand.Compile(arrays);
            return i => op(a(i));
        }
    }

    private sealed class Binary(Func<double, double, double> op, Node left, Node right) : Node
    {
        public override Func<int, double> Compile(IReadOnlyDictionary<string, double[]> arrays)
        {
            var a = left.Compile(arrays);
            var b = right.Compile(arrays);
            return i => op(a(i), b(i));
        }
    }

    private sealed class Conditional(Node condition, Node whenTrue, Node whenFalse) : Node
    {
        public override Func<int, double> Compile(IReadOnlyDictionary<string, double[]> arrays)
        {
            var c = condition.Compile(arrays);
            var t = whenTrue.Compile(arrays);
            var f = whenFalse.Compile(arrays);
            return i =>
            {
                double v = c(i);
                return double.IsNaN(v) ? double.NaN : v != 0 ? t(i) : f(i);
            };
        }
    }

    // ---------------------------------------------------------------- parser

    private sealed class Parser(string text)
    {
        private int _pos;

        public List<string> Variables { get; } = [];

        public Node ParseAll()
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormulaException("La formule est vide.", 0);
            var node = ParseComparison();
            SkipSpaces();
            if (_pos < text.Length)
                throw new FormulaException($"Caractère inattendu « {text[_pos]} ».", _pos);
            return node;
        }

        private Node ParseComparison()
        {
            var left = ParseSum();
            while (true)
            {
                SkipSpaces();
                string? op = Peek("<=") ? "<=" : Peek(">=") ? ">=" : Peek("==") ? "==" : Peek("!=") ? "!="
                    : Peek("<") ? "<" : Peek(">") ? ">" : null;
                if (op is null)
                    return left;
                _pos += op.Length;
                var right = ParseSum();
                left = new Binary(op switch
                {
                    "<=" => (a, b) => Bool(a <= b, a, b),
                    ">=" => (a, b) => Bool(a >= b, a, b),
                    "==" => (a, b) => Bool(a == b, a, b),
                    "!=" => (a, b) => Bool(a != b, a, b),
                    "<" => (a, b) => Bool(a < b, a, b),
                    _ => (a, b) => Bool(a > b, a, b),
                }, left, right);
            }
        }

        private static double Bool(bool value, double a, double b) =>
            double.IsNaN(a) || double.IsNaN(b) ? double.NaN : value ? 1 : 0;

        private Node ParseSum()
        {
            var left = ParseProduct();
            while (true)
            {
                SkipSpaces();
                if (Accept('+'))
                    left = new Binary((a, b) => a + b, left, ParseProduct());
                else if (Accept('-'))
                    left = new Binary((a, b) => a - b, left, ParseProduct());
                else
                    return left;
            }
        }

        private Node ParseProduct()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipSpaces();
                if (Accept('*'))
                    left = new Binary((a, b) => a * b, left, ParseUnary());
                else if (Accept('/'))
                    left = new Binary((a, b) => a / b, left, ParseUnary());
                else
                    return left;
            }
        }

        private Node ParseUnary()
        {
            SkipSpaces();
            if (Accept('-'))
                return new Unary(a => -a, ParseUnary());
            if (Accept('+'))
                return ParseUnary();
            return ParsePower();
        }

        private Node ParsePower()
        {
            var bottom = ParsePrimary();
            SkipSpaces();
            if (Accept('^'))
                return new Binary(Math.Pow, bottom, ParseUnary()); // right-associative, -x^2 = -(x^2)
            return bottom;
        }

        private Node ParsePrimary()
        {
            SkipSpaces();
            if (_pos >= text.Length)
                throw new FormulaException("La formule est incomplète.", _pos);

            char c = text[_pos];
            if (c == '(')
            {
                _pos++;
                var inner = ParseComparison();
                Expect(')');
                return inner;
            }
            if (c == '[')
                return ParseBracketVariable();
            if (char.IsDigit(c) || c == '.')
                return ParseNumber();
            if (char.IsLetter(c) || c == '_')
            {
                int start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                    _pos++;
                string name = text[start.._pos];
                SkipSpaces();
                if (_pos < text.Length && text[_pos] == '(')
                    return ParseFunction(name.ToLowerInvariant(), start);
                switch (name.ToLowerInvariant())
                {
                    case "pi":
                        return new Constant(Math.PI);
                    case "e":
                        return new Constant(Math.E);
                }
                return Use(name);
            }
            throw new FormulaException($"Caractère inattendu « {c} ».", _pos);
        }

        private Node ParseBracketVariable()
        {
            int start = _pos;
            _pos++; // [
            int depth = 1;
            var name = new StringBuilder();
            while (_pos < text.Length)
            {
                char c = text[_pos++];
                if (c == '[')
                    depth++;
                else if (c == ']' && --depth == 0)
                    return Use(name.ToString().Trim());
                name.Append(c);
            }
            throw new FormulaException("Crochet « ] » manquant.", start);
        }

        private Node Use(string name)
        {
            if (name.Length == 0)
                throw new FormulaException("Nom de courbe vide.", _pos);
            if (!Variables.Contains(name))
                Variables.Add(name);
            return new Variable(name);
        }

        private Node ParseNumber()
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
                _pos++;
            if (_pos < text.Length && (text[_pos] == 'e' || text[_pos] == 'E')
                && _pos + 1 < text.Length && (char.IsDigit(text[_pos + 1]) || text[_pos + 1] is '-' or '+'))
            {
                _pos += 2;
                while (_pos < text.Length && char.IsDigit(text[_pos]))
                    _pos++;
            }
            string s = text[start.._pos];
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormulaException($"Nombre invalide « {s} » (décimales avec un point).", start);
            return new Constant(value);
        }

        private Node ParseFunction(string name, int start)
        {
            Expect('(');
            var args = new List<Node>();
            SkipSpaces();
            if (!Accept(')'))
            {
                while (true)
                {
                    args.Add(ParseComparison());
                    SkipSpaces();
                    if (Accept(',') || Accept(';'))
                        continue;
                    Expect(')');
                    break;
                }
            }

            Node Arg(int count, Func<Node> make) => args.Count == count
                ? make()
                : throw new FormulaException($"{name}() attend {count} argument(s), {args.Count} donné(s).", start);
            Node One(Func<double, double> f) => Arg(1, () => new Unary(f, args[0]));
            Node Two(Func<double, double, double> f) => Arg(2, () => new Binary(f, args[0], args[1]));

            return name switch
            {
                "abs" => One(Math.Abs),
                "sqrt" => One(Math.Sqrt),
                "exp" => One(Math.Exp),
                "ln" => One(Math.Log),
                "log10" or "log" => One(Math.Log10),
                "sin" => One(Math.Sin),
                "cos" => One(Math.Cos),
                "tan" => One(Math.Tan),
                "asin" => One(Math.Asin),
                "acos" => One(Math.Acos),
                "atan" => One(Math.Atan),
                "atan2" => Two(Math.Atan2),
                "pow" => Two(Math.Pow),
                "min" => Two(Math.Min),
                "max" => Two(Math.Max),
                "round" => One(Math.Round),
                "floor" => One(Math.Floor),
                "ceil" => One(Math.Ceiling),
                "sign" => One(v => double.IsNaN(v) ? double.NaN : Math.Sign(v)),
                "if" => Arg(3, () => new Conditional(args[0], args[1], args[2])),
                _ => throw new FormulaException($"Fonction inconnue : {name}().", start),
            };
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }

        private bool Peek(string s) => string.CompareOrdinal(text, _pos, s, 0, s.Length) == 0;

        private bool Accept(char c)
        {
            SkipSpaces();
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void Expect(char c)
        {
            if (!Accept(c))
                throw new FormulaException($"« {c} » attendu.", _pos);
        }
    }
}
