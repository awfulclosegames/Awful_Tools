using System.IO;
using System.Text;
using System.Xml;
using AwfulDiff.Models;

namespace AwfulDiff.Services;

/// <summary>Language family for structural analysis — which analyzer runs on a file pair.</summary>
public enum StructuralFamily
{
    /// C/C++/C#/Java/JS/TS/ObjC — brace nesting, function definitions, control blocks.
    CFamily,
    /// Rust — fn/const items, struct/enum/trait types, closures (brace-based).
    Rust,
    /// Python — def/class via indentation depth (no braces), decorators, lambdas.
    Python,
    /// JSON documents — keys and value types.
    Json,
    /// XML/XAML/project files — elements and attributes.
    Xml
}

/// <summary>
/// Category of one structural finding. Drives the gutter glyph colour: error (brace imbalance /
/// parse failure), added, removed, or changed (everything else).
/// </summary>
public enum StructuralKind
{
    BraceImbalance,
    InvalidDocument,
    FunctionAdded,
    FunctionRemoved,
    ReturnTypeChanged,
    ParametersChanged,
    ControlBlockAdded,
    ControlBlockRemoved,
    ControlBlockModified,
    NestingChanged,
    /// Qualifier change on a function/type (virtual, static, access level, trailing const, decorators).
    ModifierChanged,
    /// Template/generic parameter list added, removed or changed.
    GenericChanged,
    /// Class base clause changed (extends/implements/: bases).
    BaseChanged,
    TypeAdded,
    TypeRemoved,
    /// Lambda/closure signature change on an aligned line pair (added/removed/changed).
    LambdaChanged,
    JsonKeyAdded,
    JsonKeyRemoved,
    JsonValueTypeChanged,
    XmlElementAdded,
    XmlElementRemoved,
    XmlAttributeChanged
}

/// <summary>
/// One structural finding. LineA/LineB are 1-based original line numbers; 0 = absent on that
/// side. Message is the user-facing tooltip text.
/// </summary>
public readonly record struct StructuralFinding(StructuralKind Kind, int LineA, int LineB, string Message);

/// <summary>
/// Advisory structural analysis for C-family, JSON and XML file pairs — highlights common
/// structural changes (functions, loops/ifs, brace nesting; JSON keys and value types; XML
/// elements and attributes) on top of the textual diff. Pure and WPF-free: it reads file
/// contents plus a cross-file line alignment derived from the row-aligned interleaved diff
/// pair, and never touches diff statuses or alignment. Heuristic by design (no real parser) —
/// findings are advisory and capped so a pathological pair can't flood the view.
/// </summary>
public static class StructuralDiff
{
    /// <summary>Above this many lines on either side, analysis is skipped (perf guard).</summary>
    public const int MaxLines = 100_000;

