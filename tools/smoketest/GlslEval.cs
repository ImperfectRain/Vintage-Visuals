using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// A small evaluator for the subset of GLSL that this mod's composition
    /// steps are written in.
    ///
    /// WHY THIS EXISTS.
    ///
    /// Every one of the seven defects found at runtime was a COMPOSITION
    /// defect: two lines that were each correct, multiplied together into
    /// something that was not. A regex over the source can prove a line is
    /// present. It cannot prove that the line above it cancels the line below
    /// it, and that is precisely what kept happening - a gate multiplied by a
    /// dimmer, an effect multiplied by the complement of its own trigger, a
    /// diffuse term removed with nothing paying it back.
    ///
    /// So the checks that guard against a repeat have to run ARITHMETIC, not
    /// pattern matching. They do it on expressions pulled out of the shipped
    /// .glsl at test time rather than on a C# transcription of them, because a
    /// transcription is a second implementation and the whole class of bug
    /// under test is two implementations disagreeing.
    ///
    /// WHAT IT IS NOT. It is not a GLSL front end. There is no control flow, no
    /// declarations, no preprocessor, no matrices, no integers-as-a-distinct-
    /// type. It evaluates ONE expression against a symbol table. Anything it
    /// cannot parse throws, loudly, naming the token - a check that silently
    /// evaluated something other than what shipped would be worse than no check
    /// at all.
    /// </summary>
    static class GlslEval
    {
        /// <summary>
        /// A GLSL value: always carried as three components, with
        /// <see cref="IsVec"/> recording whether the source said vec3.
        ///
        /// Scalars broadcast, exactly as GLSL does, so `albedo * 0.5` and
        /// `vec3(0.5) * albedo` evaluate identically here as they do on a GPU.
        /// </summary>
        public readonly struct Val
        {
            public readonly double X, Y, Z;
            public readonly bool IsVec;

            public Val(double s) { X = Y = Z = s; IsVec = false; }
            public Val(double x, double y, double z) { X = x; Y = y; Z = z; IsVec = true; }

            public double S => X;
            public Val AsVec() => new Val(X, Y, Z);

            public override string ToString()
                => IsVec ? $"vec3({X:0.#####}, {Y:0.#####}, {Z:0.#####})" : X.ToString("0.#####");
        }

        // -------------------------------------------------------------------
        // Extraction: pull the expression under test straight out of the file
        // -------------------------------------------------------------------

        /// <summary>
        /// The body of the named function, braces excluded.
        ///
        /// Brace-counted rather than regex-matched, because these bodies
        /// contain both nested blocks and comment prose full of punctuation.
        /// </summary>
        public static string FunctionBody(string source, string signatureFragment)
        {
            int at = source.IndexOf(signatureFragment, StringComparison.Ordinal);
            if (at < 0) throw new ArgumentException("no such function: " + signatureFragment);

            int open = source.IndexOf('{', at);
            if (open < 0) throw new ArgumentException("function has no body: " + signatureFragment);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open + 1, i - open - 1);
                }
            }
            throw new ArgumentException("unbalanced braces in: " + signatureFragment);
        }

        /// <summary>
        /// The one statement in <paramref name="source"/> containing every one
        /// of <paramref name="needles"/>, comments stripped, as a single line.
        ///
        /// Statements here routinely span a dozen lines with a paragraph of
        /// comment between each factor, so "the line containing X" is not a
        /// line. Ambiguity is an error rather than a first match: if two
        /// statements qualify, the check does not know which one shipped.
        /// </summary>
        public static string Statement(string source, params string[] needles)
        {
            var hits = Statements(source).Where(s => needles.All(n => s.Contains(n))).ToList();
            if (hits.Count == 0)
                throw new ArgumentException("no statement contains " + string.Join(" + ", needles));
            if (hits.Count > 1)
                throw new ArgumentException(hits.Count + " statements contain " + string.Join(" + ", needles)
                                            + " - narrow the match");
            return hits[0];
        }

        /// <summary>Every top-level-ish statement in a body, comments removed, whitespace collapsed.</summary>
        public static List<string> Statements(string source)
        {
            string bare = StripComments(source);
            var outp = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < bare.Length; i++)
            {
                char c = bare[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if ((c == ';' || c == '{' || c == '}') && depth == 0)
                {
                    string s = Collapse(bare.Substring(start, i - start));
                    if (s.Length > 0) outp.Add(s);
                    start = i + 1;
                }
            }
            string tail = Collapse(bare.Substring(start));
            if (tail.Length > 0) outp.Add(tail);
            return outp;
        }

        /// <summary>Right-hand side of an assignment, initialiser or return.</summary>
        public static string Rhs(string statement)
        {
            string s = Collapse(StripComments(statement)).TrimEnd(';', ' ');
            if (s.StartsWith("return ", StringComparison.Ordinal)) return s.Substring(7).Trim();

            // Compound assignment keeps its operator's meaning at the call
            // site, so only the value is returned here; the caller decides
            // what *= means.
            var m = Regex.Match(s, @"(?:[-+*/]?=)(?!=)");
            if (!m.Success) throw new ArgumentException("not an assignment or return: " + s);
            return s.Substring(m.Index + m.Length).Trim();
        }

        /// <summary>
        /// Replace a call this evaluator cannot execute - anything that reads a
        /// texture, a shadow map or a varying - with a bound identifier.
        ///
        /// The point is to keep the COMPOSITION under test literal. A check that
        /// retyped `clamp(shaded, 0.0, 0.85) * (1.0 - local)` into C# would pass
        /// against its own copy of the line rather than the shipped one; stubbing
        /// only the leaf calls leaves every operator, constant and factor exactly
        /// as it ships, including any factor someone adds later.
        /// </summary>
        public static string StubCall(string expr, string callName, string replacement)
        {
            var pattern = new Regex(@"(?<![A-Za-z_0-9])" + Regex.Escape(callName) + @"\s*\(");
            while (true)
            {
                Match m = pattern.Match(expr);
                if (!m.Success) return expr;

                int at = m.Index;
                int open = expr.IndexOf('(', at);
                int depth = 0, close = -1;
                for (int i = open; i < expr.Length; i++)
                {
                    if (expr[i] == '(') depth++;
                    else if (expr[i] == ')') { depth--; if (depth == 0) { close = i; break; } }
                }
                if (close < 0) throw new ArgumentException("unbalanced call " + callName);
                expr = expr.Substring(0, at) + replacement + expr.Substring(close + 1);
            }
        }

        public static string StripComments(string s)
        {
            s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            s = Regex.Replace(s, @"//[^\n]*", " ");
            return s;
        }

        static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        /// <summary>
        /// The value of a `#define NAME <number>` in the shipped source.
        ///
        /// Read rather than duplicated: a constant copied into a check drifts
        /// from the shader the first time someone retunes it, and the check
        /// then passes against a number nobody ships.
        /// </summary>
        public static double Define(string source, string name)
        {
            var m = Regex.Match(source, @"#define\s+" + Regex.Escape(name) + @"\s+([-0-9.eE+]+)");
            if (!m.Success) throw new ArgumentException("no #define " + name);
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>The value of a `const float NAME = <number>;` in the shipped source.</summary>
        public static double Const(string source, string name)
        {
            var m = Regex.Match(source, @"const\s+float\s+" + Regex.Escape(name) + @"\s*=\s*([-0-9.eE+]+)");
            if (!m.Success) throw new ArgumentException("no const float " + name);
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        // -------------------------------------------------------------------
        // Evaluation
        // -------------------------------------------------------------------

        public static Val Eval(string expr, Dictionary<string, Val> symbols)
        {
            var p = new Parser(StripComments(expr), symbols);
            Val v = p.ParseExpression();
            p.ExpectEnd();
            return v;
        }

        public static double Scalar(string expr, Dictionary<string, Val> symbols)
            => Eval(expr, symbols).S;

        /// <summary>Convenience for building symbol tables inline.</summary>
        public static Dictionary<string, Val> Syms(params object[] pairs)
        {
            var d = new Dictionary<string, Val>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2)
            {
                string k = (string)pairs[i];
                object v = pairs[i + 1];
                d[k] = v is Val val ? val : new Val(Convert.ToDouble(v, CultureInfo.InvariantCulture));
            }
            return d;
        }

        public static Val Vec(double x, double y, double z) => new Val(x, y, z);

        sealed class Parser
        {
            readonly List<string> _tok = new List<string>();
            readonly Dictionary<string, Val> _sym;
            int _i;

            public Parser(string src, Dictionary<string, Val> symbols)
            {
                _sym = symbols;
                foreach (Match m in Regex.Matches(src,
                    @"[A-Za-z_][A-Za-z_0-9]*|[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?|>=|<=|==|!=|&&|\|\||[-+*/(),.?:<>!]"))
                    _tok.Add(m.Value);
            }

            string Peek => _i < _tok.Count ? _tok[_i] : null;
            string Next() => _i < _tok.Count ? _tok[_i++] : null;
            bool Take(string t) { if (Peek == t) { _i++; return true; } return false; }
            void Expect(string t) { if (!Take(t)) throw new ArgumentException("expected '" + t + "' near '" + Peek + "'"); }
            public void ExpectEnd() { if (Peek != null) throw new ArgumentException("trailing tokens from '" + Peek + "'"); }

            // ternary < logical < comparison < additive < multiplicative < unary < primary
            public Val ParseExpression()
            {
                Val cond = ParseLogical();
                if (Take("?"))
                {
                    Val a = ParseExpression();
                    Expect(":");
                    Val b = ParseExpression();
                    return cond.S != 0.0 ? a : b;
                }
                return cond;
            }

            Val ParseLogical()
            {
                Val l = ParseComparison();
                while (Peek == "&&" || Peek == "||")
                {
                    string op = Next();
                    Val r = ParseComparison();
                    bool a = l.S != 0.0, b = r.S != 0.0;
                    l = new Val((op == "&&" ? (a && b) : (a || b)) ? 1.0 : 0.0);
                }
                return l;
            }

            Val ParseComparison()
            {
                Val l = ParseAdditive();
                while (Peek == ">" || Peek == "<" || Peek == ">=" || Peek == "<=" || Peek == "==" || Peek == "!=")
                {
                    string op = Next();
                    Val r = ParseAdditive();
                    bool b;
                    switch (op)
                    {
                        case ">": b = l.S > r.S; break;
                        case "<": b = l.S < r.S; break;
                        case ">=": b = l.S >= r.S; break;
                        case "<=": b = l.S <= r.S; break;
                        case "==": b = l.S == r.S; break;
                        default: b = l.S != r.S; break;
                    }
                    l = new Val(b ? 1.0 : 0.0);
                }
                return l;
            }

            Val ParseAdditive()
            {
                Val l = ParseMultiplicative();
                while (Peek == "+" || Peek == "-")
                {
                    string op = Next();
                    Val r = ParseMultiplicative();
                    l = Combine(l, r, (a, b) => op == "+" ? a + b : a - b);
                }
                return l;
            }

            Val ParseMultiplicative()
            {
                Val l = ParseUnary();
                while (Peek == "*" || Peek == "/")
                {
                    string op = Next();
                    Val r = ParseUnary();
                    l = Combine(l, r, (a, b) => op == "*" ? a * b : a / b);
                }
                return l;
            }

            Val ParseUnary()
            {
                if (Take("-")) { Val v = ParseUnary(); return v.IsVec ? new Val(-v.X, -v.Y, -v.Z) : new Val(-v.S); }
                if (Take("+")) return ParseUnary();
                if (Take("!")) { Val v = ParseUnary(); return new Val(v.S == 0.0 ? 1.0 : 0.0); }
                return ParsePostfix();
            }

            Val ParsePostfix()
            {
                Val v = ParsePrimary();
                while (Take("."))
                {
                    string sw = Next() ?? throw new ArgumentException("dangling swizzle");
                    v = Swizzle(v, sw);
                }
                return v;
            }

            static Val Swizzle(Val v, string sw)
            {
                double Comp(char c)
                {
                    switch (c)
                    {
                        case 'x': case 'r': return v.X;
                        case 'y': case 'g': return v.Y;
                        case 'z': case 'b': return v.Z;
                        default: throw new ArgumentException("unsupported swizzle component '" + c + "'");
                    }
                }
                if (sw.Length == 1) return new Val(Comp(sw[0]));
                if (sw.Length == 3) return new Val(Comp(sw[0]), Comp(sw[1]), Comp(sw[2]));
                throw new ArgumentException("unsupported swizzle '" + sw + "'");
            }

            Val ParsePrimary()
            {
                if (Take("("))
                {
                    Val v = ParseExpression();
                    Expect(")");
                    return v;
                }

                string t = Next() ?? throw new ArgumentException("expression ended early");

                if (char.IsDigit(t[0]) || t[0] == '.')
                    return new Val(double.Parse(t, CultureInfo.InvariantCulture));

                if (Peek == "(") return Call(t);

                if (_sym.TryGetValue(t, out Val bound)) return bound;
                throw new ArgumentException("unbound symbol '" + t + "'");
            }

            Val Call(string name)
            {
                Expect("(");
                var args = new List<Val>();
                if (Peek != ")")
                {
                    do { args.Add(ParseExpression()); } while (Take(","));
                }
                Expect(")");
                return Apply(name, args);
            }

            static Val Apply(string name, List<Val> a)
            {
                switch (name)
                {
                    case "vec3":
                        if (a.Count == 1) return a[0].IsVec ? a[0] : new Val(a[0].S, a[0].S, a[0].S);
                        if (a.Count == 3) return new Val(a[0].S, a[1].S, a[2].S);
                        break;
                    case "float": if (a.Count == 1) return new Val(a[0].S); break;
                    case "abs": return Map(a[0], Math.Abs);
                    case "sqrt": return Map(a[0], Math.Sqrt);
                    case "exp": return Map(a[0], Math.Exp);
                    case "log": return Map(a[0], Math.Log);
                    case "sin": return Map(a[0], Math.Sin);
                    case "cos": return Map(a[0], Math.Cos);
                    case "fract": return Map(a[0], x => x - Math.Floor(x));
                    case "floor": return Map(a[0], Math.Floor);
                    case "max": return Combine(a[0], a[1], Math.Max);
                    case "min": return Combine(a[0], a[1], Math.Min);
                    case "pow": return Combine(a[0], a[1], Math.Pow);
                    case "step": return Combine(a[0], a[1], (e, x) => x < e ? 0.0 : 1.0);
                    case "dot":
                        return new Val(a[0].X * a[1].X + a[0].Y * a[1].Y + a[0].Z * a[1].Z);
                    case "length":
                        return new Val(Math.Sqrt(a[0].X * a[0].X + a[0].Y * a[0].Y + a[0].Z * a[0].Z));
                    case "normalize":
                    {
                        double len = Math.Sqrt(a[0].X * a[0].X + a[0].Y * a[0].Y + a[0].Z * a[0].Z);
                        if (len == 0.0) throw new ArgumentException("normalize(0)");
                        return new Val(a[0].X / len, a[0].Y / len, a[0].Z / len);
                    }
                    case "clamp":
                        return Combine3(a[0], a[1], a[2], (x, lo, hi) => Math.Min(Math.Max(x, lo), hi));
                    case "mix":
                        return Combine3(a[0], a[1], a[2], (x, y, t) => x + (y - x) * t);
                    case "smoothstep":
                        return Combine3(a[0], a[1], a[2], (e0, e1, x) =>
                        {
                            double u = Math.Min(Math.Max((x - e0) / (e1 - e0), 0.0), 1.0);
                            return u * u * (3.0 - 2.0 * u);
                        });
                }
                throw new ArgumentException("unsupported call '" + name + "/" + a.Count + "'");
            }

            static Val Map(Val v, Func<double, double> f)
                => v.IsVec ? new Val(f(v.X), f(v.Y), f(v.Z)) : new Val(f(v.S));

            static Val Combine(Val l, Val r, Func<double, double, double> f)
                => (l.IsVec || r.IsVec)
                    ? new Val(f(l.X, r.X), f(l.Y, r.Y), f(l.Z, r.Z))
                    : new Val(f(l.S, r.S));

            static Val Combine3(Val a, Val b, Val c, Func<double, double, double, double> f)
                => (a.IsVec || b.IsVec || c.IsVec)
                    ? new Val(f(a.X, b.X, c.X), f(a.Y, b.Y, c.Y), f(a.Z, b.Z, c.Z))
                    : new Val(f(a.S, b.S, c.S));
        }
    }
}
