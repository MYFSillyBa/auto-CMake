using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeDapLink.Core;

/// <summary>Small span editor: comments, encoding, and unrelated JSONC text remain untouched.</summary>
internal static class JsoncDevelopmentEditor
{
    internal static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    internal static readonly JsonSerializerOptions PrintOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal sealed record ValueSpan(int Start, int End, string? Name = null);

    internal static JsonNode? Parse(string text) => JsonNode.Parse(text, documentOptions: ReadOptions);

    internal static byte[] Edit(byte[]? before, Func<string, string> edit)
    {
        var (text, encoding, preamble) = before == null ? ("{}", new UTF8Encoding(false, true), Array.Empty<byte>()) : JsoncTaskEditor.Decode(before);
        var updated = edit(text);
        using var verified = JsonDocument.Parse(updated, ReadOptions);
        return preamble.Concat(encoding.GetBytes(updated)).ToArray();
    }

    internal static string SetObject(string text, JsonObject desired)
    {
        if (Parse(text) is not JsonObject actual) throw new InvalidDataException("现有配置必须是 JSON 对象。");
        var (values, close) = Scan(text, true);
        var replacements = new List<(int Start, int End, string Text)>();
        var missing = new List<KeyValuePair<string, JsonNode?>>();
        foreach (var property in desired)
        {
            var span = values.SingleOrDefault(x => x.Name == property.Key);
            if (span == null) { missing.Add(property); continue; }
            if (JsonNode.DeepEquals(actual[property.Key], property.Value)) continue;
            var raw = text[span.Start..span.End];
            var value = actual[property.Key] is JsonObject && property.Value is JsonObject nested
                ? SetObject(raw, nested)
                : actual[property.Key] is JsonArray && property.Value is JsonArray array
                    ? SetArrayValues(raw, array)
                    : Print(property.Value, Indent(text, span.Start), Newline(text));
            replacements.Add((span.Start, span.End, value));
        }
        if (missing.Count > 0)
        {
            var newline = Newline(text);
            var baseIndent = Indent(text, close);
            var indent = values.Count > 0 ? Indent(text, values[0].Start) : baseIndent + "  ";
            if (indent.Length <= baseIndent.Length) indent = baseIndent + "  ";
            var insertion = newline + string.Join("," + newline, missing.Select(p => indent + JsonSerializer.Serialize(p.Key) + ": " + Print(p.Value, indent, newline))) + newline + baseIndent;
            replacements.Add((close, close, insertion));
            if (values.Count > 0 && !HasComma(text[values[^1].End..close])) replacements.Add((values[^1].End, values[^1].End, ","));
        }
        foreach (var edit in replacements.OrderByDescending(x => x.Start).ThenByDescending(x => x.End)) text = text[..edit.Start] + edit.Text + text[edit.End..];
        return text;
    }

    // Retain comments between elements while updating the corresponding values.
    private static string SetArrayValues(string text, JsonArray desired)
    {
        var (spans, _) = Scan(text, false);
        var common = Math.Min(spans.Count, desired.Count);
        var replacements = new Dictionary<int, string?>();
        for (var index = 0; index < common; index++)
        {
            var raw = text[spans[index].Start..spans[index].End];
            replacements[index] = JsonNode.DeepEquals(Parse(raw), desired[index]) ? raw
                : Print(desired[index], Indent(text, spans[index].Start), Newline(text));
        }
        for (var index = common; index < spans.Count; index++) replacements[index] = null;
        return RewriteArray(text, replacements, desired.Skip(common).Select(x => Print(x, "  ", Newline(text))).ToArray());
    }

    internal static string RewriteArray(string text, IReadOnlyDictionary<int, string?> replacements, IReadOnlyList<string> additions)
    {
        var (spans, close) = Scan(text, false);
        var open = text.IndexOf('[');
        var total = spans.Count - replacements.Count(x => x.Value == null) + additions.Count;
        var builder = new StringBuilder(text[..(open + 1)]);
        var previous = open + 1; var count = 0;
        var trailing = spans.Count > 0 && HasComma(text[spans[^1].End..close]);
        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];
            builder.Append(WithoutCommas(text[previous..span.Start]));
            var value = replacements.TryGetValue(index, out var replacement) ? replacement : text[span.Start..span.End];
            if (value != null)
            {
                builder.Append(value);
                if (++count < total || trailing) builder.Append(',');
            }
            previous = span.End;
        }
        builder.Append(WithoutCommas(text[previous..close]));
        if (additions.Count > 0)
        {
            var newline = Newline(text);
            var indent = spans.Count > 0 ? Indent(text, spans[0].Start) : Indent(text, close) + "  ";
            if (indent.Length == 0) indent = "  ";
            builder.Append(newline).Append(string.Join("," + newline, additions.Select(x => indent + x.Replace("\n", newline + indent, StringComparison.Ordinal))));
            if (trailing) builder.Append(',');
            builder.Append(newline).Append(Indent(text, close));
        }
        return builder.Append(text[close..]).ToString();
    }

    internal static (List<ValueSpan> Values, int Close) Scan(string text, bool obj)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        int Character(long offset) => Encoding.UTF8.GetCharCount(bytes.AsSpan(0, checked((int)offset)));
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var values = new List<ValueSpan>(); var names = new HashSet<string>(StringComparer.Ordinal);
        if (!reader.Read() || reader.TokenType != (obj ? JsonTokenType.StartObject : JsonTokenType.StartArray)) throw new InvalidDataException("现有配置的 JSON 类型不正确。");
        while (reader.Read())
        {
            if (reader.CurrentDepth == 0 && reader.TokenType == (obj ? JsonTokenType.EndObject : JsonTokenType.EndArray)) return (values, Character(reader.TokenStartIndex));
            string? name = null;
            if (obj)
            {
                name = reader.GetString()!;
                if (!names.Add(name)) throw new InvalidDataException("配置包含重复的 JSON 属性，无法安全合并：" + name);
                reader.Read();
            }
            var start = Character(reader.TokenStartIndex);
            reader.Skip();
            values.Add(new(start, Character(reader.BytesConsumed), name));
        }
        throw new InvalidDataException("JSON 配置不完整。");
    }

    private static string Print(JsonNode? node, string indent, string newline) => (node?.ToJsonString(PrintOptions) ?? "null").Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline + indent, StringComparison.Ordinal);
    internal static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    private static string Indent(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }
    private static bool HasComma(string text) => WithoutCommas(text).Length != text.Length;
    private static string WithoutCommas(string text)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] is '/' or '*')
            {
                var start = index;
                if (text[index + 1] == '/') { while (index < text.Length && text[index] != '\n') index++; index--; }
                else { index += 2; while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++; index++; }
                builder.Append(text[start..(index + 1)]);
            }
            else if (text[index] != ',') builder.Append(text[index]);
        }
        return builder.ToString();
    }
}
