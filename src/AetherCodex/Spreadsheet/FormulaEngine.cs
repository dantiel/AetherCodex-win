using System.Globalization;
using System.Text;

namespace AetherCodex.Spreadsheet;

public sealed class FormulaException : Exception
{
    public FormulaException(string message) : base(message) { }
}

/// <summary>
/// Recursive-descent formula evaluator. Supports arithmetic (+ - * / ^ %), unary
/// minus, string concatenation (&amp;), comparison (= &lt;&gt; &lt; &lt;= &gt; &gt;=), string literals,
/// cell references (A1, $A$1), ranges (A1:B10) as function arguments, and the
/// functions SUM, AVERAGE, MIN, MAX, COUNT, ABS, ROUND, SQRT, CONCAT, PI, IF,
/// AND, OR, NOT. Errors surface as CellValue.OfError("#...") rather than throwing.
/// </summary>
public static class FormulaEngine
{
    public static CellValue Evaluate(string formula, Sheet sheet)
    {
        if (string.IsNullOrWhiteSpace(formula)) return CellValue.Empty;
        var p = new Parser(formula, sheet);
        var node = p.ParseExpression();
        p.Expect(TokenKind.End);
        return node.Eval(sheet);
    }

    private enum TokenKind
    {
        Number, Ident, String,
        Plus, Minus, Star, Slash, Caret, Percent,
        Amp, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        LParen, RParen, Colon, Comma, End
    }

    private readonly struct Token
    {
        public readonly TokenKind Kind;
        public readonly string Text;
        public readonly double Number;
        public Token(TokenKind kind, string text = "", double number = 0)
        {
            Kind = kind; Text = text; Number = number;
        }
    }

    private sealed class Parser
    {
        private readonly string _src;
        private readonly Sheet _sheet;
        private readonly List<Token> _tokens = new();
        private int _pos;

        public Parser(string src, Sheet sheet)
        {
            _src = src;
            _sheet = sheet;
            Tokenize();
        }

        // ── lexing ──
        private void Tokenize()
        {
            int i = 0, n = _src.Length;
            while (i < n)
            {
                char c = _src[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(_src[i + 1])))
                {
                    int start = i;
                    bool dot = false;
                    while (i < n)
                    {
                        char d = _src[i];
                        if (char.IsDigit(d)) { i++; continue; }
                        if (d == '.' && !dot) { dot = true; i++; continue; }
                        break;
                    }
                    if (i < n && (_src[i] == 'e' || _src[i] == 'E'))
                    {
                        int j = i + 1;
                        if (j < n && (_src[j] == '+' || _src[j] == '-')) j++;
                        int k = j;
                        while (k < n && char.IsDigit(_src[k])) k++;
                        if (k > j) i = k;
                    }
                    string text = _src[start..i];
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num);
                    _tokens.Add(new Token(TokenKind.Number, text, num));
                    continue;
                }

