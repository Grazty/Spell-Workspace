// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace SpellWorkspace.Core;

internal sealed class ServerSnapshot {
    public string Target { get; set; } = "";
    public string BuildId { get; set; } = "";
    public List<uint> CustomSpellIds { get; set; } = [];
    public List<SnapshotFile> Files { get; set; } = [];
}
internal sealed class SnapshotFile {
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string? PreviousHash { get; set; }
    public string AppliedHash { get; set; } = "";
}

internal static class ServerSnapshots {
    private static string Contained(string root, string relative) {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("Snapshot path escapes its server folder."); }
        return full;
    }
    private static List<string> FilesToInstall(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(folder, path))
        .Where(path => !path.StartsWith("spiraldb-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("bots" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !path.Equals(Path.Combine("Config", "Imlight.ini"), StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        .ToList();
    private static void RequireStopped(string target) {
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("Imlight.Director")) {
            using (process) {
                try { if (process.MainModule?.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) { throw new InvalidOperationException("Stop this Imlight server before installing or restoring files."); } }
                catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Cannot verify whether Imlight is running. Stop it before applying this package."); }
            }
        }
    }
    internal static string Install(string package, string target, string backups) {
        RequireStopped(target);
        if (Path.GetFullPath(package).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("The build output and install folder must be different."); }
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "BuildManifest.json")));
        var snapshot = new ServerSnapshot { Target = Path.GetFullPath(target), BuildId = manifest.RootElement.GetProperty("BuildId").GetString()! };
        var project = ProjectFiles.LoadProject(Path.Combine(package, "spell_manifest.json"));
        snapshot.CustomSpellIds = project.Spells.Where(x => x.IsNew && x.Enabled).Select(x => x.TemplateId).ToList();
        var installId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot.Target))).ToLowerInvariant()[..16];
        var baseline = Path.Combine(backups, installId, "Original");
        var folder = Path.Combine(backups, installId, "Snapshots", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        foreach (var relative in FilesToInstall(package)) {
            var source = Contained(package, relative);
            var destination = Contained(target, relative);
            var item = new SnapshotFile { Path = relative, Existed = File.Exists(destination), AppliedHash = SpellRules.Hash(source) };
            if (item.Existed) {
                item.PreviousHash = SpellRules.Hash(destination);
                var copy = Contained(Path.Combine(folder, "Files"), relative); Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(destination, copy);
                if (SpellRules.Hash(copy) != item.PreviousHash) { throw new IOException("Backup verification failed: " + relative); }
            }
            snapshot.Files.Add(item);
        }
        ProjectFiles.SaveJson(Path.Combine(folder, "snapshot.json"), snapshot);
        if (!Directory.Exists(baseline)) {
            Directory.CreateDirectory(baseline);
            foreach (var item in snapshot.Files.Where(x => x.Existed)) {
                var copy = Contained(Path.Combine(baseline, "Files"), item.Path); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(Contained(Path.Combine(folder, "Files"), item.Path), copy);
            }
            ProjectFiles.SaveJson(Path.Combine(baseline, "snapshot.json"), snapshot);
        }
        foreach (var item in snapshot.Files) {
            var destination = Contained(target, item.Path);
            if (File.Exists(destination) != item.Existed || item.Existed && SpellRules.Hash(destination) != item.PreviousHash) {
                throw new IOException("Server file changed after backup. Nothing installed: " + item.Path);
            }
        }
        try {
            foreach (var item in snapshot.Files) {
                var destination = Contained(target, item.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temp = destination + ".spellworkspace.tmp";
                File.Copy(Contained(package, item.Path), temp, true);
                if (SpellRules.Hash(temp) != item.AppliedHash) { throw new IOException("Staged file verification failed: " + item.Path); }
                File.Move(temp, destination, true);
            }
        } catch {
            RestoreFiles(snapshot, folder, false);
            throw;
        }
        return Path.Combine(folder, "snapshot.json");
    }
    internal static void Restore(string manifest, bool customReferencesResolved = false) {
        var snapshot = JsonSerializer.Deserialize<ServerSnapshot>(File.ReadAllText(manifest), ProjectFiles.JsonOptions) ?? throw new InvalidDataException("Invalid snapshot.");
        if (snapshot.CustomSpellIds.Count > 0 && !customReferencesResolved) {
            throw new InvalidOperationException("This build contains custom spells. Unlearn/remove their character and deck references before restoring; automatic database migration is not yet supported. IDs: " + string.Join(", ", snapshot.CustomSpellIds));
        }
        RequireStopped(snapshot.Target);
        RestoreFiles(snapshot, Path.GetDirectoryName(manifest)!, true);
    }
    private static void RestoreFiles(ServerSnapshot snapshot, string folder, bool checkConflicts) {
        foreach (var item in snapshot.Files) {
            var destination = Contained(snapshot.Target, item.Path);
            if (checkConflicts && File.Exists(destination) && SpellRules.Hash(destination) != item.AppliedHash && SpellRules.Hash(destination) != item.PreviousHash) {
                throw new IOException("Restore conflict: this file changed after installation. " + item.Path);
            }
            if (item.Existed) {
                var copy = Contained(Path.Combine(folder, "Files"), item.Path);
                if (!File.Exists(copy) || SpellRules.Hash(copy) != item.PreviousHash) { throw new IOException("Snapshot integrity failed: " + item.Path); }
            }
        }
        foreach (var item in snapshot.Files) {
            var destination = Contained(snapshot.Target, item.Path);
            if (item.Existed) {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Contained(Path.Combine(folder, "Files"), item.Path), destination + ".spellworkspace.restore", true);
                File.Move(destination + ".spellworkspace.restore", destination, true);
            } else if (File.Exists(destination)) { File.Delete(destination); }
        }
    }
    internal static async void InstallUI(Window owner, BuildOutput? output, WorkspaceSettings settings, Action<string> log) {
        if (output?.ServerExe is null) { MessageBox.Show(owner, "Build Imlight first.", "Install Server"); return; }
        var picker = new OpenFolderDialog { Title = "Choose the existing compiled Imlight folder to update" };
        if (picker.ShowDialog(owner) != true) { return; }
        if (!File.Exists(Path.Combine(picker.FolderName, "Imlight.Director.exe"))) { MessageBox.Show(owner, "Select a compiled server folder containing Imlight.Director.exe.", "Install Server"); return; }
        var files = FilesToInstall(output.Folder);
        if (MessageBox.Show(owner, $"Install build {output.BuildId} into:\n{picker.FolderName}\n\n{files.Count} files will be added or replaced after a verified backup. Existing Imlight.ini and character databases are preserved. Stop this server first.", "Review Server Installation", MessageBoxButton.OKCancel) != MessageBoxResult.OK) { return; }
        try { var backup = await Task.Run(() => Install(output.Folder, picker.FolderName, Path.Combine(settings.OutputRoot, "Backups"))); log("Installed server. Snapshot: " + backup); MessageBox.Show(owner, "Server installed. Backup:\n" + backup, "Spell Workspace"); }
        catch (Exception exception) { log("ERROR: " + exception.Message); MessageBox.Show(owner, exception.Message, "Server Installation Failed"); }
    }
    internal static async void RestoreUI(Window owner, WorkspaceSettings settings, Action<string> log) {
        var picker = new OpenFileDialog { Title = "Select the snapshot.json to restore", Filter = "Server snapshot|snapshot.json", InitialDirectory = Path.Combine(settings.OutputRoot, "Backups") };
        if (picker.ShowDialog(owner) != true) { return; }
        if (MessageBox.Show(owner, "Restore the selected snapshot's files? Changed files are checked for conflicts before restoration.", "Review Restore", MessageBoxButton.OKCancel) != MessageBoxResult.OK) { return; }
        try { await Task.Run(() => Restore(picker.FileName)); log("Server snapshot restored: " + picker.FileName); MessageBox.Show(owner, "Snapshot restored.", "Spell Workspace"); }
        catch (Exception exception) { log("ERROR: " + exception.Message); MessageBox.Show(owner, exception.Message, "Restore Blocked"); }
    }
}
