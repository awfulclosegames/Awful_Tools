using System.IO;

namespace AwfulDiff.Models;

/// <summary>
/// Wraps a file's path, content lines, and metadata.
/// </summary>
public class FileModel
{
    public string? FilePath { get; set; }
    public List<string> Lines { get; set; } = new();
    public byte[] RawBytes { get; set; } = Array.Empty<byte>();
    public DateTime? LastWriteTime { get; set; }
    public long? FileSize { get; set; }

    /// <summary>
    /// Cached display name (file path basename) — computed once on load to avoid repeated Split() allocations.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Cached formatted size string — computed once on load to avoid repeated formatting.
    /// </summary>
    public string FormattedSize { get; set; } = "";

    /// <summary>
    /// Cached formatted timestamp — computed once on load to avoid repeated formatting.
    /// </summary>
    public string FormattedTime { get; set; } = "";

    public bool HasContent => FilePath != null && Lines.Count > 0;

    /// <summary>
    /// Detects if the file is likely binary by checking for null bytes
    /// within the first 8192 bytes.
    /// </summary>
    public bool IsBinary()
    {
        int checkLength = Math.Min(RawBytes.Length, 8192);
        for (int i = 0; i < checkLength; i++)
        {
            if (RawBytes[i] == 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when this file should open in the hex view: content sniffing finds null bytes AND
    /// the extension is not a known text/code type. Known text/code types always open in the
    /// text view even if they contain null bytes (e.g. C sources embedding \0).
    /// </summary>
    public bool RequiresHexView() => IsBinary() && !IsKnownTextType(FilePath);

    /// <summary>
    /// Extensions treated as text/code source — the union of the open-dialog filter and the
    /// syntax highlighter's language table, plus other common text formats. Files with these
    /// extensions open in the text view regardless of content sniffing.
    /// </summary>
    public static bool IsKnownTextType(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return KnownTextExtensions.Contains(Path.GetExtension(path));
    }

    private static readonly HashSet<string> KnownTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Plain text / docs
        ".txt", ".md", ".log", ".tex",
        // Code
        ".cs", ".vb", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh",
        ".js", ".jsx", ".ts", ".tsx", ".m", ".mm", ".py", ".pyw", ".java",
        ".rs", ".go", ".rb", ".swift", ".kt", ".scala", ".php", ".pl", ".lua",
        // Scripts / build
        ".sh", ".bash", ".bat", ".cmd", ".ps1", ".mk", ".gradle",
        // Markup / config / data
        ".xml", ".xaml", ".csproj", ".props", ".targets", ".svg", ".config", ".resx",
        ".html", ".htm", ".css", ".scss", ".less",
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".csv", ".sql", ".proto"
    };

    public override string ToString() => FilePath ?? "(empty)";
}
