using System.Text.RegularExpressions;

namespace CodexHelper.Core.Services;

/// <summary>
/// Conservative TOML structure validation migrated from codex-api-switcher.
/// It validates enough syntax to avoid editing a damaged configuration while
/// preserving comments and settings that Codex Helper does not own.
/// </summary>
public sealed class TomlConfigurationDocument
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public static TomlConfigurationDocument Parse(IReadOnlyList<string> lines)
    {
        var document = new TomlConfigurationDocument();
        var section = string.Empty;
        var continuationDepth = 0;
        var multilineBasic = false;
        var multilineLiteral = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var clean = StripComment(lines[index]).Trim();
            if (clean.Length == 0) continue;
            if (multilineBasic || multilineLiteral)
            {
                if (multilineBasic && CountToken(clean, "\"\"\"") % 2 == 1) multilineBasic = false;
                if (multilineLiteral && CountToken(clean, "'''") % 2 == 1) multilineLiteral = false;
                continue;
            }
            if (continuationDepth > 0)
            {
                continuationDepth += StructuralDelta(clean);
                if (continuationDepth < 0) throw Error(index, "数组或内联表括号不平衡");
                continue;
            }
            var table = Regex.Match(clean, @"^\[\[?\s*([^\]]+?)\s*\]\]?$", RegexOptions.CultureInvariant);
            if (table.Success)
            {
                section = table.Groups[1].Value.Trim();
                if (section.Length == 0) throw Error(index, "表名为空");
                continue;
            }
            var equals = FindUnquoted(clean, '=');
            if (equals <= 0) throw Error(index, "应为 key = value");
            var key = clean[..equals].Trim();
            var value = clean[(equals + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) throw Error(index, "键或值为空");
            var composite = section + "\n" + key;
            if (document.values.ContainsKey(composite)) throw Error(index, "重复键 " + key);
            document.values[composite] = value;
            if (CountToken(value, "\"\"\"") % 2 == 1) multilineBasic = true;
            else if (CountToken(value, "'''") % 2 == 1) multilineLiteral = true;
            else
            {
                continuationDepth = StructuralDelta(value);
                if (continuationDepth < 0) throw Error(index, "数组或内联表括号不平衡");
            }
        }
        if (continuationDepth != 0 || multilineBasic || multilineLiteral)
            throw new InvalidOperationException("TOML 无效：存在未结束的多行值。");
        return document;
    }

    public string GetString(string section, string key)
    {
        if (!values.TryGetValue((section ?? string.Empty) + "\n" + key, out var value)) return string.Empty;
        value = value.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static InvalidOperationException Error(int index, string message) =>
        new($"TOML 第 {index + 1} 行无效：{message}。");

    private static int CountToken(string value, string token)
    {
        var count = 0;
        for (var at = 0; (at = value.IndexOf(token, at, StringComparison.Ordinal)) >= 0; at += token.Length) count++;
        return count;
    }

    private static int StructuralDelta(string value)
    {
        var result = 0;
        var basic = false;
        var literal = false;
        var escape = false;
        foreach (var character in value)
        {
            if (basic)
            {
                if (escape) { escape = false; continue; }
                if (character == '\\') { escape = true; continue; }
                if (character == '"') basic = false;
                continue;
            }
            if (literal) { if (character == '\'') literal = false; continue; }
            if (character == '"') { basic = true; continue; }
            if (character == '\'') { literal = true; continue; }
            if (character is '[' or '{') result++;
            if (character is ']' or '}') result--;
        }
        return result;
    }

    private static int FindUnquoted(string value, char target)
    {
        var basic = false;
        var literal = false;
        var escape = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (basic)
            {
                if (escape) { escape = false; continue; }
                if (character == '\\') { escape = true; continue; }
                if (character == '"') basic = false;
                continue;
            }
            if (literal) { if (character == '\'') literal = false; continue; }
            if (character == '"') { basic = true; continue; }
            if (character == '\'') { literal = true; continue; }
            if (character == target) return index;
        }
        return -1;
    }

    private static string StripComment(string value)
    {
        var comment = FindUnquoted(value, '#');
        return comment >= 0 ? value[..comment] : value;
    }
}

