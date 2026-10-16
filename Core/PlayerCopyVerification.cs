// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using Raven.Client.Documents.Operations;
using Raven.Embedded;

namespace SpellWorkspace.Core;

internal static class PlayerCopyVerification {
    internal static async Task Run(string engine, string report) {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) { throw new InvalidDataException("Verification failed: " + name); } checks.Add(name); }
        using var scratch = new TemporaryBuildSource(Console.WriteLine);
        var project = new SpellProject { Name = "Fresh wizard database verification" };
        var settings = new WorkspaceSettings { OutputRoot = Path.Combine(scratch.Path, "Workspace"), PlayerDatabaseUrl = "http://127.0.0.1:1" };
        Check(PlayerCopies.NeedsSetup(settings, project), "A new user with no player database is offered setup");
        settings.PlayerDataSource = Path.Combine(scratch.Path, "MissingSource"); settings.PlayerDataMode = "Import";
        Check(PlayerCopies.NeedsSetup(settings, project), "A missing remembered import folder offers setup again");
        settings.PlayerDataMode = "Fresh";
        Check(!PlayerCopies.NeedsSetup(settings, project), "Fresh database mode does not require any source database");
        var portable = ProjectFiles.PortableSettings(new WorkspaceSettings { ClientExe = Path.Combine(PortablePaths.Root, "Client", "game.exe"), ServerRoot = Path.Combine(PortablePaths.Root, "Imlight"), PlayerDataMode = "Fresh" });
        Check(portable.OutputRoot == "Workspace" && portable.ClientExe == Path.Combine("Client", "game.exe") && portable.ServerRoot == "Imlight", "Paths inside the portable app are saved relative to its EXE");
        var first = await PlayerCopies.Ensure(settings, project, engine, Console.WriteLine, CancellationToken.None);
        Check(first.StartedFresh && first.Documents == 0 && File.Exists(first.OriginalBackup), "Fresh creation makes a real empty RavenDB database and verified original backup");
        var second = await PlayerCopies.Ensure(settings, project, engine, Console.WriteLine, CancellationToken.None);
        Check(second.DataDirectory == first.DataDirectory && second.BackupSha256 == first.BackupSha256, "Rebuilding before the first character preserves the same empty database and original backup");
        using (var server = PlayerCopies.StartEngine(engine, first.DataDirectory)) {
            using var store = await server.GetDocumentStoreAsync(new DatabaseOptions(first.Database) { Conventions = new() { DisableTopologyCache = true } });
            using var session = store.OpenAsyncSession();
            await session.StoreAsync(new { Marker = "Test wizard progress" }, "Verification/PortableWizard"); await session.SaveChangesAsync();
        }
        await PlayerCopies.Ensure(settings, project, engine, Console.WriteLine, CancellationToken.None);
        using (var server = PlayerCopies.StartEngine(engine, first.DataDirectory)) {
            using var store = await server.GetDocumentStoreAsync(new DatabaseOptions(first.Database) { Conventions = new() { DisableTopologyCache = true } });
            Check((await store.Maintenance.SendAsync(new GetStatisticsOperation())).CountOfDocuments == 1, "A later build preserves data written after fresh creation");
        }
        var importedProject = new SpellProject { Name = "Import verification" };
        var importedSettings = new WorkspaceSettings { OutputRoot = settings.OutputRoot, PlayerDataMode = "Import", PlayerDataSource = first.DataDirectory, PlayerDatabaseUrl = "http://127.0.0.1:1" };
        var imported = await PlayerCopies.Ensure(importedSettings, importedProject, engine, Console.WriteLine, CancellationToken.None);
        Check(imported.Documents == 1 && !imported.StartedFresh && imported.DataDirectory != first.DataDirectory, "Existing player data is copied into a separate project database");
        await PlayerCopies.RestoreOriginal(settings, project, engine, Console.WriteLine, CancellationToken.None);
        using (var server = PlayerCopies.StartEngine(engine, first.DataDirectory)) {
            using var store = await server.GetDocumentStoreAsync(new DatabaseOptions(first.Database) { Conventions = new() { DisableTopologyCache = true } });
            Check((await store.Maintenance.SendAsync(new GetStatisticsOperation())).CountOfDocuments == 0, "A fresh project's original empty backup can be restored");
        }
        var movedSettings = new WorkspaceSettings { OutputRoot = Path.Combine(scratch.Path, "MovedWorkspace"), PlayerDataMode = "Fresh" };
        var movedFolder = PlayerCopies.ProjectFolder(movedSettings, project); Directory.CreateDirectory(Path.GetDirectoryName(movedFolder)!);
        Directory.Move(PlayerCopies.ProjectFolder(settings, project), movedFolder);
        var moved = PlayerCopies.Read(movedSettings, project);
        Check(moved.DataDirectory == Path.Combine(movedFolder, "Database") && File.Exists(moved.OriginalBackup), "Relative database and backup records still resolve after moving a portable workspace");
        await PlayerCopies.Ensure(movedSettings, project, engine, Console.WriteLine, CancellationToken.None);
        Check(!PlayerCopies.NeedsSetup(movedSettings, project), "A moved portable project reuses its database without asking for a source again");
        ProjectFiles.SaveJson(report, new { Passed = true, Checks = checks, ExistingUserDatabasesTouched = false });
    }
}
