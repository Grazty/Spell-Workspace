// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Net;
using System.Text.Json;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;

namespace SpellWorkspace.Core;

internal sealed record PlayerCopy(string ProjectId, string DataDirectory, string Database, string OriginalBackup, string BackupSha256, long Documents, bool StartedFresh = false);
internal sealed record PlayerBackup(string ProjectId, string File, string Sha256, string Reason, DateTime CreatedUtc, long Documents);

internal static class PlayerCopies {
    internal const string CustomUrl = "http://127.0.0.1:12180";
    internal static bool NeedsSetup(WorkspaceSettings settings, SpellProject project) => !File.Exists(Path.Combine(ProjectFolder(settings, project), "player-copy.json"))
        && settings.PlayerDataMode != "Fresh" && (string.IsNullOrWhiteSpace(settings.PlayerDataSource) || !Directory.Exists(Path.Combine(settings.PlayerDataSource, "Databases")));
    internal static void ConfigureWizardSource(WorkspaceSettings settings) {
        if (settings.PlayerDataMode == "Fresh") { return; }
        if (settings.PlayerSourceSchema >= 1 && !string.IsNullOrWhiteSpace(settings.PlayerDataSource)) { return; }
        var profilePath = Path.Combine(settings.ServerRoot, "SpellWorkspace-launch.json");
        if (!File.Exists(profilePath)) { return; }
        using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
        var folder = profile.RootElement.GetProperty("Folder").GetString()!;
        var manifestPath = Path.Combine(folder, "BuildManifest.json");
        var copyPath = Path.Combine(folder, "PlayerCopy.json");
        if (!File.Exists(manifestPath) || !File.Exists(copyPath)) { return; }
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!Path.GetFullPath(manifest.RootElement.GetProperty("ServerRoot").GetString()!).Equals(Path.GetFullPath(settings.ServerRoot), StringComparison.OrdinalIgnoreCase)) { return; }
        var copy = ReadRecord(copyPath);
        if (copy is null || !Directory.Exists(Path.Combine(copy.DataDirectory, "Databases"))) { return; }
        settings.PlayerDataSource = copy.DataDirectory;
        settings.PlayerDatabaseName = copy.Database;
        settings.PlayerDatabaseUrl = CustomUrl;
        settings.PlayerRavenServerDirectory = "";
        settings.PlayerSourceSchema = 1;
    }
    internal static string ProjectFolder(WorkspaceSettings settings, SpellProject project) {
        if (!Guid.TryParseExact(project.Id, "N", out _)) { throw new InvalidDataException("The project identifier is invalid."); }
        return Path.GetFullPath(Path.Combine(settings.OutputRoot, "PlayerData", project.Id));
    }
    internal static PlayerCopy Read(WorkspaceSettings settings, SpellProject project) {
        var folder = ProjectFolder(settings, project);
        var copy = ReadRecord(Path.Combine(folder, "player-copy.json"))
            ?? throw new InvalidDataException("Missing player copy record.");
        if (copy.ProjectId != project.Id || !Path.GetFullPath(copy.DataDirectory).Equals(Path.Combine(folder, "Database"), StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.Combine(copy.DataDirectory, "Databases"))
            || !File.Exists(copy.OriginalBackup) || SpellRules.Hash(copy.OriginalBackup) != copy.BackupSha256) {
            throw new InvalidDataException("The original player backup or project data directory failed verification.");
        }
        return copy;
    }
    internal static PlayerCopy? ReadRecord(string file) {
        var copy = JsonSerializer.Deserialize<PlayerCopy>(File.ReadAllText(file), ProjectFiles.JsonOptions);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return copy is null ? null : copy with { DataDirectory = Path.GetFullPath(copy.DataDirectory, folder), OriginalBackup = Path.GetFullPath(copy.OriginalBackup, folder) };
    }
    internal static async Task<PlayerCopy> Ensure(WorkspaceSettings settings, SpellProject project, string engine, Action<string> log, CancellationToken token) {
        var folder = ProjectFolder(settings, project);
        if (File.Exists(Path.Combine(folder, "player-copy.json"))) {
            var existing = Read(settings, project);
            await Backup(settings, project, engine, "before-build", log, token);
            return existing;
        }
        ConfigureWizardSource(settings);
        var fresh = settings.PlayerDataMode == "Fresh";
        if (!fresh && NeedsSetup(settings, project)) {
            throw new InvalidOperationException("Choose Create Fresh Database or Copy Existing Wizards in Settings. Build All includes RavenDB and remembers your choice.");
        }
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "Backups", "Original.ravendbdump");
        if (!File.Exists(original)) {
            if (fresh) {
                log("Creating a fresh player database. Create your first wizard after launching the server and client.");
                using var seed = new TemporaryBuildSource(log);
                using var server = StartEngine(engine, Path.Combine(seed.Path, "FreshPlayers"));
                using var store = await server.GetDocumentStoreAsync(new DatabaseOptions(settings.PlayerDatabaseName) { Conventions = new() { DisableTopologyCache = true } }, token);
                await Export(store, original, token, allowEmpty: true);
            } else {
                log("Saving the original player data before creating the custom wizard copy...");
                await ExportSource(settings.PlayerDataSource, engine, settings.PlayerDatabaseUrl, settings.PlayerDatabaseName, original, token);
            }
        }
        var documents = await ImportNew(original, Path.Combine(folder, "Database"), settings.PlayerDatabaseName, engine, token, allowEmpty: fresh);
        var copy = new PlayerCopy(project.Id, Path.Combine(folder, "Database"), settings.PlayerDatabaseName, original, SpellRules.Hash(original), documents, fresh);
        ProjectFiles.SaveJson(Path.Combine(folder, "player-copy.json"), copy with { DataDirectory = "Database", OriginalBackup = Path.GetRelativePath(folder, original) });
        ProjectFiles.SaveJson(original + ".json", new PlayerBackup(project.Id, original, copy.BackupSha256, fresh ? "fresh-database-before-first-login" : "original-before-custom-spells", DateTime.UtcNow, documents));
        log(fresh ? "Fresh database verified and backed up. Ready for new characters; future builds keep them." : $"Separate player copy verified: {documents:N0} documents. Existing spellbooks are preserved; no new spells were granted.");
        return copy;
    }
    internal static async Task<PlayerBackup> Backup(WorkspaceSettings settings, SpellProject project, string engine, string reason, Action<string> log, CancellationToken token) {
        var copy = Read(settings, project);
        if (await IsOnline(CustomUrl, token)) {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var status = await http.GetStreamAsync("http://127.0.0.1:12181/status", token);
            using var document = await JsonDocument.ParseAsync(status, cancellationToken: token);
            if (document.RootElement.GetProperty("ProjectId").GetString() != project.Id || !document.RootElement.GetProperty("Ready").GetBoolean()) {
                throw new InvalidOperationException("A different project is running, or Imlight is still starting. This project's wizard backup was not mixed with another database.");
            }
        }
        var path = Path.Combine(ProjectFolder(settings, project), "Backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + reason + ".ravendbdump");
        var documents = await ExportSource(copy.DataDirectory, engine, CustomUrl, copy.Database, path, token, allowEmpty: copy.StartedFresh);
        var backup = new PlayerBackup(project.Id, path, SpellRules.Hash(path), reason, DateTime.UtcNow, documents);
        ProjectFiles.SaveJson(path + ".json", backup);
        log("Player data backed up before changes: " + path);
        return backup;
    }
    internal static async Task RestoreOriginal(WorkspaceSettings settings, SpellProject project, string engine, Action<string> log, CancellationToken token) {
        if (await IsOnline(CustomUrl, token)) { throw new InvalidOperationException("Stop the custom Imlight server before restoring its wizard database."); }
        var copy = Read(settings, project);
        await Backup(settings, project, engine, "before-restore", log, token);
        var folder = ProjectFolder(settings, project);
        var staged = Path.Combine(folder, "Restore-" + Guid.NewGuid().ToString("N"));
        var retired = Path.Combine(folder, "Retired-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var documents = await ImportNew(copy.OriginalBackup, staged, copy.Database, engine, token, allowEmpty: copy.StartedFresh);
        if (documents != copy.Documents) { throw new InvalidDataException("Restore document count differs from the original backup. The current database was preserved."); }
        var probe = Path.Combine(folder, "OfflineCheck-" + Guid.NewGuid().ToString("N"));
        ColdCopy(copy.DataDirectory, probe, token);
        Directory.Delete(probe, true);
        Directory.Move(copy.DataDirectory, retired);
        try { Directory.Move(staged, copy.DataDirectory); }
        catch { Directory.Move(retired, copy.DataDirectory); throw; }
        log("Original wizard copy restored. Previous custom progress is retained in " + retired);
    }
    private static async Task<long> ExportSource(string data, string engine, string url, string database, string path, CancellationToken token, bool allowEmpty = false) {
        if (await IsOnline(url, token)) {
            if (url.TrimEnd('/').Equals(CustomUrl, StringComparison.OrdinalIgnoreCase)) {
                var record = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(data))!, "player-copy.json");
                var sourceCopy = File.Exists(record) ? ReadRecord(record) : null;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var status = await http.GetStreamAsync("http://127.0.0.1:12181/status", token);
                using var document = await JsonDocument.ParseAsync(status, cancellationToken: token);
                if (sourceCopy is null || !Path.GetFullPath(sourceCopy.DataDirectory).Equals(Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase)
                    || sourceCopy.Database != database || document.RootElement.GetProperty("ProjectId").GetString() != sourceCopy.ProjectId
                    || !document.RootElement.GetProperty("Ready").GetBoolean()) {
                    throw new InvalidOperationException("The running clean server uses a different wizard folder. Stop it before copying the selected wizard source, or choose its wizard folder.");
                }
            }
            using var live = new DocumentStore { Urls = [url], Database = database, Conventions = { DisableTopologyCache = true } };
            live.Initialize();
            return await Export(live, path, token, allowEmpty);
        }
        using var temporaryPlayers = new TemporaryBuildSource(_ => { }, "SWPlayers-");
        var cold = temporaryPlayers.Path;
        {
            ColdCopy(data, cold, token);
            using var server = StartEngine(engine, cold);
            var uri = await server.GetServerUriAsync(token);
            using var store = new DocumentStore { Urls = [uri.ToString()], Database = database, Conventions = { DisableTopologyCache = true } };
            store.Initialize();
            var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(database), token);
            if (record is null) { throw new InvalidDataException("The selected player folder does not contain database " + database + "."); }
            return await Export(store, path, token, allowEmpty);
        }
    }
    private static async Task<long> Export(IDocumentStore store, string path, CancellationToken token, bool allowEmpty = false) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pending";
        var statistics = await store.Maintenance.SendAsync(new GetStatisticsOperation(), token);
        if (statistics.CountOfDocuments == 0 && !allowEmpty) { throw new InvalidDataException("The selected database contains no player documents. Choose Create Fresh Database to start with new wizards."); }
        var operation = await store.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), temporary, token);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5)).WaitAsync(token);
        if (new FileInfo(temporary).Length == 0) { throw new InvalidDataException("Player export is empty."); }
        File.Move(temporary, path, false);
        return statistics.CountOfDocuments;
    }
    private static async Task<long> ImportNew(string dump, string data, string database, string engine, CancellationToken token, bool allowEmpty = false) {
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()) {
            throw new InvalidOperationException("An unverified player database already exists here. Use a new project or review the interrupted import; it will not be overwritten.");
        }
        Directory.CreateDirectory(data);
        using var server = StartEngine(engine, data);
        using var store = await server.GetDocumentStoreAsync(new DatabaseOptions(database) { Conventions = new() { DisableTopologyCache = true } }, token);
        var operation = await store.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), dump, token);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5)).WaitAsync(token);
        var statistics = await store.Maintenance.SendAsync(new GetStatisticsOperation(), token);
        if (statistics.CountOfDocuments == 0 && !allowEmpty) { throw new InvalidDataException("No player data was imported. Choose Create Fresh Database to start with new wizards."); }
        return statistics.CountOfDocuments;
    }
    internal static EmbeddedServer StartEngine(string engine, string data) {
        var runtime = Path.Combine(engine, "Raven.Server.runtimeconfig.json");
        if (!File.Exists(runtime)) { throw new FileNotFoundException("The clean Imlight build is missing its bundled RavenDB engine. Build All must finish publishing Imlight before copying wizard data."); }
        using var document = JsonDocument.Parse(File.ReadAllText(runtime));
        var version = document.RootElement.GetProperty("runtimeOptions").GetProperty("frameworks")[0].GetProperty("version").GetString()!;
        var server = EmbeddedServer.Instance;
        try {
            server.StartServer(new ServerOptions { ServerDirectory = engine, DataDirectory = data, LogsPath = Path.Combine(PortablePaths.Logs, "RavenDB"), ServerUrl = "http://127.0.0.1:0",
                FrameworkVersion = version[..version.LastIndexOf('.')] + ".x", MaxServerStartupTimeDuration = TimeSpan.FromSeconds(90),
                CommandLineArgs = ["--Databases.MaxIdleTimeInSec=-1"] });
            return server;
        } catch { server.Dispose(); throw; }
    }
    private static async Task<bool> IsOnline(string url, CancellationToken token) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)) {
            throw new InvalidDataException("Player database exports must use a local loopback URL.");
        }
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(new Uri(uri, "/build/version"), token);
            return response.IsSuccessStatusCode;
        } catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
    internal static void ColdCopy(string source, string destination, CancellationToken token) {
        var root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(root) || !Directory.Exists(Path.Combine(root, "Databases")) || target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("Select an existing RavenDB data folder containing Databases. The destination must be separate.");
        }
        var handles = new List<(string Path, FileStream Stream)>();
        try {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("Player data contains a linked file. Select a regular data folder."); }
                try { handles.Add((file, new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))); }
                catch (IOException exception) { throw new IOException("The player database is in use. Start its RavenDB endpoint for a live export, or stop the server before making the first copy.", exception); }
            }
            foreach (var item in handles) {
                token.ThrowIfCancellationRequested();
                var output = Path.Combine(target, Path.GetRelativePath(root, item.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var copy = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                item.Stream.CopyTo(copy);
            }
        } finally { foreach (var item in handles) { item.Stream.Dispose(); } }
    }
}
