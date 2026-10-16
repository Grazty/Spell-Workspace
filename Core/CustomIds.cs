// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Imcodec.Cryptography;

namespace SpellWorkspace.Core;

internal static class CustomIds {
    private static readonly ConcurrentDictionary<(string Name, uint Id), string> ClientNames = new();
    internal static string ClientName(string name, uint id) {
        if (StringHash.Compute(name) == id) { return name; }
        if (id > int.MaxValue || string.IsNullOrWhiteSpace(name)) { throw new InvalidDataException("The custom spell has an invalid client identity."); }
        return ClientNames.GetOrAdd((name, id), key => MatchClientName(key.Name, key.Id));
    }
    private static uint RawHash(string text) {
        uint result = 0; var shift = 0;
        foreach (var character in text) {
            var value = (byte)character - 32;
            result ^= unchecked((uint)(value << shift));
            if (shift > 24) { result ^= unchecked((uint)(value >> (32 - shift))); }
            shift = (shift + 5) % 32;
        }
        return result;
    }
    private static string MatchClientName(string prefix, uint id) {
        // The client's template cache keys templates by the hash of m_name.
        // Solve a printable suffix so existing reserved IDs and learned spells stay valid.
        const int characters = 14;
        var basisName = prefix + "_" + new string('@', characters);
        var baseline = RawHash(basisName);
        var basis = new uint[32]; var combinations = new BigInteger[32]; var free = new List<BigInteger>();
        for (var variable = 0; variable < characters * 5; variable++) {
            var text = basisName.ToCharArray(); text[text.Length - characters + variable / 5] = (char)(64 | 1 << (variable % 5));
            var value = RawHash(new string(text)) ^ baseline; var combination = BigInteger.One << variable;
            for (var bit = 31; bit >= 0; bit--) {
                if ((value & (1u << bit)) == 0) { continue; }
                if (basis[bit] == 0) { basis[bit] = value; combinations[bit] = combination; break; }
                value ^= basis[bit]; combination ^= combinations[bit];
            }
            if (value == 0) { free.Add(combination); }
        }
        var remainder = id ^ baseline; var solution = BigInteger.Zero;
        for (var bit = 31; bit >= 0; bit--) {
            if ((remainder & (1u << bit)) == 0) { continue; }
            if (basis[bit] == 0) { throw new InvalidDataException("The custom spell's client identity cannot be represented."); }
            remainder ^= basis[bit]; solution ^= combinations[bit];
        }
        for (var attempt = 0; attempt < 4096; attempt++) {
            var random = SHA256.HashData(Encoding.UTF8.GetBytes(basisName + attempt)); var candidate = solution;
            for (var index = 0; index < free.Count; index++) { if ((random[index / 8] & (1 << (index % 8))) != 0) { candidate ^= free[index]; } }
            var suffix = new char[characters];
            for (var index = 0; index < suffix.Length; index++) { suffix[index] = (char)(64 | (int)((candidate >> (index * 5)) & 31)); }
            if (suffix.Any(character => character != '_' && (character < 'A' || character > 'Z'))) { continue; }
            var name = prefix + "_" + new string(suffix);
            if (StringHash.Compute(name) == id) { return name; }
        }
        throw new InvalidDataException("Cannot create a safe internal name for this custom spell.");
    }
    private static string RegistryPath => Path.Combine(Path.GetDirectoryName(ProjectFiles.SettingsPath)!, "custom-ids.json");
    private static Dictionary<uint, string> Read() => File.Exists(RegistryPath)
        ? JsonSerializer.Deserialize<Dictionary<uint, string>>(File.ReadAllText(RegistryPath), ProjectFiles.JsonOptions) ?? throw new InvalidDataException("Invalid custom ID registry.") : [];
    internal static uint Allocate(SpellProject project, IEnumerable<uint> existing) {
        using var mutex = new Mutex(false, "Local\\SpellWorkspaceCustomIds");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) { throw new IOException("Another workspace is reserving spell IDs. Try again."); }
        try {
            var registry = Read(); var taken = existing.Concat(registry.Keys).ToHashSet();
            var id = 0x60000000u | (BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(project.Id))) & 0x0fffffffu);
            while (taken.Contains(id)) { id = 0x60000000u | ((id + 1) & 0x0fffffffu); }
            registry.Add(id, project.Id); ProjectFiles.SaveJson(RegistryPath, registry); return id;
        } finally { mutex.ReleaseMutex(); }
    }
    internal static bool BelongsToAnotherProject(uint id, string projectId) {
        var registry = Read(); return registry.TryGetValue(id, out var owner) && owner != projectId;
    }
    internal static void Claim(SpellProject project) {
        using var mutex = new Mutex(false, "Local\\SpellWorkspaceCustomIds");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) { throw new IOException("Another workspace is reserving spell IDs. Try again."); }
        try {
            var registry = Read();
            foreach (var mod in project.Spells.Where(x => x.IsNew)) {
                if (mod.TemplateId is < 0x60000000u or > 0x6fffffffu || registry.TryGetValue(mod.TemplateId, out var owner) && owner != project.Id) {
                    throw new InvalidDataException("Custom ID belongs to another project or is outside the reserved range: " + mod.TemplateId + ". Import this project as a spell pack to remap IDs safely.");
                }
                registry[mod.TemplateId] = project.Id;
            }
            ProjectFiles.SaveJson(RegistryPath, registry);
        } finally { mutex.ReleaseMutex(); }
    }
}