    private const int MaxFindings = 200;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Language family for a path — same extension sets as SyntaxHighlighter.Create.
    /// Null = unsupported (no structural analysis).
    /// </summary>
    public static StructuralFamily? Detect(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" or ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh"
                or ".java" or ".js" or ".ts" or ".jsx" or ".tsx" or ".m" or ".mm" => StructuralFamily.CFamily,
            ".rs" => StructuralFamily.Rust,
            ".py" or ".pyw" => StructuralFamily.Python,
            ".json" or ".jsonc" => StructuralFamily.Json,
            ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".svg" or ".config" or ".resx" => StructuralFamily.Xml,
            _ => null
        };
    }

    /// <summary>
    /// Analyzes a file pair structurally. aToB[i] = 0-based B index aligned with A line i
    /// (−1 unpaired), bToA symmetric — derived by the caller from the row-aligned interleaved
    /// diff pair (non-Gap rows enumerate each side in order; paired rows are the alignment).
    /// Returns null when either file is missing, extensions are unsupported or mismatched, or
    /// either side exceeds MaxLines; otherwise a list (possibly empty) capped at MaxFindings,
    /// sorted by (LineA, LineB). Pure — never mutates its inputs.
    /// </summary>
    public static List<StructuralFinding>? Analyze(FileModel? a, FileModel? b, int[] aToB, int[] bToA)
    {
        if (a == null || b == null) return null;
        var fam = Detect(a.FilePath);
        if (fam == null || Detect(b.FilePath) != fam) return null;
        if (a.Lines.Count > MaxLines || b.Lines.Count > MaxLines) return null;
        if (aToB.Length != a.Lines.Count || bToA.Length != b.Lines.Count) return null;

        var findings = fam switch
        {
            StructuralFamily.CFamily => AnalyzeCFamily(a, b, aToB),
            StructuralFamily.Rust => AnalyzeRust(a, b, aToB),
            StructuralFamily.Python => AnalyzePython(a, b, aToB),
            StructuralFamily.Json => AnalyzeJson(a, b),
            _ => AnalyzeXml(a, b)
        };

        findings.Sort((x, y) => x.LineA != y.LineA ? x.LineA.CompareTo(y.LineA) : x.LineB.CompareTo(y.LineB));
        if (findings.Count > MaxFindings)
            findings.RemoveRange(MaxFindings, findings.Count - MaxFindings);
        return findings;
    }

    // ── C family ────────────────────────────────────────────────────────────────

    private sealed class CFile
    {
        public int LineCount;
        public string[] Lines = null!;      // raw lines — text comparison for re-targeting NestingChanged
        public char[][] Stripped = null!;   // comments/strings blanked, columns preserved
        public int[] DepthAfter = null!;    // clamped nesting depth after each line (0-based)
        public int FinalDepth;              // raw (unclamped) depth — ≠ 0 means unbalanced braces
        public List<CFunction> Functions = new();
        public List<CBlock> Blocks = new();
        public List<CType> Types = new();
    }

    private sealed class CFunction
    {
        public string Name = "";
        public int StartLine;               // 1-based
        public int EndLine;                 // 1-based, inclusive (last line if the body never closes)
        public string ReturnType = "";      // normalized prefix before the name (qualifiers stripped for extended specs)
        public string ParamList = "";       // normalized content inside the parens
        public string Qualifiers = "";      // space-joined qualifier words in source order (extended specs only)
        public string Suffix = "";          // text between ')' and '{' on the same line, e.g. "const" / "noexcept"
        public string Generics = "";        // template/generic parameter list content
    }

    private sealed class CBlock
    {
        public string Kind = "";            // "if" / "for" / "while" / "switch"
        public int StartLine;               // 1-based
        public int EndLine;                 // 1-based, inclusive (header line when bodyless)
        public string Header = "";          // normalized keyword … matching ')'
    }

    private sealed class CType
    {
        public string Kind = "";            // "class" / "struct" / "interface" / "enum" / "trait"
        public string Name = "";
        public int StartLine;               // 1-based
        public string Access = "";          // access word if present (public/private/…)
        public string Qualifiers = "";      // other qualifier words in source order
        public string TemplateClause = "";  // content of a "template<…>" header ("" for template<>)
        public bool TemplateHeader;         // a "template<…>" clause is present at all
        public string SpecArgs = "";        // normalized post-name <…> list (explicit specialization)
        public string Generics = "";        // derived: SpecArgs when present, else TemplateClause
        public string Bases = "";           // normalized base clause
    }

    /// <summary>How lambdas/closures are shaped in this language ("" = none recognized).</summary>
    private enum LambdaStyle { None, Cpp, CSharp, Java, Rust, Python }

    /// <summary>
    /// Per-extension extraction rules for the C-family analyzer. Plain (JS/TS) keeps the
    /// historical behavior byte-identical; extended specs additionally split qualifier words
    /// and template/generic lists out of the return-type prefix so MatchFunctions can report
    /// them specifically instead of folding them into a "return type changed" message.
    /// </summary>
    private sealed class LangSpec
    {
        public bool ExtendedAnalysis;       // split qualifiers / extract templates out of the prefix
        public HashSet<string> QualifierWords = new(StringComparer.Ordinal);
        public HashSet<string> AccessWords = new(StringComparer.Ordinal);
        public bool TemplatePrefix;         // "template<…>" before the return type (C++)
        public bool PostNameGenerics;       // "Name<T>(…)" — C# only; post-name <…> is a call in C++
        public bool ArrowBody;              // "=> {" / "=>" expression bodies (C#)
        public bool OperatorOverloads;      // "operator*(…)", "operator== (…)" … (C++/C#)
        public LambdaStyle Lambda = LambdaStyle.None;

        public static readonly LangSpec Plain = new() { ExtendedAnalysis = false };

        private static readonly HashSet<string> CppQualifiers = new(StringComparer.Ordinal)
        {
            // deliberately no "const" here — ambiguous with a const return type in the prefix;
            // trailing ") const" is captured via CFunction.Suffix instead
            "virtual", "static", "inline", "explicit", "extern", "friend", "final", "override"
        };

        public static readonly LangSpec Cpp = new()
        {
            ExtendedAnalysis = true,
            QualifierWords = CppQualifiers,
            TemplatePrefix = true,
            OperatorOverloads = true,
            Lambda = LambdaStyle.Cpp
        };

        public static readonly LangSpec ObjC = new()
        {
            ExtendedAnalysis = true,
            QualifierWords = CppQualifiers   // no lambdas (conservative)
        };

        public static readonly LangSpec CSharp = new()
        {
            ExtendedAnalysis = true,
            QualifierWords = new(StringComparer.Ordinal)
            {
                "public", "private", "protected", "internal", "file", "static", "virtual",
                "override", "abstract", "sealed", "extern", "partial", "async", "unsafe",
                "readonly", "new"
            },
            AccessWords = new(StringComparer.Ordinal) { "public", "private", "protected", "internal", "file" },
            PostNameGenerics = true,
            ArrowBody = true,
            OperatorOverloads = true,
            Lambda = LambdaStyle.CSharp
        };

        public static readonly LangSpec Java = new()
        {
            ExtendedAnalysis = true,
            QualifierWords = new(StringComparer.Ordinal)
            {
                "public", "private", "protected", "static", "final", "abstract", "synchronized",
                "native", "strictfp", "transient", "volatile"
            },
            AccessWords = new(StringComparer.Ordinal) { "public", "private", "protected" },
            Lambda = LambdaStyle.Java
        };

        // Python has its own family pass (AnalyzePython); the spec only carries the lambda style
        // and switches on the extended signature comparison (decorators live in Qualifiers).
        public static readonly LangSpec Python = new()
        {
            ExtendedAnalysis = true,
            Lambda = LambdaStyle.Python
        };

        // Rust has its own family pass (AnalyzeRust), but the spec is still what FindTypes /
        // MatchTypes / FindLambdas read for qualifier bucketing and closure shape.
        public static readonly LangSpec Rust = new()
        {
            ExtendedAnalysis = true,
            QualifierWords = new(StringComparer.Ordinal) { "pub", "async", "const", "unsafe", "extern" },
            AccessWords = new(StringComparer.Ordinal) { "pub" },
            Lambda = LambdaStyle.Rust
        };

        /// <summary>Spec for a CFamily extension (Detect already gated the family).</summary>
        public static LangSpec SpecFor(string ext) => ext switch
        {
            ".cs" => CSharp,
            ".rs" => Rust,
            ".py" or ".pyw" => Python,
            ".java" => Java,
            ".m" or ".mm" => ObjC,
            ".js" or ".ts" or ".jsx" or ".tsx" => Plain,
            _ => Cpp   // .c .cpp .cc .cxx .h .hpp .hh
        };
    }

    // Words that are followed by '(' but never start a function definition.
    private static readonly HashSet<string> FunctionSkipWords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "sizeof", "new", "delete",
        // statement keywords that take parens: "lock (x) {…}", "using (var x = …)", Java "synchronized (x) {…}"
        "synchronized", "lock", "using"
    };

    private static readonly (string Keyword, int Length)[] ControlKeywords =
    {
        ("if", 2), ("for", 3), ("while", 5), ("switch", 6)
    };

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// One char of an overloaded-operator token after "operator". Deliberately excludes ( and ):
    /// for a zero-argument overload ("operator*()") the parens are the parameter list, not part
    /// of the name — consuming them leaves no '(' for the param scan and the definition is
    /// dropped. The call operator still works: "operator()(int x)" keeps the name "operator" and
    /// reads "(int x)" as its params. Only reached directly after the whole word "operator", so
    /// comparison chains in ordinary code can never land here.
    /// </summary>
    private static bool IsOperatorSymbol(char c) => c is
        '+' or '-' or '*' or '/' or '%' or '^' or '&' or '|' or '<' or '>' or '=' or '~' or '!' or '[' or ']';

    /// <summary>First non-whitespace char of the line, or '\0' when the line is blank.</summary>
    private static char FirstSignificant(char[] line)
    {
        foreach (char c in line)
            if (!char.IsWhiteSpace(c)) return c;
        return '\0';
    }

    private static char LastSignificant(char[] line)
    {
        for (int t = line.Length - 1; t >= 0; t--)
            if (!char.IsWhiteSpace(line[t])) return line[t];
        return '\0';
    }

    /// <summary>
    /// A valid return-type prefix: identifier chars, whitespace, and the type punctuation
    /// *&lt;&gt;::~., — accepts "static const std::vector&lt;int&gt; C::", rejects call sites
    /// ('=', '(', ')', arithmetic).
    /// </summary>
    private static bool PrefixOk(string s)
    {
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsLetterOrDigit(c) || c == '_' || c == '*' || c == '&'
                || c == '<' || c == '>' || c == ':' || c == '~' || c == '.' || c == ',') continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// A C++ access-specifier label line — "public:" / "private:" / "protected:" with nothing
    /// but whitespace and a single colon (comments/strings already blanked). A member's
    /// signature starts after its section label, so the return-type prefix scan must stop here;
    /// otherwise a first member right after "public:" absorbs the label into ReturnType and a
    /// pair that differs only in what precedes the member reports a phantom type change.
    /// </summary>
    private static bool IsAccessLabelLine(char[] s)
    {
        int a = -1;
        for (int t = 0; t < s.Length; t++)
            if (!char.IsWhiteSpace(s[t])) { a = t; break; }
        if (a < 0) return false;

        int b = a;
        while (b < s.Length && IsIdentChar(s[b])) b++;
        string word = new string(s, a, b - a);
        if (word is not ("public" or "private" or "protected")) return false;

        int colons = 0;
        for (int t = b; t < s.Length; t++)
        {
            if (char.IsWhiteSpace(s[t])) continue;
            if (s[t] == ':') { colons++; continue; }
            return false;
        }
        return colons == 1;
    }

    /// <summary>Trim + collapse whitespace runs to single spaces.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool ws = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { ws = true; continue; }
            if (ws)
            {
                if (sb.Length > 0) sb.Append(' ');   // a leading run trims away; interior runs collapse to one space
                ws = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

     /// <summary>
    /// Phase 1 — blanks comments/strings to spaces (columns preserved, block comments tracked
    /// across lines, an unterminated string literal ends at EOL) and computes per-line brace
    /// depth. Braces inside comments/strings never count. In Rust mode "'"+identifier-start is a
    /// lifetime reference (both chars blanked) instead of a char-string open — byte literals
    /// like b'a' are a documented edge (the closing quote then opens an EOL string).
    /// </summary>
    private static CFile StripAndDepth(List<string> lines, bool rustLifetimes = false)
    {
        var f = new CFile { LineCount = lines.Count };
        f.Lines = lines.ToArray();
        f.Stripped = new char[lines.Count][];
        f.DepthAfter = new int[lines.Count];

        bool inBlockComment = false;
        int raw = 0, clamped = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            var s = new char[line.Length];
            for (int t = 0; t < s.Length; t++) s[t] = ' ';   // blanked regions read as spaces, not NULs
            int delta = 0;
            bool inStr = false;
            char strChar = '"';

            for (int j = 0; j < line.Length; j++)
            {
                char c = line[j];

                if (inBlockComment)
                {
                    if (c == '*' && j + 1 < line.Length && line[j + 1] == '/')
                    { inBlockComment = false; j++; }
                    continue;
                }
                if (inStr)
                {
                    if (c == '\\') { j++; continue; }        // skip the escaped char
                    if (c == strChar) inStr = false;
                    continue;
                }

                if (c == '/' && j + 1 < line.Length)
                {
                    if (line[j + 1] == '/') break;           // rest of line is a comment
                    if (line[j + 1] == '*') { inBlockComment = true; j++; continue; }
                }
                if (c == '\'' && rustLifetimes && j + 1 < line.Length && IsIdentStart(line[j + 1]))
                    { j++; continue; }                       // lifetime "'a" — blank both chars
                if (c == '"' || c == '\'') { inStr = true; strChar = c; continue; }

                s[j] = c;
                if (c == '{') delta++;
                else if (c == '}') delta--;
            }

            f.Stripped[i] = s;
            raw += delta;
            clamped = Math.Max(0, clamped + delta);
            f.DepthAfter[i] = clamped;
        }

        f.FinalDepth = raw;
        return f;
    }

    /// <summary>
    /// Phase 2 — function definitions: an identifier directly followed by '(' (C++/C# operator
    /// overloads extend the name over the operator symbol run — "operator*", "operator->"; the
    /// run stops before '(' so zero-arg overloads keep their param list), a
    /// return-type prefix (same-line head plus up to 3 preceding lines, stopped when a preceding
    /// line starts or ends with ;{}), the matching ')' on this line or within the next three
    /// (wrapped parameter lists), and a body '{' found by FindFunctionBody. Extended specs
    /// additionally split qualifier words out of the prefix, extract C++ template&lt;…&gt; clauses,
    /// capture C# post-name generics and trailing ") … {" suffixes (const/noexcept).
    /// </summary>
    private static void FindFunctions(CFile f, LangSpec spec)
    {
        for (int i = 0; i < f.LineCount; i++)
        {
            var line = f.Stripped[i];
            int len = line.Length;

            for (int j = 0; j < len; j++)
            {
                if (!IsIdentStart(line[j])) continue;
                int k = j + 1;
                while (k < len && IsIdentChar(line[k])) k++;
                string name = new string(line, j, k - j);

                // Skip the whole keyword — a bare continue would re-scan from its second char and
                // read "or" out of "for(" / "f" out of "if(" as a phantom function name.
                if (FunctionSkipWords.Contains(name)) { j = k; continue; }

                // C++/C# operator overloads ("operator*", "operator==", "operator()"): the symbol
                // run extends the name so distinct operators never collide under a bare
                // "operator" — without this every overload is invisible (an identifier scan stops
                // at the first symbol char).
                bool isOperator = false;
                if (spec.OperatorOverloads && name == "operator")
                {
                    int op0 = k;
                    while (k < len && IsOperatorSymbol(line[k])) k++;
                    if (k > op0) { isOperator = true; name = new string(line, j, k - j); }
                }

                int p = k;
                while (p < len && line[p] == ' ') p++;

                // C# generic methods "Name<T>(…)" — balanced post-name "<...>" between the name
                // and the '(' (C++ must not get this: post-name <…> can be explicit-argument syntax).
                // Operators are excluded: "operator<(…)" would lose its '<' to a generics scan.
                string generics = "";
                if (spec.PostNameGenerics && !isOperator && p < len && line[p] == '<')
                {
                    int dep = 0, g = p;
                    while (g < len)
                    {
                        if (line[g] == '<') dep++;
                        else if (line[g] == '>') { dep--; if (dep == 0) break; }
                        g++;
                    }
                    if (g >= len) continue;   // unbalanced — not a generic method
                    generics = Normalize(new string(line, p + 1, g - p - 1));
                    p = g + 1;
                    while (p < len && line[p] == ' ') p++;
                }

                // C++ explicit specializations "f<int>(…)" — a balanced "<…>" between the name and
                // the '(' is the specialization's argument list, not template parameters (those live
                // in the preceding "template<…>" clause that ExtractTemplatePrefix pulls out of the
                // prefix). Without this skip the definition is invisible and an added/removed/changed
                // specialization reports nothing. Not stored: the pairing key is the param list.
                if (spec.TemplatePrefix && !isOperator && p < len && line[p] == '<')
                {
                    int dep = 0, g = p;
                    while (g < len)
                    {
                        if (line[g] == '<') dep++;
                        else if (line[g] == '>') { dep--; if (dep == 0) break; }
                        g++;
                    }
                    if (g >= len) continue;   // unbalanced — not a specialization
                    p = g + 1;
                    while (p < len && line[p] == ' ') p++;
                }

                if (p >= len || line[p] != '(') continue;

                // Return-type prefix: same-line head plus up to 3 preceding lines. A ;{} first or
                // last significant char on a preceding line is a boundary (stop collecting — that
                // line is already a complete statement/definition); any other disallowed char
                // rejects the candidate (call sites, arithmetic, prior statements).
                string head = new string(line, 0, j);
                if (!PrefixOk(head)) continue;
                string prefix = head;
                bool ok = true;
                int from = Math.Max(0, i - 3);
                for (int m = i - 1; ok && m >= from; m--)
                {
                    string prev = new string(f.Stripped[m]);
                    char fs = FirstSignificant(f.Stripped[m]);
                    if (fs == '\0') continue;                    // blank line — keep looking
                    if (IsAccessLabelLine(f.Stripped[m])) break;  // C++ section label — signature starts after it
                    if (fs is ';' or '{' or '}') break;          // statement/block boundary — stop
                    if (LastSignificant(f.Stripped[m]) is ';' or '{' or '}') break;   // previous line is a complete statement/definition
                    if (!PrefixOk(prev)) { ok = false; break; }
                    prefix = prev + " " + prefix;
                }
                if (!ok) continue;

                // The matching ')' must close on this line or within the next three — wrapped
                // parameter lists are common in out-of-class template definitions. Param text is
                // accumulated across lines (inner parens included, as before); a space joins the
                // fragments so "f(a,\nb)" normalizes exactly like "f(a, b)".
                int d = 1;   // the '(' at p is open
                int qLine = -1, q = -1;
                var paramSb = new StringBuilder();
                for (int m = i; m <= Math.Min(i + 3, f.LineCount - 1); m++)
                {
                    var s2 = f.Stripped[m];
                    int from2 = (m == i ? p + 1 : 0);
                    for (int t = from2; t < s2.Length; t++)
                    {
                        char c = s2[t];
                        if (c == '(') d++;
                        else if (c == ')')
                        {
                            d--;
                            if (d == 0) { qLine = m; q = t; break; }
                        }
                        paramSb.Append(c);
                    }
                    if (qLine >= 0) break;
                    paramSb.Append(' ');
                }
                if (qLine < 0) continue;   // params don't close in the window — not a definition

                int braceLine = FindFunctionBody(f, qLine, q + 1, spec, out string suffix);
                if (braceLine == -2)
                {
                    // C# expression-bodied member ("=> expr;") — the body is the arrow itself.
                    f.Functions.Add(new CFunction
                    {
                        Name = name,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        ReturnType = Normalize(prefix),
                        ParamList = Normalize(paramSb.ToString()),
                        Generics = generics
                    });
                }
                else if (braceLine >= 0)
                {
                    f.Functions.Add(new CFunction
                    {
                        Name = name,
                        StartLine = i + 1,
                        EndLine = BodyEndLine(f, braceLine) + 1,
                        ReturnType = Normalize(prefix),
                        ParamList = Normalize(paramSb.ToString()),
                        Suffix = suffix,
                        Generics = generics
                    });
                }
                // else: declaration or call, not a definition

                j = k;   // resume past this whole identifier (the for-loop's ++ skips it)
            }
        }

        if (!spec.ExtendedAnalysis) return;   // Plain (JS/TS): byte-identical to the historical behavior

        foreach (var fn in f.Functions)
        {
            if (spec.TemplatePrefix) ExtractTemplatePrefix(fn);
            SplitQualifiers(fn, spec);
        }
    }

    /// <summary>
    /// Phase 3 — control blocks: whole-word if/for/while/switch + optional space + '(', the
    /// matching ')' on the same line, and an optional body '{' (bodyless blocks are tracked for
    /// add/remove only).
    /// </summary>
    private static void FindBlocks(CFile f)
    {
        foreach (var (kw, kwLen) in ControlKeywords)
        {
            for (int i = 0; i < f.LineCount; i++)
            {
                var line = new string(f.Stripped[i]);
                int len = line.Length;
                int idx = 0;
                while ((idx = line.IndexOf(kw, idx, StringComparison.Ordinal)) >= 0)
                {
                    int after = idx + kwLen;
                    bool wordOk = (idx == 0 || !IsIdentChar(line[idx - 1]))
                                  && (after >= len || !IsIdentChar(line[after]));
                    if (wordOk)
                    {
                        int p = after;
                        while (p < len && line[p] == ' ') p++;
                        if (p < len && line[p] == '(')
                        {
                            int d = 0, q = p;
                            while (q < len)
                            {
                                if (line[q] == '(') d++;
                                else if (line[q] == ')') { d--; if (d == 0) break; }
                                q++;
                            }
                            if (q < len)
                            {
                                int braceLine = FindBodyStart(f, i, q + 1);
                                f.Blocks.Add(new CBlock
                                {
                                    Kind = kw,
                                    StartLine = i + 1,
                                    EndLine = (braceLine < 0 ? i : BodyEndLine(f, braceLine)) + 1,
                                    Header = Normalize(line.Substring(idx, q - idx + 1))
                                });
                            }
                        }
                    }
                    idx = after;   // advance past this occurrence
                }
            }
        }
    }

    /// <summary>
    /// Line (0-based) where a body's '{' sits, scanning from afterCol on lineIdx and then the
    /// first significant char of the next two lines; −1 when no body starts in that window.
    /// Between ')' and '{' only identifier chars and whitespace are allowed ("override {",
    /// "noexcept {").
    /// </summary>
    private static int FindBodyStart(CFile f, int lineIdx, int afterCol)
    {
        var line = f.Stripped[lineIdx];
        for (int r = afterCol; r < line.Length; r++)
        {
            char c = line[r];
            if (c == '{') return lineIdx;
            if (!IsIdentChar(c) && !char.IsWhiteSpace(c)) return -1;
        }
        for (int m = lineIdx + 1; m <= Math.Min(lineIdx + 2, f.LineCount - 1); m++)
        {
            char fs = FirstSignificant(f.Stripped[m]);
            if (fs == '{') return m;
            if (fs != '\0') return -1;
        }
        return -1;
    }

    /// <summary>
    /// Function-body variant of FindBodyStart (which stays untouched for FindBlocks): same
    /// acceptance rules, but also accepts a "=>" arrow body when the spec has one (returns the
    /// −2 sentinel — an expression-bodied member ends on its own line), captures the text
    /// between ')' and '{' (a trailing "const" / "noexcept") into suffix, and — for constructor
    /// definitions — tolerates a member-initializer list between the two ("… ) : Base(x),
    /// Other(y) {", same line or on following lines). The initializer content is not part of the
    /// suffix; the window widens to ten lines while an init list may still be open (a non-blank
    /// line that is neither '{' nor a valid initializer entry fails immediately either way).
    /// </summary>
    private static int FindFunctionBody(CFile f, int lineIdx, int afterCol, LangSpec spec, out string suffix)
    {
        suffix = "";
        var line = f.Stripped[lineIdx];
        bool initList = false;
        for (int r = afterCol; r < line.Length; r++)
        {
            char c = line[r];
            if (c == '{')
            {
                if (!initList) suffix = Normalize(new string(line, afterCol, r - afterCol));
                return lineIdx;
            }
            if (spec.ArrowBody && c == '=' && r + 1 < line.Length && line[r + 1] == '>')
                return -2;   // "=> …" — no block body
            if (initList) { if (!IsInitListChar(c)) return -1; continue; }
            if (c == ':') { initList = true; continue; }
            if (!IsIdentChar(c) && !char.IsWhiteSpace(c)) return -1;
        }
        for (int m = lineIdx + 1; m <= Math.Min(lineIdx + 10, f.LineCount - 1); m++)
        {
            var s2 = f.Stripped[m];
            char fs = FirstSignificant(s2);
            if (fs == '{') return m;
            if (fs == '\0') continue;
            if (!initList && fs == ':') initList = true;
            if (initList && IsInitListLine(s2)) continue;
            return -1;
        }
        return -1;
    }

    /// <summary>Chars allowed inside a constructor member-initializer list.</summary>
    private static bool IsInitListChar(char c) => IsIdentChar(c) || char.IsWhiteSpace(c)
        || c == ':' || c == ',' || c == '(' || c == ')' || c == '&' || c == '*' || c == '.' || c == '<' || c == '>';

    /// <summary>
    /// A whole line that is one member-initializer entry: starts with ':' (first entry) or
    /// ',' (later ones), contains only initializer chars, and its parens balance within the
    /// line. Keeps the widened body window from swallowing ordinary statements.
    /// </summary>
    private static bool IsInitListLine(char[] line)
    {
        char fs = FirstSignificant(line);
        if (fs != ':' && fs != ',') return false;
        int depth = 0;
        foreach (char c in line)
        {
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth < 0) return false; }
            else if (!IsInitListChar(c)) return false;
        }
        return depth == 0;
    }

    /// <summary>
    /// C++ "template&lt;…&gt;" clause in the prefix: find the whole word "template", balanced-scan
    /// its "&lt;…&gt;", store the content as Generics and remove both from the return-type prefix
    /// (today it pollutes ReturnType since PrefixOk allows &lt;&gt;). Single-line template lists only.
    /// </summary>
    private static void ExtractTemplatePrefix(CFunction fn)
    {
        if (!TryTemplateClause(fn.ReturnType, out string content, out int start, out int end)) return;
        // An empty "template<>" is the explicit-specialization marker — keep it visible (a bare ""
        // would be indistinguishable from a non-template and its add/remove would report nothing).
        fn.Generics = content.Length == 0 ? "<>" : content;
        fn.ReturnType = Normalize(fn.ReturnType.Substring(0, start) + " " + fn.ReturnType.Substring(end));
    }

    /// <summary>
    /// Finds a whole-word "template" followed (after spaces) by a balanced "&lt;…&gt;" in text and
    /// returns its content plus the span of the whole clause — the explicit-specialization form
    /// "template&lt;&gt;" yields "" + true. False when absent or unbalanced.
    /// </summary>
    private static bool TryTemplateClause(string text, out string content, out int start, out int end)
    {
        content = ""; start = -1; end = -1;
        int idx = 0;
        while ((idx = text.IndexOf("template", idx, StringComparison.Ordinal)) >= 0)
        {
            bool wordOk = (idx == 0 || !IsIdentChar(text[idx - 1]))
                          && (idx + 8 >= text.Length || !IsIdentChar(text[idx + 8]));
            if (!wordOk) { idx += 8; continue; }
            int lt = idx + 8;
            while (lt < text.Length && text[lt] == ' ') lt++;
            if (lt >= text.Length || text[lt] != '<') { idx += 8; continue; }
            int d = 0, g = lt;
            while (g < text.Length)
            {
                if (text[g] == '<') d++;
                else if (text[g] == '>') { d--; if (d == 0) break; }
                g++;
            }
            if (g >= text.Length) return false;   // unbalanced — not a template clause
            content = Normalize(text.Substring(lt + 1, g - lt - 1));
            start = idx; end = g + 1;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Splits a normalized prefix into whole-word qualifiers (source order preserved) and the
    /// cleaned type remainder. Access words stay in Qualifiers too — MatchFunctions buckets them
    /// via spec.AccessWords for the "Access changed" phrasing.
    /// </summary>
    private static void SplitQualifiers(CFunction fn, LangSpec spec)
    {
        if (fn.ReturnType.Length == 0) return;
        var tokens = fn.ReturnType.Split(' ');
        var quals = new List<string>(tokens.Length);
        var rest = new List<string>(tokens.Length);
        foreach (var t in tokens)
            if (spec.QualifierWords.Contains(t)) quals.Add(t);
            else rest.Add(t);
        if (quals.Count == 0) return;
        fn.Qualifiers = string.Join(" ", quals);
        fn.ReturnType = string.Join(" ", rest);
    }

    /// <summary>
    /// Last line (0-based, inclusive) of a body opened by the '{' on braceLine — the first later
    /// line whose depth has returned to the pre-body depth, or the file's last line.
    /// </summary>
    private static int BodyEndLine(CFile f, int braceLine)
    {
        int bodyDepth = braceLine > 0 ? f.DepthAfter[braceLine - 1] : 0;
        for (int j = braceLine; j < f.LineCount; j++)
            if (f.DepthAfter[j] <= bodyDepth) return j;
        return f.LineCount - 1;
    }

    // ── Rust family ────────────────────────────────────────────────────────────────

    private static readonly string[] RustTypeKeywords = { "struct", "enum", "trait" };

    /// <summary>
    /// Phase R1 — Rust function definitions: whole-word "fn", an identifier name, optional
    /// post-name balanced "&lt;…&gt;" generics, same-line "(…)" params, and a body '{' found by
    /// FindRustBody (a plain FindBodyStart can't be reused: the '-'/'>' of "-> i32" fail its
    /// char check and every fn with a return type would vanish). Qualifiers are the Rust
    /// qualifier words before "fn"; the return type is the text after "->" up to the body brace.
    /// </summary>
    private static void FindRustFunctions(CFile f)
    {
        for (int i = 0; i < f.LineCount; i++)
        {
            string line = new string(f.Stripped[i]);
            int len = line.Length;

            int idx = 0;
            while ((idx = IndexOfWord(line, "fn", idx)) >= 0)
            {
                int q = idx + 2;
                while (q < len && line[q] == ' ') q++;
                if (q >= len || !IsIdentStart(line[q])) { idx += 2; continue; }
                int r = q;
                while (r < len && IsIdentChar(line[r])) r++;
                string name = line.Substring(q, r - q);

                // Optional post-name balanced "<...>" generics ("fn foo<T>(…)")
                int g0 = r;
                while (g0 < len && line[g0] == ' ') g0++;
                string generics = "";
                if (g0 < len && line[g0] == '<')
                {
                    int dep = 0, g2 = g0;
                    while (g2 < len)
                    {
                        if (line[g2] == '<') dep++;
                        else if (line[g2] == '>') { dep--; if (dep == 0) break; }
                        g2++;
                    }
                    if (g2 >= len) { idx += 2; continue; }   // unbalanced — not a generic fn
                    generics = Normalize(line.Substring(g0 + 1, g2 - g0 - 1));
                    g0 = g2 + 1;
                    while (g0 < len && line[g0] == ' ') g0++;
                }

                // Params "(…)" must close on the same line
                if (g0 >= len || line[g0] != '(') { idx += 2; continue; }
                int d = 0, p = g0;
                for (; p < len; p++)
                {
                    if (line[p] == '(') d++;
                    else if (line[p] == ')') { d--; if (d == 0) break; }
                }
                if (p >= len) { idx += 2; continue; }

                int braceLine = FindRustBody(f, i, p + 1);
                if (braceLine < 0) { idx += 2; continue; }   // declaration / trait signature

                // Qualifier words before "fn" (same line only — Rust puts them there)
                var quals = new List<string>();
                foreach (var tok in Normalize(line.Substring(0, idx)).Split(' '))
                    if (LangSpec.Rust.QualifierWords.Contains(tok) && !quals.Contains(tok)) quals.Add(tok);

                // Return type: text after "->" up to the body brace (or EOL)
                int arrow = -1, braceCol = len;
                for (int a2 = p + 1; a2 < len; a2++)
                {
                    if (line[a2] == '{') { braceCol = a2; break; }
                    if (arrow < 0 && line[a2] == '-' && a2 + 1 < len && line[a2 + 1] == '>') arrow = a2;
                }
                string ret = arrow >= 0 ? Normalize(line.Substring(arrow + 2, braceCol - arrow - 2)) : "";

                f.Functions.Add(new CFunction
                {
                    Name = name,
                    StartLine = i + 1,
                    EndLine = BodyEndLine(f, braceLine) + 1,
                    ReturnType = ret,
                    ParamList = Normalize(line.Substring(g0 + 1, p - g0 - 1)),
                    Qualifiers = string.Join(" ", quals),
                    Generics = generics
                });
                idx = r;   // resume past the name
            }
        }
    }

    /// <summary>
    /// Rust body scan: like FindBodyStart but also accepts '-' and '>' between the params and
    /// the '{' ("-> i32 {"). Same two-line window.
    /// </summary>
    private static int FindRustBody(CFile f, int lineIdx, int afterCol)
    {
        var line = f.Stripped[lineIdx];
        for (int r = afterCol; r < line.Length; r++)
        {
            char c = line[r];
            if (c == '{') return lineIdx;
            if (!IsIdentChar(c) && !char.IsWhiteSpace(c) && c != '-' && c != '>') return -1;
        }
        for (int m = lineIdx + 1; m <= Math.Min(lineIdx + 2, f.LineCount - 1); m++)
        {
            char fs = FirstSignificant(f.Stripped[m]);
            if (fs == '{') return m;
            if (fs != '\0') return -1;
        }
        return -1;
    }

    /// <summary>
    /// Phase R2 — top-level "const NAME: Type = …;" / "static [mut] NAME: Type …;" items,
    /// recorded function-shaped (empty param list, ReturnType = the type between ':' and
    /// '='/';', body on their own line) so a second MatchFunctions pass can pair them under a
    /// "Constant" label. Only Rust qualifier words may precede the keyword — this rejects fn
    /// qualifiers ("pub const fn …") and consts inside statements/bodies.
    /// </summary>
    private static List<CFunction> FindRustConstants(CFile f)
    {
        var list = new List<CFunction>();
        for (int i = 0; i < f.LineCount; i++)
        {
            string line = new string(f.Stripped[i]);
            int len = line.Length;

            foreach (var kw in new[] { "const", "static" })
            {
                int idx = 0;
                while ((idx = IndexOfWord(line, kw, idx)) >= 0)
                {
                    bool headOk = true;
                    foreach (var tok in Normalize(line.Substring(0, idx)).Split(' '))
                        if (tok.Length > 0 && !LangSpec.Rust.QualifierWords.Contains(tok)) { headOk = false; break; }
                    if (!headOk) { idx += kw.Length; continue; }

                    int q = idx + kw.Length;
                    while (q < len && line[q] == ' ') q++;
                    if (q + 3 <= len && line[q] == 'm' && line[q + 1] == 'u' && line[q + 2] == 't'
                        && (q + 3 >= len || !IsIdentChar(line[q + 3])))   // optional "mut"
                    { q += 3; while (q < len && line[q] == ' ') q++; }

                    if (q >= len || !IsIdentStart(line[q])) { idx += kw.Length; continue; }
                    int r = q;
                    while (r < len && IsIdentChar(line[r])) r++;
                    string name = line.Substring(q, r - q);

                    int c = r;
                    while (c < len && line[c] == ' ') c++;
                    if (c >= len || line[c] != ':') { idx += kw.Length; continue; }

                    int e = c + 1;
                    while (e < len && line[e] != '=' && line[e] != ';') e++;
                    if (e >= len) { idx += kw.Length; continue; }

                    list.Add(new CFunction
                    {
                        Name = name,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        ReturnType = Normalize(line.Substring(c + 1, e - c - 1)),
                        ParamList = ""
                    });
                    idx = r;
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Rust family pass: same shape as the C family except functions/constants come from the
    /// Rust finders, types accept a ';' terminator (tuple/unit structs), and there is no
    /// MatchBlocks — Rust control flow has no parens ("if x {…}"), so block matching would only
    /// produce noise (documented follow-up). BraceImbalance and NestingRuns run unchanged.
    /// </summary>
    private static List<StructuralFinding> AnalyzeRust(FileModel a, FileModel b, int[] aToB)
    {
        var fa = StripAndDepth(a.Lines, rustLifetimes: true);
        var fb = StripAndDepth(b.Lines, rustLifetimes: true);
        FindRustFunctions(fa);
        FindRustFunctions(fb);
        var constsA = FindRustConstants(fa);
        var constsB = FindRustConstants(fb);

        var findings = new List<StructuralFinding>();

        if (fa.FinalDepth != 0)
            findings.Add(new StructuralFinding(StructuralKind.BraceImbalance, fa.LineCount, 0,
                ImbalanceMessage(fa.FinalDepth)));
        if (fb.FinalDepth != 0)
            findings.Add(new StructuralFinding(StructuralKind.BraceImbalance, 0, fb.LineCount,
                ImbalanceMessage(fb.FinalDepth)));

        MatchFunctions(fa.Functions, fb.Functions, LangSpec.Rust, "Function", "Return type changed", findings);
        MatchFunctions(constsA, constsB, LangSpec.Rust, "Constant", "Constant type changed", findings);

        FindTypes(fa, LangSpec.Rust, RustTypeKeywords, true);
        FindTypes(fb, LangSpec.Rust, RustTypeKeywords, true);
        MatchTypes(fa.Types, fb.Types, LangSpec.Rust, findings);

        MatchLambdas(FindLambdas(fa, LangSpec.Rust), FindLambdas(fb, LangSpec.Rust), aToB, findings);

        NestingRuns(fa, fb, aToB, findings);

        return findings;
    }

    // ── Python family ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase P1 — Python strip: blanks '#' comments, single/double-quoted strings (backslash
    /// escapes; an unterminated string runs to EOL) and triple-quoted multi-line strings via
    /// cross-line state (a docstring containing "{ def fake(): }" must not produce phantoms).
    /// Depth is an indentation stack: for each line that carries code, take its leading width w
    /// (tab = 1 column, space = 1), pop while top &gt; w, push if empty or w &gt; top;
    /// DepthAfter[i] = stack.Count. Blank/comment/string-only lines keep the previous depth.
    /// Absolute widths are arbitrary but consistent across files — a 4-space file and a
    /// 2-space file yield identical depth arrays for the same code, which is all NestingRuns
    /// consumes. FinalDepth stays 0: there are no braces, so a non-empty final indent stack is
    /// normal and BraceImbalance is skipped for this family.
    /// </summary>
    private static CFile StripPython(List<string> lines)
    {
        var f = new CFile { LineCount = lines.Count };
        f.Lines = lines.ToArray();
        f.Stripped = new char[lines.Count][];
        f.DepthAfter = new int[lines.Count];

        bool inTriple = false;
        char tripleChar = '"';
        var stack = new List<int>();
        int depth = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            var s = new char[line.Length];
            Array.Fill(s, ' ');   // blanked regions read as spaces, not NULs

            bool inStr = false;
            char strChar = '"';
            for (int j = 0; j < line.Length; j++)
            {
                char c = line[j];

                if (inTriple)
                {
                    if (c == tripleChar && j + 2 < line.Length
                        && line[j + 1] == tripleChar && line[j + 2] == tripleChar)
                    { inTriple = false; j += 2; continue; }   // resume just after the closing quotes
                    continue;                                 // string content stays blanked
                }

                if (inStr)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == strChar) inStr = false;
                    continue;
                }

                if (c == '#') break;                          // comment to EOL
                if (c == '"' || c == '\'')
                {
                    if (j + 2 < line.Length && line[j + 1] == c && line[j + 2] == c)
                    { inTriple = true; tripleChar = c; j += 2; continue; }   // opening quotes skipped
                    inStr = true; strChar = c;
                    continue;
                }

                s[j] = c;
            }

            f.Stripped[i] = s;

            int w = 0;
            while (w < line.Length && (line[w] == ' ' || line[w] == '\t')) w++;
            bool hasCode = false;
            foreach (char c2 in s) if (c2 != ' ') { hasCode = true; break; }

            if (hasCode)
            {
                while (stack.Count > 0 && stack[stack.Count - 1] > w) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0 || w > stack[stack.Count - 1]) stack.Add(w);
                depth = stack.Count;
            }
            f.DepthAfter[i] = depth;
        }

        return f;
    }

    /// <summary>
    /// Phase P2 — Python function definitions: a line starting (after indent) with whole-word
    /// "def" or "async def", an identifier name, '(' on the same line (a header whose paren is
    /// on the next line is skipped — documented), a matching ')' within three further lines
    /// whose closing line ends with ':', and the annotation after "->" as ReturnType.
    /// Consecutive preceding decorator lines (first significant char '@') join Qualifiers in
    /// source order, "async" last, so MatchFunctions phrases both specifically. EndLine is the
    /// last line indented deeper than the def line itself (matchers never read CFunction.EndLine).
    /// A decorator-only change on an otherwise identical def line lands its finding on the
    /// Unchanged def line and is dropped by the VM stamp — the plain text diff still shows it.
    /// </summary>
    private static void FindPythonFunctions(CFile f)
    {
        for (int i = 0; i < f.LineCount; i++)
        {
            string line = new string(f.Stripped[i]);
            int p = 0;
            while (p < line.Length && char.IsWhiteSpace(line[p])) p++;

            int idx = -1;
            bool isAsync = false;
            if (IndexOfWord(line, "def", p) == p) idx = p;
            else if (IndexOfWord(line, "async", p) == p)
            {
                int q2 = p + 5;
                while (q2 < line.Length && char.IsWhiteSpace(line[q2])) q2++;
                if (IndexOfWord(line, "def", q2) == q2) { idx = q2; isAsync = true; }
            }
            if (idx < 0) continue;

            int n = idx + 3;
            while (n < line.Length && char.IsWhiteSpace(line[n])) n++;
            if (n >= line.Length || !IsIdentStart(line[n])) continue;
            int r = n;
            while (r < line.Length && IsIdentChar(line[r])) r++;
            string name = line.Substring(n, r - n);

            int c0 = r;
            while (c0 < line.Length && char.IsWhiteSpace(line[c0])) c0++;
            if (c0 >= line.Length || line[c0] != '(') continue;   // header continues — documented skip

            // Matching ')' within ~3 further lines; the closing line must end with ':'.
            int dep = 0, closeLine = -1, closeCol = -1;
            for (int m = i; closeLine < 0 && m <= Math.Min(i + 3, f.LineCount - 1); m++)
            {
                var s2 = f.Stripped[m];
                for (int t = (m == i ? c0 : 0); t < s2.Length; t++)
                {
                    if (s2[t] == '(') dep++;
                    else if (s2[t] == ')') { dep--; if (dep == 0) { closeLine = m; closeCol = t; break; } }
                }
            }
            if (closeLine < 0 || LastSignificant(f.Stripped[closeLine]) != ':') continue;

            var paramSb = new StringBuilder();
            for (int m = i; m <= closeLine; m++)
            {
                var s3 = f.Stripped[m];
                int from = (m == i ? c0 + 1 : 0);
                int to = (m == closeLine ? closeCol : s3.Length);
                if (to > from) paramSb.Append(s3, from, to - from).Append(' ');
            }

            // Return annotation: text after "->" up to the trailing ':' on the closing line.
            var cl = f.Stripped[closeLine];
            string ret = "";
            int arrow = -1;
            for (int t = closeCol + 1; t < cl.Length - 1; t++)
                if (cl[t] == '-' && cl[t + 1] == '>') { arrow = t; break; }
            if (arrow >= 0)
            {
                int e2 = arrow + 2;
                while (e2 < cl.Length && cl[e2] != ':') e2++;
                ret = Normalize(new string(cl, arrow + 2, e2 - arrow - 2));
            }

            // Decorators: consecutive preceding lines starting with '@', source order.
            var quals = new List<string>();
            for (int m = i - 1; m >= 0; m--)
            {
                if (FirstSignificant(f.Stripped[m]) != '@') break;
                quals.Insert(0, Normalize(new string(f.Stripped[m])));
            }
            if (isAsync) quals.Add("async");

            int end = i;
            for (int j = i + 1; j < f.LineCount; j++)
                if (f.DepthAfter[j] <= f.DepthAfter[i]) { end = j - 1; break; }

            f.Functions.Add(new CFunction
            {
                Name = name,
                StartLine = i + 1,
                EndLine = end + 1,
                ReturnType = ret,
                ParamList = Normalize(paramSb.ToString()),
                Qualifiers = string.Join(" ", quals)
            });
        }
    }

    /// <summary>
    /// Phase P3 — Python classes: a line starting (after indent) with whole-word "class", an
    /// identifier, an optional balanced "(bases)", then the mandatory ':' on the same line.
    /// Decorators are not recorded (a class decorator change is rare; the text diff covers it).
    /// </summary>
    private static void FindPythonTypes(CFile f)
    {
        for (int i = 0; i < f.LineCount; i++)
        {
            string line = new string(f.Stripped[i]);
            int p = 0;
            while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
            if (IndexOfWord(line, "class", p) != p) continue;

            int n = p + 5;
            while (n < line.Length && char.IsWhiteSpace(line[n])) n++;
            if (n >= line.Length || !IsIdentStart(line[n])) continue;
            int r = n;
            while (r < line.Length && IsIdentChar(line[r])) r++;
            string name = line.Substring(n, r - n);

            // Optional "(bases)", then the mandatory ':' — anything between name and either is
            // skipped (valid Python has nothing there).
            string bases = "";
            bool colon = false;
            for (int t = r; t < line.Length; t++)
            {
                char c = line[t];
                if (c == '(')
                {
                    int dep2 = 0, e = t;
                    for (; e < line.Length; e++)
                    {
                        if (line[e] == '(') dep2++;
                        else if (line[e] == ')') { dep2--; if (dep2 == 0) break; }
                    }
                    if (e >= line.Length) continue;   // unterminated — not a class header
                    bases = Normalize(line.Substring(t + 1, e - t - 1));
                    t = e;
                }
                else if (c == ':') { colon = true; break; }
            }
            if (!colon) continue;

            f.Types.Add(new CType { Kind = "class", Name = name, StartLine = i + 1, Bases = bases });
        }
    }

    /// <summary>
    /// Python family pass: indentation-stack depth instead of braces, so no BraceImbalance and
    /// no MatchBlocks ("if x:" has no parens — documented follow-up). Functions, classes and
    /// lambdas feed the same matchers as the other families.
    /// </summary>
    private static List<StructuralFinding> AnalyzePython(FileModel a, FileModel b, int[] aToB)
    {
        var fa = StripPython(a.Lines);
        var fb = StripPython(b.Lines);
        FindPythonFunctions(fa);
        FindPythonFunctions(fb);
        FindPythonTypes(fa);
        FindPythonTypes(fb);

        var findings = new List<StructuralFinding>();

        MatchFunctions(fa.Functions, fb.Functions, LangSpec.Python, "Function", "Return type changed", findings);
        MatchTypes(fa.Types, fb.Types, LangSpec.Python, findings);
        MatchLambdas(FindLambdas(fa, LangSpec.Python), FindLambdas(fb, LangSpec.Python), aToB, findings);

        NestingRuns(fa, fb, aToB, findings);

        return findings;
    }

    private static List<StructuralFinding> AnalyzeCFamily(FileModel a, FileModel b, int[] aToB)
    {
        // Per-extension spec; a cross-language pair (e.g. .cs vs .java — both CFamily) falls back
        // to Plain so the qualifier comparison never mixes two languages' keyword sets.
        var sa = LangSpec.SpecFor(Path.GetExtension(a.FilePath ?? "").ToLowerInvariant());
        var sb = LangSpec.SpecFor(Path.GetExtension(b.FilePath ?? "").ToLowerInvariant());
        if (!ReferenceEquals(sa, sb)) { sa = LangSpec.Plain; sb = LangSpec.Plain; }

        var fa = StripAndDepth(a.Lines);
        var fb = StripAndDepth(b.Lines);
        FindFunctions(fa, sa);
        FindFunctions(fb, sb);
        FindBlocks(fa);
        FindBlocks(fb);

        var findings = new List<StructuralFinding>();

        // Brace imbalance — one finding per unbalanced side, on that file's last line.
        if (fa.FinalDepth != 0)
            findings.Add(new StructuralFinding(StructuralKind.BraceImbalance, fa.LineCount, 0,
                ImbalanceMessage(fa.FinalDepth)));
        if (fb.FinalDepth != 0)
            findings.Add(new StructuralFinding(StructuralKind.BraceImbalance, 0, fb.LineCount,
                ImbalanceMessage(fb.FinalDepth)));

        MatchFunctions(fa.Functions, fb.Functions, sa, "Function", "Return type changed", findings);

        FindTypes(fa, sa, CFamilyTypeKeywords, false);
        FindTypes(fb, sb, CFamilyTypeKeywords, false);
        MatchTypes(fa.Types, fb.Types, sa, findings);

        MatchLambdas(FindLambdas(fa, sa), FindLambdas(fb, sb), aToB, findings);

        MatchBlocks(fa, fb, aToB, findings);
        NestingRuns(fa, fb, aToB, findings);

        return findings;
    }

    private static string ImbalanceMessage(int depth) => depth > 0
        ? $"Brace nesting is unbalanced ({depth} unclosed brace{(depth == 1 ? "" : "s")})"
        : $"Brace nesting is unbalanced ({-depth} extra close brace{(-depth == 1 ? "" : "s")})";

    /// <summary>
    /// Phase 4 — cross-match functions (or Rust constants) by name: identical param lists pair
    /// up, a single unpaired def on each side means the parameters changed, and defs with no
    /// counterpart at all are additions/removals. Multiple unmatched overloads are too ambiguous
    /// to match and are skipped. On every paired definition — including the parameters-changed
    /// pair, whose other field changes used to be hidden behind that finding — the return type,
    /// qualifiers (access/modifiers/decorators), generic list and trailing suffix are compared
    /// in fixed order so a qualifier-only change gets its own specific finding.
    /// </summary>
    private static void MatchFunctions(IReadOnlyList<CFunction> la, IReadOnlyList<CFunction> lb, LangSpec spec, string label, string typeChangedVerb, List<StructuralFinding> findings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in la) names.Add(f.Name);
        foreach (var f in lb) names.Add(f.Name);

        foreach (var name in names)
        {
            var a = new List<CFunction>();
            foreach (var f in la) if (f.Name == name) a.Add(f);
            var b = new List<CFunction>();
            foreach (var f in lb) if (f.Name == name) b.Add(f);

            if (a.Count == 0)
            {
                foreach (var f in b)
                    findings.Add(new StructuralFinding(StructuralKind.FunctionAdded, 0, f.StartLine, $"{label} added: {name}"));
                continue;
            }
            if (b.Count == 0)
            {
                foreach (var f in a)
                    findings.Add(new StructuralFinding(StructuralKind.FunctionRemoved, f.StartLine, 0, $"{label} removed: {name}"));
                continue;
            }

            // Pair by identical param list (first unused match); compare signature fields on pairs.
            var usedB = new bool[b.Count];
            var unpairedA = new List<CFunction>();
            for (int i = 0; i < a.Count; i++)
            {
                int hit = -1;
                for (int j = 0; j < b.Count; j++)
                    if (!usedB[j] && b[j].ParamList == a[i].ParamList) { hit = j; break; }
                if (hit >= 0)
                {
                    usedB[hit] = true;
                    CompareSignature(a[i], b[hit], spec, typeChangedVerb, findings);
                }
                else unpairedA.Add(a[i]);
            }

            var unpairedB = new List<CFunction>();
            for (int j = 0; j < b.Count; j++) if (!usedB[j]) unpairedB.Add(b[j]);

            // Exactly one unpaired def per side → the parameters changed; more than that is
            // ambiguous overload shuffling and is skipped. The pair's other fields are still
            // compared so a param+qualifier change reports both.
            if (unpairedA.Count == 1 && unpairedB.Count == 1)
            {
                findings.Add(new StructuralFinding(StructuralKind.ParametersChanged, unpairedA[0].StartLine, unpairedB[0].StartLine,
                    $"Parameters changed: ({unpairedA[0].ParamList}) → ({unpairedB[0].ParamList})"));
                CompareSignature(unpairedA[0], unpairedB[0], spec, typeChangedVerb, findings);
            }
            else if (unpairedA.Count > 0 && unpairedB.Count == 0)
            {
                // One-sided leftovers: a definition with no counterpart at all — e.g. an explicit
                // specialization removed while the primary template survives. The param list is in
                // the message because sibling overloads that paired up share the bare name.
                foreach (var fn in unpairedA)
                    findings.Add(new StructuralFinding(StructuralKind.FunctionRemoved, fn.StartLine, 0, $"{label} removed: {name}({fn.ParamList})"));
            }
            else if (unpairedB.Count > 0 && unpairedA.Count == 0)
            {
                foreach (var fn in unpairedB)
                    findings.Add(new StructuralFinding(StructuralKind.FunctionAdded, 0, fn.StartLine, $"{label} added: {name}({fn.ParamList})"));
            }
        }
    }

    /// <summary>Compares the signature fields of one paired definition in fixed order.</summary>
    private static void CompareSignature(CFunction da, CFunction db, LangSpec spec, string typeChangedVerb, List<StructuralFinding> findings)
    {
        if (da.ReturnType != db.ReturnType)
            findings.Add(new StructuralFinding(StructuralKind.ReturnTypeChanged, da.StartLine, db.StartLine,
                $"{typeChangedVerb}: {(da.ReturnType.Length == 0 ? "(none)" : da.ReturnType)} → {(db.ReturnType.Length == 0 ? "(none)" : db.ReturnType)}"));

        if (!spec.ExtendedAnalysis) return;   // Plain: the historical return-type comparison only

        if (da.Qualifiers != db.Qualifiers)
            findings.Add(new StructuralFinding(StructuralKind.ModifierChanged, da.StartLine, db.StartLine,
                QualifierMessage(da.Qualifiers, db.Qualifiers, spec)));
        if (da.Generics != db.Generics)
            findings.Add(new StructuralFinding(StructuralKind.GenericChanged, da.StartLine, db.StartLine,
                $"Generic parameters changed: {(da.Generics.Length == 0 ? "(none)" : da.Generics)} → {(db.Generics.Length == 0 ? "(none)" : db.Generics)}"));
        if ((da.Suffix.Length > 0 || db.Suffix.Length > 0) && da.Suffix != db.Suffix)
            findings.Add(new StructuralFinding(StructuralKind.ModifierChanged, da.StartLine, db.StartLine,
                SuffixMessage(da.Suffix, db.Suffix)));
    }

    /// <summary>
    /// Specific qualifier-change message: access words get an "Access changed" phrasing, @-tokens
    /// (Python decorators) a "Decorator" phrasing, everything else modifier added/removed/changed.
    /// Changed buckets are joined with "; " so one finding can carry e.g. an access AND a static
    /// change. Caller guarantees qa ≠ qb, so at least one part is produced.
    /// </summary>
    private static string QualifierMessage(string qa, string qb, LangSpec spec)
    {
        var ta = qa.Length == 0 ? Array.Empty<string>() : qa.Split(' ');
        var tb = qb.Length == 0 ? Array.Empty<string>() : qb.Split(' ');

        string Bucket(IEnumerable<string> ts, Func<string, bool> pred)
        {
            var list = new List<string>();
            foreach (var t in ts) if (pred(t) && !list.Contains(t)) list.Add(t);
            return string.Join(", ", list);
        }
        static string Show(string s) => s.Length == 0 ? "(none)" : s;

        var parts = new List<string>();
        string accA = Bucket(ta, spec.AccessWords.Contains), accB = Bucket(tb, spec.AccessWords.Contains);
        if (accA != accB && (accA.Length > 0 || accB.Length > 0))
            parts.Add($"Access changed: {Show(accA)} → {Show(accB)}");

        string decA = Bucket(ta, t => t.StartsWith('@')), decB = Bucket(tb, t => t.StartsWith('@'));
        if (decA != decB && (decA.Length > 0 || decB.Length > 0))
            parts.Add(decA.Length == 0 ? $"Decorator added: {decB}"
                      : decB.Length == 0 ? $"Decorator removed: {decA}"
                      : $"Decorators changed: {decA} → {decB}");

        string modA = Bucket(ta, t => !spec.AccessWords.Contains(t) && !t.StartsWith('@')),
               modB = Bucket(tb, t => !spec.AccessWords.Contains(t) && !t.StartsWith('@'));
        if (modA != modB && (modA.Length > 0 || modB.Length > 0))
            parts.Add(modA.Length == 0 ? $"Modifier added: {modB}"
                      : modB.Length == 0 ? $"Modifier removed: {modA}"
                      : $"Modifiers changed: {modA} → {modB}");

        return string.Join("; ", parts);
    }

    private static string SuffixMessage(string sa, string sb) => sa.Length == 0
        ? $"Trailing qualifier added: {sb}"
        : sb.Length == 0 ? $"Trailing qualifier removed: {sa}"
        : $"Trailing qualifier changed: {sa} → {sb}";

    /// <summary>Type-definition keywords for the C family (Rust adds trait in its own pass).</summary>
    private static readonly string[] CFamilyTypeKeywords = { "class", "struct", "interface", "enum" };

    /// <summary>Whole-word index of word in s at or after from; −1 when absent.</summary>
    private static int IndexOfWord(string s, string word, int from)
    {
        int idx = from;
        while ((idx = s.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool wordOk = (idx == 0 || !IsIdentChar(s[idx - 1]))
                          && (idx + word.Length >= s.Length || !IsIdentChar(s[idx + word.Length]));
            if (wordOk) return idx;
            idx += word.Length;
        }
        return -1;
    }

    /// <summary>
    /// Phase 4b — type definitions: whole-word class/struct/interface/enum with an identifier
    /// name on the same line, optional post-name balanced "&lt;…&gt;" generics, and a terminating
    /// '{' on the same line or within the next two lines (';' additionally when allowed — Rust
    /// tuple/unit structs). The base clause is the text between the name/generics and the
    /// terminator — when the brace sits on a later line that is the rest of the class line. "enum class X" / "enum struct X" is one unit (kind "enum") — the inner
    /// class/struct word is skipped so it never becomes a phantom type of its own. Lines where
    /// the keyword is preceded by non-prefix text (call sites, parameter lists) are rejected;
    /// forward declarations (no terminator in the window) are skipped.
    /// </summary>
    private static void FindTypes(CFile f, LangSpec spec, string[] keywords, bool allowSemicolonTerminator)
    {
        for (int i = 0; i < f.LineCount; i++)
        {
            string line = new string(f.Stripped[i]);
            int len = line.Length;

            // A same-line "template<…>" clause — keyword hits INSIDE it ("class T" in
            // "template<class T> class Foo") are parameter declarations, not type definitions.
            bool hasClause = TryTemplateClause(line, out _, out int clStart, out int clEnd);

            foreach (var kw in keywords)
            {
                int idx = 0;
                while ((idx = IndexOfWord(line, kw, idx)) >= 0)
                {
                    if (hasClause && idx >= clStart && idx < clEnd) { idx += kw.Length; continue; }

                    // "enum class X" — the inner class/struct belongs to the enum, not a type of its own
                    if (kw is "class" or "struct")
                    {
                        int b = idx;
                        while (b > 0 && line[b - 1] == ' ') b--;
                        int e = b;
                        while (b > 0 && IsIdentChar(line[b - 1])) b--;
                        if (line.Substring(b, e - b) == "enum") { idx += kw.Length; continue; }
                    }

                    string head = line.Substring(0, idx);
                    if (!PrefixOk(head)) { idx += kw.Length; continue; }   // call site / parameter list

                    // Name after the keyword (consuming a scoped-enum "class"/"struct" first)
                    int q = idx + kw.Length;
                    while (q < len && line[q] == ' ') q++;
                    if (kw == "enum" && q < len && IsIdentStart(line[q]))
                    {
                        int e2 = q;
                        while (e2 < len && IsIdentChar(line[e2])) e2++;
                        if (line.Substring(q, e2 - q) is "class" or "struct") q = e2;
                    }
                    while (q < len && line[q] == ' ') q++;
                    if (q >= len || !IsIdentStart(line[q])) { idx += kw.Length; continue; }
                    int r = q;
                    while (r < len && IsIdentChar(line[r])) r++;
                    string name = line.Substring(q, r - q);

                    // Optional post-name balanced "<...>" generics
                    int g0 = r;
                    while (g0 < len && line[g0] == ' ') g0++;
                    string generics = "";
                    if (g0 < len && line[g0] == '<')
                    {
                        int dep = 0, g2 = g0;
                        while (g2 < len)
                        {
                            if (line[g2] == '<') dep++;
                            else if (line[g2] == '>') { dep--; if (dep == 0) break; }
                            g2++;
                        }
                        if (g2 >= len) { idx += kw.Length; continue; }   // unbalanced — not a type def
                        generics = Normalize(line.Substring(g0 + 1, g2 - g0 - 1));
                        g0 = g2 + 1;
                    }

                    // Terminator: '{' on the same line or within the next two lines (';' too when
                    // allowed). Between the name and the terminator only base-clause characters are
                    // allowed (idents, : , . < > &, plus () for Rust tuple structs).
                    string basesRaw = "";
                    int termLine = -1;
                    int t = g0;
                    bool bad = false;
                    for (; t < len; t++)
                    {
                        char c = line[t];
                        if (c == '{' || (c == ';' && allowSemicolonTerminator))
                        { basesRaw = line.Substring(g0, t - g0); termLine = i; break; }
                        if (!IsIdentChar(c) && !char.IsWhiteSpace(c)
                            && c != ':' && c != ',' && c != '.' && c != '<' && c != '>' && c != '&'
                            && !(allowSemicolonTerminator && (c == '(' || c == ')'))) { bad = true; break; }
                    }
                    if (termLine < 0 && !bad)
                    {
                        for (int m2 = i + 1; m2 <= Math.Min(i + 2, f.LineCount - 1); m2++)
                        {
                            char fs = FirstSignificant(f.Stripped[m2]);
                            if (fs == '{' || (allowSemicolonTerminator && fs == ';')) { termLine = m2; break; }
                            if (fs != '\0') { bad = true; break; }
                        }
                    }
                    if (termLine < 0) { idx += kw.Length; continue; }   // forward declaration / usage
                    if (termLine > i) basesRaw = line.Substring(g0);    // '{' on a later line — the rest of this line is the base clause

                    // Access + qualifier words from the head (anything else is ignored — e.g. a C++ template clause)
                    string access = "", quals = "";
                    foreach (var tok in head.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (spec.AccessWords.Contains(tok)) { if (access.Length == 0) access = tok; }
                        else if (spec.QualifierWords.Contains(tok)) quals += (quals.Length > 0 ? " " : "") + tok;
                    }

                    // A "template<…>" clause belongs to this type (functions get the equivalent via
                    // ExtractTemplatePrefix) — capture it so MatchTypes can report template-parameter
                    // and explicit-specialization changes on templated classes. Same-line head first
                    // ("template<class T> class Foo"), else up to 3 preceding lines joined oldest-first
                    // with the function prefix's boundary rules (multi-line parameter lists).
                    string tmpl = "";
                    bool tmplPresent = false;
                    if (TryTemplateClause(head, out tmpl, out _, out _)) tmplPresent = true;
                    if (!tmplPresent)
                    {
                        string back = "";
                        for (int m = i - 1; m >= Math.Max(0, i - 3); m--)
                        {
                            char fs = FirstSignificant(f.Stripped[m]);
                            if (fs == '\0') continue;                    // blank line — keep looking
                            if (IsAccessLabelLine(f.Stripped[m])) break;  // C++ section label — not a template clause
                            if (fs is ';' or '{' or '}') break;          // statement/block boundary — stop
                            if (LastSignificant(f.Stripped[m]) is ';' or '{' or '}') break;   // complete statement/definition
                            string prevLine = new string(f.Stripped[m]);
                            if (!PrefixOk(prevLine)) break;              // not prefix material — stop (the type itself still stands)
                            back = back.Length == 0 ? prevLine : prevLine + " " + back;
                        }
                        if (back.Length > 0 && TryTemplateClause(back, out tmpl, out _, out _)) tmplPresent = true;
                    }

                    f.Types.Add(new CType
                    {
                        Kind = kw,
                        Name = name,
                        StartLine = i + 1,
                        Access = access,
                        Qualifiers = quals,
                        SpecArgs = generics,
                        TemplateClause = tmpl,
                        TemplateHeader = tmplPresent,
                        Generics = generics.Length > 0 ? generics : (tmplPresent ? tmpl : ""),
                        Bases = NormalizeBases(basesRaw, spec)
                    });
                    idx = r;   // keep scanning the rest of the line (minified one-line files)
                }
            }
        }
    }

    /// <summary>Java "extends"/"implements" are syntax, not base names — drop them. A leading
    /// ':' (C++ base-clause marker) is dropped too — it is syntax, not part of a base name.</summary>
    private static string NormalizeBases(string raw, LangSpec spec)
    {
        // Re-normalise after the colon strip: ": private B" → "private B", not " private B".
        var s = Normalize(Normalize(raw).TrimStart(':'));
        if (s.Length == 0 || spec.Lambda != LambdaStyle.Java) return s;
        var list = new List<string>();
        foreach (var t in s.Split(' '))
            if (t != "extends" && t != "implements") list.Add(t);
        return string.Join(" ", list);
    }

    /// <summary>
    /// Phase 4b' — cross-match types by name: no counterpart at all is an addition/removal;
    /// paired types get generic-list, base-clause and access/qualifier comparisons.
    /// </summary>
    private static void MatchTypes(IReadOnlyList<CType> la, IReadOnlyList<CType> lb, LangSpec spec, List<StructuralFinding> findings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in la) names.Add(t.Name);
        foreach (var t in lb) names.Add(t.Name);

        foreach (var name in names)
        {
            var a = new List<CType>();
            foreach (var t in la) if (t.Name == name) a.Add(t);
            var b = new List<CType>();
            foreach (var t in lb) if (t.Name == name) b.Add(t);

            if (a.Count == 0) { foreach (var t in b) findings.Add(new StructuralFinding(StructuralKind.TypeAdded, 0, t.StartLine, $"Type added: {Show(t)}")); continue; }
            if (b.Count == 0) { foreach (var t in a) findings.Add(new StructuralFinding(StructuralKind.TypeRemoved, t.StartLine, 0, $"Type removed: {Show(t)}")); continue; }

            // Pair by exact template context (header clause + post-name argument list): a primary
            // template and its explicit specializations share a name, so first-occurrence pairing
            // shadowed every specialization change.
            string Key(CType t) => t.TemplateClause + "`u0001" + t.SpecArgs;
            var usedB = new bool[b.Count];
            var unpairedA = new List<CType>();
            for (int i = 0; i < a.Count; i++)
            {
                int hit = -1;
                for (int j = 0; j < b.Count; j++)
                    if (!usedB[j] && Key(b[j]) == Key(a[i])) { hit = j; break; }
                if (hit >= 0) { usedB[hit] = true; CompareType(a[i], b[hit]); }
                else unpairedA.Add(a[i]);
            }
            var unpairedB = new List<CType>();
            for (int j = 0; j < b.Count; j++) if (!usedB[j]) unpairedB.Add(b[j]);

            if (unpairedA.Count == 1 && unpairedB.Count == 1)
                CompareType(unpairedA[0], unpairedB[0]);
            else
            {
                foreach (var t in unpairedA) findings.Add(new StructuralFinding(StructuralKind.TypeRemoved, t.StartLine, 0, $"Type removed: {Show(t)}"));
                foreach (var t in unpairedB) findings.Add(new StructuralFinding(StructuralKind.TypeAdded, 0, t.StartLine, $"Type added: {Show(t)}"));
            }
        }

        static string Show(CType t) => t.SpecArgs.Length > 0 ? $"{t.Kind} {t.Name}<{t.SpecArgs}>" : $"{t.Kind} {t.Name}";
        static string ShowClause(CType t) => t.TemplateHeader ? (t.TemplateClause.Length == 0 ? "<>" : t.TemplateClause) : "(none)";

        void CompareType(CType ta, CType tb)
        {
            bool specArgsChanged = ta.SpecArgs != tb.SpecArgs;
            if (specArgsChanged || ta.TemplateClause != tb.TemplateClause)
            {
                // Post-name lists that differ while the template header is unchanged (and present —
                // a "template<>" marker at least) are explicit-specialization ARGUMENTS, not parameters.
                bool args = specArgsChanged && ta.SpecArgs.Length > 0 && tb.SpecArgs.Length > 0
                            && ta.TemplateClause == tb.TemplateClause
                            && (ta.TemplateHeader || tb.TemplateHeader);
                if (args)
                    findings.Add(new StructuralFinding(StructuralKind.GenericChanged, ta.StartLine, tb.StartLine,
                        $"Template arguments changed: {ta.SpecArgs} → {tb.SpecArgs}"));
                else if (ta.TemplateClause != tb.TemplateClause && (ta.TemplateHeader || tb.TemplateHeader))
                    findings.Add(new StructuralFinding(StructuralKind.GenericChanged, ta.StartLine, tb.StartLine,
                        $"Template parameters changed: {ShowClause(ta)} → {ShowClause(tb)}"));
                else if (ta.Generics != tb.Generics)
                    findings.Add(new StructuralFinding(StructuralKind.GenericChanged, ta.StartLine, tb.StartLine,
                        $"Generic parameters changed: {(ta.Generics.Length == 0 ? "(none)" : ta.Generics)} → {(tb.Generics.Length == 0 ? "(none)" : tb.Generics)}"));
            }
            if (ta.Bases != tb.Bases)
                findings.Add(new StructuralFinding(StructuralKind.BaseChanged, ta.StartLine, tb.StartLine,
                    $"Base changed: {(ta.Bases.Length == 0 ? "(none)" : ta.Bases)} → {(tb.Bases.Length == 0 ? "(none)" : tb.Bases)}"));
            string qa = (ta.Access + " " + ta.Qualifiers).Trim(), qb = (tb.Access + " " + tb.Qualifiers).Trim();
            if (qa != qb)
                findings.Add(new StructuralFinding(StructuralKind.ModifierChanged, ta.StartLine, tb.StartLine, QualifierMessage(qa, qb, spec)));
        }
    }

    /// <summary>0-based lines on which a function definition starts (expression-bodied members included).</summary>
    private static HashSet<int> FunctionStartLines(CFile f)
    {
        var set = new HashSet<int>();
        foreach (var fn in f.Functions) set.Add(fn.StartLine - 1);
        return set;
    }

    /// <summary>
    /// Phase 4c — per-line lambda/closure signatures for the spec's lambda style ("" = none on
    /// that line). First marker per line only; extraction is strictly per-line, so identical
    /// lines always yield identical signatures and findings can never land on unchanged pairs.
    /// </summary>
    private static string[] FindLambdas(CFile f, LangSpec spec)
    {
        var sigs = new string[f.LineCount];
        for (int i = 0; i < f.LineCount; i++) sigs[i] = "";

        switch (spec.Lambda)
        {
            case LambdaStyle.Cpp:
                // Balanced "[...]" (optionally "mutable") immediately followed by "(params)". An
                // identifier char/digit right before '[' rejects array subscripts.
                for (int i = 0; i < f.LineCount; i++)
                {
                    string s = new string(f.Stripped[i]);
                    for (int j = 0; j < s.Length; j++)
                    {
                        if (s[j] != '[') continue;
                        int b = j;
                        while (b > 0 && s[b - 1] == ' ') b--;
                        if (b > 0 && IsIdentChar(s[b - 1])) { j++; continue; }
                        int dep = 0, e = j;
                        for (; e < s.Length; e++)
                        {
                            if (s[e] == '[') dep++;
                            else if (s[e] == ']') { dep--; if (dep == 0) break; }
                        }
                        if (e >= s.Length) continue;
                        int p = e + 1;
                        while (p < s.Length && s[p] == ' ') p++;
                        if (s.IndexOf("mutable", p, StringComparison.Ordinal) == p)
                        {
                            int m2 = p + 7;
                            if (m2 >= s.Length || !IsIdentChar(s[m2])) p = m2;
                            while (p < s.Length && s[p] == ' ') p++;
                        }
                        if (p >= s.Length || s[p] != '(') continue;
                        int d2 = 0, q = p;
                        for (; q < s.Length; q++)
                        {
                            if (s[q] == '(') d2++;
                            else if (s[q] == ')') { d2--; if (d2 == 0) break; }
                        }
                        if (q >= s.Length) continue;
                        sigs[i] = Normalize(s.Substring(j, q - j + 1));
                        break;   // first lambda per line
                    }
                }
                break;

            case LambdaStyle.CSharp:
            case LambdaStyle.Java:
            {
                // First "=>" (C#) / "->" (Java); a line where a function definition starts is an
                // expression-bodied member, not a lambda — skipped entirely. Java switch arrows
                // ("case 1 -> …") are rejected via the preceding-word check below.
                var fnStart = FunctionStartLines(f);
                for (int i = 0; i < f.LineCount; i++)
                {
                    string s = new string(f.Stripped[i]);
                    int at = -1;
                    for (int j = 0; j + 1 < s.Length; j++)
                    {
                        bool arrow = spec.Lambda == LambdaStyle.CSharp
                            ? (s[j] == '=' && s[j + 1] == '>')   // C# "=>"
                            : (s[j] == '-' && s[j + 1] == '>');  // Java "->"
                        if (arrow) { at = j; break; }
                    }
                    if (at < 0 || fnStart.Contains(i)) continue;

                    int p = at;
                    while (p > 0 && s[p - 1] == ' ') p--;
                    string sig;
                    int before;   // position of the token's start, for the "case" guard
                    if (p > 0 && s[p - 1] == ')')
                    {
                        int dep = 0, e2 = p - 1;
                        for (; e2 >= 0; e2--)
                        {
                            if (s[e2] == ')') dep++;
                            else if (s[e2] == '(') { dep--; if (dep == 0) break; }
                        }
                        if (e2 < 0) continue;
                        sig = Normalize(s.Substring(e2 + 1, p - 1 - e2 - 1));   // inside the parens
                        before = e2;
                    }
                    else
                    {
                        int e2 = p;
                        while (e2 > 0 && IsIdentChar(s[e2 - 1])) e2--;
                        if (e2 == p) continue;   // nothing before the arrow — not a lambda
                        sig = Normalize(s.Substring(e2, p - e2));
                        before = e2;
                    }

                    int w = before;
                    while (w > 0 && s[w - 1] == ' ') w--;
                    int we = w;
                    while (we > 0 && IsIdentChar(s[we - 1])) we--;
                    if (s.Substring(we, w - we) == "case") continue;   // Java switch arrow

                    sigs[i] = sig;
                }
                break;
            }

            case LambdaStyle.Rust:
                // Balanced "|params|" where the char before the first '|' is one of {= ( , [ ;} or
                // start-of-line, or the word return/move/async — kills bitwise-OR false positives.
                for (int i = 0; i < f.LineCount; i++)
                {
                    string s = new string(f.Stripped[i]);
                    for (int j = 0; j < s.Length; j++)
                    {
                        if (s[j] != '|') continue;
                        int b = j;
                        while (b > 0 && s[b - 1] == ' ') b--;
                        bool ctxOk = false;
                        if (b == 0) ctxOk = true;
                        else
                        {
                            char c = s[b - 1];
                            if (c == '=' || c == '(' || c == ',' || c == '[' || c == ';' || c == '{') ctxOk = true;
                            else
                            {
                                int we = b;
                                while (we > 0 && IsIdentChar(s[we - 1])) we--;
                                if (s.Substring(we, b - we) is "return" or "move" or "async") ctxOk = true;
                            }
                        }
                        if (!ctxOk) { j++; continue; }

                        int e = j + 1;
                        bool ok2 = true;
                        for (; e < s.Length; e++)
                        {
                            if (s[e] == '|') break;
                            if (!IsIdentChar(s[e]) && !char.IsWhiteSpace(s[e])
                                && s[e] != ':' && s[e] != ',' && s[e] != '&' && s[e] != '*') { ok2 = false; break; }
                        }
                        if (!ok2 || e >= s.Length) { j++; continue; }

                        // A preceding move/async is part of the signature
                        int we2 = b;
                        while (we2 > 0 && IsIdentChar(s[we2 - 1])) we2--;
                        string prevWord = s.Substring(we2, b - we2);
                        int start = prevWord is "move" or "async" ? we2 : j;
                        sigs[i] = Normalize(s.Substring(start, e - start + 1));
                        break;   // first closure per line
                    }
                }
                break;

            case LambdaStyle.Python:
                // Whole-word "lambda", params up to the first ':' at bracket depth 0 (dict defaults
                // like "lambda d={}: d" stay inside the signature).
                for (int i = 0; i < f.LineCount; i++)
                {
                    string s = new string(f.Stripped[i]);
                    int idx = 0;
                    while ((idx = IndexOfWord(s, "lambda", idx)) >= 0)
                    {
                        int p = idx + 6;
                        int dep2 = 0, e3 = p;
                        for (; e3 < s.Length; e3++)
                        {
                            char c = s[e3];
                            if (c == '(' || c == '[' || c == '{') dep2++;
                            else if (c == ')' || c == ']' || c == '}') dep2--;
                            else if (c == ':' && dep2 == 0) break;
                        }
                        if (e3 >= s.Length) { idx += 6; continue; }   // no colon on this line
                        sigs[i] = Normalize("lambda " + s.Substring(p, e3 - p));
                        break;
                    }
                }
                break;
        }

        return sigs;
    }

    /// <summary>
    /// Phase 4d — cross-compare lambda signatures on aligned line pairs only: both present and
    /// different is a signature change, one side only is an add/remove. Max one finding per pair.
    /// </summary>
    private static void MatchLambdas(string[] sa, string[] sb, int[] aToB, List<StructuralFinding> findings)
    {
        for (int i = 0; i < sa.Length; i++)
        {
            int m = aToB[i];
            if (m < 0 || m >= sb.Length) continue;
            string xa = sa[i], xb = sb[m];
            if (xa == xb) continue;
            string msg = xa.Length == 0 ? $"Lambda removed: {xb}"
                        : xb.Length == 0 ? $"Lambda added: {xa}"
                        : $"Lambda signature changed: {xa} → {xb}";
            findings.Add(new StructuralFinding(StructuralKind.LambdaChanged, i + 1, m + 1, msg));
        }
    }

    /// <summary>
    /// Phase 5 — cross-match control blocks through the alignment, in line order. An A block's
    /// counterpart is the first unused B block whose header sits within ±2 of our own header's
    /// aligned B line; only when our header is itself unpaired do we fall back to the image span
    /// of the whole block. Line order matters: an outer block must match before the blocks
    /// nested inside it, so a "first unused" scan always reaches the counterpart before any
    /// orphan in its body. Unmatched blocks on either side are additions/removals.
    /// </summary>
    private static void MatchBlocks(CFile fa, CFile fb, int[] aToB, List<StructuralFinding> findings)
    {
        // FindBlocks emits per keyword (all ifs, then all fors…) — sort both sides by start line
        // so the outer/inner nesting order is preserved for the first-unused scan below.
        fa.Blocks.Sort((x, y) => x.StartLine.CompareTo(y.StartLine));
        fb.Blocks.Sort((x, y) => x.StartLine.CompareTo(y.StartLine));

        var usedB = new bool[fb.Blocks.Count];

        for (int i = 0; i < fa.Blocks.Count; i++)
        {
            var ba = fa.Blocks[i];
            int sa = ba.StartLine - 1;
            int m = aToB[sa];

            int hitIdx = -1;
            if (m >= 0)
            {
                // Anchor on the header line: the counterpart's header sits within ±2 of our
                // header's aligned B line (positional-pairing drift inside change blocks).
                // Anchoring on the start — not the whole body image — keeps a block from
                // matching an unrelated block nested inside its own body.
                int lo = Math.Max(0, m - 2), hi = Math.Min(fb.LineCount - 1, m + 2);
                for (int j = 0; j < fb.Blocks.Count; j++)
                {
                    if (usedB[j]) continue;
                    int s = fb.Blocks[j].StartLine - 1;
                    if (s > hi) break;                 // sorted — later blocks start even later
                    if (s >= lo) { hitIdx = j; break; }
                }
            }
            else
            {
                // Header unpaired (the header line itself was rewritten or deleted): fall back
                // to the image of the whole block — first unused B block intersecting that span.
                int ea = Math.Min(ba.EndLine - 1, aToB.Length - 1);
                int minB = int.MaxValue, maxB = int.MinValue;
                for (int k = sa; k <= ea; k++)
                {
                    int mm = aToB[k];
                    if (mm >= 0)
                    {
                        if (mm < minB) minB = mm;
                        if (mm > maxB) maxB = mm;
                    }
                }
                if (minB <= maxB)
                {
                    int lo = Math.Max(0, minB - 2), hi = Math.Min(fb.LineCount - 1, maxB + 2);
                    for (int j = 0; j < fb.Blocks.Count; j++)
                    {
                        if (usedB[j]) continue;
                        var cand = fb.Blocks[j];
                        if (cand.StartLine - 1 > hi) break;
                        if (cand.EndLine - 1 >= lo) { hitIdx = j; break; }
                    }
                }
            }

            if (hitIdx < 0)
            {
                findings.Add(new StructuralFinding(StructuralKind.ControlBlockRemoved, ba.StartLine, 0,
                    $"Control block removed: {ba.Header}"));
                continue;
            }

            usedB[hitIdx] = true;
            var bb = fb.Blocks[hitIdx];
            if (bb.Kind != ba.Kind)
                findings.Add(new StructuralFinding(StructuralKind.ControlBlockModified, ba.StartLine, bb.StartLine,
                    $"Control flow changed: {ba.Kind} → {bb.Kind}"));
            else if (bb.Header != ba.Header)
                findings.Add(new StructuralFinding(StructuralKind.ControlBlockModified, ba.StartLine, bb.StartLine,
                    ba.Kind == "if" ? "If condition changed" : $"{ba.Kind} condition changed"));
        }

        for (int j = 0; j < fb.Blocks.Count; j++)
            if (!usedB[j])
                findings.Add(new StructuralFinding(StructuralKind.ControlBlockAdded, 0, fb.Blocks[j].StartLine,
                    $"Control block added: {fb.Blocks[j].Header}"));
    }

    /// <summary>
    /// Phase 6 — nesting drift: over aligned line pairs, a maximal run (≥ 2 consecutive pairs)
    /// where A's depth is consistently offset from B's means the nesting structure shifted there.
    /// One finding at the first pair in the run whose text actually differs — a hint must sit on
    /// a real diff line; runs where every pair is identical (a pure structural shift with no
    /// reindent) are dropped, because the brace lines that caused the shift are already reported
    /// as block additions/removals on their own diff lines. Single-line offsets are noise.
    /// </summary>
    private static void NestingRuns(CFile fa, CFile fb, int[] aToB, List<StructuralFinding> findings)
    {
        int runStart = -1, runEnd = -1, runDiff = 0;

        void Flush()
        {
            if (runStart >= 0 && runEnd - runStart + 1 >= 2)
            {
                for (int k = runStart; k <= runEnd; k++)
                {
                    int bIdx = aToB[k];
                    if (bIdx < 0) continue;
                    if (fa.Lines[k] != fb.Lines[bIdx])
                    {
                        findings.Add(new StructuralFinding(StructuralKind.NestingChanged, k + 1, bIdx + 1,
                            $"Nesting depth shifted by {(runDiff > 0 ? "+" : "")}{runDiff}"));
                        break;
                    }
                }
            }
            runStart = -1;
        }

        for (int aIdx = 0; aIdx < aToB.Length; aIdx++)
        {
            int bIdx = aToB[aIdx];
            int d = bIdx < 0 ? 0 : fa.DepthAfter[aIdx] - fb.DepthAfter[bIdx];

            if (d != 0 && runStart >= 0 && d == runDiff) { runEnd = aIdx; continue; }
            Flush();
            if (d != 0) { runStart = aIdx; runEnd = aIdx; runDiff = d; }
        }
        Flush();
    }

    // ── JSON ────────────────────────────────────────────────────────────────────

    private sealed class JsonNode
    {
        public string Path = "";
        public string Kind = "";      // "object" / "array" / "string" / "number" / "bool" / "null"
        public int StartLine;         // 1-based
    }

    private sealed class JsonFrame
    {
        public bool IsObject;
        public string Path = "";
        public int ArrayIndex;
        public string? PendingKey;
    }

    /// <summary>Carries the 1-based line of a JSON parse failure.</summary>
    private sealed class JsonParseError : Exception
    {
        public int Line { get; }
        public JsonParseError(int line) : base("json parse error") => Line = line;
    }

    /// <summary>
    /// Parses JSON into a path → node tree (System.Text.Json has no line numbers, so this is a
    /// small hand-rolled scanner). Paths are "$", "$.key", "$[i]", … A malformed document yields
    /// (null, errorLine) instead of a partial tree.
    /// </summary>
    private static (Dictionary<string, JsonNode>?, int errorLine) ParseJson(List<string> lines)
    {
        var text = string.Join("\n", lines);
        var nodes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var frames = new List<JsonFrame>();
        int i = 0, len = text.Length, curLine = 1;

        // States: 0 root expected · 1 object key or '}' · 2 ':' expected · 3 value expected ·
        //         4 ',' or '}' after an object value · 5 array element or ']' · 6 ',' or ']'
        //         after an array value · 7 done
        int state = 0;

        void Fail() => throw new JsonParseError(curLine);

        string ChildPath(JsonFrame top) =>
            top.IsObject ? $"{top.Path}.{top.PendingKey}" : $"{top.Path}[{top.ArrayIndex}]";

        void Pop()
        {
            frames.RemoveAt(frames.Count - 1);
            state = frames.Count == 0 ? 7 : (frames[^1].IsObject ? 4 : 6);
        }

        string ReadString()
        {
            int start = ++i;                          // past the opening quote
            while (i < len)
            {
                char c = text[i];
                if (c == '\\') { i += 2; continue; }
                if (c == '"') { string s = text.Substring(start, i - start); i++; return s; }
                if (c == '\n') Fail();                // raw newlines are illegal in JSON strings
                i++;
            }
            Fail();
            return "";
        }

        void AddScalar(string kind)
        {
            var top = frames[^1];
            string p = ChildPath(top);
            nodes[p] = new JsonNode { Path = p, Kind = kind, StartLine = curLine };
            if (top.IsObject) top.PendingKey = null;
            else top.ArrayIndex++;
            state = top.IsObject ? 4 : 6;
        }

        try
        {
            while (true)
            {
                while (i < len)
                {
                    char c0 = text[i];
                    if (c0 == '\n') { curLine++; i++; continue; }
                    if (c0 == ' ' || c0 == '\t' || c0 == '\r') { i++; continue; }
                    break;
                }
                if (i >= len) break;

                char c = text[i];

                switch (state)
                {
                    case 0:
                        if (c != '{' && c != '[') Fail();
                        break;
                    case 1:
                        if (c == '}') { i++; Pop(); continue; }
                        if (c == '"')
                        {
                            frames[^1].PendingKey = ReadString();
                            state = 2;
                            continue;
                        }
                        Fail();
                        break;
                    case 2:
                        if (c != ':') Fail();
                        i++;
                        state = 3;
                        continue;
                    case 3:
                        break;
                    case 4:
                        if (c == ',') { i++; state = 1; continue; }
                        if (c == '}') { i++; Pop(); continue; }
                        Fail();
                        break;
                    case 5:
                        if (c == ']') { i++; Pop(); continue; }
                        break;
                    case 6:
                        if (c == ',') { i++; state = 5; continue; }
                        if (c == ']') { i++; Pop(); continue; }
                        Fail();
                        break;
                    default:
                        Fail();                       // trailing content after the root value
                        break;
                }

                // Value dispatch
                var top = frames.Count > 0 ? frames[^1] : null;
                if (c == '{' || c == '[')
                {
                    var frame = new JsonFrame
                    {
                        IsObject = c == '{',
                        Path = top == null ? "$" : ChildPath(top)
                    };
                    frames.Add(frame);
                    nodes[frame.Path] = new JsonNode
                    {
                        Path = frame.Path,
                        Kind = frame.IsObject ? "object" : "array",
                        StartLine = curLine
                    };
                    if (top != null && !top.IsObject) top.ArrayIndex++;
                    state = frame.IsObject ? 1 : 5;
                    i++;
                }
                else if (c == '"')
                {
                    ReadString();
                    AddScalar("string");
                }
                else if (text.Substring(i, Math.Min(4, len - i)) == "true")
                {
                    i += 4;
                    AddScalar("bool");
                }
                else if (text.Substring(i, Math.Min(5, len - i)) == "false")
                {
                    i += 5;
                    AddScalar("bool");
                }
                else if (text.Substring(i, Math.Min(4, len - i)) == "null")
                {
                    i += 4;
                    AddScalar("null");
                }
                else if (char.IsDigit(c) || c == '-')
                {
                    if (c == '-') i++;
                    bool digits = false;
                    while (i < len && char.IsDigit(text[i])) { i++; digits = true; }
                    if (i < len && text[i] == '.')
                    {
                        i++;
                        while (i < len && char.IsDigit(text[i])) { i++; digits = true; }
                    }
                    if (i < len && (text[i] == 'e' || text[i] == 'E'))
                    {
                        i++;
                        if (i < len && (text[i] == '+' || text[i] == '-')) i++;
                        while (i < len && char.IsDigit(text[i])) i++;
                    }
                    if (!digits) Fail();
                    AddScalar("number");
                }
                else Fail();
            }

            if (state != 7) Fail();                   // unterminated document
        }
        catch (JsonParseError e)
        {
            return (null, e.Line);
        }

        return (nodes, 0);
    }

    private static List<StructuralFinding> AnalyzeJson(FileModel a, FileModel b)
    {
        var ra = ParseJson(a.Lines);
        var rb = ParseJson(b.Lines);
        var findings = new List<StructuralFinding>();

        if (ra.Item1 == null || rb.Item1 == null)
        {
            if (ra.Item1 == null)
                findings.Add(new StructuralFinding(StructuralKind.InvalidDocument, ra.Item2, 0, $"JSON parse error at line {ra.Item2}"));
            if (rb.Item1 == null)
                findings.Add(new StructuralFinding(StructuralKind.InvalidDocument, 0, rb.Item2, $"JSON parse error at line {rb.Item2}"));
            return findings;
        }

        var ta = ra.Item1!;
        var tb = rb.Item1!;

        // Paths whose value kind changed — the whole subtree is new on both sides, so add/
        // remove findings below such a path are suppressed.
        var typeChanged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in ta)
            if (tb.TryGetValue(kv.Key, out var nb) && kv.Value.Kind != nb.Kind)
                typeChanged.Add(kv.Key);

        foreach (var kv in tb)
        {
            if (ta.ContainsKey(kv.Key)) continue;
            if (JsonSuppressed(kv.Key, ta, typeChanged)) continue;
            findings.Add(new StructuralFinding(StructuralKind.JsonKeyAdded, 0, kv.Value.StartLine,
                JsonPathMessage(kv.Key, "added")));
        }
        foreach (var kv in ta)
        {
            if (tb.ContainsKey(kv.Key)) continue;
            if (JsonSuppressed(kv.Key, tb, typeChanged)) continue;
            findings.Add(new StructuralFinding(StructuralKind.JsonKeyRemoved, kv.Value.StartLine, 0,
                JsonPathMessage(kv.Key, "removed")));
        }

        foreach (var kv in ta)
            if (tb.TryGetValue(kv.Key, out var nb) && kv.Value.Kind != nb.Kind)
                findings.Add(new StructuralFinding(StructuralKind.JsonValueTypeChanged, kv.Value.StartLine, nb.StartLine,
                    $"Value type changed: {kv.Value.Kind} → {nb.Kind}"));

        return findings;
    }

    /// <summary>"JSON key added: $.d" / "JSON array element removed: $[3]" — by path shape.</summary>
    private static string JsonPathMessage(string path, string direction) =>
        $"JSON {(path.EndsWith("]") ? "array element" : "key")} {direction}: {path}";

    /// <summary>
    /// True when an add/remove finding for path should be suppressed: an ancestor is also new on
    /// the other side (report only the highest new node), or an ancestor's value kind changed
    /// (the whole subtree is new).
    /// </summary>
    private static bool JsonSuppressed(string path, Dictionary<string, JsonNode> other, HashSet<string> typeChanged)
    {
        for (int i = path.Length - 1; i >= 0; i--)
        {
            char c = path[i];
            if (c != '.' && c != '[') continue;
            string anc = path.Substring(0, i);
            if (anc.Length == 0) continue;
            if (!other.ContainsKey(anc) || typeChanged.Contains(anc)) return true;
        }
        return false;
    }

    // ── XML ──────────────────────────────────────────────────────────────────────

    private sealed class XmlNode
    {
        public string Path = "";      // "root/a/b" (qualified names, no leading slash)
        public Dictionary<string, string> Attrs = new(StringComparer.Ordinal);
        public int StartLine;         // 1-based (XmlTextReader.LineNumber)
    }

    /// <summary>
    /// Parses XML into a path → node tree via XmlTextReader — the only built-in reader that still
    /// exposes LineNumber (1-based element start lines; plain XmlReader dropped it). A malformed
    /// document yields (null, errorLine). DTD processing is off — no external entities are
    /// fetched or expanded.
    /// </summary>
    private static (Dictionary<string, XmlNode>?, int errorLine) ParseXml(List<string> lines)
    {
        var nodes = new Dictionary<string, XmlNode>(StringComparer.Ordinal);
        var stack = new Stack<XmlNode>();

        try
        {
 #pragma warning disable CS0618   // XmlTextReader is obsolete but the only built-in reader with LineNumber
            using var reader = new XmlTextReader(new StringReader(string.Join("\n", lines)));
#pragma warning restore CS0618
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string path = stack.Count == 0 ? reader.Name : stack.Peek().Path + "/" + reader.Name;
                    var node = new XmlNode { Path = path, StartLine = reader.LineNumber };
                    while (reader.MoveToNextAttribute())
                        node.Attrs[reader.Name] = reader.Value;
                    reader.MoveToElement();
                    stack.Push(node);
                    nodes[path] = node;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && stack.Count > 0)
                {
                    stack.Pop();
                }
            }
        }
        catch (XmlException e)
        {
            return (null, Math.Max(1, e.LineNumber));
        }
        catch (InvalidOperationException)
        {
            // XmlTextReader's DtdProcessing.Prohibit default throws this on a DOCTYPE declaration.
            return (null, 1);
        }

        return (nodes, 0);
    }

    private static List<StructuralFinding> AnalyzeXml(FileModel a, FileModel b)
    {
        var ra = ParseXml(a.Lines);
        var rb = ParseXml(b.Lines);
        var findings = new List<StructuralFinding>();

        if (ra.Item1 == null || rb.Item1 == null)
        {
            if (ra.Item1 == null)
                findings.Add(new StructuralFinding(StructuralKind.InvalidDocument, ra.Item2, 0, $"XML parse error at line {ra.Item2}"));
            if (rb.Item1 == null)
                findings.Add(new StructuralFinding(StructuralKind.InvalidDocument, 0, rb.Item2, $"XML parse error at line {rb.Item2}"));
            return findings;
        }

        var ta = ra.Item1!;
        var tb = rb.Item1!;

        foreach (var kv in tb)
        {
            if (ta.ContainsKey(kv.Key)) continue;
            if (XmlSuppressed(kv.Key, ta)) continue;
            findings.Add(new StructuralFinding(StructuralKind.XmlElementAdded, 0, kv.Value.StartLine,
                $"Element added: {kv.Key}"));
        }
        foreach (var kv in ta)
        {
            if (tb.ContainsKey(kv.Key)) continue;
            if (XmlSuppressed(kv.Key, tb)) continue;
            findings.Add(new StructuralFinding(StructuralKind.XmlElementRemoved, kv.Value.StartLine, 0,
                $"Element removed: {kv.Key}"));
        }

        // Attribute changes on common elements — one aggregated finding per element.
        foreach (var kv in ta)
        {
            if (!tb.TryGetValue(kv.Key, out var nb)) continue;
            var na = kv.Value;
            var parts = new List<string>();
            foreach (var attr in na.Attrs)
                if (nb.Attrs.TryGetValue(attr.Key, out var vb) && attr.Value != vb)
                    parts.Add($"Attribute changed: {attr.Key}=\"{attr.Value}\" → \"{vb}\"");
            foreach (var attr in nb.Attrs)
                if (!na.Attrs.ContainsKey(attr.Key))
                    parts.Add($"Attribute added: {attr.Key}");
            foreach (var attr in na.Attrs)
                if (!nb.Attrs.ContainsKey(attr.Key))
                    parts.Add($"Attribute removed: {attr.Key}");
            if (parts.Count > 0)
                findings.Add(new StructuralFinding(StructuralKind.XmlAttributeChanged, na.StartLine, nb.StartLine,
                    string.Join("; ", parts)));
        }

        return findings;
    }

    /// <summary>True when an ancestor element is also missing on the other side (report only the highest new/removed node).</summary>
    private static bool XmlSuppressed(string path, Dictionary<string, XmlNode> other)
    {
        int slash = path.LastIndexOf('/');
        while (slash >= 0)
        {
            if (!other.ContainsKey(path.Substring(0, slash))) return true;
            slash = path.LastIndexOf('/', slash - 1);
        }
        return false;
    }
}