                if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(_src[i]) || _src[i] == '_' || _src[i] == '$')) i++;
                    _tokens.Add(new Token(TokenKind.Ident, _src[start..i]));
                    continue;
                }

                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < n)
                    {
                        if (_src[i] == '"')
                        {
                            if (i + 1 < n && _src[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(_src[i]); i++;
                    }
                    _tokens.Add(new Token(TokenKind.String, sb.ToString()));
                    continue;
                }

                switch (c)
                {
                    case '+': _tokens.Add(new Token(TokenKind.Plus)); i++; break;
                    case '-': _tokens.Add(new Token(TokenKind.Minus)); i++; break;
                    case '*': _tokens.Add(new Token(TokenKind.Star)); i++; break;
                    case '/': _tokens.Add(new Token(TokenKind.Slash)); i++; break;
                    case '^': _tokens.Add(new Token(TokenKind.Caret)); i++; break;
                    case '%': _tokens.Add(new Token(TokenKind.Percent)); i++; break;
                    case '&': _tokens.Add(new Token(TokenKind.Amp)); i++; break;
                    case '(': _tokens.Add(new Token(TokenKind.LParen)); i++; break;
                    case ')': _tokens.Add(new Token(TokenKind.RParen)); i++; break;
                    case ':': _tokens.Add(new Token(TokenKind.Colon)); i++; break;
                    case ',': _tokens.Add(new Token(TokenKind.Comma)); i++; break;
                    case '=': _tokens.Add(new Token(TokenKind.Equal)); i++; break;
                    case '<':
                        if (i + 1 < n && _src[i + 1] == '>') { _tokens.Add(new Token(TokenKind.NotEqual)); i += 2; }
                        else if (i + 1 < n && _src[i + 1] == '=') { _tokens.Add(new Token(TokenKind.LessEqual)); i += 2; }
                        else { _tokens.Add(new Token(TokenKind.Less)); i++; }
                        break;
                    case '>':
                        if (i + 1 < n && _src[i + 1] == '=') { _tokens.Add(new Token(TokenKind.GreaterEqual)); i += 2; }
                        else { _tokens.Add(new Token(TokenKind.Greater)); i++; }
                        break;
                    default: throw new FormulaException($"Unexpected character '{c}'");
                }
            }
            _tokens.Add(new Token(TokenKind.End));
        }

        private Token Peek() => _tokens[_pos];
        private Token Next() => _tokens[_pos++];
        private bool At(TokenKind k) => Peek().Kind == k;

        internal void Expect(TokenKind k)
        {
            if (!At(k)) throw new FormulaException("Incomplete formula");
            _pos++;
        }

        // ── AST ──
        internal abstract class Node { public abstract CellValue Eval(Sheet sheet); }

        private sealed class NumberNode : Node
        {
            private readonly double _v;
            public NumberNode(double v) { _v = v; }
            public override CellValue Eval(Sheet sheet) => CellValue.OfNumber(_v);
        }

        private sealed class TextNode : Node
        {
            private readonly string _s;
            public TextNode(string s) { _s = s; }
            public override CellValue Eval(Sheet sheet) => CellValue.OfText(_s);
        }

        private sealed class RefNode : Node
        {
            private readonly int _r, _c;
            public RefNode(int r, int c) { _r = r; _c = c; }
            public override CellValue Eval(Sheet sheet) => sheet.GetValue(_r, _c);
        }

        private sealed class UnaryNode : Node
        {
            private readonly char _op;
            private readonly Node _x;
            public UnaryNode(char op, Node x) { _op = op; _x = x; }
            public override CellValue Eval(Sheet sheet)
            {
                var v = Num(_x.Eval(sheet));
                return CellValue.OfNumber(_op == '-' ? -v : v);
            }
        }

        private sealed class BinaryNode : Node
        {
            private readonly char _op;
            private readonly Node _l, _r;
            public BinaryNode(char op, Node l, Node r) { _op = op; _l = l; _r = r; }
            public override CellValue Eval(Sheet sheet)
            {
                switch (_op)
                {
                    case '&': return CellValue.OfText(Str(_l.Eval(sheet)) + Str(_r.Eval(sheet)));
                    case '=': return CellValue.OfText(Cmp(_l.Eval(sheet), _r.Eval(sheet)) == 0 ? "TRUE" : "FALSE");
                    case '#': return CellValue.OfText(Cmp(_l.Eval(sheet), _r.Eval(sheet)) != 0 ? "TRUE" : "FALSE");
                    case '<': return CellValue.OfText(Cmp(_l.Eval(sheet), _r.Eval(sheet)) < 0 ? "TRUE" : "FALSE");
                    case '>': return CellValue.OfText(Cmp(_l.Eval(sheet), _r.Eval(sheet)) > 0 ? "TRUE" : "FALSE");
                    case '[': return CellValue.OfText(Cmp(_l.Eval(sheet), _r.Eval(sheet)) <= 0 ? "TRUE" : "FALSE");
                    case ']': return CellValue.OfText(Cmp(_l.Eval(sheet), _r.Eval(sheet)) >= 0 ? "TRUE" : "FALSE");
                    default:
                    {
                        double a = Num(_l.Eval(sheet)), b = Num(_r.Eval(sheet));
                        return CellValue.OfNumber(_op switch
                        {
                            '+' => a + b,
                            '-' => a - b,
                            '*' => a * b,
                            '/' => b == 0 ? throw new FormulaException("#DIV/0!") : a / b,
                            '^' => Math.Pow(a, b),
                            '%' => a % b,
                            _ => throw new FormulaException("#ERROR")
                        });
                    }
                }
            }
        }

        private sealed class FuncNode : Node
        {
            private readonly string _name;
            private readonly List<Arg> _args;
            public FuncNode(string name, List<Arg> args) { _name = name; _args = args; }

            public override CellValue Eval(Sheet sheet)
            {
                string fn = _name.ToUpperInvariant();

                // Logical / conditional functions evaluate lazily.
                switch (fn)
                {
                    case "IF":
                    {
                        if (_args.Count < 2 || _args.Count > 3) return CellValue.OfError("#VALUE!");
                        bool cond = ToBool(_args[0].EvalScalar(sheet));
                        return cond ? _args[1].EvalScalar(sheet)
                                    : (_args.Count > 2 ? _args[2].EvalScalar(sheet) : CellValue.OfError("#VALUE!"));
                    }
                    case "AND":
                    {
                        foreach (var a in _args)
                            if (!ToBool(a.EvalScalar(sheet))) return CellValue.OfText("FALSE");
                        return CellValue.OfText("TRUE");
                    }
                    case "OR":
                    {
                        foreach (var a in _args)
                            if (ToBool(a.EvalScalar(sheet))) return CellValue.OfText("TRUE");
                        return CellValue.OfText("FALSE");
                    }
                    case "NOT":
                        return CellValue.OfText(!ToBool(_args.Count > 0 ? _args[0].EvalScalar(sheet) : CellValue.Empty) ? "TRUE" : "FALSE");
                }

                var vals = new List<CellValue>();
                foreach (var a in _args) a.Collect(sheet, vals);

                switch (fn)
                {
                    case "SUM": return CellValue.OfNumber(vals.Where(v => v.IsNumber).Sum(v => v.Number));
                    case "COUNT": return CellValue.OfNumber(vals.Count(v => v.IsNumber));
                    case "COUNTA": return CellValue.OfNumber(vals.Count(v => !v.IsEmpty));
                    case "AVERAGE":
                    {
                        var nums = vals.Where(v => v.IsNumber).Select(v => v.Number).ToList();
                        return nums.Count == 0 ? CellValue.OfError("#DIV/0!") : CellValue.OfNumber(nums.Average());
                    }
                    case "MIN":
                    {
                        var nums = vals.Where(v => v.IsNumber).Select(v => v.Number).ToList();
                        return nums.Count == 0 ? CellValue.OfError("#VALUE!") : CellValue.OfNumber(nums.Min());
                    }
                    case "MAX":
                    {
                        var nums = vals.Where(v => v.IsNumber).Select(v => v.Number).ToList();
                        return nums.Count == 0 ? CellValue.OfError("#VALUE!") : CellValue.OfNumber(nums.Max());
                    }
                    case "ABS":
                        return CellValue.OfNumber(Math.Abs(Num(vals.Count > 0 ? vals[0] : CellValue.Empty)));
                    case "ROUND":
                        return CellValue.OfNumber(Math.Round(Num(vals.Count > 0 ? vals[0] : CellValue.Empty),
                            vals.Count > 1 ? (int)Num(vals[1]) : 0));
                    case "INT":
                        return CellValue.OfNumber(Math.Floor(Num(vals.Count > 0 ? vals[0] : CellValue.Empty)));
                    case "POWER":
                        return CellValue.OfNumber(Math.Pow(Num(vals.Count > 0 ? vals[0] : CellValue.Empty),
                            vals.Count > 1 ? Num(vals[1]) : 0));
                    case "SQRT":
                    {
                        double x = Num(vals.Count > 0 ? vals[0] : CellValue.Empty);
                        return x < 0 ? CellValue.OfError("#NUM!") : CellValue.OfNumber(Math.Sqrt(x));
                    }
                    case "CONCAT":
                        return CellValue.OfText(string.Concat(vals.Select(v => v.Display)));
                    case "PI":
                        return CellValue.OfNumber(Math.PI);
                    default:
                        return CellValue.OfError("#NAME?");
                }
            }
        }

        private abstract class Arg
        {
            public abstract void Collect(Sheet sheet, List<CellValue> into);
            public virtual CellValue EvalScalar(Sheet sheet)
            {
                var list = new List<CellValue>();
                Collect(sheet, list);
                return list.Count > 0 ? list[0] : CellValue.Empty;
            }
        }

        private sealed class ScalarArg : Arg
        {
            private readonly Node _n;
            public ScalarArg(Node n) { _n = n; }
            public override void Collect(Sheet sheet, List<CellValue> into) => into.Add(_n.Eval(sheet));
            public override CellValue EvalScalar(Sheet sheet) => _n.Eval(sheet);
        }

        private sealed class RangeArg : Arg
        {
            private readonly int _r1, _c1, _r2, _c2;
            public RangeArg(int r1, int c1, int r2, int c2) { _r1 = r1; _c1 = c1; _r2 = r2; _c2 = c2; }
            public override void Collect(Sheet sheet, List<CellValue> into)
            {
                int rlo = Math.Min(_r1, _r2), rhi = Math.Max(_r1, _r2);
                int clo = Math.Min(_c1, _c2), chi = Math.Max(_c1, _c2);
                for (int r = rlo; r <= rhi; r++)
                    for (int c = clo; c <= chi; c++)
                        into.Add(sheet.GetValue(r, c));
            }
        }

        // ── parsing ──
        internal Node ParseExpression() => ParseComparison();

        private Node ParseComparison()
        {
            var l = ParseConcat();
            while (true)
            {
                char op;
                if (At(TokenKind.Equal)) op = '=';
                else if (At(TokenKind.NotEqual)) op = '#';
                else if (At(TokenKind.Less)) op = '<';
                else if (At(TokenKind.Greater)) op = '>';
                else if (At(TokenKind.LessEqual)) op = '[';
                else if (At(TokenKind.GreaterEqual)) op = ']';
                else break;
                Next();
                l = new BinaryNode(op, l, ParseConcat());
            }
            return l;
        }

        private Node ParseConcat()
        {
            var l = ParseAddSub();
            while (At(TokenKind.Amp))
            {
                Next();
                l = new BinaryNode('&', l, ParseAddSub());
            }
            return l;
        }

        private Node ParseAddSub()
        {
            var l = ParseMulDiv();
            while (At(TokenKind.Plus) || At(TokenKind.Minus))
            {
                char op = Next().Kind == TokenKind.Plus ? '+' : '-';
                l = new BinaryNode(op, l, ParseMulDiv());
            }
            return l;
        }

        private Node ParseMulDiv()
        {
            var l = ParsePower();
            while (At(TokenKind.Star) || At(TokenKind.Slash) || At(TokenKind.Percent))
            {
                char op = Next().Kind switch { TokenKind.Star => '*', TokenKind.Slash => '/', _ => '%' };
                l = new BinaryNode(op, l, ParsePower());
            }
            return l;
        }

        private Node ParsePower()
        {
            var l = ParseUnary();
            if (At(TokenKind.Caret)) { Next(); l = new BinaryNode('^', l, ParsePower()); }
            return l;
        }

        private Node ParseUnary()
        {
            if (At(TokenKind.Minus) || At(TokenKind.Plus))
            {
                char op = Next().Kind == TokenKind.Minus ? '-' : '+';
                return new UnaryNode(op, ParseUnary());
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var t = Peek();
            switch (t.Kind)
            {
                case TokenKind.Number: Next(); return new NumberNode(t.Number);
                case TokenKind.String: Next(); return new TextNode(t.Text);
                case TokenKind.LParen:
                {
                    Next(); var e = ParseExpression(); Expect(TokenKind.RParen); return e;
                }
                case TokenKind.Ident:
                {
                    Next();
                    if (At(TokenKind.LParen)) return ParseFunction(t.Text);
                    if (SheetRef.TryParse(t.Text, out int r, out int c)) return new RefNode(r, c);
                    throw new FormulaException($"Unknown name '{t.Text}'");
                }
                default:
                    throw new FormulaException("Unexpected token in formula");
            }
        }

        private Node ParseFunction(string name)
        {
            Expect(TokenKind.LParen);
            var args = new List<Arg>();
            if (!At(TokenKind.RParen))
            {
                args.Add(ParseArg());
                while (At(TokenKind.Comma)) { Next(); args.Add(ParseArg()); }
            }
            Expect(TokenKind.RParen);
            return new FuncNode(name, args);
        }

        private Arg ParseArg()
        {
            if (At(TokenKind.Ident))
            {
                var t = Peek().Text;
                if (SheetRef.TryParse(t, out int r1, out int c1))
                {
                    int save = _pos;
                    Next();
                    if (At(TokenKind.Colon))
                    {
                        Next();
                        var t2 = Peek();
                        if (t2.Kind != TokenKind.Ident || !SheetRef.TryParse(t2.Text, out int r2, out int c2))
                            throw new FormulaException("Invalid range");
                        Next();
                        return new RangeArg(r1, c1, r2, c2);
                    }
                    _pos = save; // not a range — fall through to scalar
                }
            }
            return new ScalarArg(ParseExpression());
        }

        // ── coercion ──
        private static double Num(CellValue v)
        {
            if (v.IsNumber) return v.Number;
            if (v.IsEmpty) return 0;
            if (double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            throw new FormulaException("#VALUE!");
        }

        private static string Str(CellValue v) => v.IsEmpty ? "" : v.Display;

        private static bool ToBool(CellValue v)
        {
            if (v.IsNumber) return v.Number != 0;
            if (v.IsEmpty) return false;
            string s = v.Text.Trim();
            if (string.Equals(s, "TRUE", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
            if (string.Equals(s, "FALSE", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0;
        }

        private static int Cmp(CellValue a, CellValue b)
        {
            if (a.IsNumber && b.IsNumber) return a.Number.CompareTo(b.Number);
            if (a.IsEmpty && b.IsEmpty) return 0;
            string sa = a.IsEmpty ? "" : a.Display;
            string sb = b.IsEmpty ? "" : b.Display;
            if (double.TryParse(sa, NumberStyles.Float, CultureInfo.InvariantCulture, out double na) &&
                double.TryParse(sb, NumberStyles.Float, CultureInfo.InvariantCulture, out double nb))
                return na.CompareTo(nb);
            return string.CompareOrdinal(sa, sb);
        }
    }
}
