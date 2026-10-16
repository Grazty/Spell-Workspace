// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpellWorkspace.Core;

internal static class SpellPacks {
    internal static void Export(string path, SpellProject project) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(project, ProjectFiles.JsonOptions);
        using var zip = ZipFile.Open(path + ".tmp", ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry("project.json", CompressionLevel.Optimal).Open()) { stream.Write(bytes); }
        using (var stream = new StreamWriter(zip.CreateEntry("sha256.txt").Open())) { stream.Write(Convert.ToHexString(SHA256.HashData(bytes))); }
        zip.Dispose(); File.Move(path + ".tmp", path, true);
    }
    internal static SpellProject Read(string path) {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count != 2 || zip.Entries.Any(e => e.FullName is not ("project.json" or "sha256.txt") || e.Length > 33554432)) {
            throw new InvalidDataException("This is not a supported data-only spell pack.");
        }
        var entry = zip.GetEntry("project.json") ?? throw new InvalidDataException("Missing project manifest.");
        using var input = entry.Open(); using var memory = new MemoryStream(); input.CopyTo(memory);
        using var checksum = new StreamReader(zip.GetEntry("sha256.txt")!.Open());
        var bytes = memory.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(checksum.ReadToEnd().Trim(), StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("Spell pack checksum failed."); }
        var project = JsonSerializer.Deserialize<SpellProject>(bytes, ProjectFiles.JsonOptions) ?? throw new InvalidDataException("Invalid spell pack.");
        if (project.SchemaVersion != 1) { throw new InvalidDataException("Unsupported spell pack schema."); }
        return project;
    }
    internal static Dictionary<uint, uint> Merge(ResourceCatalog catalog, SpellProject current, SpellProject incoming, IEnumerable<uint> selectedIds) {
        var draft = JsonSerializer.Deserialize<SpellProject>(JsonSerializer.Serialize(current, ProjectFiles.JsonOptions), ProjectFiles.JsonOptions)!;
        var selected = selectedIds.ToHashSet();
        if (incoming.Spells.Select(x => x.TemplateId).Distinct().Count() != incoming.Spells.Count) { throw new InvalidDataException("Spell pack contains duplicate IDs."); }
        var existing = catalog.Manifest.m_serializedTemplates.Select(x => x.m_id).Concat(draft.Spells.Select(x => x.TemplateId)).ToHashSet();
        var names = catalog.Spells.Select(x => Imcodec.Cryptography.StringHash.Compute(x.InternalName)).Concat(draft.Spells.Where(x => x.IsNew).Select(x => Imcodec.Cryptography.StringHash.Compute(x.InternalName))).ToHashSet();
        var remap = new Dictionary<uint, uint>();
        foreach (var original in incoming.Spells.Where(x => selected.Contains(x.TemplateId))) {
            var mod = JsonSerializer.Deserialize<SpellModification>(JsonSerializer.Serialize(original, ProjectFiles.JsonOptions), ProjectFiles.JsonOptions)!;
            if (!catalog.ById.ContainsKey(mod.SourceTemplateId)) { throw new InvalidDataException($"The source template {mod.SourceTemplateId} is unavailable in this client."); }
            if (!mod.IsNew && draft.Spells.Any(x => x.TemplateId == mod.TemplateId)) { throw new InvalidDataException($"Spell {mod.TemplateId} already has an override. Review/reset it before importing this override."); }
            if (mod.IsNew && (existing.Contains(mod.TemplateId) || names.Contains(Imcodec.Cryptography.StringHash.Compute(mod.InternalName)) || CustomIds.BelongsToAnotherProject(mod.TemplateId, draft.Id))) {
                var replacement = SpellRules.Create(catalog, draft, catalog.ById[mod.SourceTemplateId], mod.DisplayName ?? "Custom Spell");
                remap[mod.TemplateId] = replacement.TemplateId;
                mod.TemplateId = replacement.TemplateId; mod.InternalName = replacement.InternalName;
            }
            existing.Add(mod.TemplateId); names.Add(Imcodec.Cryptography.StringHash.Compute(mod.InternalName)); draft.Spells.Add(mod);
        }
        var errors = SpellRules.Validate(catalog, draft);
        if (errors.Count > 0) { throw new InvalidDataException(string.Join("\n", errors)); }
        CustomIds.Claim(draft); current.Spells = draft.Spells;
        return remap;
    }
}
