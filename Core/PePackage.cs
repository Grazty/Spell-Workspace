// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SpellWorkspace.Core;

internal static class PePackage {
    private static int Read16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint Read32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static void Write32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    internal static byte[] BuildClient(byte[] original, string modImport = "SpellWorkspace.SpellMods.dll") {
        if (modImport.Length > 240 || modImport.Any(character => character > 127 || character == 0) || Path.IsPathRooted(modImport) || modImport.Contains("..")) { throw new InvalidDataException("Invalid mod DLL import path."); }
        if (original.Length < 0x100 || Read16(original, 0) != 0x5a4d) { throw new InvalidDataException("Expected a Windows executable."); }
        _ = ClientTypeDumpReader.RegistryGlobals(original);
        var pe = checked((int)Read32(original, 0x3c));
        if (Read32(original, pe) != 0x4550 || Read16(original, pe + 4) != 0x8664) { throw new InvalidDataException("Expected a Windows x64 executable."); }
        var sectionCount = Read16(original, pe + 6);
        var optional = pe + 24;
        var sectionTable = optional + Read16(original, pe + 20);
        var header = sectionTable + sectionCount * 40;
        var fileAlignment = checked((int)Read32(original, optional + 36));
        var sectionAlignment = checked((int)Read32(original, optional + 32));
        var firstRaw = int.MaxValue;
        var virtualEnd = 0;
        var sections = new List<(int Header, int Raw, int Length)>();
        for (var i = 0; i < sectionCount; i++) {
            var section = sectionTable + i * 40;
            var raw = checked((int)Read32(original, section + 20));
            var length = checked((int)Read32(original, section + 16));
            var va = checked((int)Read32(original, section + 12));
            var size = checked((int)Read32(original, section + 8));
            if (raw > 0) { firstRaw = Math.Min(firstRaw, raw); }
            virtualEnd = Math.Max(virtualEnd, checked(va + Math.Max(size, length)));
            sections.Add((section, raw, length));
        }
        if (header + 40 > firstRaw || original.AsSpan(header, 40).ContainsAnyExcept((byte)0)) { throw new InvalidDataException("This executable has no available section-header space."); }
        var directory = optional + 112;
        int RvaToOffset(uint rva) {
            foreach (var section in sections) {
                var va = Read32(original, section.Header + 12);
                if (rva >= va && rva < va + Math.Max(Read32(original, section.Header + 8), (uint)section.Length)) {
                    return checked(section.Raw + (int)(rva - va));
                }
            }
            throw new InvalidDataException("Import table is outside recognized sections.");
        }
        var importOffset = RvaToOffset(Read32(original, directory + 8));
        var importCount = 0;
        while (Read32(original, importOffset + importCount * 20) != 0 || Read32(original, importOffset + importCount * 20 + 12) != 0) {
            if (++importCount > 4096) { throw new InvalidDataException("Invalid imports."); }
        }
        var rva = Align(virtualEnd, sectionAlignment);
        using var payloadStream = new MemoryStream();
        using var writer = new BinaryWriter(payloadStream, Encoding.ASCII, true);
        writer.Write(original, importOffset, importCount * 20);
        writer.Write(new byte[40]);
        var name = checked((int)payloadStream.Position); writer.Write(Encoding.ASCII.GetBytes(modImport + "\0"));
        if (payloadStream.Position % 2 != 0) { writer.Write((byte)0); }
        var hintName = checked((int)payloadStream.Position); writer.Write((ushort)0); writer.Write("SpellWorkspaceInitialize\0"u8.ToArray());
        while (payloadStream.Position % 8 != 0) { writer.Write((byte)0); }
        var lookup = checked((int)payloadStream.Position); writer.Write((ulong)(rva + hintName)); writer.Write(0UL);
        var iat = checked((int)payloadStream.Position); writer.Write((ulong)(rva + hintName)); writer.Write(0UL);
        var entry = checked((int)payloadStream.Position);
        // The initializer runs outside the Windows loader lock, before the original entry point.
        writer.Write(new byte[] { 0x48, 0x83, 0xec, 0x28, 0xff, 0x15 });
        writer.Write(iat - (entry + 10));
        writer.Write(new byte[] { 0x48, 0x83, 0xc4, 0x28, 0x85, 0xc0, 0x74, 0x05, 0xe9 });
        writer.Write(checked((int)Read32(original, optional + 16) - (rva + entry + 23)));
        writer.Write(new byte[] { 0xb8, 0x01, 0x00, 0x00, 0x00, 0xc3 });
        var payload = payloadStream.ToArray();
        var descriptor = importCount * 20;
        Write32(payload, descriptor, (uint)(rva + lookup));
        Write32(payload, descriptor + 12, (uint)(rva + name));
        Write32(payload, descriptor + 16, (uint)(rva + iat));
        var originalLength = original.Length;
        var certificate = checked((int)Read32(original, directory + 4 * 8));
        var certificateLength = checked((int)Read32(original, directory + 4 * 8 + 4));
        if (certificate > 0 && certificate + certificateLength == originalLength) { originalLength = certificate; }
        var rawStart = Align(originalLength, fileAlignment);
        var rawSize = Align(payload.Length, fileAlignment);
        var output = new byte[checked(rawStart + rawSize)];
        original.AsSpan(0, originalLength).CopyTo(output);
        payload.CopyTo(output, rawStart);
        ".swmods\0"u8.CopyTo(output.AsSpan(header, 8));
        Write32(output, header + 8, (uint)payload.Length);
        Write32(output, header + 12, (uint)rva);
        Write32(output, header + 16, (uint)rawSize);
        Write32(output, header + 20, (uint)rawStart);
        Write32(output, header + 36, 0xe0000060);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pe + 6), checked((ushort)(sectionCount + 1)));
        Write32(output, optional + 16, (uint)(rva + entry));
        Write32(output, optional + 56, (uint)Align(rva + payload.Length, sectionAlignment));
        Write32(output, directory + 8, (uint)rva);
        Write32(output, directory + 12, (uint)((importCount + 2) * 20));
        Write32(output, directory + 4 * 8, 0); Write32(output, directory + 4 * 8 + 4, 0);
        Write32(output, directory + 11 * 8, 0); Write32(output, directory + 11 * 8 + 4, 0);
        Write32(output, optional + 64, 0);
        foreach (var section in sections) {
            if (!original.AsSpan(section.Raw, section.Length).SequenceEqual(output.AsSpan(section.Raw, section.Length))) { throw new InvalidDataException("Original executable section changed."); }
        }
        return output;
    }
}
