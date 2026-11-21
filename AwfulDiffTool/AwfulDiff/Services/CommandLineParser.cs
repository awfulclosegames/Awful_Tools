using System;
using System.Collections.Generic;
using System.Linq;

namespace AwfulDiff.Services;

/// <summary>
/// Parsed command-line arguments for launching AwfulDiff as a diff or merge tool.
/// </summary>
public sealed record CommandLineArgs
{
    public bool ShowHelp { get; init; }

    /// <summary>Positional file paths, capped at 3 (left/right for diff, base/A/B for merge).</summary>
    public List<string> Files { get; init; } = new();

    /// <summary>Optional merge output path (-o/--out/--output, or a 4th positional). Null = save to the base file.</summary>
    public string? OutputPath { get; init; }
}

/// <summary>
/// Parses launch arguments so external VCS tools (Perforce p4diff/p4merge, git difftool/mergetool,
/// SourceTree) can drive AwfulDiff. Deliberately lenient: unknown flags are ignored because VCS
/// tools may pass extras the tool doesn't understand.
/// </summary>
public static class CommandLineParser
{
    public const string Usage = """
        AwfulDiff — diff and merge tool

        Usage:
          AwfulDiff.exe [left] [right]
              Opens a 2-way diff window (0 to 2 files).

          AwfulDiff.exe base fileA fileB [-o output]
              Opens a 3-way merge window directly. Argument order is base-first
              (matches Perforce p4merge and Meld). The merged result is saved to
              the base file, or to 'output' when given via -o or as a 4th path.

        Options:
          -h, --help             Show this help text.
          -o, --out, --output    Merge output file (instead of the base file).

        Exit codes:
          0   diff mode, or merge mode where the merged result was written to disk
          1   merge mode where the result was discarded / not written, or a load failure

        Notes:
          Unknown -flags are ignored. File paths must not start with '-' (such
          tokens are treated as flags).
        """;

    /// <summary>
    /// Parses argument tokens (program name excluded). Never throws on malformed input —
    /// unrecognized tokens are dropped rather than aborting a VCS launch.
    /// </summary>
    public static CommandLineArgs Parse(IReadOnlyList<string> tokens)
    {
        bool showHelp = false;
        string? outputPath = null;
        var files = new List<string>();

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == "-" || (t.Length > 1 && t.StartsWith('-')))
            {
                switch (t.ToLowerInvariant())
                {
                    case "-h" or "--help":
                        showHelp = true;
                        break;
                    case "-o" or "--out" or "--output":
                        // Consume the next token as the value unless it is missing or looks
                        // like another flag — never swallow a flag.
                        if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                            outputPath = tokens[++i];
                        break;
                    default:
                        // Unknown flag (or a lone '-') — ignore.
                        break;
                }
            }
            else
            {
                files.Add(t);
            }
        }

        // A 4th positional becomes the merge output when no explicit -o was given.
        if (outputPath == null && files.Count >= 4)
            outputPath = files[3];

        return new CommandLineArgs
        {
            ShowHelp = showHelp,
            Files = files.Take(3).ToList(),
            OutputPath = outputPath
        };
    }
}
