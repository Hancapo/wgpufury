using System.Text;
using System.Text.RegularExpressions;

namespace WGpuFury.Generator;

/// <summary>Converts the Doxygen comments of the headers into C# XML documentation.</summary>
internal static partial class DocText
{
    /// <summary>Strips comment delimiters. Returns null for Doxygen group markers, which document no declaration.</summary>
    public static string? Clean(string comment)
    {
        string body = comment[3..^2];
        var lines = body.Split('\n')
            .Select(l => l.Trim())
            .Select(l => l.StartsWith('*') ? l[1..] : l)
            .Select(l => l.StartsWith(' ') ? l[1..] : l)
            .Select(l => l.TrimEnd())
            .ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        string text = string.Join('\n', lines);
        if (text.Length == 0 || text.Contains("\\defgroup", StringComparison.Ordinal) || text.Contains("@{", StringComparison.Ordinal) || text.Contains("@}", StringComparison.Ordinal))
        {
            return null;
        }

        return text;
    }

    /// <summary>Renders a cleaned doc comment as <c>///</c> lines.</summary>
    public static void Write(StringBuilder sb, string indent, string? doc, IReadOnlyCollection<string>? paramNames = null, string? extra = null)
    {
        if (doc == null && extra == null)
        {
            return;
        }

        var summary = new List<string>();
        var paramDocs = new List<(string Name, List<string> Lines)>();
        List<string>? returns = null;
        List<string> current = summary;
        bool inCode = false;

        foreach (string raw in (doc ?? "").Split('\n'))
        {
            string line = raw;
            if (!inCode)
            {
                Match p = ParamRegex().Match(line);
                if (p.Success)
                {
                    current = [];
                    paramDocs.Add((p.Groups[1].Value, current));
                    line = p.Groups[2].Value;
                    if (line.Length == 0)
                    {
                        continue;
                    }
                }
                else if (ReturnsRegex().Match(line) is { Success: true } r)
                {
                    current = returns = [];
                    line = r.Groups[1].Value;
                    if (line.Length == 0)
                    {
                        continue;
                    }
                }
            }

            if (line.TrimStart().StartsWith("@code", StringComparison.Ordinal))
            {
                inCode = true;
                current.Add("<code>");
                continue;
            }

            if (line.TrimStart().StartsWith("@endcode", StringComparison.Ordinal))
            {
                inCode = false;
                current.Add("</code>");
                continue;
            }

            current.Add(inCode ? Escape(line) : Inline(line));
        }

        if (extra != null)
        {
            if (summary.Count > 0)
            {
                summary.Add("");
            }

            summary.Add(extra);
        }

        WriteBlock(sb, indent, "summary", null, summary);
        foreach (var (name, lines) in paramDocs)
        {
            // Only emit docs for parameters that still exist in the C# signature.
            if (paramNames == null || paramNames.Contains(name))
            {
                WriteBlock(sb, indent, "param", name, lines);
            }
        }

        if (returns != null)
        {
            WriteBlock(sb, indent, "returns", null, returns);
        }
    }

    private static void WriteBlock(StringBuilder sb, string indent, string tag, string? name, List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        if (lines.Count == 0)
        {
            return;
        }

        string open = name == null ? $"<{tag}>" : $"<{tag} name=\"{Naming.Identifier(name).TrimStart('@')}\">";
        sb.Append(indent).Append("/// ").Append(open).Append('\n');
        bool previousBlank = false;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                if (!previousBlank)
                {
                    sb.Append(indent).Append("/// <para/>\n");
                }

                previousBlank = true;
                continue;
            }

            previousBlank = false;
            sb.Append(indent).Append("/// ").Append(line).Append('\n');
        }

        sb.Append(indent).Append("/// </").Append(tag).Append(">\n");
    }

    private static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Inline(string line)
    {
        // Mark code spans on the raw text first, escape everything, then turn the marks into <c> tags.
        string s = line.Replace("\\brief ", "", StringComparison.Ordinal);
        s = BacktickRegex().Replace(s, "\u0001$1\u0002");
        s = RefRegex().Replace(s, "\u0001$1\u0002");
        s = CodeWordRegex().Replace(s, "\u0001$1\u0002");
        s = s.Replace("@note", "Note:", StringComparison.Ordinal).Replace("@deprecated", "Deprecated:", StringComparison.Ordinal);
        return Escape(s).Replace("\u0001", "<c>", StringComparison.Ordinal).Replace("\u0002", "</c>", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s*[@\\]param(?:\[\w+\])?\s+(\w+)\s*(.*)$")]
    private static partial Regex ParamRegex();

    [GeneratedRegex(@"^\s*[@\\]returns?\s*(.*)$")]
    private static partial Regex ReturnsRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex BacktickRegex();

    [GeneratedRegex(@"[@\\]ref\s+([\w:.]+[\w])")]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"[@\\]c\s+([^\s,;)]+)")]
    private static partial Regex CodeWordRegex();
}

internal static class Naming
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    ];

    public static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    public static string Identifier(string name) => Keywords.Contains(name) ? "@" + name : name;
}
