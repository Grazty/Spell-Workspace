// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpellWorkspace.Core;

internal sealed class BuildService(Action<string> log, Action<string>? progress = null) {
    internal static string ContentHash(SpellProject project) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project, ProjectFiles.JsonOptions))).ToLowerInvariant();
    internal static string AssetsDirectory => AppContext.BaseDirectory;

    internal async Task<BuildPair> BuildAll(WorkspaceSettings settings, SpellProject project, ResourceCatalog catalog, CancellationToken cancellation, bool publishLauncher = true) {
        var snapshot = JsonSerializer.Deserialize<SpellProject>(JsonSerializer.Serialize(project, ProjectFiles.JsonOptions), ProjectFiles.JsonOptions)!;
        var configuration = JsonSerializer.Deserialize<WorkspaceSettings>(JsonSerializer.Serialize(settings, ProjectFiles.JsonOptions), ProjectFiles.JsonOptions)!;
        Directory.CreateDirectory(configuration.OutputRoot);
        using var buildLock = new FileStream(Path.Combine(configuration.OutputRoot, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var updates = new BuildUpdates();
        var buildId = ContentHash(snapshot)[..12] + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..4];
        log("Step 1 of 3: build Imlight and back up the project's wizard data.");
        progress?.Invoke("Building server, please wait... (1 of 3)");
        var server = await BuildServer(configuration, snapshot, catalog, cancellation, buildId, false, updates);
        cancellation.ThrowIfCancellationRequested();
        log("Step 2 of 3: check the reusable DLL and client. Step 3 of 3: update matching spell files.");
        var client = await BuildClient(configuration, snapshot, catalog, cancellation, buildId, updates);
        if (client.ManifestHash != server.ManifestHash || client.BuildId != server.BuildId) { throw new InvalidDataException("The client and Imlight build did not match. The previous ready build was preserved."); }
        cancellation.ThrowIfCancellationRequested();
        var pair = new BuildPair(client, server);
        if (publishLauncher) { WriteSourceLauncher(Path.GetFullPath(configuration.ServerRoot), server.Folder, server.ServerExe!, catalog.GameData, snapshot.Id, updates); }
        updates.Json(Path.Combine(configuration.OutputRoot, "last-builds.json"), pair);
        updates.Publish(cancellation);
        BuildUpdates.RemovePreviousBuilds(configuration, snapshot, log);
        log("Complete: updated the reusable server and client together for build " + buildId + ". Restart the server and client to load the changes.");
        return pair;
    }

    internal async Task<BuildOutput> BuildClient(WorkspaceSettings settings, SpellProject project, ResourceCatalog catalog, CancellationToken cancellation, string? pairedBuildId = null, BuildUpdates? stagedUpdates = null) {
        if (!catalog.ClientExe.Equals(Path.GetFullPath(settings.ClientExe), StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("The client path changed after indexing. Choose the client with Browse to reload its library before building."); }
        var initialHash = SpellRules.Hash(settings.ClientExe);
        _ = ClientTypeDumpReader.RegistryGlobals(await File.ReadAllBytesAsync(settings.ClientExe, cancellation));
        var errors = SpellRules.Validate(catalog, project);
        if (errors.Count > 0) { throw new InvalidDataException(string.Join("\n", errors)); }
        CustomIds.Claim(project);
        var hash = ContentHash(project);
        var buildId = pairedBuildId ?? hash[..12] + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var updates = stagedUpdates ?? new BuildUpdates();
        using var runtime = new ClientRuntimeCache(settings, catalog, cancellation, project);
        var folder = BuildUpdates.ProjectRuntime(settings, project, "Client", runtime.Fingerprint);
        Directory.CreateDirectory(folder);
        var nativeRoot = Path.Combine(AssetsDirectory, "Native");
        var fileName = Path.GetFileName(settings.ClientExe).Equals("WizardGraphicalClient-Revive.exe", StringComparison.OrdinalIgnoreCase)
            ? "WizardGraphicalClient-Revive.exe" : "WizardGraphicalClient.Custom.exe";
        var outputExe = Path.Combine(folder, fileName);
        if (runtime.IsReady(cancellation)) {
            log("Reusing the existing modded client and DLL. No client compilation is needed for spell edits or new spells.");
            progress?.Invoke("Reusing your client and DLL, please wait... (2 of 3)");
        } else {
            log("Building the modded client and DLL once for this client and mod version...");
            progress?.Invoke("Building DLL, please wait... (2 of 3)");
            using var nativeLock = new FileStream(Path.Combine(nativeRoot, "compile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await Run("cmd.exe", ["/c", Path.Combine(nativeRoot, "build.cmd")], nativeRoot, cancellation);
            Directory.CreateDirectory(runtime.Runtime);
            progress?.Invoke("Preparing your reusable client, please wait... (3 of 3)");
            await File.WriteAllBytesAsync(Path.Combine(runtime.Runtime, fileName), PePackage.BuildClient(await File.ReadAllBytesAsync(settings.ClientExe, cancellation)), cancellation);
            foreach (var name in new[] { "SpellWorkspace.SpellMods.dll", "SpellWorkspace.Client.exe" }) {
                File.Copy(Path.Combine(nativeRoot, "build", name), Path.Combine(runtime.Runtime, name), true);
            }
            foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(settings.ClientExe)!, "*.dll")) {
                File.Copy(dll, Path.Combine(runtime.Runtime, Path.GetFileName(dll)), true);
            }
            AudioProviders.Export(catalog, runtime.Runtime);
            runtime.Complete();
        }
        BuildUpdates.CopyChanged(runtime.Runtime, folder, cancellation);
        var audioProviders = Path.Combine(folder, "Audio", "Miles");
        progress?.Invoke("Updating spell files, please wait... (3 of 3)");
        var entries = SpellRules.Generate(catalog, project);
        updates.Bytes(Path.Combine(folder, "spell_overlay.bin"), SpellRules.Bundle(entries));
        updates.Json(Path.Combine(folder, "spell_manifest.json"), project);
        var rootPath = Path.Combine(catalog.GameData, "Root.wad");
        var rootHash = SpellRules.Hash(rootPath);
        var ini = $"[Workspace]\r\nClientName={fileName}\r\nClientSha256={SpellRules.Hash(outputExe)}\r\nRootPath={rootPath}\r\nRootSha256={rootHash}\r\nSourceBin={Path.GetDirectoryName(settings.ClientExe)}\r\nAudioProviderDirectory={audioProviders}\r\nManifestHash={hash}\r\nServerBuildId={pairedBuildId}\r\nRequireMatchingServer={(pairedBuildId is not null || project.Spells.Any(x => x.Enabled) ? 1 : 0)}\r\n";
        updates.Text(Path.Combine(folder, "SpellWorkspace.client.ini"), ini, new UnicodeEncoding(false, true));
        if (initialHash != SpellRules.Hash(settings.ClientExe)) { throw new InvalidDataException("Original client changed during the build. Package was not validated."); }
        updates.Json(Path.Combine(folder, "BuildManifest.json"), new {
            Product = "Spell Workspace", Version = "Beta v0.5", SchemaVersion = 1, BuildId = buildId, ManifestHash = hash,
            OriginalClient = settings.ClientExe, OriginalClientSha256 = initialHash, RootSha256 = rootHash,
            LocalhostArguments = "-L 127.0.0.1 12000", ResourceWrites = false,
            ResourceCount = entries.Count, OriginalSectionsUnchanged = true,
            RuntimeFingerprint = runtime.Fingerprint,
            RuntimeVerified = false,
            RuntimeFiles = runtime.Files()
        });
        if (stagedUpdates is null) { updates.Publish(cancellation); }
        log($"Reusable client ready: {folder}. Spell files update here on future builds.");
        return new(folder, buildId, hash, outputExe, Path.Combine(folder, "SpellWorkspace.Client.exe"), null);
    }

    internal async Task<BuildOutput> BuildServer(WorkspaceSettings settings, SpellProject project, ResourceCatalog catalog, CancellationToken cancellation, string? pairedBuildId = null, bool publishLauncher = true, BuildUpdates? stagedUpdates = null, bool compileOnly = false) {
        if (!catalog.ClientExe.Equals(Path.GetFullPath(settings.ClientExe), StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Reload the selected client with Browse before building Imlight."); }
        var errors = SpellRules.Validate(catalog, project);
        if (errors.Count > 0) { throw new InvalidDataException(string.Join("\n", errors)); }
        CustomIds.Claim(project);
        var director = Path.Combine(settings.ServerRoot, "src", "Imlight.Director", "Imlight.Director.csproj");
        var loaderPath = Path.Combine(settings.ServerRoot, "src", "Imlight.CoreLib", "Shared", "Resources", "RootArchiveLoader.cs");
        if (!File.Exists(director) || !File.Exists(loaderPath)) { throw new InvalidDataException("Select an Imlight source folder containing src/Imlight.Director and RootArchiveLoader.cs."); }
        var originalLoader = File.ReadAllText(loaderPath);
        if (!originalLoader.Contains("s_rootWad = ResourceWad();", StringComparison.Ordinal)) { throw new InvalidDataException("This Imlight revision has an unsupported resource-loading contract. It was not changed."); }
        var hash = ContentHash(project);
        var buildId = pairedBuildId ?? hash[..12] + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        using var temporarySource = new TemporaryBuildSource(log);
        var staging = temporarySource.Path;
        log("Temporary compiler folder: " + staging);
        var sourceRoot = Path.GetFullPath(settings.ServerRoot);
        var updates = stagedUpdates ?? new BuildUpdates();
        var definitions = await ClientTypeDefinitions.Ensure(settings, log, progress, cancellation);
        using var runtimeCache = new ServerRuntimeCache(settings, catalog, cancellation, definitions.Sha256, compileOnly ? null : project);
        var folder = BuildUpdates.ProjectRuntime(settings, project, "Server", runtimeCache.Fingerprint);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, ".workspace"));
        if (runtimeCache.IsReady(cancellation)) {
            log("Reusing the clean Imlight runtime and its RavenDB engine. Updating this build's spell files.");
        } else {
            log("Preparing the clean Imlight runtime because source or adapter files changed...");
            CopyTree(Path.Combine(sourceRoot, "src"), Path.Combine(staging, "src"), cancellation);
            CopyTree(Path.Combine(sourceRoot, "submodule", "Imcodec", "src"), Path.Combine(staging, "submodule", "Imcodec", "src"), cancellation);
            definitions.Stage(staging, log, cancellation);
            if (!ClientTypeDefinitions.HasDefinitions(staging)) { throw new InvalidDataException("Client definition setup did not finish. Extract a complete Spell Workspace app package and try Build All again."); }
            var messageInputs = Path.Combine(staging, "submodule", "Imcodec", "src", "Imcodec.MessageLayer", "GeneratorInput");
            Directory.CreateDirectory(messageInputs);
            foreach (var file in catalog.GetArchive("Root").Files.Keys.Where(x => Path.GetFileName(x).Contains("Messages", StringComparison.Ordinal) && x.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))) {
                File.WriteAllBytes(Path.Combine(messageInputs, Path.GetFileName(file)), catalog.Read("Root", file));
            }
            log("Compiler message definitions extracted directly from the selected clean client.");
            var networkPath = Path.Combine(staging, "src", "Imlight.CoreLib", "Shared", "Networking", "Server.cs");
            var networkText = File.ReadAllText(networkPath);
            var networkAnchor = "string ip = null) {";
            if (!networkText.Contains(networkAnchor, StringComparison.Ordinal)) { throw new InvalidDataException("Unsupported Imlight localhost networking contract."); }
            File.WriteAllText(networkPath, networkText.Replace(networkAnchor, networkAnchor + "\n        ip = \"127.0.0.1\";", StringComparison.Ordinal));
            var listenerPath = Path.Combine(staging, "src", "Imlight.CoreLib", "Shared", "Networking", "TcpListenerActor.cs");
            var listenerText = File.ReadAllText(listenerPath);
            if (!listenerText.Contains("IPAddress.Parse(\"0.0.0.0\")", StringComparison.Ordinal)) { throw new InvalidDataException("Unsupported Imlight listener contract."); }
            File.WriteAllText(listenerPath, listenerText.Replace("IPAddress.Parse(\"0.0.0.0\")", "IPAddress.Loopback", StringComparison.Ordinal));
            var generatedLoader = Path.Combine(staging, "src", "Imlight.CoreLib", "Shared", "Resources", "RootArchiveLoader.cs");
            var stagedLoader = originalLoader.Replace("s_rootWad = ResourceWad();", "s_rootWad = SpellWorkspaceOverlay.Apply(SpellWorkspaceOverlay.ReadLocalArchive(ROOT_WAD_NAME));", StringComparison.Ordinal);
            var namedAnchor = "private static Archive LoadNamedWad(string wadName) {";
            if (!stagedLoader.Contains(namedAnchor, StringComparison.Ordinal)) { throw new InvalidDataException("The selected source does not support the verified named-resource contract."); }
            stagedLoader = stagedLoader.Replace(namedAnchor, namedAnchor + "\n        return SpellWorkspaceOverlay.ReadLocalArchive(wadName);\n#pragma warning disable CS0162", StringComparison.Ordinal);
            File.WriteAllText(generatedLoader, stagedLoader);
            var resourceManagerPath = Path.Combine(staging, "src", "Imlight.CoreLib", "Shared", "Resources", "ResourceManager.cs");
            var resourceManager = File.ReadAllText(resourceManagerPath);
            var archiveAnchor = "private static Archive ResourceWad(string wadName) {";
            if (!resourceManager.Contains(archiveAnchor, StringComparison.Ordinal)) { throw new InvalidDataException("Unsupported Imlight zone archive loader contract."); }
            File.WriteAllText(resourceManagerPath, resourceManager.Replace(archiveAnchor, archiveAnchor + "\n        return SpellWorkspaceOverlay.ReadLocalArchive(wadName);\n#pragma warning disable CS0162", StringComparison.Ordinal));
            log("Root, named resources, and world zones will read the selected client's local archives.");
            var helper = Path.Combine(staging, "src", "Imlight.CoreLib", "Shared", "Resources", "SpellWorkspaceOverlay.cs");
            File.Copy(Path.Combine(AssetsDirectory, "ServerAdapter", "SpellWorkspaceOverlay.cs"), helper);
            File.Copy(Path.Combine(AssetsDirectory, "ServerAdapter", "SpellWorkspaceGrant.cs"), Path.Combine(Path.GetDirectoryName(helper)!, "SpellWorkspaceGrant.cs"));
            var spellServicePath = Path.Combine(staging, "src", "Imlight.CoreLib", "Game", "Services", "SpellBookService.cs");
            var spellServiceText = File.ReadAllText(spellServicePath);
            var serviceAnchor = "internal class SpellbookService(SessionActor sessionActor) : MessageService(sessionActor) {";
            if (!spellServiceText.Contains(serviceAnchor, StringComparison.Ordinal)) { throw new InvalidDataException("Unsupported Imlight spellbook service contract."); }
            var grantHandler = File.ReadAllText(Path.Combine(AssetsDirectory, "ServerAdapter", "GrantHandler.txt"));
            File.WriteAllText(spellServicePath, spellServiceText.Replace(serviceAnchor, serviceAnchor + "\n" + grantHandler, StringComparison.Ordinal));
            var programPath = Path.Combine(staging, "src", "Imlight.Director", "Program.cs");
            var programText = File.ReadAllText(programPath);
            var worldDataAnchor = "SpiralDB.Load();";
            if (!programText.Contains(worldDataAnchor, StringComparison.Ordinal)) { throw new InvalidDataException("Unsupported Imlight world-data startup contract."); }
            programText = programText.Replace(worldDataAnchor, worldDataAnchor + "\n        if (SpiralDB.QuestTemplates.Count == 0 || !SpiralDB.QuestExists(\"Tutorial_Intro\")) { throw new System.IO.InvalidDataException(\"SpiralDB quest data is missing. Open Spell Workspace and run Build All to verify the portable world-data cache.\"); }", StringComparison.Ordinal);
            var readyAnchor = "OnlinePlayerCollection.Clear();";
            if (!programText.Contains(readyAnchor, StringComparison.Ordinal)) { throw new InvalidDataException("Unsupported Imlight startup contract."); }
            File.WriteAllText(programPath, programText.Replace(readyAnchor, readyAnchor + "\n        Imlight.CoreLib.Shared.Resources.SpellWorkspaceOverlay.SetReady(s_imlightSystem);", StringComparison.Ordinal));
            log("Building Imlight with the memory-only spell adapter...");
            var published = Path.Combine(staging, "Published");
            await Run("dotnet", ["publish", Path.Combine(staging, "src", "Imlight.Director", "Imlight.Director.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "false", "--disable-build-servers", "-p:UseSharedCompilation=false", "-o", published], staging, cancellation);
            BuildUpdates.CopyChanged(published, runtimeCache.Runtime, cancellation,
                relative => !relative.Equals(Path.Combine("Config", "Imlight.ini"), StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(runtimeCache.Runtime, relative)));
            runtimeCache.Complete(cancellation, published);
        }
        if (compileOnly) { return new(runtimeCache.Runtime, buildId, hash, null, null, Path.Combine(runtimeCache.Runtime, "Imlight.Director.exe")); }
        BuildUpdates.CopyChanged(runtimeCache.Runtime, folder, cancellation, relative => !relative.Equals(Path.Combine("Config", "Imlight.ini"), StringComparison.OrdinalIgnoreCase));
        var entries = SpellRules.Generate(catalog, project);
        var dataFolder = Path.Combine(folder, "Config", "SpellWorkspace");
        Directory.CreateDirectory(dataFolder);
        var controlToken = Path.Combine(dataFolder, "control-token.txt");
        if (!File.Exists(controlToken)) { updates.Text(controlToken, Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); }
        updates.Bytes(Path.Combine(dataFolder, "spell_overlay.bin"), SpellRules.Bundle(entries));
        updates.Json(Path.Combine(dataFolder, "manifest.json"), new {
            SchemaVersion = 1, BuildId = buildId, ManifestHash = hash, ProjectId = project.Id,
            BaseRootPath = Path.Combine(catalog.GameData, "Root.wad"),
            RootSha256 = SpellRules.Hash(Path.Combine(catalog.GameData, "Root.wad")),
            Spells = project.Spells.Where(x => x.Enabled).Select(x => new { x.TemplateId, x.InternalName, x.IsNew })
        });
        updates.Json(Path.Combine(folder, "spell_manifest.json"), project);
        var worldData = await WorldDataCache.Ensure(settings, log, progress, cancellation);
        var bots = Path.Combine(sourceRoot, "bots");
        if (Directory.Exists(bots) && !Directory.Exists(Path.Combine(folder, "bots"))) { CopyTree(bots, Path.Combine(folder, "bots"), cancellation); }
        var exe = Path.Combine(folder, "Imlight.Director.exe");
        if (!File.Exists(exe)) { throw new FileNotFoundException("Imlight publish returned without a server executable."); }
        progress?.Invoke("Saving a backup of your wizards, please wait...");
        var players = await PlayerCopies.Ensure(settings, project, Path.Combine(folder, "RavenDBServer"), log, cancellation);
        var configPath = Path.Combine(folder, "Config", "Imlight.ini");
        var config = File.ReadAllText(Path.Combine(sourceRoot, "src", "Imlight.Director", "Config", "Imlight.ini"));
        void Set(string name, string value) {
            var pattern = @"(?m)^" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*=.*$";
            if (!System.Text.RegularExpressions.Regex.IsMatch(config, pattern)) { throw new InvalidDataException("Imlight configuration contract missing: " + name); }
            config = System.Text.RegularExpressions.Regex.Replace(config, pattern, _ => name + " = " + value);
        }
        Set("GameRevision", definitions.Revision); Set("LoginServerPort", "12000"); Set("GameServerIP", "127.0.0.1");
        Set("PlayerDatabaseUrl", ""); Set("PlayerDatabaseName", players.Database);
        Set("EmbeddedDatabaseDataDirectory", players.DataDirectory); Set("EmbeddedDatabasePort", "12180");
        Set("EmbeddedDatabaseUseFull", "False"); Set("LocalWadCachePath", Path.Combine(folder, ".workspace", "wad-cache.db"));
        Set("SpiralDBLocalPath", Path.GetRelativePath(folder, worldData)); Set("SpiralDBAutoFetch", "False"); Set("SpiralDBDisableRemote", "True");
        updates.Text(configPath, config);
        updates.Json(Path.Combine(folder, "PlayerCopy.json"), players);
        updates.Json(Path.Combine(folder, "BuildManifest.json"), new { Product = "Spell Workspace", Version = "Beta v0.5", BuildId = buildId, ManifestHash = hash, ServerRoot = sourceRoot,
            PlayerDataDirectory = players.DataDirectory, OriginalPlayerBackup = players.OriginalBackup, RuntimeFingerprint = runtimeCache.Fingerprint, Staged = true, RuntimeVerified = false });
        updates.Text(Path.Combine(folder, "Start Imlight.cmd"), "@echo off\r\ncd /d \"%~dp0\"\r\nset \"IMLIGHT_CLIENT_GAME_DATA_PATH=" + catalog.GameData + "\"\r\nset \"IMLIGHT_LISTEN_ADDRESS=127.0.0.1\"\r\nImlight.Director.exe\r\npause\r\n");
        if (publishLauncher) { WriteSourceLauncher(sourceRoot, folder, exe, catalog.GameData, project.Id, updates); }
        if (stagedUpdates is null) { updates.Publish(cancellation); }
        log($"Reusable Imlight ready: {folder}. Wizard saves and existing world cache are preserved.");
        return new(folder, buildId, hash, null, null, exe);
    }

    private static void WriteSourceLauncher(string sourceRoot, string folder, string executable, string gameData, string projectId, BuildUpdates updates) {
        var profile = new { Folder = folder, ServerExe = executable, GameData = gameData, ProjectId = projectId, PortableRoot = PortablePaths.Root };
        var batch = "@echo off\r\ntitle Spell Workspace - Imlight - Beta v0.5\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0Start Imlight.ps1\"\r\nif errorlevel 1 pause\r\n";
        var script = """
            $ErrorActionPreference = 'Stop'
            try {
                $profile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SpellWorkspace-launch.json') -Raw | ConvertFrom-Json
                if (-not (Test-Path -LiteralPath $profile.ServerExe)) { throw 'The built server is missing. Open Spell Workspace and press Build All.' }
                $ports = 12000,12180,12181,12333,12334,12335,12500
                $busy = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $ports }
                if ($busy) { throw 'A server already occupies the localhost ports. Stop it before starting this separate Imlight build.' }
                $env:IMLIGHT_CLIENT_GAME_DATA_PATH = $profile.GameData
                $env:IMLIGHT_LISTEN_ADDRESS = '127.0.0.1'
                $temporary = Join-Path $profile.PortableRoot 'Build\Temporary'
                [System.IO.Directory]::CreateDirectory($temporary) | Out-Null
                $env:TEMP = $temporary
                $env:TMP = $temporary
                Write-Host 'Spell Workspace - Beta v0.5' -ForegroundColor Magenta
                Write-Host 'Starting the latest separate Imlight build with the saved wizard copy.'
                Push-Location -LiteralPath $profile.Folder
                try { & $profile.ServerExe; exit $LASTEXITCODE } finally { Pop-Location }
            } catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
            """;
        foreach (var destination in new[] { sourceRoot, folder }) {
            updates.Json(Path.Combine(destination, "SpellWorkspace-launch.json"), profile);
            updates.Text(Path.Combine(destination, "Start Imlight.cmd"), batch);
            updates.Text(Path.Combine(destination, "Start Imlight.ps1"), script);
        }
    }

    internal async Task<string> DownloadSource(string url, string destination, CancellationToken cancellation) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2) {
            throw new InvalidDataException("Enter a GitHub repository URL such as https://github.com/Revive101/Imlight.git.");
        }
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) { throw new InvalidOperationException("Choose an empty destination. Existing source will not be overwritten."); }
        log("Downloading Imlight and its pinned submodules from GitHub...");
        await Run("git", ["clone", "--recurse-submodules", "--depth", "1", "--shallow-submodules", url, destination], Path.GetDirectoryName(destination)!, cancellation);
        if (!File.Exists(Path.Combine(destination, "src", "Imlight.Director", "Imlight.Director.csproj"))) { throw new InvalidDataException("The downloaded repository does not contain the supported Imlight project."); }
        log("Source downloaded: " + destination);
        return destination;
    }

    internal async Task Run(string file, IEnumerable<string> arguments, string working, CancellationToken cancellation) {
        using var temporary = new TemporaryBuildSource(log);
        var start = new ProcessStartInfo(file) { WorkingDirectory = working, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        PortablePaths.ConfigureBuild(start, temporary.Path);
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = new Process { StartInfo = start };
        if (!process.Start()) { throw new InvalidOperationException("Could not start " + file); }
        async Task Read(StreamReader reader) {
            while (await reader.ReadLineAsync(cancellation) is string line) { log(line); }
        }
        try {
            await Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError), process.WaitForExitAsync(cancellation));
        } catch (OperationCanceledException) {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            throw;
        }
        if (process.ExitCode != 0) { throw new InvalidOperationException($"{Path.GetFileName(file)} failed with exit code {process.ExitCode}. See the build log."); }
    }

    private static void CopyTree(string source, string destination, CancellationToken cancellation) {
        if (!Directory.Exists(source)) { throw new DirectoryNotFoundException(source); }
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) { cancellation.ThrowIfCancellationRequested(); File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true); }
        foreach (var directory in Directory.EnumerateDirectories(source)) {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj" or ".git" or ".idea" or "node_modules" or "RavenDB") { continue; }
            CopyTree(directory, Path.Combine(destination, name), cancellation);
        }
    }
}
