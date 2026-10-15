using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeDapLink.ExtensionHost;

/// <summary>Localizes explicitly approved presentation fields, never project data or file snapshots.</summary>
internal sealed class DisplayLocalization
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    { "stage", "message", "suggestion", "summary", "label", "title", "action", "instructions", "problem", "problems", "severity", "version", "chipEvidence", "notes", "errors", "warnings" };
    private sealed record Template(string Source, string English, Regex Pattern, HashSet<string> HelperArguments);
    private static readonly Template[] Catalog = LoadCatalog();
    public string Language { get; set; } = "zh-CN";
    public string Display(string source) => Render(source, new JsonObject());

    public static string CanonicalActionLabel(string label)
    {
        if (label.StartsWith("Restore · ", StringComparison.Ordinal)) return "恢复 · " + CanonicalActionLabel(label[10..]);
        if (label.StartsWith("恢复 · ", StringComparison.Ordinal)) return "恢复 · " + CanonicalActionLabel(label[5..]);
        foreach (var source in new[] { "配置 VS Code 编译与烧录任务", "配置 VS Code 编译任务", "应用预览修改", "更新 CMake 源文件：{0}", "工程转换：{0}" })
        {
            var entry = Catalog.First(x => x.Source == source);
            var pattern = Regex.Escape(entry.English).Replace("\\{0}", "(?<value>[\\s\\S]*)", StringComparison.Ordinal);
            var match = Regex.Match(label, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (match.Success) return source.Replace("{0}", match.Groups["value"].Value, StringComparison.Ordinal);
        }
        return label;
    }

    public void Apply(JsonObject envelope)
    {
        var translations = new JsonObject();
        Visit(envelope, null, translations);
        envelope["translations"] = translations;
    }

    private void Visit(JsonNode node, string? role, JsonObject pairs)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (key is "changes" or "conflicts" or "before" or "after" or "translations") continue;
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var source))
                {
                    if (key == "chipEvidence") obj[key] = RenderLines(source, pairs);
                    else if (key == "message" && obj["rawMessage"]?.GetValue<bool>() == true) continue;
                    else if (Roles.Contains(key)) obj[key] = Render(key == "label" ? CanonicalActionLabel(source) : source, pairs);
                }
                else if (obj[key] is { } child) Visit(child, key, pairs);
            }
        }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var source) && role != null && Roles.Contains(role)) array[i] = Render(source, pairs);
                else if (array[i] is { } child) Visit(child, role, pairs);
    }

    private string Render(string source, JsonObject pairs)
    {
        if (source.Length > 32768)
        {
            // Preserve large external output while still translating a helper-generated error prefix.
            foreach (var entry in Catalog)
                if (entry.Source.EndsWith("{0}", StringComparison.Ordinal) && !entry.Source[..^3].Contains('{') && source.StartsWith(entry.Source[..^3], StringComparison.Ordinal))
                    return Language == "en" ? entry.English.Replace("{0}", source[(entry.Source.Length - 3)..], StringComparison.Ordinal) : source;
            return source;
        }
        string english;
        try { english = Translate(source); }
        catch (RegexMatchTimeoutException) { return source; }
        if (english != source && source.Length <= 8192 && pairs.Count < 512) pairs[source] = english;
        return Language == "en" ? english : source;
    }

    private string RenderLines(string source, JsonObject pairs)
    {
        string english;
        try { english = Regex.Replace(source, @"[^\r\n]+", m => Translate(m.Value)); }
        catch (RegexMatchTimeoutException) { return source; }
        if (english != source && source.Length <= 8192 && pairs.Count < 512) pairs[source] = english;
        return Language == "en" ? english : source;
    }

    private static string Translate(string source, int depth = 0)
    {
        if (depth > 8) return source;
        foreach (var entry in Catalog)
        {
            var match = entry.Pattern.Match(source);
            if (!match.Success) continue;
            return Format(entry, match, depth);
        }
        // ArgumentException appends a runtime parameter annotation after its helper message.
        var annotation = source.IndexOf(" (Parameter '", StringComparison.Ordinal);
        if (annotation < 0) annotation = source.IndexOf(" (参数“", StringComparison.Ordinal);
        if (annotation >= 0)
        {
            var reason = source[..annotation];
            var translated = TranslateReason(reason);
            if (reason != translated) return translated + source[annotation..];
        }
        // Conversion issues attach a user path before a generated explanation. Preserve that path.
        for (var index = source.IndexOf(": ", StringComparison.Ordinal); index >= 0; index = source.IndexOf(": ", index + 2, StringComparison.Ordinal))
        {
            var tail = source[(index + 2)..];
            var translated = TranslateReason(tail);
            if (tail != translated) return source[..(index + 2)] + translated;
        }
        return source;
    }

    private static string TranslateReason(string source)
    {
        foreach (var entry in Catalog)
        {
            var match = entry.Pattern.Match(source);
            if (match.Success) return Format(entry, match, 0);
        }
        return source;
    }

    private static string Format(Template entry, Match match, int depth)
        => Regex.Replace(entry.English, @"\{(\d+)\}", placeholder =>
        {
            var index = placeholder.Groups[1].Value;
            var value = match.Groups["v" + index].Value;
            // Only catalog-declared helper arguments may contain nested messages.
            // Paths, names, code, and external tool text are opaque by default.
            if (!entry.HelperArguments.Contains(index)) return value;
            var translated = Translate(value, depth + 1);
            return translated != value ? translated : Regex.Replace(value, @"[^；\r\n]+", part => Translate(part.Value, depth + 1));
        });

    private static Template[] LoadCatalog()
    {
        using var stream = typeof(DisplayLocalization).Assembly.GetManifestResourceStream("CMakeDapLink.ExtensionHost.DisplayMessages.tsv")!;
        using var reader = new StreamReader(stream);
        var entries = new List<Template>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = line.IndexOf('\t');
            if (split < 0) throw new InvalidDataException("Invalid display localization catalog.");
            var columns = line.Split('\t');
            var source = columns[0].Replace("\\n", "\n", StringComparison.Ordinal); var english = columns[1].Replace("\\n", "\n", StringComparison.Ordinal);
            var helperArguments = (columns.Length > 2 ? columns[2].Split(',') : []).ToHashSet(StringComparer.Ordinal);
            var pattern = Regex.Escape(source);
            pattern = Regex.Replace(pattern, @"\\\{(\d+)\}", m => "(?<v" + m.Groups[1].Value + ">[\\s\\S]*?)");
            entries.Add(new(source, english, new Regex("\\A" + pattern + "\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)), helperArguments));
        }
        return entries.OrderBy(x => x.Source.Contains('{'))
            .ThenByDescending(x => Regex.Replace(x.Source, @"\{\d+\}", "").Length).ToArray();
    }
}
