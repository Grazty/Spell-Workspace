// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using SpellWorkspace.Core;

namespace Imlight.CoreLib.Shared.Resources {
    internal static class CardResourceBridge {
        internal static ResourceCatalog? Catalog { get; set; }
        internal static Dictionary<string, string> Overrides { get; } = [];
    }
    internal static class Locale {
        internal static string GetEnglishName(string key) => CardResourceBridge.Overrides.GetValueOrDefault(key) ?? CardResourceBridge.Catalog?.English(key) ?? "";
        internal static string GetEnglishName(string table, string key) => GetEnglishName(table + "_" + key);
    }
    internal static class RootArchiveLoader {
        internal static T? GetFile<T>(string name) where T : PropertyClass => CardResourceBridge.Catalog is null ? null : ResourceCatalog.Decode<T>(CardResourceBridge.Catalog.Read("Root", name));
    }
    internal static class CoreObjectFactory {
        internal static CoreTemplate? GetCoreTemplate(ulong id) {
            var catalog = CardResourceBridge.Catalog;
            var location = catalog?.Manifest.m_serializedTemplates.FirstOrDefault(x => x.m_id == id);
            if (catalog is null || location is null) { return null; }
            var path = location.m_filename.ToString();
            if (path.StartsWith('|')) {
                var parts = path.Split('|', 4);
                return parts.Length == 4 ? ResourceCatalog.Decode<CoreTemplate>(catalog.Read(parts[1] + "-WorldData", parts[3])) : null;
            }
            return ResourceCatalog.Decode<CoreTemplate>(catalog.Read("Root", path));
        }
    }
}
namespace Imlight.Common {
    internal static class Logger {
        internal static object[] Args(params object[] args) => args;
        internal static void Debug(string text, object[] args) { System.Diagnostics.Debug.WriteLine(text); }
        internal static void Information(string text, object[] args) { System.Diagnostics.Debug.WriteLine(text); }
        internal static void Warning(string text, object[] args) { System.Diagnostics.Debug.WriteLine(text); }
    }
}
