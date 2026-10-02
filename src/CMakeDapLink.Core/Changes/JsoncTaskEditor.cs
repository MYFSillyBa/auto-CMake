using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeDapLink.Core;

/// <summary>Edits task values while retaining user values, comments and surrounding text.</summary>
internal static class JsoncTaskEditor
{
    internal const string ManagedDetail = "由 STM32 工程助手管理";
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions PrintOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Update(byte[]? original, IReadOnlyList<JsonObject> managed)
    {
        if (original == null)
        {
            var tasks = new JsonArray(managed.Select(t => (JsonNode)t.DeepClone()).ToArray());
            return Encoding.UTF8.GetBytes(new JsonObject { ["version"] = "2.0.0", ["tasks"] = tasks }.ToJsonString(PrintOptions));
        }
        var (text, encoding, preamble) = Decode(original);
        var root = JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject
            ?? throw new InvalidDataException("现有 .vscode/tasks.json 不是 JSON 对象。");
        if (root["tasks"] is not null && root["tasks"] is not JsonArray)
            throw new InvalidDataException("现有 tasks.json 的 tasks 不是数组。");
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var tokens = Scan(text);
        string updated;
        if (tokens.Open < 0 && tokens.TasksValue != null)
        {
            var array = "[" + newline + string.Join("," + newline, managed.Select(t => Format(t, "    ", newline))) + newline + "  ]";
            updated = text[..tokens.TasksValue.Start] + array + text[tokens.TasksValue.End..];
        }
        else if (tokens.Open < 0)
        {
            var insertion = newline + "  \"tasks\": [" + newline + string.Join("," + newline,
                managed.Select(t => Format(t, "    ", newline))) + newline + "  ]" + newline;
            var withComma = text;
            if (root.Count > 0 && Commas(text[tokens.LastValueEnd..tokens.RootEnd]).Count == 0)
                withComma = text.Insert(tokens.LastValueEnd, ",");
            var shift = withComma.Length - text.Length;
            updated = withComma.Insert(tokens.RootEnd + shift, insertion);
        }
        else
        {
            var replacements = managed.ToDictionary(t => t["label"]!.GetValue<string>(), StringComparer.Ordinal);
            var present = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<(Span Span, string Value)>();
            foreach (var span in tokens.Elements)
            {
                var raw = text[span.Start..span.End];
                var node = JsonNode.Parse(raw, documentOptions: DocumentOptions);
                var label = node is JsonObject obj && obj["label"] is JsonValue labelNode && labelNode.TryGetValue<string>(out var value) ? value : null;
                if (label != null && IsManagedTask(node))
                {
                    if (!replacements.TryGetValue(label, out var replacement) || !present.Add(label)) continue;
                    var indent = LineIndent(text, span.Start);
                    if (indent.Length == 0) indent = "    ";
                    var formatted = Format(replacement, indent, newline);
                    kept.Add((span, JsonNode.DeepEquals(node, replacement) ? raw : formatted[indent.Length..]));
                }
                else kept.Add((span, raw));
            }
            var additions = managed.Where(t => !present.Contains(t["label"]!.GetValue<string>())).ToArray();
            var byStart = kept.ToDictionary(k => k.Span.Start);
            var builder = new StringBuilder(text[..(tokens.Open + 1)]);
            var previous = tokens.Open + 1;
            var count = 0;
            var total = kept.Count + additions.Length;
            var tailStart = tokens.Elements.Count > 0 ? tokens.Elements[^1].End : tokens.Open + 1;
            var trailingComma = Commas(text[tailStart..tokens.Close]).Count > 0;
            foreach (var span in tokens.Elements)
            {
                builder.Append(WithoutCommas(text[previous..span.Start]));
                if (byStart.TryGetValue(span.Start, out var entry))
                {
                    builder.Append(entry.Value);
                    if (++count < total || (trailingComma && additions.Length == 0)) builder.Append(',');
                }
                previous = span.End;
            }
            builder.Append(WithoutCommas(text[previous..tokens.Close]));
            if (additions.Length > 0)
            {
                var indent = tokens.Elements.Count > 0 ? LineIndent(text, tokens.Elements[0].Start) : "    ";
                if (indent.Length == 0) indent = "    ";
                builder.Append(newline);
                builder.Append(string.Join("," + newline, additions.Select(t => Format(t, indent, newline))));
                if (trailingComma) builder.Append(',');
                builder.Append(newline).Append(LineIndent(text, tokens.Close));
            }
            builder.Append(text[tokens.Close..]);
            updated = builder.ToString();
        }
        // Validate the resulting document before it can be written.
        using var verified = JsonDocument.Parse(updated, DocumentOptions);
        return preamble.Concat(encoding.GetBytes(updated)).ToArray();
    }

