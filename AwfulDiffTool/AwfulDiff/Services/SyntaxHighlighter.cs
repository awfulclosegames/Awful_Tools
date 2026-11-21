using System.Collections.Generic;
using System.IO;

namespace AwfulDiff.Services;

/// <summary>
/// Category of a syntax token. Tokens store the kind, never a brush — brushes are resolved
/// from the active theme at render time, so switching themes requires no re-tokenization.
/// </summary>
public enum SyntaxTokenKind
{
    Keyword,
    String,
    Comment,
    Number,
    Preprocessor,
    /// <summary>Identifier immediately followed by '(' — a function/method call or definition.</summary>
    Function,
    /// <summary>Capitalized identifier (PascalCase heuristic) — a type/class/struct name.</summary>
    Type
}

/// <summary>
/// A contiguous run of characters within one line. Only coloured (non-default) tokens are
/// stored; the gaps between them render in the diff status foreground. Tokens never overlap
/// and appear in ascending order.
/// </summary>
public readonly struct SyntaxToken
{
    public int Start { get; }
    public int Length { get; }
    public SyntaxTokenKind Kind { get; }

    public SyntaxToken(int start, int length, SyntaxTokenKind kind)
    {
        Start = start;
        Length = length;
        Kind = kind;
    }
}

/// <summary>
/// Line-oriented syntax tokenizer with Visual Studio-style categories (keyword / string /
/// comment / number / preprocessor). Language is detected from the file extension.
///
/// Stateful across lines only for unterminated block comments ("/* ... */", "&lt;!-- ... --&gt;"),
/// so a whole file must be tokenized sequentially with one instance (see <see cref="Reset"/>).
/// </summary>
public sealed class SyntaxHighlighter
{
    /// <summary>Above this many lines, tokenization is skipped (perf guard for huge files).</summary>
    public const int MaxLines = 100_000;

    // ── Language profiles ──────────────────────────────────────────────────────

    private sealed class Profile
    {
        public readonly HashSet<string> Keywords;
        public readonly string? LineComment;                       // "//" or "#"
        public readonly (string Open, string Close)? BlockComment; // ("/*","*/") or ("<!--","-->")
        public readonly bool HashIsPreprocessor;                   // C-family '#' directives

        public Profile(string[] keywords, string? lineComment, (string, string)? blockComment, bool hashIsPreprocessor)
        {
            Keywords = new(keywords, System.StringComparer.Ordinal);
            LineComment = lineComment;
            BlockComment = blockComment;
            HashIsPreprocessor = hashIsPreprocessor;
        }
    }

    // Union keyword set for the C-family (C/C++/C#/Java/JS/TS/ObjC) — a superset is fine:
    // the worst case is an ordinary identifier that happens to spell a keyword of another
    // language in the union getting coloured.
    private static readonly string[] CFamilyKeywords =
    {
        "abstract", "alignas", "alignof", "and", "as", "assert", "async", "auto", "await",
        "base", "bool", "break", "byte", "case", "catch", "char", "class", "const",
        "constexpr", "continue", "def", "decltype", "default", "delegate", "del", "delete",
        "do", "double", "dynamic_cast", "else", "elif", "enum", "except", "explicit",
        "export", "extern", "False", "false", "final", "finally", "fixed", "float", "for",
        "friend", "from", "global", "goto", "if", "implicit", "import", "in", "int",
        "interface", "internal", "is", "lambda", "let", "long", "match", "mutable",
        "namespace", "new", "nil", "None", "nonlocal", "noexcept", "not", "null", "nullptr",
        "operator", "or", "override", "package", "pass", "private", "protected", "public",
        "raise", "readonly", "register", "return", "sealed", "short", "sizeof", "static",
        "struct", "switch", "template", "this", "throw", "True", "true", "try", "typedef",
        "typeof", "union", "unsigned", "using", "var", "virtual", "void", "volatile",
        "while", "with", "yield"
    };

    private static readonly Profile CFamily = new(
        CFamilyKeywords, lineComment: "//", blockComment: ("/*", "*/"), hashIsPreprocessor: true);

    private static readonly Profile Python = new(
        CFamilyKeywords, lineComment: "#", blockComment: null, hashIsPreprocessor: false);

    private static readonly Profile Json = new(
        System.Array.Empty<string>(), lineComment: null, blockComment: null, hashIsPreprocessor: false);

    private static readonly Profile JsonC = new(
        System.Array.Empty<string>(), lineComment: "//", blockComment: ("/*", "*/"), hashIsPreprocessor: false);

    private static readonly Profile Xml = new(
        System.Array.Empty<string>(), lineComment: null, blockComment: ("<!--", "-->"), hashIsPreprocessor: false);

    // ── State ──────────────────────────────────────────────────────────────────

    private readonly Profile _profile;
    private bool _inBlockComment;

    private SyntaxHighlighter(Profile profile) => _profile = profile;

