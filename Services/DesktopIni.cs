using System.IO;
using System.Text;

namespace FolderIconStudio.Services;

/// <summary>
/// Minimal desktop.ini editor: keeps unrelated sections and encoding intact, and only
/// rewrites the icon keys inside [.ShellClassInfo].
/// </summary>
internal static class DesktopIni
{
    public const string FileName = "desktop.ini";
    private const string ShellSection = ".ShellClassInfo";
    private static readonly string[] IconKeys = ["iconresource", "iconfile", "iconindex"];

    public static void SetIcon(string iniPath, string iconFileName)
    {
        var document = IniDocument.Load(iniPath);
        var lines = document.Lines;

        var header = FindSectionHeader(lines, ShellSection);
        var iconLine = $"IconResource={iconFileName},0";

        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add(string.Empty);
            lines.Add($"[{ShellSection}]");
            lines.Add(iconLine);
        }
        else
        {
            var end = FindSectionEnd(lines, header);
            for (var i = end - 1; i > header; i--)
            {
                if (IsIconKeyLine(lines[i])) lines.RemoveAt(i);
            }

            lines.Insert(header + 1, iconLine);
        }

        document.Save(iniPath, lines, forceUnicode: true);
    }

    /// <summary>Removes the icon keys. Returns true when the file no longer holds any content.</summary>
    public static bool ClearIcon(string iniPath)
    {
        var document = IniDocument.Load(iniPath);
        var lines = document.Lines;

        var header = FindSectionHeader(lines, ShellSection);
        if (header >= 0)
        {
            var end = FindSectionEnd(lines, header);
            for (var i = end - 1; i > header; i--)
            {
                if (IsIconKeyLine(lines[i])) lines.RemoveAt(i);
            }

            if (!HasKeys(lines, header + 1, FindSectionEnd(lines, header)))
            {
                lines.RemoveAt(header);
            }
        }

        TrimTrailingBlankLines(lines);
        var empty = !lines.Any(line => IsMeaningful(line));
        if (!empty) document.Save(iniPath, lines);
        return empty;
    }

    public static string? GetIconFile(string iniPath)
    {
        if (!File.Exists(iniPath)) return null;
        var lines = IniDocument.Load(iniPath).Lines;
        var header = FindSectionHeader(lines, ShellSection);
        if (header < 0) return null;

        var end = FindSectionEnd(lines, header);
        for (var i = header + 1; i < end; i++)
        {
            if (!TryGetKeyValue(lines[i], out var key, out var value)) continue;
            if (key != "iconresource") continue;
            var name = value.Split(',')[0].Trim();
            return name.Length > 0 ? name : null;
        }

        return null;
    }

    private static int FindSectionHeader(List<string> lines, string section)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' &&
                trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSectionEnd(List<string> lines, int header)
    {
        for (var i = header + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('[')) return i;
        }

        return lines.Count;
    }

    private static bool IsIconKeyLine(string line) =>
        TryGetKeyValue(line, out var key, out _) && IconKeys.Contains(key);

    private static bool TryGetKeyValue(string line, out string key, out string value)
    {
        var trimmed = line.Trim();
        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = trimmed[..separator].Trim().ToLowerInvariant();
        value = trimmed[(separator + 1)..].Trim();
        return true;
    }

    private static bool HasKeys(List<string> lines, int start, int end)
    {
        for (var i = start; i < end && i < lines.Count; i++)
        {
            if (TryGetKeyValue(lines[i], out _, out _)) return true;
        }

        return false;
    }

    private static bool IsMeaningful(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith(';') && !trimmed.StartsWith('#');
    }

    private static void TrimTrailingBlankLines(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
    }

    private sealed class IniDocument
    {
        public List<string> Lines { get; private init; } = [];
        private Encoding Encoding { get; init; } = new UnicodeEncoding(false, false);
        private bool HasBom { get; init; } = true;

        public static IniDocument Load(string path)
        {
            if (!File.Exists(path))
            {
                return new IniDocument { Lines = [], Encoding = new UnicodeEncoding(false, false), HasBom = true };
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return new IniDocument
                {
                    Lines = Split(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)),
                    Encoding = new UnicodeEncoding(false, false),
                    HasBom = true
                };
            }

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new IniDocument
                {
                    Lines = Split(new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3)),
                    Encoding = new UTF8Encoding(false),
                    HasBom = true
                };
            }

            try
            {
                return new IniDocument
                {
                    Lines = Split(new UTF8Encoding(false, true).GetString(bytes)),
                    Encoding = new UTF8Encoding(false),
                    HasBom = false
                };
            }
            catch (DecoderFallbackException)
            {
                // Legacy ANSI file: Latin-1 round-trips untouched bytes exactly.
                return new IniDocument
                {
                    Lines = Split(Encoding.Latin1.GetString(bytes)),
                    Encoding = Encoding.Latin1,
                    HasBom = false
                };
            }
        }

        public void Save(string path, List<string> lines, bool forceUnicode = false)
        {
            TrimTrailingBlankLines(lines);
            var text = string.Join("\r\n", lines) + "\r\n";

            // Explorer ignores icon keys in UTF-8 desktop.ini files, so applying always
            // writes UTF-16 LE (the format Explorer itself uses). Existing content is kept.
            var encoding = forceUnicode ? new UnicodeEncoding(false, false) : Encoding;
            var includeBom = forceUnicode || HasBom;
            var body = encoding.GetBytes(text);
            byte[] bytes;
            if (includeBom)
            {
                var preamble = encoding is UnicodeEncoding
                    ? System.Text.Encoding.Unicode.GetPreamble()
                    : System.Text.Encoding.UTF8.GetPreamble();
                bytes = [.. preamble, .. body];
            }
            else
            {
                bytes = body;
            }

            File.WriteAllBytes(path, bytes);
        }

        private static List<string> Split(string text)
        {
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n').ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }
    }
}
