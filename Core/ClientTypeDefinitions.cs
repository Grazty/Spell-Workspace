// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Diagnostics;
using System.Text.Json;

namespace SpellWorkspace.Core;

internal sealed record ExtractedTypeDefinitions(string File, string Sha256, string Revision, int ClassCount) {
    internal void Stage(string stagedSource, Action<string> log, CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();
        if (!System.IO.File.Exists(File) || SpellRules.Hash(File) != Sha256) { throw new InvalidDataException("The extracted type definitions changed. Run Build All again to extract a verified copy."); }
        var folder = ClientTypeDefinitions.InputFolder(stagedSource);
        Directory.CreateDirectory(folder);
        // This is the build's private temporary source, never the user's selected checkout.
        foreach (var existing in Directory.EnumerateFiles(folder, "*.json")) { System.IO.File.Delete(existing); }
        System.IO.File.Copy(File, Path.Combine(folder, "ClientDump.json"), false);
        log("Prepared " + ClassCount.ToString("N0") + " type definitions extracted from " + Revision + " for Imlight.");
    }
}

internal static class ClientTypeDefinitions {
    internal const int ExtractorVersion = 2;
    private sealed record Manifest(int ExtractorVersion, string ClientSha256, string DumpSha256, string Revision, int ClassCount);
    internal static string InputFolder(string sourceRoot) => Path.Combine(sourceRoot, "submodule", "Imcodec", "src", "Imcodec.ObjectProperty", "GeneratorInput");
    internal static bool HasDefinitions(string sourceRoot) => Directory.Exists(InputFolder(sourceRoot)) && Directory.EnumerateFiles(InputFolder(sourceRoot), "*.json").Any();
    internal static string CacheFolder(WorkspaceSettings settings, string hash) => Path.Combine(settings.OutputRoot, "RuntimeCache", "TypeDefinitions", hash);

    internal static ExtractedTypeDefinitions? ReadCache(string folder, string clientHash) {
        try {
            var path = Path.Combine(folder, "ClientDump.json");
            var manifest = JsonSerializer.Deserialize<Manifest>(System.IO.File.ReadAllText(Path.Combine(folder, "manifest.json")), ProjectFiles.JsonOptions);
            if (manifest is null || manifest.ExtractorVersion != ExtractorVersion || manifest.ClientSha256 != clientHash || manifest.ClassCount < 1000
                || !System.IO.File.Exists(path) || SpellRules.Hash(path) != manifest.DumpSha256) { return null; }
            return new(path, manifest.DumpSha256, manifest.Revision, manifest.ClassCount);
        } catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<ExtractedTypeDefinitions> Ensure(WorkspaceSettings settings, Action<string> log, Action<string>? progress, CancellationToken cancellation) {
        cancellation.ThrowIfCancellationRequested();
        var exe = Path.GetFullPath(settings.ClientExe);
        var hash = SpellRules.Hash(exe);
        var folder = CacheFolder(settings, hash);
        Directory.CreateDirectory(folder);
        using var cacheLock = new FileStream(Path.Combine(folder, "extract.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ReadCache(folder, hash) is { } cached) {
            log("Reusing verified type definitions previously extracted from this exact client executable.");
            return cached;
        }
        var candidates = ClientTypeDumpReader.RegistryGlobals(await System.IO.File.ReadAllBytesAsync(exe, cancellation).ConfigureAwait(false));
        var revisionFile = Path.Combine(Path.GetDirectoryName(exe)!, "revision.dat");
        var revision = System.IO.File.Exists(revisionFile) ? (await System.IO.File.ReadAllTextAsync(revisionFile, cancellation).ConfigureAwait(false)).Trim() : Path.GetFileName(exe);
        progress?.Invoke("Extracting types from your client, please wait... (1 of 3)");
        log("Extracting actual type metadata from the selected client. A temporary client will connect only to localhost port 1, with patching disabled; no login or server is required.");
        var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-L", "127.0.0.1", "1", "-P", "0" }) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The selected client could not start for type extraction.");
        SortedDictionary<string, object?>? dump = null;
        try {
            await Task.Delay(2000, cancellation).ConfigureAwait(false);
            var timer = Stopwatch.StartNew(); Exception? last = null; var previousCount = 0;
            while (timer.Elapsed < TimeSpan.FromSeconds(45)) {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) { throw new InvalidOperationException("The temporary client exited before extraction finished. Confirm that the original client starts normally from its installation.", last); }
                try {
                    using var reader = new ClientTypeDumpReader(process);
                    dump = await Task.Run(() => reader.Dump(candidates, cancellation), cancellation).ConfigureAwait(false);
                    var count = ((SortedDictionary<string, object?>)dump["classes"]!).Count;
                    if (count == previousCount) { break; }
                    previousCount = count; dump = null;
                } catch (InvalidDataException exception) { last = exception; }
                await Task.Delay(500, cancellation).ConfigureAwait(false);
            }
            if (dump is null) { throw new InvalidDataException("This client's type registry could not be extracted completely. No bundled or unrelated type dump was used.", last); }
        } finally {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        cancellation.ThrowIfCancellationRequested();
        if (SpellRules.Hash(exe) != hash) { throw new InvalidDataException("The selected executable changed during type extraction. Try again after the client update finishes."); }
        var path = Path.Combine(folder, "ClientDump.json");
        var temporary = path + ".tmp";
        await System.IO.File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(dump), cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        System.IO.File.Move(temporary, path, true);
        var dumpHash = SpellRules.Hash(path); var classCount = ((SortedDictionary<string, object?>)dump["classes"]!).Count;
        ProjectFiles.SaveJson(Path.Combine(folder, "manifest.json"), new Manifest(ExtractorVersion, hash, dumpHash, revision, classCount));
        log("Extracted " + classCount.ToString("N0") + " classes from " + revision + ". Cached by executable fingerprint for future builds.");
        return new(path, dumpHash, revision, classCount);
    }
}
