using System;
using System.Collections.Generic;
using System.Text;

namespace SaveGuard.Services;

/// <summary>
/// Minimal reader for Valve KeyValues text (.vdf / .acf): nested brace blocks of
/// "quoted-key" "quoted-value" pairs. Handles <c>//</c> line comments and
/// <c>\"</c> / <c>\\</c> escapes. Not a full KeyValues implementation (no
/// <c>#base</c>/<c>#include</c>, no conditional tags) — just enough for Steam's
/// <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c>. Never throws on
/// malformed input: it returns whatever parsed so far.
/// </summary>
public sealed class VdfNode
{
    /// <summary>Leaf <c>"key" "value"</c> pairs (last one wins). Case-insensitive keys.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nested <c>"key" { ... }</c> blocks. Case-insensitive keys.</summary>
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public VdfNode? GetChild(string key) => Children.TryGetValue(key, out var c) ? c : null;

    /// <summary>Parses KeyValues text. The returned root's children are the
    /// top-level blocks (e.g. "libraryfolders", "AppState").</summary>
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        int pos = 0;
        try { ParseBlock(text, ref pos, root); }
        catch { /* tolerate a truncated / garbage tail */ }
        return root;
    }

    // Reads "key value" / "key { ... }" pairs until EOF or a closing brace.
    private static void ParseBlock(string s, ref int pos, VdfNode node)
    {
        while (true)
        {
            var key = NextToken(s, ref pos, out var keyBrace);
            if (key == null) return;        // EOF
            if (keyBrace == '}') return;    // end of this block
            if (keyBrace == '{') continue;  // stray block with no key — skip

            var val = NextToken(s, ref pos, out var valBrace);
            if (val == null) return;        // dangling key at EOF
            if (valBrace == '{')
            {
                var child = new VdfNode();
                ParseBlock(s, ref pos, child);
                node.Children[key] = child;
            }
            else if (valBrace == '}')
            {
                return;                     // malformed; close the block
            }
            else
            {
                node.Values[key] = val;
            }
        }
    }

    // Next token: a (possibly quoted) string, or null at EOF. For a bare '{' or
    // '}', returns "" and reports the brace char via <paramref name="brace"/>.
    private static string? NextToken(string s, ref int pos, out char brace)
    {
        brace = '\0';
        while (pos < s.Length)
        {
            char c = s[pos];

            if (c is ' ' or '\t' or '\r' or '\n') { pos++; continue; }

            // "//" line comment
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                pos += 2;
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }

            if (c == '{') { pos++; brace = '{'; return ""; }
            if (c == '}') { pos++; brace = '}'; return ""; }
            if (c == '"') { pos++; return ReadQuoted(s, ref pos); }

            return ReadUnquoted(s, ref pos); // legacy bare token / conditional tag
        }
        return null;
    }

    private static string ReadQuoted(string s, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < s.Length)
        {
            char c = s[pos++];
            if (c == '\\' && pos < s.Length)
            {
                char n = s[pos++];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => n });
            }
            else if (c == '"') break;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ReadUnquoted(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c is ' ' or '\t' or '\r' or '\n' or '{' or '}' or '"') break;
            pos++;
        }
        return s.Substring(start, pos - start);
    }
}
