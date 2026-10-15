using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal sealed record LinkedMemoryRegion(string Name, string Attributes, ulong Origin, ulong? Length);
internal sealed record LinkedSectionAssignment(string? Runtime, string? Load);
internal sealed record LinkedMemoryLayout(IReadOnlyList<LinkedMemoryRegion> Regions, IReadOnlyDictionary<string, LinkedSectionAssignment> Assignments);

/// <summary>Evaluates a deliberately bounded subset of GNU ld MEMORY expressions. Unknown expressions stay unknown.</summary>
internal static class FirmwareLinkerMemory
{
    private sealed record RegionExpression(string Name, string Attributes, string Origin, string Length);
    private static readonly Regex Region = new(@"(?<name>[A-Za-z_][\w.]*)\s*(?:\((?<attrs>[^)]*)\))?\s*:\s*(?:ORIGIN|org|o)\s*=\s*(?<origin>[^,]+),\s*(?:LENGTH|len|l)\s*=\s*(?<length>.*?)(?=\s+[A-Za-z_][\w.]*\s*(?:\([^)]*\))?\s*:|$)", RegexOptions.Singleline);

    public static LinkedMemoryLayout Read(IEnumerable<string> scripts, List<string> notes)
    {
        var roots = scripts.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var texts = new List<string>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompleteScripts = false;
        var expressions = new Dictionary<string, RegionExpression>(StringComparer.Ordinal);
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        var assignments = new Dictionary<string, LinkedSectionAssignment>(StringComparer.Ordinal);
        foreach (var script in roots) Load(script);
        foreach (var text in texts)
        {
            foreach (Match symbol in Regex.Matches(text, @"(?m)^\s*(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>[^;{}]+);"))
            {
                var name = symbol.Groups["name"].Value; var value = symbol.Groups["value"].Value;
                if (symbols.TryGetValue(name, out var previous) && previous != value) symbols[name] = "?";
                else symbols.TryAdd(name, value);
            }
            foreach (Match memory in Regex.Matches(text, @"\bMEMORY\s*\{(?<body>[^{}]*)\}"))
            {
                var body = memory.Groups["body"].Value;
                var residue = Region.Replace(body, "").Replace(";", "").Trim();
                if (residue.Length > 0) notes.Add("链接脚本的部分 MEMORY 声明无法解析；不推测区域容量。");
                foreach (Match region in Region.Matches(body))
                {
                    var value = new RegionExpression(region.Groups["name"].Value, region.Groups["attrs"].Value,
                        region.Groups["origin"].Value.Trim().TrimEnd(';'), region.Groups["length"].Value.Trim().TrimEnd(';'));
                    if (expressions.TryGetValue(value.Name, out var previous) && previous != value)
                        expressions[value.Name] = value with { Origin = "?", Length = "?" };
                    else expressions.TryAdd(value.Name, value);
                }
            }
            foreach (Match section in Regex.Matches(text, @"(?m)^\s*(?<name>\.[A-Za-z_][\w.]*)\s*[^;{}]*?:\s*[^{}]*?\{"))
            {
                var opening = section.Index + section.Length - 1; var end = ClosingBrace(text, opening);
                if (end < 0) continue;
                var tail = text[(end + 1)..Math.Min(text.Length, end + 512)];
                var mapping = Regex.Match(tail, @"^\s*(?:>\s*(?<runtime>[A-Za-z_][\w.]*))?(?:\s*AT\s*>\s*(?<load>[A-Za-z_][\w.]*))?");
                var value = new LinkedSectionAssignment(Value(mapping, "runtime"), Value(mapping, "load"));
                var name = section.Groups["name"].Value;
                if (assignments.TryGetValue(name, out var previous) && previous != value) assignments[name] = new(null, null);
                else assignments.TryAdd(name, value);
            }
            if (Regex.IsMatch(text, @"\bOVERLAY\b")) notes.Add("链接脚本包含 OVERLAY；重叠地址按并集统计，装载映像依据 ELF 程序头分别计入。");
        }
        var regions = new List<LinkedMemoryRegion>();
        foreach (var region in expressions.Values)
        {
            var origin = Resolve(region.Origin, new HashSet<string>(StringComparer.Ordinal));
            var length = Resolve(region.Length, new HashSet<string>(StringComparer.Ordinal));
            if (origin == null || length.HasValue && length.Value > ulong.MaxValue - origin.Value)
            { notes.Add("链接脚本区域地址无法可靠求值；对应容量和分类汇总显示为未知。"); continue; }
            if (length == null) notes.Add("链接脚本区域容量包含未支持的表达式；保留实际地址，用未知容量显示。");
            regions.Add(new(region.Name, region.Attributes, origin.Value, length));
        }
        if (roots.Length == 0 || regions.Count == 0) notes.Add("未找到可可靠求值的实际链接脚本 MEMORY 区域；仅列出 ELF 分配段，不推测芯片容量。");
        // An unread script could redefine MEMORY or constants. Avoid treating a partial layout as authoritative.
        return new(incompleteScripts ? [] : regions, assignments);

        void Load(string path)
        {
            if (!visited.Add(path)) return;
            if (visited.Count > 64) { incompleteScripts = true; notes.Add("链接脚本 INCLUDE 数量超过分析范围；未解析部分保持未知。"); return; }
            if (!File.Exists(path)) { incompleteScripts = true; notes.Add("实际链接脚本或 INCLUDE 文件不可读取；未解析部分保持未知。"); return; }
            for (var item = path; item != null; item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                { incompleteScripts = true; notes.Add("链接脚本包含符号链接或联接目录；未解析部分保持未知。"); return; }
            if (new FileInfo(path).Length > 4 * 1024 * 1024) { incompleteScripts = true; notes.Add("链接脚本超过分析读取范围；未解析部分保持未知。"); return; }
            var text = Regex.Replace(File.ReadAllText(path), @"/\*[\s\S]*?\*/|(?m)^\s*#[^\r\n]*|//[^\r\n]*", "");
            texts.Add(text);
            foreach (Match include in Regex.Matches(text, "\\bINCLUDE\\s+(?:\"(?<quoted>[^\"]+)\"|(?<plain>[^\\s;]+))"))
            {
                var relative = include.Groups["quoted"].Success ? include.Groups["quoted"].Value : include.Groups["plain"].Value;
                var candidates = roots.Select(script => Path.GetDirectoryName(script)!).Prepend(Path.GetDirectoryName(path)!)
                    .Select(directory => Path.GetFullPath(relative, directory)).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
                if (candidates.Length == 1) Load(candidates[0]);
                else { incompleteScripts = true; notes.Add("链接脚本 INCLUDE 路径无法唯一解析；未解析部分保持未知。"); }
            }
        }
        ulong? Resolve(string expression, HashSet<string> active)
        {
            try
            {
                var parser = new IntegerExpression(expression, (name, function) =>
                {
                    var key = function + ":" + name;
                    if (active.Count >= 64 || !active.Add(key)) return null;
                    try
                    {
                        if (function == "" && symbols.TryGetValue(name, out var symbol)) return Resolve(symbol, active);
                        if (expressions.TryGetValue(name, out var region))
                            return function == "ORIGIN" ? Resolve(region.Origin, active) : function == "LENGTH" ? Resolve(region.Length, active) : null;
                        return null;
                    }
                    finally { active.Remove(key); }
                });
                return parser.Read();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or DivideByZeroException) { return null; }
        }
    }

    public static string? Kind(LinkedMemoryRegion region)
    {
        if (Regex.IsMatch(region.Name, @"(?i)(?:FLASH|ROM|IROM|NVM)")) return "Flash";
        if (Regex.IsMatch(region.Name, @"(?i)(?:RAM|DTCM|ITCM|TCM)")) return "RAM";
        var attributes = region.Attributes.Split('!')[0];
        if (attributes.Contains('w', StringComparison.OrdinalIgnoreCase)) return "RAM";
        if (attributes.Contains('r', StringComparison.OrdinalIgnoreCase) && attributes.Contains('x', StringComparison.OrdinalIgnoreCase)) return "Flash";
        return null;
    }

    private static string? Value(Match match, string group) => match.Groups[group].Success ? match.Groups[group].Value : null;
    private static int ClosingBrace(string text, int start)
    {
        var depth = 0;
        for (var index = start; index < text.Length; index++)
        { if (text[index] == '{') depth++; else if (text[index] == '}' && --depth == 0) return index; }
        return -1;
    }

    private sealed class IntegerExpression(string source, Func<string, string, ulong?> resolve)
    {
        private readonly string[] tokens = Regex.Matches(source, @"0[xX][\da-fA-F]+[kKmM]?|\d+[kKmM]?|[A-Za-z_]\w*|<<|>>|[()+*/%&|~^-]").Select(match => match.Value).ToArray();
        private int position;
        private int depth;
        public ulong? Read()
        {
            // Tokenization must consume every character: never accept a known numeric prefix of an unknown expression.
            if (tokens.Length > 4096 || Regex.Replace(source, @"0[xX][\da-fA-F]+[kKmM]?|\d+[kKmM]?|[A-Za-z_]\w*|<<|>>|[()+*/%&|~^-]|\s", "").Length != 0) return null;
            var value = Binary(0);
            return position == tokens.Length && value >= 0 && value <= ulong.MaxValue ? (ulong)value : null;
        }
        private BigInteger Binary(int minimum)
        {
            if (++depth > 64) throw new FormatException();
            var left = Unary();
            while (position < tokens.Length && Precedence(tokens[position]) is var precedence && precedence >= minimum)
            {
                var op = tokens[position++]; var right = Binary(precedence + 1);
                left = op switch
                {
                    "+" => left + right, "-" => left - right, "*" => left * right, "/" => left / right, "%" => left % right,
                    "&" => left & right, "|" => left | right, "^" => left ^ right,
                    "<<" when right >= 0 && right <= 63 => left << (int)right,
                    ">>" when right >= 0 && right <= 63 => left >> (int)right,
                    _ => throw new FormatException()
                };
                if (left < -(BigInteger)ulong.MaxValue || left > ulong.MaxValue) throw new OverflowException();
            }
            depth--; return left;
        }
        private BigInteger Unary()
        {
            if (position >= tokens.Length) throw new FormatException();
            var token = tokens[position++];
            if (token is "+" or "-" or "~")
            {
                if (++depth > 64) throw new FormatException();
                var value = Unary(); depth--;
                return token == "-" ? -value : token == "~" ? ~value : value;
            }
            if (token == "(") { var value = Binary(0); Require(")"); return value; }
            if (char.IsDigit(token[0]))
            {
                var multiplier = token[^1] is 'k' or 'K' ? 1024UL : token[^1] is 'm' or 'M' ? 1024UL * 1024 : 1;
                var number = multiplier > 1 ? token[..^1] : token;
                return (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? BigInteger.Parse("0" + number[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : BigInteger.Parse(number, CultureInfo.InvariantCulture)) * multiplier;
            }
            if (token is "ORIGIN" or "LENGTH")
            {
                Require("("); if (position >= tokens.Length) throw new FormatException();
                var name = tokens[position++]; Require(")");
                return resolve(name, token) is { } value ? value : throw new FormatException();
            }
            return resolve(token, "") is { } symbol ? symbol : throw new FormatException();
        }
        private void Require(string token) { if (position >= tokens.Length || tokens[position++] != token) throw new FormatException(); }
        private static int Precedence(string token) => token switch { "|" => 0, "^" => 1, "&" => 2, "<<" or ">>" => 3, "+" or "-" => 4, "*" or "/" or "%" => 5, _ => -1 };
    }
}
