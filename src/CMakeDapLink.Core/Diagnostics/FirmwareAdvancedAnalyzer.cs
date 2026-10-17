using System.Buffers.Binary;
using System.Text;

namespace CMakeDapLink.Core;

public sealed record FirmwareSymbol(string Name, string Address, ulong Size, string Kind, string? Section);
public sealed record FirmwareMapSection(string Name, string Address, ulong Size, string? ObjectFile);
public sealed record FirmwareAnalysisReport(string? FirmwareElfPath, string? MapFilePath,
    IReadOnlyList<FirmwareSymbol> Symbols, IReadOnlyList<FirmwareMemoryRegion> Regions,
    IReadOnlyList<FirmwareMapSection> MapSections, IReadOnlyList<string> Notes);

/// <summary>Reads linked ELF symbol tables directly, without executing binaries or guessing symbol sizes.</summary>
public static class FirmwareAdvancedAnalyzer
{
    private sealed record Section(uint Name, uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size, uint Link, ulong EntrySize);

    public static FirmwareAnalysisReport AnalyzeMap(string path, int top, CancellationToken cancellation)
        => FirmwareGnuMapReader.Read(path, Limit(top), cancellation);

    public static FirmwareAnalysisReport AnalyzeElf(string path, int top, CancellationToken cancellation)
    {
        Limit(top); cancellation.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(path);
        using var stream = File.OpenRead(full);
        if (stream.Length > 256 * 1024 * 1024) throw new InvalidDataException("ELF 文件超过 256 MB 分析范围。");
        // Share the existing linked-image validation and allocation rules with static memory analysis.
        _ = FirmwareElfReader.Read(full);
        var ident = Read(0, 16); var wide = ident[4] == 2; var little = ident[5] == 1;
        var header = Read(0, wide ? 64 : 52);
        var offset = wide ? U64(header, 40) : U32(header, 32);
        var stride = U16(header, wide ? 58 : 46);
        uint count = U16(header, wide ? 60 : 48); uint namesIndex = U16(header, wide ? 62 : 50);
        var first = ReadSection(offset);
        if (count == 0) count = checked((uint)first.Size);
        if (namesIndex == 0xffff) namesIndex = first.Link;
        if (count > 65536 || namesIndex >= count) throw new InvalidDataException("ELF 段数量超过符号分析范围。");
        var sections = new List<Section>((int)count);
        for (uint index = 0; index < count; index++) sections.Add(ReadSection(checked(offset + (ulong)index * stride)));
        var sectionNames = Table(sections[(int)namesIndex]);
        var notes = new List<string>(); var tables = 0; var zeroSize = 0; ulong examined = 0;
        var best = new PriorityQueue<FirmwareSymbol, ulong>();
        var retained = new HashSet<(string, string, ulong)>();
        foreach (var table in sections.Where(section => section.Type is 2 or 11)) // SHT_SYMTAB / SHT_DYNSYM
        {
            cancellation.ThrowIfCancellationRequested(); tables++;
            if (table.Link >= count || sections[(int)table.Link].Type != 3 || table.EntrySize < (ulong)(wide ? 24 : 16) || table.Size % table.EntrySize != 0)
                throw new InvalidDataException("ELF 符号表或字符串表链接无效。");
            examined = checked(examined + table.Size / table.EntrySize);
            if (examined > 2000000) throw new InvalidDataException("ELF 符号数量超过分析范围。");
            var strings = Table(sections[(int)table.Link]); var entries = Table(table);
            for (ulong entry = 0; entry < table.Size; entry += table.EntrySize)
            {
                if ((entry / table.EntrySize & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var at = checked((int)entry); var info = entries[at + (wide ? 4 : 12)]; var kind = info & 0xf;
                var sectionIndex = U16(entries, at + (wide ? 6 : 14));
                if (kind is not (1 or 2 or 6 or 10) || sectionIndex == 0 || sectionIndex >= count || (sections[sectionIndex].Flags & 2) == 0) continue;
                var size = wide ? U64(entries, at + 16) : U32(entries, at + 8);
                if (size == 0) { zeroSize++; continue; }
                var address = wide ? U64(entries, at + 8) : U32(entries, at + 4);
                if (size > ulong.MaxValue - address) throw new InvalidDataException("ELF 符号地址或大小无效。");
                var name = Name(strings, U32(entries, at)); if (name.Length == 0) continue;
                var symbol = new FirmwareSymbol(name, "0x" + address.ToString(wide ? "X16" : "X8"), size,
                    kind is 2 or 10 ? "function" : "variable", Name(sectionNames, sections[sectionIndex].Name));
                var key = (symbol.Name, symbol.Address, symbol.Size);
                if (retained.Contains(key) || best.Count == top && best.Peek().Size >= size) continue;
                if (best.Count == top) { var removed = best.Dequeue(); retained.Remove((removed.Name, removed.Address, removed.Size)); }
                best.Enqueue(symbol, size); retained.Add(key);
            }
        }
        if (tables == 0) notes.Add("ELF 没有符号表，可能已被 strip；请使用未剥离符号的 ELF 获取函数和变量排行。");
        else if (best.Count == 0) notes.Add("ELF 中没有具有已知大小的已分配函数或变量；不根据地址差推测符号大小。");
        if (zeroSize > 0) notes.Add("大小为零的符号未参与排行；符号别名和重叠地址不能相加作为总内存用量。");
        notes.Add("符号排行使用 ELF 中的真实大小，只包含已分配函数和变量；不包含未定义符号或调试段。");
        return new(full, null, best.UnorderedItems.Select(item => item.Element).OrderByDescending(item => item.Size)
            .ThenBy(item => item.Name, StringComparer.Ordinal).ToArray(), [], [], notes);

        Section ReadSection(ulong position)
        {
            var data = Read(position, wide ? 64 : 40);
            return wide ? new(U32(data, 0), U32(data, 4), U64(data, 8), U64(data, 16), U64(data, 24), U64(data, 32), U32(data, 40), U64(data, 56))
                : new(U32(data, 0), U32(data, 4), U32(data, 8), U32(data, 12), U32(data, 16), U32(data, 20), U32(data, 24), U32(data, 36));
        }
        byte[] Table(Section section)
        {
            if (section.Size > 64 * 1024 * 1024) throw new InvalidDataException("ELF 符号表或名称表超过 64 MB 分析范围。");
            return Read(section.Offset, checked((int)section.Size));
        }
        byte[] Read(ulong position, int size)
        {
            if (position > (ulong)stream.Length || (ulong)size > (ulong)stream.Length - position)
                throw new InvalidDataException("ELF 文件中的偏移或大小超出文件范围。");
            stream.Position = (long)position; var bytes = new byte[size]; stream.ReadExactly(bytes); return bytes;
        }
        ushort U16(byte[] data, int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        uint U32(byte[] data, int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        ulong U64(byte[] data, int at) => little ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at));
    }

    private static string Name(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) throw new InvalidDataException("ELF 名称索引无效。");
        var end = Array.IndexOf(strings, (byte)0, (int)offset, Math.Min(4097, strings.Length - (int)offset));
        if (end < 0) throw new InvalidDataException("ELF 名称未结束或超过分析范围。");
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }
    private static int Limit(int top) => top is >= 1 and <= 100 ? top : throw new ArgumentOutOfRangeException(nameof(top), "排行数量必须为 1–100。");
}
