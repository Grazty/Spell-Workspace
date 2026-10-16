// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpellWorkspace.Core;

internal sealed class ServerRuntimeCache : IDisposable {
    private readonly FileStream _lock;
    private readonly string _root;
    internal string Fingerprint { get; }
    internal string Runtime { get; }

    internal ServerRuntimeCache(WorkspaceSettings settings, ResourceCatalog catalog, CancellationToken token, string? extractedDefinitionsHash = null, SpellProject? project = null) {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => digest.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add(Path.GetFullPath(settings.ServerRoot)); Add(catalog.RootSha256);
        Add("ServerAdapterPipeline-v1");
        if (extractedDefinitionsHash is not null) { Add("ExtractedClientDefinitions-v1"); Add(extractedDefinitionsHash); }
        foreach (var tree in new[] { Path.Combine(settings.ServerRoot, "src"), Path.Combine(settings.ServerRoot, "submodule", "Imcodec", "src"), Path.Combine(BuildService.AssetsDirectory, "ServerAdapter") }) {
            foreach (var file in SourceFiles(tree).Order(StringComparer.Ordinal)) {
                token.ThrowIfCancellationRequested(); Add(Path.GetRelativePath(tree, file)); Add(SpellRules.Hash(file));
            }
        }
        Fingerprint = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        Runtime = project is null ? Path.Combine(settings.OutputRoot, "RuntimeCache", Fingerprint, "Server") : BuildUpdates.ProjectRuntime(settings, project, "Server", Fingerprint);
        _root = project is null ? Path.GetDirectoryName(Runtime)! : Path.Combine(Runtime, ".runtime");
        Directory.CreateDirectory(_root);
        try { _lock = new FileStream(Path.Combine(_root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new IOException("Another build is preparing this Imlight runtime. Let it finish, then press Build All again.", exception); }
    }

    internal bool IsReady(CancellationToken token) {
        if (!File.Exists(Path.Combine(_root, "fingerprint.txt")) || File.ReadAllText(Path.Combine(_root, "fingerprint.txt")) != Fingerprint) { return false; }
        var manifest = Path.Combine(_root, "files.json");
        if (!File.Exists(manifest)) { return false; }
        Dictionary<string, string>? files;
        try { files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest), ProjectFiles.JsonOptions); }
        catch (Exception exception) when (exception is JsonException or IOException) { return false; }
        if (files is null || !files.ContainsKey("Imlight.Director.exe") || !files.ContainsKey(Path.Combine("RavenDBServer", "Raven.Server.dll"))) { return false; }
        foreach (var pair in files) {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(Runtime, pair.Key));
            if (!path.StartsWith(Path.GetFullPath(Runtime) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || SpellRules.Hash(path) != pair.Value) { return false; }
        }
        return true;
    }

    internal void Complete(CancellationToken token, string? published = null) {
        var files = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(published ?? Runtime, "*", SearchOption.AllDirectories)) {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(published ?? Runtime, path);
            if (relative.StartsWith(".runtime" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.Equals(Path.Combine("Config", "Imlight.ini"), StringComparison.OrdinalIgnoreCase)) { continue; }
            files.Add(relative, SpellRules.Hash(Path.Combine(Runtime, relative)));
        }
        ProjectFiles.SaveJson(Path.Combine(_root, "files.json"), files);
        File.WriteAllText(Path.Combine(_root, "fingerprint.txt"), Fingerprint);
    }

    private static IEnumerable<string> SourceFiles(string folder) {
        foreach (var file in Directory.EnumerateFiles(folder)) { yield return file; }
        foreach (var child in Directory.EnumerateDirectories(folder)) {
            if (Path.GetFileName(child) is "bin" or "obj" or ".git" or ".idea" or "node_modules" or "RavenDB") { continue; }
            foreach (var file in SourceFiles(child)) { yield return file; }
        }
    }

    public void Dispose() => _lock.Dispose();
}
