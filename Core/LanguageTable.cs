// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Globalization;
using System.Text;

namespace SpellWorkspace.Core;

internal sealed class LanguageTable {
    internal string Name { get; }
    internal Dictionary<string, string> Values { get; } = [];
    private readonly byte[] _original;
    private readonly StringBuilder _added = new();
    private uint _nextKey;
    private LanguageTable(string name, byte[] original) { Name = name; _original = original; }
    internal static LanguageTable Read(byte[] data, string name) {
        var lines = Encoding.Unicode.GetString(data).TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        if (lines[0] != "1:" + name) { throw new InvalidDataException("Unsupported language table header: " + name); }
        var table = new LanguageTable(name, data);
        for (var i = 1; i < lines.Length && lines[i].Length > 0; i += 3) {
            if (i + 2 >= lines.Length) { throw new InvalidDataException("Invalid language table entry: " + name); }
            table.Values[lines[i]] = lines[i + 2];
        }
        table._nextKey = checked(table.Values.Keys.Where(x => x.Length == 8 && uint.TryParse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            .Select(x => uint.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).DefaultIfEmpty(0u).Max() + 1);
        return table;
    }
    internal string Add(string value) {
        var key = _nextKey.ToString("X8", CultureInfo.InvariantCulture); _nextKey = checked(_nextKey + 1);
        var text = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");
        Values.Add(key, text);
        _added.Append(key).Append("\r\n\r\n").Append(text).Append("\r\n");
        return Name + "_" + key;
    }
    internal byte[] Encode() {
        var separator = Encoding.Unicode.GetString(_original).EndsWith('\n') ? "" : "\r\n";
        return _original.Concat(Encoding.Unicode.GetBytes(separator + _added)).ToArray();
    }
}