    /// <summary>
    /// Creates a tokenizer for the language of the given file path, or null when the
    /// extension is not recognized (no highlighting). Returns a fresh instance — block
    /// comment state starts clean.
    /// </summary>
    public static SyntaxHighlighter? Create(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;

        Profile? profile = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" or ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh"
                or ".java" or ".js" or ".ts" or ".jsx" or ".tsx" or ".m" or ".mm" => CFamily,
            ".py" or ".pyw" => Python,
            ".json" => Json,
            ".jsonc" => JsonC,
            ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".svg" or ".config" or ".resx" => Xml,
            _ => null
        };

        return profile == null ? null : new SyntaxHighlighter(profile);
    }

    /// <summary>Clears cross-line state (unterminated block comment). Call before tokenizing a new file.</summary>
    public void Reset() => _inBlockComment = false;

    /// <summary>
    /// Tokenizes one line, continuing any unterminated block comment from the previous line.
    /// Returns only the coloured tokens; an empty list means the whole line renders in the
    /// diff status foreground.
    /// </summary>
    public List<SyntaxToken> TokenizeLine(string line)
    {
        var tokens = new List<SyntaxToken>(4);
        int len = line.Length;
        int i = 0;

        // Resume a block comment opened on an earlier line
        if (_inBlockComment && _profile.BlockComment is (var open, var close))
        {
            int end = line.IndexOf(close, System.StringComparison.Ordinal);
            if (end < 0)
            {
                tokens.Add(new SyntaxToken(0, len, SyntaxTokenKind.Comment));
                return tokens;
            }
            i = end + close.Length;
            tokens.Add(new SyntaxToken(0, i, SyntaxTokenKind.Comment));
            _inBlockComment = false;
        }

        while (i < len)
        {
            char c = line[i];

            // Preprocessor directive — '#' only when it starts the line (C-family)
            if (c == '#' && _profile.HashIsPreprocessor)
            {
                bool atLineStart = true;
                for (int k = 0; k < i; k++)
                    if (!char.IsWhiteSpace(line[k])) { atLineStart = false; break; }

                if (atLineStart)
                {
                    tokens.Add(new SyntaxToken(i, len - i, SyntaxTokenKind.Preprocessor));
                    return tokens;   // the whole rest of the line is the directive
                }
                i++;
                continue;
            }

            // Line comment — runs to end of line
            if (_profile.LineComment != null && c == _profile.LineComment[0]
                && line.IndexOf(_profile.LineComment, i, System.StringComparison.Ordinal) == i)
            {
                tokens.Add(new SyntaxToken(i, len - i, SyntaxTokenKind.Comment));
                return tokens;
            }

            // Block comment — may or may not close on this line (bOpen/bClose: the names
            // open/close are taken by the enclosing-scope pattern variables above)
            if (_profile.BlockComment is (var bOpen, var bClose) && c == bOpen[0]
                && line.IndexOf(bOpen, i, System.StringComparison.Ordinal) == i)
            {
                int start = i;
                int end = line.IndexOf(bClose, i + bOpen.Length, System.StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new SyntaxToken(start, len - start, SyntaxTokenKind.Comment));
                    _inBlockComment = true;
                    return tokens;
                }
                i = end + bClose.Length;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Comment));
                continue;
            }

            // String / char literal with backslash escapes
            if (c == '"' || c == '\'')
            {
                int start = i;
                int j = i + 1;
                while (j < len)
                {
                    if (line[j] == '\\') { j += 2; continue; }
                    if (line[j] == c) { j++; break; }
                    j++;
                }
                tokens.Add(new SyntaxToken(start, Math.Min(j, len) - start, SyntaxTokenKind.String));
                i = Math.Min(j, len);
                continue;
            }

            // Identifier — keyword if in the language table; otherwise a function when an
            // opening paren follows (skipping whitespace), or a type when capitalized. Both
            // are heuristics (no full parse), matching how VS colours calls/PascalCase names.
            if (char.IsLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < len && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                string word = line.Substring(i, j - i);
                if (_profile.Keywords.Contains(word))
                {
                    tokens.Add(new SyntaxToken(i, j - i, SyntaxTokenKind.Keyword));
                }
                else
                {
                    int k = j;
                    while (k < len && char.IsWhiteSpace(line[k])) k++;
                    if (k < len && line[k] == '(')
                        tokens.Add(new SyntaxToken(i, j - i, SyntaxTokenKind.Function));
                    else if (char.IsUpper(word[0]))
                        tokens.Add(new SyntaxToken(i, j - i, SyntaxTokenKind.Type));
                }
                i = j;
                continue;
            }

            // Number literal (decimal/hex/float plus trailing suffix chars like 'f' or 'L')
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int j = i + 1;
                while (j < len && (char.IsLetterOrDigit(line[j]) || line[j] == '.')) j++;
                tokens.Add(new SyntaxToken(i, j - i, SyntaxTokenKind.Number));
                i = j;
                continue;
            }

            i++;
        }

        return tokens;
    }
}
