// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;

namespace SpellWorkspace.Core;

internal sealed class WorkspaceSettings {
    public string ClientExe { get; set; } = "";
    public string ServerRoot { get; set; } = "";
    public string OutputRoot { get; set; } = PortablePaths.Workspace;
    public string GitHubUrl { get; set; } = "https://github.com/Revive101/Imlight.git";
    public string LoginHost { get; set; } = "127.0.0.1";
    public int LoginPort { get; set; } = 12000;
    public string AdminUrl { get; set; } = "http://localhost:8088";
    public string PlayerDataSource { get; set; } = "";
    public string PlayerDataMode { get; set; } = "";
    public string PlayerRavenServerDirectory { get; set; } = "";
    public string PlayerDatabaseName { get; set; } = "Playerdata";
    public string PlayerDatabaseUrl { get; set; } = "http://127.0.0.1:8080";
    public int PlayerSourceSchema { get; set; }
    public string? LastProjectPath { get; set; }
    public string ThemeMode { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#8C62FF";
}

internal sealed class SpellProject {
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My Custom Spells";
    public string ClientSha256 { get; set; } = "";
    public string RootSha256 { get; set; } = "";
    public List<SpellModification> Spells { get; set; } = [];
}

internal sealed class SpellModification {
    public bool IsNew { get; set; }
    public bool Enabled { get; set; } = true;
    public uint SourceTemplateId { get; set; }
    public uint TemplateId { get; set; }
    public string InternalName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? School { get; set; }
    public int? Pips { get; set; }
    public int? ShadowPips { get; set; }
    public bool? XPips { get; set; }
    public Dictionary<string, byte>? SchoolPips { get; set; }
    public int? Accuracy { get; set; }
    public bool? RangeDamage { get; set; }
    public SecondaryEffectModification? SecondaryEffect { get; set; }
    public string? Artwork { get; set; }
    public string? ImportedArtworkBase64 { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ImportedDeckIconBase64 { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseCardArtworkForDeckIcon { get; set; }
    public uint? AnimationTemplateId { get; set; }
    public string? Description { get; set; }
    public bool? Treasure { get; set; }
    public int? Level { get; set; }
    public List<EffectModification> Effects { get; set; } = [];
}

internal sealed class EffectModification {
    public int Index { get; set; }
    public int? Amount { get; set; }
    public int? Rounds { get; set; }
    public string? Target { get; set; }
    public string? School { get; set; }
}

internal sealed class SecondaryEffectModification {
    public string Kind { get; set; } = "Shield";
    public string School { get; set; } = "All";
    public string Target { get; set; } = "Caster";
    public int Amount { get; set; } = 50;
    public int Rounds { get; set; } = 3;
    public string? Description { get; set; }
}

internal sealed record SpellEntry(uint Id, string File, string Name, string InternalName, string School, string Type,
    string Artwork, SpellTemplate Template) {
    public int Pips => Template.m_spellRank?.m_spellRank ?? 0;
    public int Accuracy => Template.m_accuracy;
    public string Details => $"ID: {Id}  ·  {Pips} Pip{(Pips == 1 ? "" : "s")}  ·  {School}  ·  {Type}";
    public bool Hidden => Template.m_Treasure || InternalName.Contains("MOB", StringComparison.OrdinalIgnoreCase)
        || InternalName.Contains("PvP", StringComparison.OrdinalIgnoreCase);
}

internal sealed record OverlayEntry(string Archive, string File, byte[] Data);
internal sealed record BuildOutput(string Folder, string BuildId, string ManifestHash, string? ClientExe, string? Launcher, string? ServerExe);
internal sealed record BuildPair(BuildOutput Client, BuildOutput Server);

internal static class ProjectFiles {
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static string SettingsPath => Path.Combine(PortablePaths.Settings, "settings.json");
    internal static string RecoveryPath => Path.Combine(Path.GetDirectoryName(SettingsPath)!, "recovery.spellproject");
    internal static WorkspaceSettings LoadSettings() {
        try {
            var settings = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new();
            MapSettingsPaths(settings, path => Path.GetFullPath(path, PortablePaths.Root));
            return settings;
        }
        catch { return new(); }
    }
    internal static void SaveSettings(WorkspaceSettings settings) {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        SaveJson(SettingsPath, PortableSettings(settings));
    }
    internal static WorkspaceSettings PortableSettings(WorkspaceSettings settings) {
        var copy = JsonSerializer.Deserialize<WorkspaceSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
        MapSettingsPaths(copy, path => Path.GetFullPath(path, PortablePaths.Root).StartsWith(PortablePaths.Root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(PortablePaths.Root, Path.GetFullPath(path, PortablePaths.Root)) : path);
        return copy;
    }
    private static void MapSettingsPaths(WorkspaceSettings settings, Func<string, string> map) {
        string Map(string? path) => string.IsNullOrWhiteSpace(path) ? "" : map(path);
        settings.ClientExe = Map(settings.ClientExe); settings.ServerRoot = Map(settings.ServerRoot);
        settings.OutputRoot = Map(settings.OutputRoot); settings.PlayerDataSource = Map(settings.PlayerDataSource);
        settings.PlayerRavenServerDirectory = Map(settings.PlayerRavenServerDirectory);
        settings.LastProjectPath = string.IsNullOrWhiteSpace(settings.LastProjectPath) ? null : map(settings.LastProjectPath);
    }
    internal static void SaveJson<T>(string path, T value) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, true);
    }
    internal static SpellProject LoadProject(string path) {
        var project = JsonSerializer.Deserialize<SpellProject>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty project.");
        if (project.SchemaVersion != 1) { throw new InvalidDataException("This project requires a different Spell Workspace version."); }
        return project;
    }
}
