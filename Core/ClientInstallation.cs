// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpellWorkspace.Core;

internal static class ClientInstallation {
    internal const string DataDirectoryName = "Spell Workshop Data";
    internal static BuildPair Install(BuildPair pair, WorkspaceSettings settings, CancellationToken token, Action<string> log) {
        var bin = Path.GetDirectoryName(Path.GetFullPath(settings.ClientExe))!;
        var folder = Path.Combine(bin, DataDirectoryName);
        var exe = Path.Combine(bin, "WizardGraphicalClient.Custom.exe");
        if (exe.Equals(Path.GetFullPath(settings.ClientExe), StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("Select the original executable, not the custom executable."); }
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(pair.Client.Folder, "BuildManifest.json")));
        var originalHash = SpellRules.Hash(settings.ClientExe);
        if (!package.RootElement.GetProperty("OriginalClientSha256").GetString()!.Equals(originalHash, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The selected client changed before installation."); }
        var manifestPath = Path.Combine(folder, "BuildManifest.json");
        string? installedHash = null;
        if (File.Exists(exe)) {
            if (!File.Exists(manifestPath)) { throw new IOException("WizardGraphicalClient.Custom.exe already exists without a Spell Workspace installation record. Keep that file safe before installing here."); }
            using var previous = JsonDocument.Parse(File.ReadAllText(manifestPath));
            installedHash = previous.RootElement.GetProperty("InstalledClientSha256").GetString();
            if (installedHash != SpellRules.Hash(exe)) { throw new IOException("The existing custom executable changed outside Spell Workspace. It was not overwritten."); }
        }
        token.ThrowIfCancellationRequested();
        var bytes = PePackage.BuildClient(File.ReadAllBytes(settings.ClientExe), DataDirectoryName + "\\SpellWorkspace.SpellMods.dll");
        var customHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var updates = new BuildUpdates(); updates.Bytes(exe, bytes);
        var runtimeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(pair.Client.Folder, "*", SearchOption.AllDirectories)) {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(pair.Client.Folder, file);
            if (relative is not ("SpellWorkspace.SpellMods.dll" or "SpellWorkspace.Client.exe" or "spell_overlay.bin" or "spell_manifest.json")
                && !relative.StartsWith("Audio" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { continue; }
            updates.Bytes(Path.Combine(folder, relative), File.ReadAllBytes(file));
            if (relative.EndsWith(".dll") || relative.EndsWith(".exe") || relative.StartsWith("Audio" + Path.DirectorySeparatorChar)) { runtimeFiles[relative] = SpellRules.Hash(file); }
        }
        var ini = File.ReadAllText(Path.Combine(pair.Client.Folder, "SpellWorkspace.client.ini"));
        string Set(string text, string name, string value) => Regex.Replace(text, "(?m)^" + Regex.Escape(name) + "=.*$", _ => name + "=" + value);
        ini = Set(ini, "ClientName", Path.GetFileName(exe)); ini = Set(ini, "ClientSha256", customHash);
        ini = Set(ini, "AudioProviderDirectory", Path.Combine(folder, "Audio", "Miles"));
        ini += "ClientPath=" + exe + "\r\n";
        updates.Text(Path.Combine(folder, "SpellWorkspace.client.ini"), ini, new UnicodeEncoding(false, true));
        updates.Json(manifestPath, new { Product = "Spell Workspace", Version = "Beta v0.5", ClientInstallationVersion = 1, pair.Client.BuildId, pair.Client.ManifestHash,
            OriginalClient = settings.ClientExe, OriginalClientSha256 = originalHash, InstalledClientSha256 = customHash,
            RootSha256 = package.RootElement.GetProperty("RootSha256").GetString(), RuntimeFingerprint = package.RootElement.GetProperty("RuntimeFingerprint").GetString(),
            ResourceCount = package.RootElement.GetProperty("ResourceCount").GetInt32(), OriginalSectionsUnchanged = true, ResourceWrites = false,
            LocalhostArguments = "-L 127.0.0.1 12000", RuntimeVerified = false, RuntimeFiles = runtimeFiles });
        var installed = new BuildPair(new(folder, pair.Client.BuildId, pair.Client.ManifestHash, exe, Path.Combine(folder, "SpellWorkspace.Client.exe"), null), pair.Server);
        updates.Json(Path.Combine(settings.OutputRoot, "last-builds.json"), installed);
        if (SpellRules.Hash(settings.ClientExe) != originalHash) { throw new InvalidDataException("The original client changed during installation."); }
        using var buildLock = new FileStream(Path.Combine(settings.OutputRoot, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        updates.Publish(token);
        log((installedHash == customHash ? "Reused installed custom client: " : "Installed custom client: ") + exe);
        log("Mod DLL and spell data: " + folder);
        return installed;
    }
}
