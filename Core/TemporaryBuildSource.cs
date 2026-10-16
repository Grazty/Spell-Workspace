// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
namespace SpellWorkspace.Core;

internal sealed class TemporaryBuildSource : IDisposable {
    private readonly Action<string> _log;
    private readonly FileStream _lease;
    internal string Path { get; }

    internal TemporaryBuildSource(Action<string> log, string prefix = "SWBuild-") {
        if (prefix is not ("SWBuild-" or "SWPlayers-")) { throw new ArgumentException("Unknown temporary folder type.", nameof(prefix)); }
        _log = log;
        Directory.CreateDirectory(PortablePaths.Temporary);
        Path = System.IO.Path.Combine(PortablePaths.Temporary, prefix + Guid.NewGuid().ToString("N"));
        _lease = new FileStream(Path + ".lease", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() {
        try { RemoveOwned(Path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _log("Temporary files are still in use and will be cleaned later: " + Path); }
        finally {
            _lease.Dispose();
            if (!Directory.Exists(Path)) { File.Delete(Path + ".lease"); }
        }
    }

    internal static void CleanAbandoned() {
        if (!Directory.Exists(PortablePaths.Temporary)) { return; }
        foreach (var folder in Directory.EnumerateDirectories(PortablePaths.Temporary)) {
            if (!OwnedName(System.IO.Path.GetFileName(folder)) || Directory.GetLastWriteTimeUtc(folder) > DateTime.UtcNow.AddDays(-1)) { continue; }
            try {
                // An active build holds this lease even if its directory is old.
                using (var lease = new FileStream(folder + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { RemoveOwned(folder); }
                if (!Directory.Exists(folder)) { File.Delete(folder + ".lease"); }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool OwnedName(string name) {
        var prefix = new[] { "SWBuild-", "SWPlayers-", "SWLauncher-" }.FirstOrDefault(name.StartsWith);
        return prefix is not null && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private static void RemoveOwned(string folder) {
        var resolved = System.IO.Path.GetFullPath(folder);
        if (!string.Equals(System.IO.Path.GetDirectoryName(resolved), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(PortablePaths.Temporary)), StringComparison.OrdinalIgnoreCase)
            || !OwnedName(System.IO.Path.GetFileName(resolved)) || !Directory.Exists(resolved) || !PlainTree(resolved)) { return; }
        Directory.Delete(resolved, true);
    }

    internal static bool PlainTree(string folder) {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { return false; }
        foreach (var item in Directory.EnumerateFileSystemEntries(folder)) {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0 && !PlainTree(item))) { return false; }
        }
        return true;
    }
}
