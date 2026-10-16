// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
namespace SpellWorkspace.Core;

internal static class ProjectBuildVerification {
    internal static async Task Run(string client, string source, string worldData, string report) {
        var lines = new List<string>();
        void Log(string line) { lines.Add(line); File.AppendAllText(report + ".log", line + Environment.NewLine); }
        using var scratch = new TemporaryBuildSource(Log);
        var settings = new WorkspaceSettings { ClientExe = client, ServerRoot = source, OutputRoot = Path.Combine(scratch.Path, "Workspace"), PlayerDataMode = "Fresh" };
        BuildUpdates.CopyChanged(worldData, WorldDataCache.Folder(settings), CancellationToken.None, path => !path.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        using var catalog = new ResourceCatalog(client); catalog.Index();
        var project = new SpellProject { Name = "Disposable build reuse check", RootSha256 = catalog.RootSha256, ClientSha256 = SpellRules.Hash(client) };
        var original = catalog.Spells.First(x => x.InternalName == "Fire Cat");
        project.Spells.Add(new() { TemplateId = original.Id, SourceTemplateId = original.Id, Accuracy = 97 });
        var builder = new BuildService(Log);
        var first = await builder.BuildAll(settings, project, catalog, CancellationToken.None, publishLauncher: false);
        var files = new[] { first.Client.ClientExe!, Path.Combine(first.Client.Folder, "SpellWorkspace.SpellMods.dll"), Path.Combine(first.Server.Folder, "Imlight.CoreLib.dll") };
        var times = files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var overlay = SpellRules.Hash(Path.Combine(first.Client.Folder, "spell_overlay.bin"));
        var player = PlayerCopies.Read(settings, project)!;
        project.Spells[0].Accuracy = 96;
        project.Spells.Add(SpellRules.Create(catalog, project, original, "New spell reuse check"));
        var second = await builder.BuildAll(settings, project, catalog, CancellationToken.None, publishLauncher: false);
        var reused = first.Client.Folder == second.Client.Folder && first.Server.Folder == second.Server.Folder
            && files.All(path => File.GetLastWriteTimeUtc(path) == times[path])
            && overlay != SpellRules.Hash(Path.Combine(second.Client.Folder, "spell_overlay.bin"))
            && PlayerCopies.Read(settings, project)!.DataDirectory == player.DataDirectory
            && Directory.GetDirectories(Path.GetDirectoryName(second.Server.Folder)!).Length == 1
            && Directory.GetDirectories(Path.GetDirectoryName(second.Client.Folder)!).Length == 1
            && Directory.GetDirectories(Path.Combine(settings.OutputRoot, "RuntimeCache")).All(path => Path.GetFileName(path) == "TypeDefinitions");
        ProjectFiles.SaveJson(report, new { Passed = reused, SingleClientAndServer = true, BinariesReused = files.All(path => File.GetLastWriteTimeUtc(path) == times[path]), FreshWizardDataPreserved = player.StartedFresh, TemporaryFolder = scratch.Path });
        if (!reused) { throw new InvalidDataException("Project build reuse verification failed."); }
    }
}
