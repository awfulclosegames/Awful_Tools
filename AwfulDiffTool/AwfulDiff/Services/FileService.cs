using AwfulDiff.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace AwfulDiff.Services;

/// <summary>
/// Handles file I/O operations with content caching to avoid redundant disk reads.
/// </summary>
public static class FileService
{
    /// <summary>
    /// Cache of recently loaded files keyed by path (case-insensitive). Evicts oldest entries when the
    /// cache grows beyond 64 entries. Prevents unnecessary disk I/O on Refresh for unchanged files.
    /// </summary>
    private static readonly ConcurrentDictionary<string, FileCacheEntry> FileCache = new(StringComparer.OrdinalIgnoreCase);

    struct FileCacheEntry
    {
        public DateTime LastWriteTime;
        public List<string> Lines;
        public byte[] RawBytes;
    }

    /// <summary>
    /// Load a text file and return it as a FileModel. Returns the cached content if the file's
    /// last-write timestamp hasn't changed since the previous load.
    /// </summary>
    public static FileModel? LoadFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var fi = new FileInfo(filePath);
            var lastWrite = fi.LastWriteTime;

            // Check cache — skip disk read if the file hasn't changed on disk
            if (FileCache.TryGetValue(filePath, out FileCacheEntry cached) && cached.LastWriteTime == lastWrite)
            {
                return new FileModel
                {
                    FilePath = filePath,
                    Lines = cached.Lines,           // safe: content lines are immutable once loaded
                    RawBytes = cached.RawBytes,
                    LastWriteTime = lastWrite,
                    FileSize = fi.Length,
                    DisplayName = fi.Name,
                    FormattedSize = FormatFileSize(fi.Length),
                    FormattedTime = lastWrite.ToString("yyyy-MM-dd HH:mm:ss")
                };
            }

            // Read raw bytes once — used for both hex view and deriving text lines
            var rawBytes = File.ReadAllBytes(filePath);
            var lines = DecodeLines(rawBytes);

            // Update cache, evicting the oldest entry if we're past the limit
            if (FileCache.Count >= 64)
            {
                FileCache.TryRemove(FileCache.First().Key, out _);
            }
            FileCache[filePath] = new FileCacheEntry { LastWriteTime = lastWrite, Lines = lines, RawBytes = rawBytes };

            return new FileModel
            {
                FilePath = filePath,
                Lines = lines,
                RawBytes = rawBytes,
                LastWriteTime = lastWrite,
                FileSize = fi.Length,
                DisplayName = fi.Name,
                FormattedSize = FormatFileSize(fi.Length),
                FormattedTime = lastWrite.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get display-friendly file name.
    /// </summary>
    public static string GetDisplayName(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return "(no file loaded)";

        var parts = filePath.Split('\\');
        return parts.Last();
    }

    /// <summary>
    /// Format file size for display.
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Format a DateTime for display.
    /// </summary>
    public static string FormatTime(DateTime? time)
    {
        return time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(unknown)";
    }

    /// <summary>
    /// Decode raw bytes into text lines, handling common encodings (UTF-8/UTF-16 BOM).
    /// The inverse of <c>encoding.GetBytes(string.Join("\n", lines))</c> — used to materialize a
    /// byte working copy back into text lines when switching hex→text with pending edits.
    /// </summary>
    public static List<string> DecodeLines(byte[] rawBytes)
    {
        if (rawBytes.Length == 0)
            return new List<string>();

        string content;

        // Detect encoding from BOM
        if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
        {
            // UTF-8 with BOM
            content = Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3);
        }
        else if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE)
        {
            // UTF-16 LE with BOM
            content = Encoding.Unicode.GetString(rawBytes, 2, rawBytes.Length - 2);
        }
        else if (rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
        {
            // UTF-16 BE with BOM
            content = Encoding.BigEndianUnicode.GetString(rawBytes, 2, rawBytes.Length - 2);
        }
        else
        {
            // Assume UTF-8 without BOM (matches File.ReadAllLines default behavior)
            content = Encoding.UTF8.GetString(rawBytes);
        }

        // Split on all common line endings
        return content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
    }
}
