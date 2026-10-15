using System.Buffers.Binary;
using System.Text;

namespace CMakeDapLink.Core;

internal sealed record ElfMemorySection(string Name, ulong Address, ulong Size, ulong Flags, ulong? LoadAddress, bool FileBacked);

/// <summary>Reads section and load addresses directly; debug data and ELF container size are never memory usage.</summary>
internal static class FirmwareElfReader
{
    private sealed record Header(uint Name, uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size, uint Link, uint Info);
    private sealed record Segment(ulong Offset, ulong Address, ulong Physical, ulong FileSize, ulong MemorySize);

    public static IReadOnlyList<ElfMemorySection> Read(string path)
    {
        using var stream = File.OpenRead(path);
        var ident = ReadAt(0, 16);
        if (!ident.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) || ident[4] is not (1 or 2) || ident[5] is not (1 or 2) || ident[6] != 1)
            throw new InvalidDataException("固件不是支持的 ELF32/ELF64 文件。");
        var wide = ident[4] == 2; var little = ident[5] == 1;
        var fileHeader = ReadAt(0, wide ? 64 : 52);
        if (U16(fileHeader, 16) is not (2 or 3))
            throw new InvalidDataException("内存分析需要已链接的 ELF 可执行文件。");
        var programOffset = wide ? U64(fileHeader, 32) : U32(fileHeader, 28);
        var sectionOffset = wide ? U64(fileHeader, 40) : U32(fileHeader, 32);
        var programSize = U16(fileHeader, wide ? 54 : 42);
        uint programCount = U16(fileHeader, wide ? 56 : 44);
        var sectionSize = U16(fileHeader, wide ? 58 : 46);
        uint sectionCount = U16(fileHeader, wide ? 60 : 48);
        uint namesIndex = U16(fileHeader, wide ? 62 : 50);
        if (sectionOffset == 0 || sectionSize < (wide ? 64 : 40))
            throw new InvalidDataException("ELF 缺少可读取的段表，无法可靠统计静态内存。");
        var first = ReadSection(sectionOffset);
        if (sectionCount == 0) sectionCount = checked((uint)first.Size);
        if (namesIndex == 0xffff) namesIndex = first.Link;
        if (programCount == 0xffff) programCount = first.Info;
        if (sectionCount > 1000000 || programCount > 1000000 || namesIndex >= sectionCount ||
            programCount > 0 && programSize < (wide ? 56 : 32))
            throw new InvalidDataException("ELF 段表或程序头无效。");
        Bounds(sectionOffset, checked((ulong)sectionSize * sectionCount));
        Bounds(programOffset, checked((ulong)programSize * programCount));
        var headers = new List<Header>((int)sectionCount);
        for (uint i = 0; i < sectionCount; i++) headers.Add(ReadSection(sectionOffset + (ulong)i * sectionSize));
        var namesHeader = headers[(int)namesIndex];
        if (namesHeader.Size > 16 * 1024 * 1024) throw new InvalidDataException("ELF 段名称表超过安全读取范围。");
        var names = ReadAt(namesHeader.Offset, checked((int)namesHeader.Size));
        var segments = new List<Segment>();
        for (uint i = 0; i < programCount; i++)
        {
            var data = ReadAt(programOffset + (ulong)i * programSize, wide ? 56 : 32);
            if (U32(data, 0) != 1) continue; // PT_LOAD
            var segment = wide ? new Segment(U64(data, 8), U64(data, 16), U64(data, 24), U64(data, 32), U64(data, 40))
                : new Segment(U32(data, 4), U32(data, 8), U32(data, 12), U32(data, 16), U32(data, 20));
            Bounds(segment.Offset, segment.FileSize);
            if (segment.FileSize > segment.MemorySize || segment.Address > ulong.MaxValue - segment.MemorySize || segment.Physical > ulong.MaxValue - segment.MemorySize)
                throw new InvalidDataException("ELF 装载段地址或大小无效。");
            segments.Add(segment);
        }
        var result = new List<ElfMemorySection>();
        foreach (var section in headers.Where(section => (section.Flags & 2) != 0)) // SHF_ALLOC
        {
            if (section.Address > ulong.MaxValue - section.Size) throw new InvalidDataException("ELF 分配段地址或大小无效。");
            var name = Name(section.Name);
            ulong? load = null;
            if (section.Type != 8 && section.Size > 0) // SHT_NOBITS has no load image.
            {
                Bounds(section.Offset, section.Size);
                var mapped = segments.Where(segment => section.Address >= segment.Address && section.Size <= segment.MemorySize &&
                    section.Address - segment.Address <= segment.MemorySize - section.Size && section.Offset >= segment.Offset &&
                    section.Size <= segment.FileSize && section.Offset - segment.Offset <= segment.FileSize - section.Size &&
                    section.Offset - segment.Offset == section.Address - segment.Address)
                    .Select(segment => checked(segment.Physical + (section.Address - segment.Address))).Distinct().ToArray();
                if (mapped.Length == 1) load = mapped[0];
            }
            result.Add(new(name, section.Address, section.Size, section.Flags, load, section.Type != 8));
        }
        return result;

        Header ReadSection(ulong offset)
        {
            var data = ReadAt(offset, wide ? 64 : 40);
            return wide ? new(U32(data, 0), U32(data, 4), U64(data, 8), U64(data, 16), U64(data, 24), U64(data, 32), U32(data, 40), U32(data, 44))
                : new(U32(data, 0), U32(data, 4), U32(data, 8), U32(data, 12), U32(data, 16), U32(data, 20), U32(data, 24), U32(data, 28));
        }
        string Name(uint offset)
        {
            if (offset >= names.Length) throw new InvalidDataException("ELF 段名称索引无效。");
            var end = Array.IndexOf(names, (byte)0, (int)offset);
            if (end < 0) throw new InvalidDataException("ELF 段名称未结束。");
            return Encoding.UTF8.GetString(names, (int)offset, end - (int)offset);
        }
        void Bounds(ulong offset, ulong size)
        {
            if (offset > (ulong)stream.Length || size > (ulong)stream.Length - offset)
                throw new InvalidDataException("ELF 文件中的偏移或大小超出文件范围。");
        }
        byte[] ReadAt(ulong offset, int count)
        {
            Bounds(offset, (ulong)count); stream.Position = (long)offset;
            var bytes = new byte[count]; stream.ReadExactly(bytes); return bytes;
        }
        ushort U16(byte[] data, int offset) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        uint U32(byte[] data, int offset) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        ulong U64(byte[] data, int offset) => little ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));
    }
}
