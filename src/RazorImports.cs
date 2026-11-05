using System.Text;
using System.Text.RegularExpressions;

namespace BipinPaul.DotNet.CodeStyle;

/// <summary>
/// Cleans standalone imports in the Razor header. Unsupported syntax ends the
/// header scan, so markup, C# blocks and multiline directives remain untouched.
/// </summary>
internal static class RazorImports
{
    private const string Identifier = @"@?[\p{L}_][\p{L}\p{Nd}_]*";
    private const string QualifiedName = "(?:global::)?" + Identifier + @"(?:\." + Identifier + ")*";

    private static readonly Regex Import = new(
        @"^(?<indent>[ \t]*)@using[ \t]+(?<name>(?:static[ \t]+)?" + QualifiedName
        + "|" + Identifier + @"[ \t]*=[ \t]*" + QualifiedName + @")[ \t]*;?[ \t]*$", RegexOptions.NonBacktracking);

    private static readonly Regex HeaderDirective = new(
        """^@(page[ \t]+"(?:[^"\\]|\\.)*"|attribute[ \t]+\[(?:[^"'\[\]]|"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')*\]|(?:namespace|layout|inherits|implements|inject|rendermode|typeparam|preservewhitespace)[ \t]+[@\p{L}\p{Nd}_.,<>?: \t]+)[ \t]*$""");

    internal static void Format(string path)
    {
        // Detect BOMs and retain the original encoding and line endings.
        using StreamReader reader = new(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string original = reader.ReadToEnd();
        Encoding encoding = reader.CurrentEncoding;
        reader.Close();
        List<string> lines = [.. Regex.Matches(original, @"[^\r\n]*(?:\r\n|\r|\n|$)").Select(match => match.Value)];
        List<int> importLines = [];
        bool inComment = false;
        for (int index = 0; index < lines.Count; index++)
        {
            string content = lines[index].TrimEnd('\r', '\n');
            string trimmed = content.Trim();
            if (!inComment && Import.IsMatch(content))
            {
                importLines.Add(index);
                continue;
            }

            SortBlock();
            if (inComment || trimmed.StartsWith("@*", StringComparison.Ordinal))
            {
                int end = trimmed.IndexOf("*@", StringComparison.Ordinal);
                inComment = end < 0;
                if (end >= 0 && trimmed[(end + 2)..].Trim().Length > 0) break;
                continue;
            }
            if (trimmed.Length == 0 || HeaderDirective.IsMatch(trimmed)) continue;
            break;
        }
        SortBlock();
        string formatted = string.Concat(lines);
        if (!string.Equals(original, formatted, StringComparison.Ordinal))
            File.WriteAllText(path, formatted, encoding);

        void SortBlock()
        {
            string[] imports = [.. importLines.Select(index => Import.Match(lines[index].TrimEnd('\r', '\n')).Groups["name"].Value)
                .Select(name => Regex.Replace(name, @"[ \t]+", " "))
                .OrderBy(Kind)
                .ThenBy(name => IsSystem(name) ? 0 : 1)
                .ThenBy(name => name, StringComparer.Ordinal)];
            for (int item = 0; item < importLines.Count; item++)
            {
                int index = importLines[item];
                string line = lines[index];
                string content = line.TrimEnd('\r', '\n');
                string indent = Import.Match(content).Groups["indent"].Value;
                lines[index] = indent + "@using " + imports[item] + line[content.Length..];
            }
            importLines.Clear();
        }
    }

    private static int Kind(string name) => name.Contains('=') ? 2 : name.StartsWith("static ", StringComparison.Ordinal) ? 1 : 0;

    private static bool IsSystem(string name)
    {
        string target = name.StartsWith("static ", StringComparison.Ordinal) ? name[7..] : name;
        if (target.StartsWith("global::", StringComparison.Ordinal)) target = target[8..];
        return target == "System" || target.StartsWith("System.", StringComparison.Ordinal);
    }
}
