// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
// Metadata layout adapted from WizType; see Licenses/WizType-MIT.txt.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SpellWorkspace.Core;

internal sealed class ClientTypeDumpReader : IDisposable {
    private readonly SafeProcessHandle _handle;
    private readonly ulong _base;
    private readonly Dictionary<ulong, byte[]> _pages = new();
    private readonly Dictionary<ulong, string> _strings = new();
    private readonly Dictionary<ulong, (string Name, bool Dynamic)> _containers = new();

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint read);

    internal ClientTypeDumpReader(Process process) {
        _handle = OpenProcess(0x410, false, process.Id); // Query information and read memory only.
        if (_handle.IsInvalid) { throw new InvalidOperationException("Cannot read the selected client process. Run the editor and client with the same Windows permission level."); }
        _base = checked((ulong)process.MainModule!.BaseAddress.ToInt64());
    }

    internal static IReadOnlyList<uint> RegistryGlobals(byte[] image) {
        using var pe = new PEReader(new MemoryStream(image));
        if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64) { throw new InvalidDataException("Type extraction currently requires a Windows x64 client."); }
        int Offset(int rva) {
            foreach (var section in pe.PEHeaders.SectionHeaders) {
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData) { return section.PointerToRawData + rva - section.VirtualAddress; }
            }
            throw new InvalidDataException("Type registry code points outside the executable image.");
        }
        var globals = new HashSet<uint>();
        foreach (var section in pe.PEHeaders.SectionHeaders.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0)) {
            var text = Encoding.Latin1.GetString(image, section.PointerToRawData, section.SizeOfRawData);
            foreach (var pattern in new[] { "\u00e8....\u0048\u003b\u0018\u0074\u0012", "\u00e8....\u0048\u00bd........\u0048\u003b\u0038\u0074\u0012" }) {
                foreach (Match match in Regex.Matches(text, pattern, RegexOptions.Singleline)) {
                    var callRva = section.VirtualAddress + match.Index;
                    var callOffset = section.PointerToRawData + match.Index;
                    var functionRva = checked(callRva + 5 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(callOffset + 1, 4)));
                    var functionOffset = Offset(functionRva);
                    for (var i = 0; i < 96; i++) {
                        if (image[functionOffset + i] != 0x48 || image[functionOffset + i + 1] != 0x8b || image[functionOffset + i + 2] != 0x05) { continue; }
                        var global = functionRva + i + 7 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(functionOffset + i + 3, 4));
                        if (global > 0 && global < pe.PEHeaders.PEHeader!.SizeOfImage) { globals.Add((uint)global); }
                    }
                }
            }
        }
        if (globals.Count == 0) { throw new InvalidDataException("This client uses an unrecognized type-registry layout. No existing or bundled dump was substituted."); }
        return globals.Order().ToArray();
    }

    private byte[] Read(ulong address, int size) {
        if (address < 0x10000 || size < 0 || size > 32 * 1024 * 1024) { throw new InvalidDataException("Invalid type metadata address or size."); }
        var result = new byte[size];
        for (var offset = 0; offset < size;) {
            var current = address + (uint)offset;
            var page = current & ~4095UL;
            if (!_pages.TryGetValue(page, out var data)) {
                data = new byte[4096];
                if (!ReadProcessMemory(_handle, (nint)page, data, 4096, out var read) || read != 4096) { throw new InvalidDataException("Client type metadata is not ready or uses an unsupported layout."); }
                _pages.Add(page, data);
            }
            var at = (int)(current - page); var count = Math.Min(size - offset, 4096 - at);
            data.AsSpan(at, count).CopyTo(result.AsSpan(offset)); offset += count;
        }
        return result;
    }
    private ulong U64(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(Read(address, 8));
    private uint U32(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4));
    private int I32(ulong address) => BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4));
    private bool Bool(ulong address) => Read(address, 1)[0] != 0;
    private string CppString(ulong address) {
        if (_strings.TryGetValue(address, out var cached)) { return cached; }
        var length = U64(address + 16); var capacity = U64(address + 24);
        if (length > 16384 || capacity < length || capacity > 1024 * 1024) { throw new InvalidDataException("Unsupported client metadata string layout."); }
        var value = length == 0 ? "" : Encoding.UTF8.GetString(Read(capacity >= 16 ? U64(address) : address, (int)length));
        _strings[address] = value; return value;
    }
    private string CString(ulong address, int maximum = 1024) {
        var bytes = new List<byte>();
        for (var i = 0; i < maximum; i++) { var b = Read(address + (uint)i, 1)[0]; if (b == 0) { return Encoding.UTF8.GetString(bytes.ToArray()); } bytes.Add(b); }
        throw new InvalidDataException("Unterminated client metadata string.");
    }
    private (string Name, bool Dynamic) Container(ulong address) {
        if (_containers.TryGetValue(address, out var known)) { return known; }
        var table = U64(address); var nameFunction = U64(table + 8);
        var code = Read(nameFunction, 7);
        if (code[0] != 0x48 || code[1] != 0x8d || (code[2] & 0xc7) != 5) { throw new InvalidDataException("Unsupported type container-name accessor."); }
        var name = CString((ulong)((long)nameFunction + 7 + BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(3))), 128);
        var dynamicFunction = U64(table + 0x20); var dynamicCode = Read(dynamicFunction, 8);
        bool dynamic;
        if ((dynamicCode[0] is 0x32 or 0x33) && dynamicCode[1] == 0xc0) { dynamic = false; }
        else if (dynamicCode[0] == 0xb0 && dynamicCode[1] <= 1) { dynamic = dynamicCode[1] == 1; }
        else if (dynamicCode[0] == 0xb8 && U32(dynamicFunction + 1) <= 1) { dynamic = U32(dynamicFunction + 1) == 1; }
        else { throw new InvalidDataException("Unsupported type container-dynamic accessor."); }
        return _containers[address] = (name, dynamic);
    }

    internal SortedDictionary<string, object?> Dump(IReadOnlyList<uint> candidates, CancellationToken cancellation) {
        _pages.Clear(); _strings.Clear(); _containers.Clear();
        ulong root = 0;
        foreach (var global in candidates) {
            try {
                var owner = U64(_base + global); var sentinel = U64(owner); var candidate = U64(sentinel + 8);
                if (!Bool(sentinel + 0x19) || Bool(candidate + 0x19)) { continue; }
                var name = CppString(U64(candidate + 0x28) + 0x38);
                if (name.Length is > 0 and < 1000) { root = candidate; break; }
            } catch (InvalidDataException) { }
        }
        if (root == 0) { throw new InvalidDataException("The selected client's type registry is not initialized yet."); }
        var classes = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var pending = new Stack<ulong>(); var visited = new HashSet<ulong>(); pending.Push(root);
        while (pending.TryPop(out var node)) {
            cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(node) || Bool(node + 0x19)) { continue; }
            if (visited.Count > 100000) { throw new InvalidDataException("Client type registry exceeded its safety bound."); }
            var left = U64(node); var right = U64(node + 0x10);
            if (left != 0) { pending.Push(left); } if (right != 0) { pending.Push(right); }
            var type = U64(node + 0x28); var name = CppString(type + 0x38); var hash = U32(type + 0x58); var fields = U64(type + 0x90);
            // Compact network messages have no property hashes. Their fields must retain
            // the client's registration order; alphabetic sorting corrupts their layout.
            var bases = new List<string>(); var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (fields != 0) {
                var seenBases = new HashSet<ulong>(); var current = fields;
                while ((current = U64(current + 0x18)) != 0) {
                    if (!seenBases.Add(current) || seenBases.Count > 100) { throw new InvalidDataException("Cyclic client base type metadata."); }
                    bases.Add(CppString(current + 0xb8));
                }
                var head = U64(fields + 0x58); var tail = U64(fields + 0x60);
                if (tail < head || (tail - head) % 16 != 0 || tail - head > 4096 * 16) { throw new InvalidDataException("Unsupported client property-vector layout."); }
                for (var at = head; at < tail; at += 16) {
                    var property = U64(at); var propertyType = U64(property + 0x70); var propertyList = U64(property + 0x38);
                    var container = Container(U64(property + 0x40));
                    var item = new SortedDictionary<string, object?> {
                        ["type"] = CppString(propertyType + 0x38), ["id"] = I32(property + 0x50), ["offset"] = I32(property + 0x68),
                        ["flags"] = U32(property + 0x80), ["container"] = container.Name, ["dynamic"] = container.Dynamic,
                        ["singleton"] = Bool(propertyList + 9), ["pointer"] = Bool(propertyType + 0x88), ["hash"] = U32(property + 0x64)
                    };
                    var enumHead = U64(property + 0x98); var enumTail = U64(property + 0xa0);
                    if (enumHead != 0) {
                        if (enumTail < enumHead || (enumTail - enumHead) % 0x48 != 0 || enumTail - enumHead > 100000 * 0x48) { throw new InvalidDataException("Unsupported client enumeration metadata."); }
                        var options = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                        for (var option = enumHead; option < enumTail; option += 0x48) {
                            var text = CppString(option);
                            options[CppString(option + 0x28)] = text.Length == 0 ? U32(option + 0x20)
                                : long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? unchecked((uint)number) : text;
                        }
                        if (options.Count != 0) { item["enum_options"] = options; }
                    }
                    properties[CString(U64(property + 0x58))] = item;
                }
            }
            classes[hash.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new SortedDictionary<string, object?> {
                ["name"] = name, ["bases"] = bases, ["hash"] = hash, ["properties"] = properties
            };
        }
        if (classes.Count < 1000) { throw new InvalidDataException("The selected client has not finished registering its type definitions."); }
        foreach (var required in new[] { "SpellTemplate", "TemplateManifest", "MadlibIconDefinition", "CinematicTemplate" }) {
            if (!classes.Values.Cast<SortedDictionary<string, object?>>().Any(c => c["name"]?.ToString() == "class " + required)) { throw new InvalidDataException("The extracted client registry is missing " + required + "."); }
        }
        return new() { ["version"] = 2, ["classes"] = classes };
    }

    public void Dispose() => _handle.Dispose();
}
