// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;
using Imlight.CoreLib.Game.Quests.Management;

namespace SpellWorkspace.Core;

internal sealed class ResourceCatalog : IDisposable {
    private readonly Dictionary<string, (FileStream Stream, Archive Archive)> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _locale = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BitmapSource> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CinematicTemplate> _cinematics = new(StringComparer.Ordinal);
    internal string GameData { get; }
    internal string ClientExe { get; }
    internal string RootSha256 { get; }
    internal TemplateManifest Manifest { get; }
    internal IReadOnlyList<SpellEntry> Spells { get; private set; } = [];
    internal IReadOnlyDictionary<uint, SpellEntry> ById { get; private set; } = new Dictionary<uint, SpellEntry>();

    internal ResourceCatalog(string clientExe) {
        var bin = Path.GetDirectoryName(Path.GetFullPath(clientExe))!;
        ClientExe = Path.GetFullPath(clientExe);
        GameData = Path.GetFullPath(Path.Combine(bin, "..", "Data", "GameData"));
        if (!File.Exists(Path.Combine(GameData, "Root.wad"))) { throw new FileNotFoundException("Select the game client in its installed Bin folder. Data/GameData/Root.wad is required."); }
        Manifest = Decode<TemplateManifest>(Read("Root", "TemplateManifest.xml"));
        using var root = File.OpenRead(Path.Combine(GameData, "Root.wad"));
        RootSha256 = Convert.ToHexString(SHA256.HashData(root)).ToLowerInvariant();
    }

    internal void Index(Action<int, int>? progress = null) {
        var records = Manifest.m_serializedTemplates.Where(x => x is not null && x.m_filename.ToString().StartsWith("Spells/", StringComparison.OrdinalIgnoreCase)).ToList();
        var spells = new List<SpellEntry>();
        var rootFiles = GetArchive("Root").Files;
        for (var i = 0; i < records.Count; i++) {
            var location = records[i];
            if (i % 100 == 0) { progress?.Invoke(i, records.Count); }
            if (!rootFiles.ContainsKey(location.m_filename)) { continue; }
            try {
                var template = Decode<SpellTemplate>(Read("Root", location.m_filename));
                if (template.m_spellRank is null || string.IsNullOrEmpty(template.m_sMagicSchoolName)) { continue; }
                var name = English(template.m_displayName);
                spells.Add(new(location.m_id, location.m_filename, name.Length == 0 ? template.m_name : name,
                    template.m_name, template.m_sMagicSchoolName, template.m_sTypeName, ImagePath(template.m_imageName), template));
            } catch (Exception) { /* Unsupported template types stay out of the editable catalog. */ }
        }
        Spells = spells.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToArray();
        ById = Spells.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        progress?.Invoke(records.Count, records.Count);
    }

    internal byte[] Read(string archive, string file) {
        var memory = GetArchive(archive).OpenFile(file) ?? throw new FileNotFoundException($"Resource not found: {archive}/{file}");
        return memory.ToArray();
    }
    internal Archive GetArchive(string name) {
        lock (_archives) {
            if (_archives.TryGetValue(name, out var cached)) { return cached.Archive; }
            var path = Path.Combine(GameData, name.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ? name : name + ".wad");
            var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try {
            var archive = ArchiveParser.Parse(stream) ?? throw new InvalidDataException("Invalid archive.");
                _archives.Add(name, (stream, archive));
                return archive;
            } catch { stream.Dispose(); throw; }
        }
    }
    internal CinematicTemplate Cinematic(string name) {
        lock (_cinematics) {
            if (_cinematics.TryGetValue(name, out var cached)) { return cached; }
            var candidates = GetArchive("Root").Files.Keys.Where(path => path.StartsWith("Cinematics/", StringComparison.Ordinal)
                && Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase)).OrderBy(path => path.Length);
            foreach (var path in candidates) {
                var cinematic = Decode<CinematicTemplate>(Read("Root", path));
                if (cinematic.m_name.ToString() == name) { _cinematics[name] = cinematic; return cinematic; }
            }
            throw new InvalidDataException("No cinematic was found for " + name + ".");
        }
    }
    internal string English(string key) {
        var underscore = key?.IndexOf('_') ?? -1;
        if (underscore < 1) { return key ?? ""; }
        var table = key![..underscore];
        var dictionary = _locale.GetOrAdd(table, name => {
            try {
                var content = Encoding.Unicode.GetString(Read("Root", "Locale/en-US/" + name + ".lang")).TrimStart('\uFEFF');
                var lines = content.Split('\n');
                var result = new Dictionary<string, string>();
                for (var i = 1; i + 2 < lines.Length; i += 3) { result[lines[i].TrimEnd('\r')] = lines[i + 2].TrimEnd('\r'); }
                return result;
            } catch { return new(); }
        });
        return dictionary.GetValueOrDefault(key[(underscore + 1)..], "");
    }
    internal BitmapSource? Texture(string path) {
        path = ImagePath(path);
        if (path.Length == 0) { return null; }
        if (_textures.TryGetValue(path, out var cached)) { return cached; }
        byte[]? data = null;
        foreach (var archive in new[] { "Root", "_Shared-WorldData" }) {
            try { data = Read(archive, path); break; } catch { }
        }
        if (data is null || !DdsDecoder.TryDecode(data, 512, out var width, out var height, out var rgba, out _)) { return null; }
        for (var i = 0; i < rgba.Length; i += 4) { (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
        bitmap.Freeze();
        _textures.TryAdd(path, bitmap);
        return bitmap;
    }
    internal static string ImagePath(string? input) {
        var result = input ?? "";
        if (result.StartsWith('|')) { result = result.Split('|').Last(); }
        result = result.Replace('\\', '/');
        return result.Length > 0 && !result.Contains('/') ? "GUI/SummonedImages/" + result : result;
    }
    internal static T Decode<T>(byte[] data) where T : PropertyClass {
        if (!new BindSerializer().Deserialize(data, 1, out T value) || value is null) { throw new InvalidDataException($"Cannot decode {typeof(T).Name}."); }
        return value;
    }
    internal static byte[] Encode(PropertyClass value) {
        // Numeric enums avoid C# enum names that differ from the client's qualified names.
        var serializer = new ObjectSerializer(true, SerializerFlags.SerializeFlags | SerializerFlags.CompactLength);
        if (!serializer.Serialize(value, 1, out var output)) { throw new InvalidDataException($"Cannot encode {value.GetType().Name}."); }
        var encoded = (byte[])output;
        var result = new byte[encoded.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, BindSerializer.BiNDMagic);
        encoded.CopyTo(result, 4);
        return result;
    }
    internal static T Clone<T>(T value) where T : PropertyClass => Decode<T>(Encode(value));
    public void Dispose() {
        foreach (var cached in _archives.Values) { cached.Stream.Dispose(); }
    }
}