    internal static HashSet<string> UserLabels(byte[]? original)
    {
        if (original == null) return new HashSet<string>(StringComparer.Ordinal);
        var (text, _, _) = Decode(original);
        var root = JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject
            ?? throw new InvalidDataException("现有 .vscode/tasks.json 不是 JSON 对象。");
        if (root["tasks"] is not null && root["tasks"] is not JsonArray)
            throw new InvalidDataException("现有 tasks.json 的 tasks 不是数组。");
        return (root["tasks"] as JsonArray ?? new JsonArray()).Where(t => !IsManagedTask(t)).OfType<JsonObject>()
            .Select(t => t["label"] is JsonValue label && label.TryGetValue<string>(out var value) ? value : null)
            .Where(label => label != null).Select(label => label!).ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsManagedTask(JsonNode? node)
    {
        if (node is not JsonObject task) return false;
        string? Text(string key) => task[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (Text("detail") is ManagedDetail or "由 CMake · DAPLink 配置助手管理") return true;
        var label = Text("label");
        if (label is not ("CMake 配置（自动）" or "一键编译" or "一键烧录(DAPLINK)" or "一键启动（DAPLINK）" or "一键启动(DAPLINK)")) return false;
        if (Text("type") != "process" || task["args"] is not JsonArray arguments) return false;
        var command = Path.GetFileName(Text("command")?.Replace('\\', '/'));
        if (!new[] { "powershell", "powershell.exe", "pwsh", "pwsh.exe" }.Contains(command, StringComparer.OrdinalIgnoreCase)) return false;
        var values = arguments.Select(argument => argument is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).ToArray();
        if (values.Any(value => value == null)) return false;
        var scriptIndex = values.Length >= 5 && string.Equals(values[0], "-NoProfile", StringComparison.OrdinalIgnoreCase)
            && string.Equals(values[1], "-ExecutionPolicy", StringComparison.OrdinalIgnoreCase)
            && string.Equals(values[2], "Bypass", StringComparison.OrdinalIgnoreCase)
            && string.Equals(values[3], "-File", StringComparison.OrdinalIgnoreCase) ? 4
            : values.Length >= 3 && string.Equals(values[0], "-NoProfile", StringComparison.OrdinalIgnoreCase)
                && string.Equals(values[1], "-File", StringComparison.OrdinalIgnoreCase) ? 2 : -1;
        if (scriptIndex < 0) return false;
        var script = values[scriptIndex]!.Replace('\\', '/');
        var workspaceScript = string.Equals(script, "${workspaceFolder}/.vscode/cmake-daplink.ps1", StringComparison.OrdinalIgnoreCase);
        var relativeScript = string.Equals(script, ".vscode/cmake-daplink.ps1", StringComparison.OrdinalIgnoreCase)
            && task["options"] is JsonObject options && options["cwd"] is JsonValue cwd
            && cwd.TryGetValue<string>(out var directory) && directory == "${workspaceFolder}";
        if (!workspaceScript && !relativeScript) return false;
        // Only the old generated helper invocation is evidence of ownership; native task labels alone are not.
        return values.Length == scriptIndex + 1 || values.Length == scriptIndex + 3
            && string.Equals(values[scriptIndex + 1], "-Action", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(values[scriptIndex + 2], "build", StringComparison.OrdinalIgnoreCase)
                || string.Equals(values[scriptIndex + 2], "flash", StringComparison.OrdinalIgnoreCase));
    }

    internal static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
    {
        foreach (var encoding in new Encoding[] { new UTF32Encoding(false, true), new UTF32Encoding(true, true),
                     new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
        {
            var bom = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(bom)) return (encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length), encoding, bom);
        }
        var utf8 = new UTF8Encoding(false, true);
        return (utf8.GetString(bytes), utf8, []);
    }

    private static string Format(JsonObject obj, string indent, string newline) => indent + obj.ToJsonString(PrintOptions)
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline + indent, StringComparison.Ordinal);

    private static string LineIndent(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1));
        start = start < 0 ? 0 : start + 1;
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }

    private static ScanResult Scan(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        int Character(long offset) => Encoding.UTF8.GetCharCount(utf8.AsSpan(0, checked((int)offset)));
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var result = new ScanResult();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                var tasks = reader.ValueTextEquals("tasks");
                reader.Read();
                if (tasks && reader.TokenType == JsonTokenType.StartArray)
                {
                    result.Open = Character(reader.TokenStartIndex);
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        var start = Character(reader.TokenStartIndex);
                        reader.Skip();
                        result.Elements.Add(new Span(start, Character(reader.BytesConsumed)));
                    }
                    result.Close = Character(reader.TokenStartIndex);
                }
                else
                {
                    var start = Character(reader.TokenStartIndex);
                    reader.Skip();
                    if (tasks) result.TasksValue = new Span(start, Character(reader.BytesConsumed));
                }
                result.LastValueEnd = Character(reader.BytesConsumed);
            }
            else if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                result.RootEnd = Character(reader.TokenStartIndex);
        }
        return result;
    }

    private static List<int> Commas(string trivia)
    {
        var found = new List<int>();
        for (var index = 0; index < trivia.Length; index++)
        {
            if (trivia[index] == '/' && index + 1 < trivia.Length)
            {
                if (trivia[index + 1] == '/') { while (index < trivia.Length && trivia[index] != '\n') index++; continue; }
                if (trivia[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < trivia.Length && !(trivia[index] == '*' && trivia[index + 1] == '/')) index++;
                    index++; continue;
                }
            }
            if (trivia[index] == ',') found.Add(index);
        }
        return found;
    }

    private static string WithoutCommas(string trivia)
    {
        var commas = Commas(trivia);
        if (commas.Count == 0) return trivia;
        var builder = new StringBuilder(trivia);
        foreach (var index in commas.AsEnumerable().Reverse()) builder.Remove(index, 1);
        return builder.ToString();
    }

    private sealed record Span(int Start, int End);
    private sealed class ScanResult
    {
        public int Open = -1, Close, RootEnd, LastValueEnd;
        public Span? TasksValue;
        public List<Span> Elements { get; } = [];
    }
}
