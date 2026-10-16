// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpellWorkspace.Core;

internal static class WorldDataCache {
    internal const string DefaultRepository = "https://github.com/Revive101/spiraldb.git";
    internal static readonly string[] RequiredQuests = ["Tutorial_Intro", "WC-TUT-C05-001", "WC-TUT-C08-001", "WC-COMMONS-MAIN-001"];
    internal static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static string Folder(WorkspaceSettings settings, string repository = DefaultRepository, string branch = "main") {
        var suffix = repository == DefaultRepository && branch == "main" ? "" : "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository + "\n" + branch)))[..10].ToLowerInvariant();
        return Path.Combine(settings.OutputRoot, "WorldData", "SpiralDB" + suffix);
    }
    internal static int Validate(string folder) {
        var quests = Path.Combine(folder, "QuestTemplates"); var registry = Path.Combine(folder, "GlobalRegistry");
        if (!Directory.Exists(quests) || !Directory.Exists(registry)) { throw new InvalidDataException("SpiralDB is incomplete: quest templates and the global registry are required. World-data folder: " + folder); }
        foreach (var quest in RequiredQuests) {
            var path = Path.Combine(quests, "questtemplates_" + quest + ".json");
            if (!File.Exists(path)) { throw new InvalidDataException("SpiralDB is missing required quest " + quest + ". World-data folder: " + folder); }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), JsonOptions);
            if (!document.RootElement.TryGetProperty("m_questName", out var name) || name.GetString() != quest) { throw new InvalidDataException("SpiralDB contains an invalid quest file: " + path); }
        }
        var hasRegistry = false;
        foreach (var path in Directory.EnumerateFiles(registry, "*.json")) {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), JsonOptions);
            if (document.RootElement.TryGetProperty("GlobalRegistryValues", out var values) && values.ValueKind == JsonValueKind.Object && values.EnumerateObject().Any()) { hasRegistry = true; }
        }
        if (!hasRegistry) { throw new InvalidDataException("SpiralDB has no global registry values. World-data folder: " + folder); }
        return Directory.EnumerateFiles(quests, "*.json").Count();
    }
    internal static async Task<string> Ensure(WorkspaceSettings settings, Action<string> log, Action<string>? progress, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        var config = File.ReadAllText(Path.Combine(settings.ServerRoot, "src", "Imlight.Director", "Config", "Imlight.ini"));
        string Read(string key, string fallback) { var match = Regex.Match(config, @"(?m)^\s*" + Regex.Escape(key) + @"\s*=\s*([^\r\n]*)"); return match.Success ? match.Groups[1].Value.Trim() : fallback; }
        var repository = Read("SpiralDBRemoteUrl", DefaultRepository); var branch = Read("SpiralDBBranch", "main");
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(branch)) { throw new InvalidDataException("Imlight must specify an HTTPS SpiralDB repository and a branch."); }
        var folder = Folder(settings, repository, branch); Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        using var cacheLock = new FileStream(folder + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(folder)) {
            log("Reusing shared SpiralDB world data: " + Validate(folder).ToString("N0") + " quest templates. No download is needed.");
            return folder;
        }
        progress?.Invoke("Downloading quest and world data, please wait... (1 of 3)");
        log("Preparing SpiralDB once in the shared portable world-data folder: " + folder);
        var pending = folder + ".download-" + Guid.NewGuid().ToString("N");
        try {
            var service = new BuildService(log);
            try { await service.Run("git", ["-c", "core.longpaths=true", "clone", "--depth", "1", "--single-branch", "--branch", branch, repository, pending], Path.GetDirectoryName(folder)!, token); }
            catch (System.ComponentModel.Win32Exception exception) { throw new InvalidOperationException("Git is required for the first world-data download. Run Install Build Requirements.cmd and install Git, then reopen Spell Workspace and build again.", exception); }
            await service.Run("git", ["-C", pending, "config", "core.longpaths", "true"], Path.GetDirectoryName(folder)!, token);
            var quests = Validate(pending); token.ThrowIfCancellationRequested();
            Directory.Move(pending, folder);
            log("Verified SpiralDB: " + quests.ToString("N0") + " quest templates, tutorial quests and global registry. Future builds reuse this data.");
            return folder;
        } finally {
            if (Directory.Exists(pending) && Path.GetDirectoryName(Path.GetFullPath(pending)) == Path.GetDirectoryName(Path.GetFullPath(folder))
                && (File.GetAttributes(pending) & FileAttributes.ReparsePoint) == 0) {
                try { Directory.Delete(pending, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { log("Incomplete world-data download retained for cleanup: " + pending); }
            }
        }
    }
}
