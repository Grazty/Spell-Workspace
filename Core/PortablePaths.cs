// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Diagnostics;

namespace SpellWorkspace.Core;

internal static class PortablePaths {
    internal static string Root => ResolveRoot(AppContext.BaseDirectory);
    internal static string ResolveRoot(string applicationDirectory) {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var parent = Path.GetDirectoryName(directory);
        return Path.GetFileName(directory).Equals("Application", StringComparison.OrdinalIgnoreCase)
            && parent is not null && File.Exists(Path.Combine(parent, "SpellWorkspace.exe"))
            ? parent : directory;
    }
    internal static string Settings => Path.Combine(Root, "Settings");
    internal static string Workspace => Path.Combine(Root, "Workspace");
    internal static string Temporary => Path.Combine(Root, "Build", "Temporary");
    internal static string Dependencies => Path.Combine(Root, "Build", "Dependencies");
    internal static string Logs => Path.Combine(Root, "Logs");

    internal static IReadOnlyDictionary<string, string> BuildEnvironment => new Dictionary<string, string> {
        ["TEMP"] = Temporary,
        ["TMP"] = Temporary,
        ["DOTNET_CLI_HOME"] = Path.Combine(Root, "Build", "Dotnet"),
        ["NUGET_PACKAGES"] = Path.Combine(Dependencies, "Packages"),
        ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(Dependencies, "HttpCache"),
        ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(Dependencies, "PluginCache"),
        ["NUGET_SCRATCH"] = Path.Combine(Temporary, "NuGetScratch"),
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
        ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true"
    };

    internal static void Initialize() {
        try {
            Directory.CreateDirectory(Settings);
            Directory.CreateDirectory(Logs);
            foreach (var pair in BuildEnvironment) {
                if (Path.IsPathFullyQualified(pair.Value)) { Directory.CreateDirectory(pair.Value); }
                Environment.SetEnvironmentVariable(pair.Key, pair.Value, EnvironmentVariableTarget.Process);
            }
            TemporaryBuildSource.CleanAbandoned();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            throw new IOException("Spell Workspace needs permission to save beside its EXE. Extract the whole app to a writable folder, then reopen it. App folder: " + Root, exception);
        }
    }

    internal static void ConfigureBuild(ProcessStartInfo start, string? temporary = null) {
        foreach (var pair in BuildEnvironment) { start.Environment[pair.Key] = pair.Value; }
        if (temporary is not null) {
            Directory.CreateDirectory(temporary);
            start.Environment["TEMP"] = temporary;
            start.Environment["TMP"] = temporary;
            start.Environment["NUGET_SCRATCH"] = Path.Combine(temporary, "NuGetScratch");
        }
    }
}
