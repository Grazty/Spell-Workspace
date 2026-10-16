// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
namespace SpellWorkspace.Core;

internal static class AudioProviders {
    internal static string Export(ResourceCatalog catalog, string clientFolder) {
        var providers = catalog.GetArchive("_Shared-WorldData").Files.Keys
            .Where(x => x.StartsWith("Sound/Miles/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(x) == x["Sound/Miles/".Length..]
                && Path.GetExtension(x).ToLowerInvariant() is ".asi" or ".flt" or ".m3d" or ".dll").ToArray();
        if (!providers.Any(x => x.EndsWith("mss64mp3.asi", StringComparison.OrdinalIgnoreCase))
            || !providers.Any(x => x.EndsWith("mss64ogg.asi", StringComparison.OrdinalIgnoreCase))) {
            throw new InvalidDataException("The selected client's shared resources are missing its MP3 or OGG audio decoder.");
        }
        var folder = Path.Combine(clientFolder, "Audio", "Miles"); Directory.CreateDirectory(folder);
        foreach (var file in providers) { File.WriteAllBytes(Path.Combine(folder, Path.GetFileName(file)), catalog.Read("_Shared-WorldData", file)); }
        return folder;
    }
}
