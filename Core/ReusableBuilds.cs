/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * SPELL WORKSPACE BUILD REUSE
 * ========================================================================
 *
 * PURPOSE:
 * Reuse verified client binaries and publish matching project data together.
 *
 * USAGE EXAMPLE:
 * Build All prepares both packages before publishing their data files.
 *
 * NOTE:
 * Runtime caches exclude player saves and project spell data.
 *
 * TODO:
 *
 * Created by: Spell Workspace contributors
 * Version: KALI 1.0
 * Last Updated: 10/13/2026
 */
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpellWorkspace.Core;

internal sealed class BuildUpdates {
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    internal void Bytes(string path, byte[] data) => _files[Path.GetFullPath(path)] = data;
    internal void Text(string path, string text, Encoding? encoding = null) {
        encoding ??= new UTF8Encoding(false);
        Bytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
    }
    internal void Json<T>(string path, T value) => Bytes(path, JsonSerializer.SerializeToUtf8Bytes(value, ProjectFiles.JsonOptions));
    internal void Publish(CancellationToken token) {
        var pending = new List<(string Path, string Temp, byte[]? Original)>();
        var installed = new List<(string Path, byte[]? Original)>();
        try {
            foreach (var pair in _files) {
                token.ThrowIfCancellationRequested();
                var original = File.Exists(pair.Key) ? File.ReadAllBytes(pair.Key) : null;
                if (original is not null && original.SequenceEqual(pair.Value)) { continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                var temp = pair.Key + ".pending-" + Guid.NewGuid().ToString("N");
                pending.Add((pair.Key, temp, original));
                File.WriteAllBytes(temp, pair.Value);
            }
            token.ThrowIfCancellationRequested();
            foreach (var file in pending) {
                File.Move(file.Temp, file.Path, true);
                installed.Add((file.Path, file.Original));
            }
        } catch {
            foreach (var file in installed.AsEnumerable().Reverse()) {
                if (file.Original is null) { File.Delete(file.Path); }
                else { File.WriteAllBytes(file.Path, file.Original); }
            }
            throw;
        } finally {
            foreach (var file in pending) { if (File.Exists(file.Temp)) { File.Delete(file.Temp); } }
        }
    }
    internal static string ProjectRuntime(WorkspaceSettings settings, SpellProject project, string kind, string fingerprint) {
        if (!Guid.TryParseExact(project.Id, "N", out _)) { throw new InvalidDataException("Invalid project identifier."); }
        if (kind is not ("Client" or "Server")) { throw new InvalidDataException("Invalid build type."); }
        return Path.GetFullPath(Path.Combine(settings.OutputRoot, "Projects", project.Id, kind, "Current"));
    }
    internal static void CopyChanged(string source, string destination, CancellationToken token, Func<string, bool>? include = null) {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) { return; }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (include is not null && !include(relative)) { continue; }
            var target = Path.Combine(destination, relative);
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length && SpellRules.Hash(target) == SpellRules.Hash(file)) { continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
    internal static void RemovePreviousBuilds(WorkspaceSettings settings, SpellProject project, Action<string> log) {
        foreach (var kind in new[] { "Client", "Server" }) {
            var root = Path.GetDirectoryName(ProjectRuntime(settings, project, kind, ""))!;
            if (!Directory.Exists(root)) { continue; }
            foreach (var folder in Directory.EnumerateDirectories(root)) {
                var name = Path.GetFileName(folder);
                if (name.Length != 64 || !name.All(Uri.IsHexDigit)) { continue; }
                try {
                    if (!TemporaryBuildSource.PlainTree(folder)) { continue; }
                    var manifest = Path.Combine(folder, "BuildManifest.json");
                    var spells = Path.Combine(folder, "spell_manifest.json");
                    if (!File.Exists(manifest) || !File.Exists(spells)) { continue; }
                    using var build = JsonDocument.Parse(File.ReadAllText(manifest));
                    using var saved = JsonDocument.Parse(File.ReadAllText(spells));
                    if (build.RootElement.GetProperty("Product").GetString() != "Spell Workspace" || saved.RootElement.GetProperty("Id").GetString() != project.Id) { continue; }
                    if (build.RootElement.TryGetProperty("PlayerDataDirectory", out var players)
                        && Path.GetFullPath(players.GetString()!).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { continue; }
                    // Never remove a previous build while its executable is running.
                    foreach (var executable in Directory.EnumerateFiles(folder, "*.exe")) {
                        using var check = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    if (!Path.GetDirectoryName(Path.GetFullPath(folder))!.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) { continue; }
                    Directory.Delete(folder, true);
                    log("Removed an older generated " + kind.ToLowerInvariant() + " build. Current build and wizard saves were kept.");
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) {
                    log("An older " + kind.ToLowerInvariant() + " build is still in use or could not be verified; it was kept.");
                }
            }
        }
    }
}

internal sealed class ClientRuntimeCache : IDisposable {
    private readonly FileStream _lock;
    private readonly string _root;
    internal string Fingerprint { get; }
    internal string Runtime { get; }
    internal ClientRuntimeCache(WorkspaceSettings settings, ResourceCatalog catalog, CancellationToken token, SpellProject? project = null) {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => digest.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add("ClientPackage-v1"); Add(SpellRules.Hash(settings.ClientExe)); Add(catalog.RootSha256);
        var native = Path.Combine(BuildService.AssetsDirectory, "Native");
        foreach (var file in Directory.EnumerateFiles(native, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
            var relative = Path.GetRelativePath(native, file);
            if (relative == "compile.lock" || relative.StartsWith("build" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { continue; }
            token.ThrowIfCancellationRequested(); Add(relative); Add(SpellRules.Hash(file));
        }
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(settings.ClientExe)!, "*.dll").Order(StringComparer.Ordinal)) {
            token.ThrowIfCancellationRequested(); Add(Path.GetFileName(file)); Add(SpellRules.Hash(file));
        }
        foreach (var file in catalog.GetArchive("_Shared-WorldData").Files.Keys.Where(x => x.StartsWith("Sound/Miles/", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)) {
            token.ThrowIfCancellationRequested(); Add(file); digest.AppendData(SHA256.HashData(catalog.Read("_Shared-WorldData", file)));
        }
        Fingerprint = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        Runtime = project is null ? Path.Combine(settings.OutputRoot, "RuntimeCache", "Client", Fingerprint, "Client") : BuildUpdates.ProjectRuntime(settings, project, "Client", Fingerprint);
        _root = project is null ? Path.GetDirectoryName(Runtime)! : Path.Combine(Runtime, ".runtime");
        Directory.CreateDirectory(_root);
        _lock = new FileStream(Path.Combine(_root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal bool IsReady(CancellationToken token) {
        if (!File.Exists(Path.Combine(_root, "fingerprint.txt")) || File.ReadAllText(Path.Combine(_root, "fingerprint.txt")) != Fingerprint) { return false; }
        var path = Path.Combine(_root, "files.json");
        if (!File.Exists(path)) { return false; }
        Dictionary<string, string>? files;
        try { files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), ProjectFiles.JsonOptions); }
        catch (Exception exception) when (exception is JsonException or IOException) { return false; }
        if (files is null || !files.ContainsKey("SpellWorkspace.SpellMods.dll") || !files.ContainsKey("SpellWorkspace.Client.exe")
            || !files.Keys.Any(x => x.StartsWith("WizardGraphicalClient", StringComparison.Ordinal))) { return false; }
        foreach (var pair in files) {
            token.ThrowIfCancellationRequested();
            var file = Path.GetFullPath(Path.Combine(Runtime, pair.Key));
            if (!file.StartsWith(Path.GetFullPath(Runtime) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(file) || SpellRules.Hash(file) != pair.Value) { return false; }
        }
        return true;
    }
    internal static bool RuntimeFile(string relative) => !relative.StartsWith(".runtime" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && !relative.Equals("BuildManifest.json", StringComparison.OrdinalIgnoreCase)
        && !relative.Equals("spell_manifest.json", StringComparison.OrdinalIgnoreCase)
        && !relative.Equals("spell_overlay.bin", StringComparison.OrdinalIgnoreCase)
        && !relative.Equals("SpellWorkspace.client.ini", StringComparison.OrdinalIgnoreCase);
    internal Dictionary<string, string> Files() => Directory.EnumerateFiles(Runtime, "*", SearchOption.AllDirectories)
        .Where(file => RuntimeFile(Path.GetRelativePath(Runtime, file))).ToDictionary(file => Path.GetRelativePath(Runtime, file), SpellRules.Hash);
    internal void Complete() {
        ProjectFiles.SaveJson(Path.Combine(_root, "files.json"), Files());
        File.WriteAllText(Path.Combine(_root, "fingerprint.txt"), Fingerprint);
    }
    public void Dispose() => _lock.Dispose();
}
