// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Imcodec.ObjectProperty.TypeCache;
using SpellWorkspace.UI;

namespace SpellWorkspace.Core;

internal static class WorkspaceVerification {
    internal static void Run(ResourceCatalog catalog, string reportPath) {
        var checks = new List<string>();
        void Check(bool success, string name) { if (!success) { throw new InvalidDataException("Verification failed: " + name); } checks.Add(name); }
        var folder = Path.Combine(Path.GetDirectoryName(reportPath)!, "Checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        Check(ProjectFiles.SettingsPath == Path.Combine(PortablePaths.Root, "Settings", "settings.json")
            && ProjectFiles.RecoveryPath == Path.Combine(PortablePaths.Root, "Settings", "recovery.spellproject")
            && new WorkspaceSettings().OutputRoot == Path.Combine(PortablePaths.Root, "Workspace"), "App settings, recovery and default workspace live beside the top-level launcher");
        var layoutRoot = Path.Combine(folder, "Portable layout");
        Directory.CreateDirectory(Path.Combine(layoutRoot, "Application"));
        File.WriteAllText(Path.Combine(layoutRoot, "SpellWorkspace.exe"), "launcher fixture");
        Check(PortablePaths.ResolveRoot(Path.Combine(layoutRoot, "Application")) == layoutRoot, "Application subfolder resolves portable storage to the launcher folder");
        Check(PortablePaths.ResolveRoot(layoutRoot) == layoutRoot, "Existing flat installations keep their storage location");
        Check(Directory.Exists(Path.Combine(BuildService.AssetsDirectory, "Native")) && Directory.Exists(Path.Combine(BuildService.AssetsDirectory, "ServerAdapter")), "Client and server build resources are available beside the actual application");
        var environmentLog = new List<string>();
        Task.Run(() => new BuildService(environmentLog.Add).Run("cmd.exe", ["/d", "/c", "echo %TEMP%&echo %TMP%&echo %DOTNET_CLI_HOME%&echo %NUGET_PACKAGES%&echo %NUGET_HTTP_CACHE_PATH%&echo %NUGET_PLUGINS_CACHE_PATH%&echo %NUGET_SCRATCH%"], PortablePaths.Root, CancellationToken.None)).GetAwaiter().GetResult();
        Check(environmentLog.Count == 7 && environmentLog[0] == environmentLog[1]
            && Path.GetDirectoryName(environmentLog[0]) == PortablePaths.Temporary
            && !Directory.Exists(environmentLog[0])
            && environmentLog.Skip(2).Take(4).SequenceEqual(new[] { "DOTNET_CLI_HOME", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH" }.Select(key => PortablePaths.BuildEnvironment[key]))
            && environmentLog[6] == Path.Combine(environmentLog[0], "NuGetScratch"), "Child compilers use disposable portable temporary folders and shared reusable dependency caches; their temporary files are removed after exit");
        string scratchPath;
        using (var scratch = new TemporaryBuildSource(_ => { })) {
            scratchPath = scratch.Path; Directory.CreateDirectory(scratchPath); File.WriteAllText(Path.Combine(scratchPath, "compile.tmp"), "test");
            Check(Path.GetDirectoryName(scratchPath) == PortablePaths.Temporary, "Compiler scratch folder is visible inside the app folder");
        }
        Check(!Directory.Exists(scratchPath), "Compiler scratch files are removed when their build scope ends");
        string failedScratchPath = "";
        try {
            using var scratch = new TemporaryBuildSource(_ => { }); failedScratchPath = scratch.Path;
            Directory.CreateDirectory(failedScratchPath); File.WriteAllText(Path.Combine(failedScratchPath, "compile.tmp"), "test");
            throw new InvalidOperationException("Simulated compiler failure");
        } catch (InvalidOperationException) { }
        Check(!Directory.Exists(failedScratchPath), "Compiler scratch files are also removed after a failed build");
        using (var active = new TemporaryBuildSource(_ => { })) {
            Directory.CreateDirectory(active.Path); File.WriteAllText(Path.Combine(active.Path, "active.tmp"), "active");
            Directory.SetLastWriteTimeUtc(active.Path, DateTime.UtcNow.AddDays(-2));
            TemporaryBuildSource.CleanAbandoned();
            Check(Directory.Exists(active.Path), "Automatic cleanup preserves an active build even when its folder is old");
        }
        var abandoned = Path.Combine(PortablePaths.Temporary, "SWBuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandoned); File.WriteAllText(Path.Combine(abandoned, "abandoned.tmp"), "interrupted build");
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));
        TemporaryBuildSource.CleanAbandoned();
        Check(!Directory.Exists(abandoned), "Automatic cleanup removes abandoned compiler folders older than one day");
        var worldSettings = new WorkspaceSettings { OutputRoot = Path.Combine(folder, "WorldCacheCheck"), ServerRoot = Path.Combine(folder, "WorldSource") };
        var worldFolder = WorldDataCache.Folder(worldSettings);
        Check(worldFolder == Path.Combine(worldSettings.OutputRoot, "WorldData", "SpiralDB")
            && WorldDataCache.Folder(worldSettings, branch: "other") != worldFolder, "World data has one short portable cache per repository and branch outside project/server hashes");
        try { WorldDataCache.Validate(worldFolder); Check(false, "Missing world data must be rejected"); }
        catch (InvalidDataException) { Check(true, "A missing world-data cache cannot pass build validation"); }
        Directory.CreateDirectory(Path.Combine(worldFolder, "QuestTemplates")); Directory.CreateDirectory(Path.Combine(worldFolder, "GlobalRegistry"));
        foreach (var quest in WorldDataCache.RequiredQuests) { ProjectFiles.SaveJson(Path.Combine(worldFolder, "QuestTemplates", "questtemplates_" + quest + ".json"), new { m_questName = quest }); }
        var registryFile = Path.Combine(worldFolder, "GlobalRegistry", "registry.json");
        File.WriteAllText(registryFile, "{\"GlobalRegistryValues\":{\"Localization\":1,},}");
        Check(WorldDataCache.Validate(worldFolder) == WorldDataCache.RequiredQuests.Length, "Quest data requires the tutorial, first story quest and nonempty global registry; upstream trailing commas are supported");
        var sourceConfig = Path.Combine(worldSettings.ServerRoot, "src", "Imlight.Director", "Config", "Imlight.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceConfig)!); File.WriteAllText(sourceConfig, "[Database]\nSpiralDBRemoteUrl = " + WorldDataCache.DefaultRepository + "\nSpiralDBBranch = main\n");
        var worldLog = new List<string>(); var worldTime = File.GetLastWriteTimeUtc(registryFile);
        var reusedWorld = Task.Run(() => WorldDataCache.Ensure(worldSettings, worldLog.Add, null, CancellationToken.None)).GetAwaiter().GetResult();
        Check(reusedWorld == worldFolder && File.GetLastWriteTimeUtc(registryFile) == worldTime && worldLog.Single().StartsWith("Reusing"), "Repeated builds reuse verified quest data without starting Git or overwriting cached files");
        File.WriteAllText(registryFile, "{\"GlobalRegistryValues\":{}}");
        try { WorldDataCache.Validate(worldFolder); Check(false, "Empty global registry must be rejected"); }
        catch (InvalidDataException) { Check(true, "An empty global registry cannot silently produce a ready server"); }
        ProjectFiles.SaveJson(registryFile, new { GlobalRegistryValues = new { Localization = 1 } });
        var introFile = Path.Combine(worldFolder, "QuestTemplates", "questtemplates_Tutorial_Intro.json");
        ProjectFiles.SaveJson(introFile, new { m_questName = "WrongQuest" });
        try { WorldDataCache.Validate(worldFolder); Check(false, "Mismatched tutorial quest must be rejected"); }
        catch (InvalidDataException) { Check(true, "An incorrect tutorial template cannot pass world-data validation"); }
        var freshSource = Path.Combine(folder, "AutomaticTypes-Source");
        Directory.CreateDirectory(freshSource);
        var extractionSettings = new WorkspaceSettings { ClientExe = catalog.ClientExe, OutputRoot = Path.Combine(folder, "ExtractedTypes") };
        var extractionLog = new List<string>();
        var extracted = Task.Run(() => ClientTypeDefinitions.Ensure(extractionSettings, extractionLog.Add, null, CancellationToken.None)).GetAwaiter().GetResult();
        Check(extracted.ClassCount > 1000 && File.Exists(extracted.File) && extractionLog.Any(x => x.StartsWith("Extracted ")),
            "Type definitions are freshly extracted from the selected client process without a bundled dump");
        using (var types = JsonDocument.Parse(File.ReadAllText(extracted.File))) {
            foreach (var name in new[] { "class WizardCharacterCreationInfo", "class WizardCharacterBehavior" }) {
                var properties = types.RootElement.GetProperty("classes").EnumerateObject().Select(type => type.Value)
                    .Single(type => type.GetProperty("name").GetString() == name).GetProperty("properties").EnumerateObject().ToArray();
                var identifiers = properties.Select(property => property.Value.GetProperty("id").GetInt32()).ToArray();
                Check(identifiers.SequenceEqual(identifiers.Order()) && !properties.Select(property => property.Name).SequenceEqual(properties.Select(property => property.Name).Order(StringComparer.Ordinal)),
                    name + " preserves client field order for compact character-creation messages");
            }
        }
        var stagedSource = Path.Combine(folder, "AutomaticTypes-Staging");
        Directory.CreateDirectory(ClientTypeDefinitions.InputFolder(stagedSource));
        File.WriteAllText(Path.Combine(ClientTypeDefinitions.InputFolder(stagedSource), "Unrelated.json"), "{}");
        extracted.Stage(stagedSource, extractionLog.Add, CancellationToken.None);
        var stagedDump = Path.Combine(ClientTypeDefinitions.InputFolder(stagedSource), "ClientDump.json");
        Check(SpellRules.Hash(stagedDump) == extracted.Sha256 && !ClientTypeDefinitions.HasDefinitions(freshSource)
            && Directory.EnumerateFiles(ClientTypeDefinitions.InputFolder(stagedSource), "*.json").Count() == 1,
            "Staging supplies only the selected client's extracted types without modifying the source checkout");
        var cachedTime = File.GetLastWriteTimeUtc(extracted.File);
        extractionLog.Clear();
        var reused = Task.Run(() => ClientTypeDefinitions.Ensure(extractionSettings, extractionLog.Add, null, CancellationToken.None)).GetAwaiter().GetResult();
        Check(reused.Sha256 == extracted.Sha256 && File.GetLastWriteTimeUtc(extracted.File) == cachedTime && extractionLog.Count == 1 && extractionLog[0].StartsWith("Reusing"),
            "A repeat build reuses the same exact-client dump without starting an extraction client");
        var cacheFolder = ClientTypeDefinitions.CacheFolder(extractionSettings, SpellRules.Hash(catalog.ClientExe));
        var manifestPath = Path.Combine(cacheFolder, "manifest.json");
        var currentManifest = File.ReadAllText(manifestPath);
        var obsoleteManifest = System.Text.Json.Nodes.JsonNode.Parse(currentManifest)!; obsoleteManifest["ExtractorVersion"] = 1;
        File.WriteAllText(manifestPath, obsoleteManifest.ToJsonString());
        Check(ClientTypeDefinitions.ReadCache(cacheFolder, SpellRules.Hash(catalog.ClientExe)) is null, "Alphabetically ordered dumps from the old extractor are invalidated automatically");
        File.WriteAllText(manifestPath, currentManifest);
        Check(ClientTypeDefinitions.ReadCache(cacheFolder, new string('0', 64)) is null, "A dump cached for another executable is never reused");
        File.AppendAllText(extracted.File, "damaged");
        Check(ClientTypeDefinitions.ReadCache(cacheFolder, SpellRules.Hash(catalog.ClientExe)) is null, "A damaged extracted dump is invalidated before compilation");
        File.Copy(stagedDump, extracted.File, true);
        Check(ClientTypeDefinitions.ReadCache(cacheFolder, SpellRules.Hash(catalog.ClientExe)) is not null, "A verified complete extraction cache remains reusable");
        var canceledSettings = new WorkspaceSettings { ClientExe = catalog.ClientExe, OutputRoot = Path.Combine(folder, "CanceledTypeExtraction") };
        using (var canceledSetup = new CancellationTokenSource()) {
            canceledSetup.Cancel(); var rejected = false;
            try { Task.Run(() => ClientTypeDefinitions.Ensure(canceledSettings, _ => { }, null, canceledSetup.Token)).GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && !Directory.Exists(canceledSettings.OutputRoot), "Canceled extraction does not create a cache or launch a client");
        }
        Directory.CreateDirectory(Path.Combine(freshSource, "src"));
        Directory.CreateDirectory(Path.Combine(freshSource, "submodule", "Imcodec", "src"));
        var definitionSettings = new WorkspaceSettings { ServerRoot = freshSource, OutputRoot = Path.Combine(folder, "AutomaticTypes-Cache") };
        string definitionFingerprint;
        using (var cache = new ServerRuntimeCache(definitionSettings, catalog, CancellationToken.None, extracted.Sha256)) { definitionFingerprint = cache.Fingerprint; }
        using (var cache = new ServerRuntimeCache(definitionSettings, catalog, CancellationToken.None, extracted.Sha256)) {
            Check(cache.Fingerprint == definitionFingerprint, "Repeated builds reuse the same extracted definition fingerprint");
        }
        using (var cache = new ServerRuntimeCache(definitionSettings, catalog, CancellationToken.None, new string('1', 64))) {
            Check(cache.Fingerprint != definitionFingerprint, "A new extracted schema invalidates the server runtime cache");
        }
        var oldSettings = JsonSerializer.Deserialize<WorkspaceSettings>("{\"ClientExe\":\"unchanged.exe\"}", ProjectFiles.JsonOptions)!;
        Check(oldSettings.ThemeMode == "Dark" && oldSettings.AccentColor == "#8C62FF" && oldSettings.ClientExe == "unchanged.exe",
            "Existing settings load with the dark purple theme while retaining their selected client");
        foreach (var mode in new[] { "Dark", "Light" }) {
            foreach (var accent in WorkspaceTheme.Accents.Concat(new[] { (Name: "White", Hex: "#FFFFFF"), (Name: "Black", Hex: "#000000") })) {
                var theme = new WorkspaceSettings { ThemeMode = mode, AccentColor = accent.Hex, ClientExe = "keep.exe", ServerRoot = "keep-source" };
                WorkspaceTheme.Apply(theme);
                var resources = System.Windows.Application.Current.Resources;
                var text = ((SolidColorBrush)resources["Text"]).Color;
                var surface = ((SolidColorBrush)resources["Surface"]).Color;
                var accentText = ((SolidColorBrush)resources["AccentText"]).Color;
                Check(WorkspaceTheme.Contrast(text, surface) >= 4.5 && WorkspaceTheme.Contrast(accentText, surface) >= 4.5,
                    mode + " / " + accent.Name + " retains readable text and accent labels");
                var restored = JsonSerializer.Deserialize<WorkspaceSettings>(JsonSerializer.Serialize(theme, ProjectFiles.JsonOptions), ProjectFiles.JsonOptions)!;
                Check(restored.ThemeMode == mode && restored.AccentColor == accent.Hex && restored.ClientExe == "keep.exe" && restored.ServerRoot == "keep-source",
                    mode + " / " + accent.Name + " saves appearance without changing client and server selections");
            }
        }
        var invalidTheme = new WorkspaceSettings { ThemeMode = "unknown", AccentColor = "not-a-color" };
        WorkspaceTheme.Apply(invalidTheme);
        Check(invalidTheme.ThemeMode == "Dark" && invalidTheme.AccentColor == "#8C62FF", "Invalid saved appearance safely returns to the default theme");
        var source = catalog.Spells.First(x => x.InternalName == "Fire Cat");
        var utilityVm = new WorkspaceViewModel(new() { ClientExe = catalog.ClientExe }) { Project = new() { RootSha256 = catalog.RootSha256 } };
        utilityVm.SetCatalog(catalog);
        foreach (var name in new[] { "Fire Prism", "Fireblade", "Tower Shield", "Reshuffle", "Sprite", "Fire Elf" }) {
            var utilitySource = catalog.Spells.First(x => x.InternalName == name);
            utilityVm.SelectedItem = new LibraryItem(catalog, utilitySource);
            Check(utilityVm.HasNumericEffects == SpellRules.Leaves(utilitySource.Template.m_effects).Any(SpellRules.Numeric),
                name + " shows damage or healing inputs only for its actual numeric effects");
            if (name is "Fire Prism" or "Fireblade" or "Tower Shield" or "Reshuffle") {
                Check(!utilityVm.IsFixedDamage && !utilityVm.IsRangeDamage && utilityVm.HasPrimaryMechanics,
                    name + " exposes utility mechanics without attack controls");
            }
        }
        var prismSource = catalog.Spells.First(x => x.InternalName == "Fire Prism");
        var originalPrismText = Imlight.CoreLib.Game.Quests.Management.SpellCardText.Render(prismSource.Template);
        Check(originalPrismText.Plain.Contains("Fire") && originalPrismText.Plain.Contains("Ice") && !originalPrismText.Plain.Contains("dTA") && !originalPrismText.Plain.Contains("dTB"),
            "Native prism conversion description tokens resolve to school names and icons in the editor preview");
        utilityVm.NewSpell("Utility Prism Test", prismSource);
        Check(utilityVm.SelectedItem!.Type == "Prism" && utilityVm.CurrentTemplate!.m_sTypeName == prismSource.Template.m_sTypeName
            && utilityVm.CurrentTemplate.m_spellBase == prismSource.Template.m_spellBase && !utilityVm.HasNumericEffects,
            "A new prism inherits its prism mechanic and native animation without becoming an attack");
        var prismControl = utilityVm.PrimaryMechanics.Single();
        Check(prismControl.IsPrism && !prismControl.HasAmount && prismControl.School == "Fire" && prismControl.ConversionSchool == "Ice",
            "Native Fire Prism displays Fire-to-Ice conversion instead of its numeric school hash as damage");
        prismControl.ConversionSchool = "Storm";
        prismControl.School = "Ice";
        var prismOverlay = SpellRules.Generate(catalog, utilityVm.Project);
        var prismExport = ResourceCatalog.Decode<SpellTemplate>(prismOverlay.Single(x => x.File == "Spells/SpellWorkspace/" + utilityVm.SelectedId + ".xml").Data);
        var prismEffect = SpellRules.Leaves(prismExport.m_effects).Single();
        Check(prismEffect.m_effectType == kSpellEffects.kModifyIncomingDamageType && prismEffect.m_sDamageType == "Ice"
            && unchecked((uint)prismEffect.m_effectParam) == Imcodec.Cryptography.StringHash.Compute("Storm")
            && prismEffect.m_actNum == SpellRules.Leaves(prismSource.Template.m_effects).Single().m_actNum,
            "Prism conversion changes persist through the real client/server serialization while retaining the native effect type and act");
        var prismLocale = LanguageTable.Read(prismOverlay.Single(x => x.File == "Locale/en-US/Spells.lang").Data, "Spells");
        var prismText = prismLocale.Values[prismExport.m_description.ToString().Split('_')[1]];
        Check(prismText.Contains("$IceSchoolIcon$") && prismText.Contains("$StormSchoolIcon$") && !prismText.Contains("$FireSchoolIcon$"),
            "An edited prism's game description uses the selected conversion schools and their icons");
        utilityVm.Undo(); utilityVm.Redo();
        Check(utilityVm.PrimaryMechanics.Single().School == "Ice" && utilityVm.PrimaryMechanics.Single().ConversionSchool == "Storm",
            "Prism schools survive undo and redo");
        var reshuffle = catalog.Spells.First(x => x.InternalName == "Reshuffle");
        utilityVm.SelectedItem = new LibraryItem(catalog, reshuffle);
        Check(!utilityVm.PrimaryMechanics.Single().HasAmount && !utilityVm.PrimaryMechanics.Single().HasRounds,
            "A parameterless reshuffle is not given damage or fake round values");
        var elemental = catalog.Spells.First(x => x.InternalName == "ElementalBlade" || x.Name == "Elemental Blade");
        utilityVm.SelectedItem = new LibraryItem(catalog, elemental);
        Check(utilityVm.PrimaryMechanics.Count() == SpellRules.Leaves(elemental.Template.m_effects).Count && utilityVm.PrimaryMechanics.Count() > 1,
            "A utility card with multiple effects exposes every primary mechanic");
        var balanceAnimation = catalog.ById[565690458];
        var originalBalanceAnimation = catalog.Cinematic(balanceAnimation.Template.m_spellBase);
        var originalBalanceBytes = ResourceCatalog.Encode(originalBalanceAnimation);
        var prismProject = new SpellProject { RootSha256 = catalog.RootSha256,
            Spells = [new() { IsNew = true, SourceTemplateId = prismSource.Id, TemplateId = 1800000100, InternalName = "Prism Announcement Test",
                School = "Balance", AnimationTemplateId = balanceAnimation.Id,
                Effects = [new() { Index = 0, Amount = unchecked((int)Imcodec.Cryptography.StringHash.Compute("Shadow")) }] }] };
        var shadowOverlay = SpellRules.Generate(catalog, prismProject);
        var shadowPrism = ResourceCatalog.Decode<SpellTemplate>(shadowOverlay.Single(x => x.File == "Spells/SpellWorkspace/1800000100.xml").Data);
        var shadowAnimation = ResourceCatalog.Decode<CinematicTemplate>(shadowOverlay.Single(x => x.File == "Cinematics/" + shadowPrism.m_spellBase + ".xml").Data);
        var shadowStage = shadowAnimation.m_stages.OfType<AddHangingCinematicStageTemplate>().Single(x => x.m_name == "AddHanging");
        var announcements = LanguageTable.Read(shadowOverlay.Single(x => x.File == "Locale/en-US/CombatMessages.lang").Data, "CombatMessages");
        var announcement = announcements.Values[shadowStage.m_spellText.ToString().Split('_')[1]];
        Check(announcement.Contains("$BalanceSchoolIcon$") && announcement.Contains("$ShadowSchoolIcon$")
            && !announcement.Contains("$FireSchoolIcon$") && !announcement.Contains("$dTA_image$") && !announcement.Contains("$dTB_image$"),
            "A Balance-to-Shadow prism with the Elemental Prism animation announces its selected schools instead of the source's Fire conversion");
        var nativeBalanceStage = originalBalanceAnimation.m_stages.OfType<AddHangingCinematicStageTemplate>().Single(x => x.m_name == "AddHanging");
        Check(shadowStage.m_hanging == nativeBalanceStage.m_hanging && shadowStage.m_sound == nativeBalanceStage.m_sound
            && shadowStage.m_burnSound == nativeBalanceStage.m_burnSound && SpellRules.Leaves(shadowPrism.m_effects).Count == 1
            && unchecked((uint)SpellRules.Leaves(shadowPrism.m_effects).Single().m_effectParam) == Imcodec.Cryptography.StringHash.Compute("Shadow"),
            "The prism announcement update retains one conversion, its Shadow destination and the chosen Balance ward visual and sounds");
        var originalCombatText = LanguageTable.Read(catalog.Read("Root", "Locale/en-US/CombatMessages.lang"), "CombatMessages");
        Check(originalCombatText.Values.All(x => announcements.Values.TryGetValue(x.Key, out var text) && text == x.Value)
            && ResourceCatalog.Encode(originalBalanceAnimation).SequenceEqual(originalBalanceBytes),
            "Custom announcements preserve all original combat messages and the shared Elemental Prism cinematic");
        var elementalPrismChange = new SpellModification { SourceTemplateId = balanceAnimation.Id, TemplateId = balanceAnimation.Id,
            Effects = new[] { "Shadow", "Death", "Life" }.Select((school, index) => new EffectModification {
                Index = index, Amount = unchecked((int)Imcodec.Cryptography.StringHash.Compute(school)) }).ToList() };
        var multiPrismOverlay = SpellRules.Generate(catalog, new() { RootSha256 = catalog.RootSha256, Spells = [elementalPrismChange] });
        var multiPrism = ResourceCatalog.Decode<SpellTemplate>(multiPrismOverlay.Single(x => x.File == balanceAnimation.File).Data);
        var multiAnimation = ResourceCatalog.Decode<CinematicTemplate>(multiPrismOverlay.Single(x => x.File == "Cinematics/" + multiPrism.m_spellBase + ".xml").Data);
        var multiAnnouncements = LanguageTable.Read(multiPrismOverlay.Single(x => x.File == "Locale/en-US/CombatMessages.lang").Data, "CombatMessages");
        Check(SpellRules.Leaves(multiPrism.m_effects).All(effect => {
            var stageName = "AddHanging" + (effect.m_actNum == 0 ? "" : effect.m_actNum.ToString());
            var stage = multiAnimation.m_stages.OfType<AddHangingCinematicStageTemplate>().Single(x => x.m_name == stageName);
            var text = multiAnnouncements.Values[stage.m_spellText.ToString().Split('_')[1]];
            var destination = SpellRules.Schools.Single(school => Imcodec.Cryptography.StringHash.Compute(school) == unchecked((uint)effect.m_effectParam));
            return text.Contains("$" + destination + "SchoolIcon$") && !text.Contains("$FireSchoolIcon$");
        }), "A random multi-conversion prism binds each act to its own selected destination announcement");
        var original = ResourceCatalog.Encode(source.Template);
        var project = new SpellProject { RootSha256 = catalog.RootSha256 };
        var changes = new SpellModification { SourceTemplateId = source.Id, TemplateId = source.Id, Pips = 4, Accuracy = 90, XPips = true,
            SchoolPips = new() { ["Ice"] = 2 }, Effects = [new() { Index = 0, Amount = 495 }] };
        project.Spells.Add(changes);
        var edited = SpellRules.Apply(catalog, changes);
        Check(edited.m_spellRank.m_spellRank == 4 && edited.m_spellRank.m_xPipSpell && edited.m_spellRank.m_icePips == 2 && edited.m_accuracy == 90, "Real pip, school-pip, X-pip and accuracy fields change together");
        Check(ResourceCatalog.Encode(source.Template).SequenceEqual(original), "Original template remains unchanged");
        var oldLeaves = SpellRules.Leaves(source.Template.m_effects); var newLeaves = SpellRules.Leaves(edited.m_effects);
        Check(newLeaves[0].m_effectParam == 495 && newLeaves.Skip(1).Select(x => x.m_effectParam).SequenceEqual(oldLeaves.Skip(1).Select(x => x.m_effectParam)), "One effect changes without changing sibling random outcomes");
        Check(edited.m_effects[0].GetType() == source.Template.m_effects[0].GetType() && edited.m_spellBase == source.Template.m_spellBase, "Nested effect structure and casting animation are preserved");
        var custom = SpellRules.Create(catalog, project, source, "Verification Spell"); project.Spells.Add(custom);
        var originalBlade = catalog.Spells.First(entry => entry.InternalName == "Fireblade");
        var bladePrototype = SpellRules.Leaves(originalBlade.Template.m_effects).First(effect => effect.m_effectType == kSpellEffects.kModifyOutgoingDamage);
        var exportedBlade = SecondaryEffects.Create(catalog, source.Template, new() { Kind = "Blade", School = "Fire", Target = "Caster", Amount = 25 });
        Check(exportedBlade.m_damageType == bladePrototype.m_damageType && exportedBlade.m_damageType == 0
            && exportedBlade.m_sDamageType == bladePrototype.m_sDamageType && exportedBlade.m_disposition == bladePrototype.m_disposition
            && exportedBlade.m_effectType == bladePrototype.m_effectType,
            "Custom Fire blades use the original blade's school-name and runtime-cache resource format");
        var changedSchool = SpellRules.Apply(catalog, new() { SourceTemplateId = source.Id, TemplateId = source.Id, School = "Ice" });
        Check(SpellRules.Leaves(changedSchool.m_effects).All(effect => effect.m_sDamageType == "Ice" && effect.m_damageType == 0),
            "Changing attack school updates the school name without preloading a client runtime school cache");
        var second = SpellRules.Create(catalog, new SpellProject(), source, "Second Project Spell");
        Check(custom.TemplateId != second.TemplateId && CustomIds.BelongsToAnotherProject(custom.TemplateId, Guid.NewGuid().ToString("N")), "Custom IDs are reserved across projects");
        var overlay = SpellRules.Generate(catalog, project);
        var manifest = ResourceCatalog.Decode<TemplateManifest>(overlay.Single(x => x.File == "TemplateManifest.xml").Data);
        Check(manifest.m_serializedTemplates.Count == catalog.Manifest.m_serializedTemplates.Count + 1 && manifest.m_serializedTemplates.Last().m_id == custom.TemplateId, "New spell is registered without replacing existing template IDs");
        var locale = LanguageTable.Read(overlay.Single(x => x.File == "Locale/en-US/Spells.lang").Data, "Spells");
        var baseLocale = LanguageTable.Read(catalog.Read("Root", "Locale/en-US/Spells.lang"), "Spells");
        var customTemplate = ResourceCatalog.Decode<SpellTemplate>(overlay.Single(x => x.File == "Spells/SpellWorkspace/" + custom.TemplateId + ".xml").Data);
        Check(customTemplate.GetType() == typeof(SpellTemplate) && source.Template is TieredSpellTemplate && edited is TieredSpellTemplate,
            "A new Fire Cat card exports as an independent learned spell while an edited Fire Cat keeps its original upgrade type");
        Check(customTemplate.m_spellFusion == 0 && customTemplate.m_previousSpellName.ToString().Length == 0
            && !customTemplate.m_adjectives.Any(adjective => adjective.ToString().StartsWith("Fusion_", StringComparison.Ordinal))
            && source.Template.m_adjectives.Any(adjective => adjective.ToString() == "Fusion_FireCat"),
            "New cards detach source fusion grouping without changing the source spell");
        Check(customTemplate.m_spellBase == source.Template.m_spellBase && customTemplate.m_imageName == source.Template.m_imageName
            && SpellRules.Leaves(customTemplate.m_effects).Select(effect => effect.m_effectParam).SequenceEqual(oldLeaves.Select(effect => effect.m_effectParam)),
            "Detaching a new card preserves its source artwork, casting animation, costs and original damage outcomes");
        var specialized = catalog.Spells.FirstOrDefault(entry => entry.Template.GetType() != typeof(SpellTemplate) && entry.Template is not TieredSpellTemplate);
        if (specialized is not null) {
            var specializedMod = SpellRules.Create(catalog, project, specialized, "Independent specialized spell");
            var specializedClone = ResourceCatalog.Decode<SpellTemplate>(ResourceCatalog.Encode(SpellRules.Apply(catalog, specializedMod)));
            var specializedProperties = specialized.Template.GetType().GetProperties().Where(property => typeof(SpellTemplate).GetProperty(property.Name) is null);
            Check(specializedClone.GetType() == specialized.Template.GetType()
                && specializedProperties.All(property => JsonSerializer.Serialize(property.GetValue(specializedClone), ProjectFiles.JsonOptions)
                    == JsonSerializer.Serialize(property.GetValue(specialized.Template), ProjectFiles.JsonOptions)),
                "Cloning other specialized spell types retains their subtype and extra behavior properties");
        }
        Check(Imcodec.Cryptography.StringHash.Compute(customTemplate.m_name) == custom.TemplateId, "A new spell's internal-name hash matches its manifest, learned-spell and client cache ID");
        var legacy = new SpellModification { IsNew = true, SourceTemplateId = source.Id, TemplateId = 1749819234, InternalName = "SpellWorkspace_11111111111111111111111111111111_1749819234" };
        var legacySpell = SpellRules.Apply(catalog, legacy);
        Check(Imcodec.Cryptography.StringHash.Compute(legacySpell.m_name) == legacy.TemplateId && legacy.TemplateId == 1749819234
            && legacySpell.m_name != legacy.InternalName, "The reported crashing spell gets a matching client cache name without changing its already learned ID");
        Check((System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ResourceCatalog.Encode(legacySpell).AsSpan(4)) & 4) == 0
            && legacySpell.m_delayEnchantmentOrder == source.Template.m_delayEnchantmentOrder, "Exported resources use numeric enums and preserve the delay-order value, avoiding the rejected kAnyOrder spelling");
        for (uint index = 0; index < 128; index++) {
            var id = 0x60000000u + index * 1299709u;
            var name = CustomIds.ClientName("SpellWorkspace_IdentityCheck_" + index, id);
            Check(Imcodec.Cryptography.StringHash.Compute(name) == id && name == CustomIds.ClientName("SpellWorkspace_IdentityCheck_" + index, id), "Custom identity " + index + " is deterministic and matches the allocated ID");
        }
        Check(locale.Values[customTemplate.m_displayName.ToString().Split('_')[1]] == "Verification Spell"
            && baseLocale.Values.All(x => locale.Values.GetValueOrDefault(x.Key) == x.Value), "Generated client text uses versioned headers and numeric keys in a registered table, preserving all original text");
        var packPath = Path.Combine(folder, "verification.spellpack"); SpellPacks.Export(packPath, project);
        var restoredPack = SpellPacks.Read(packPath); Check(restoredPack.Spells.Count == 2, "Spell pack checksum round trip");
        var imported = new SpellProject(); var remap = SpellPacks.Merge(catalog, imported, restoredPack, [custom.TemplateId]);
        Check(remap.ContainsKey(custom.TemplateId) && imported.Spells.Single().TemplateId != custom.TemplateId, "Pack import remaps an ID owned by another project");
        var before = JsonSerializer.Serialize(imported);
        var invalid = new SpellProject { Spells = [new() { SourceTemplateId = source.Id, TemplateId = source.Id }, new() { SourceTemplateId = uint.MaxValue, TemplateId = uint.MaxValue }] };
        try { SpellPacks.Merge(catalog, imported, invalid, invalid.Spells.Select(x => x.TemplateId)); throw new Exception("Invalid pack unexpectedly imported."); }
        catch (InvalidDataException) { Check(JsonSerializer.Serialize(imported) == before, "Failed import leaves the entire project unchanged"); }
        var artwork = catalog.Texture(source.Artwork) ?? throw new InvalidDataException("Source artwork is missing.");
        var png = Path.Combine(folder, "FireCat-artwork.png"); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(artwork));
        using (var stream = File.Create(png)) { encoder.Save(stream); }
        var dds = MainWindow.ImportTexture(png); File.WriteAllBytes(Path.Combine(folder, "FireCat-artwork.dds"), dds);
        var reimported = SpellCardView.DecodeImage(dds) ?? throw new InvalidDataException("Reimported DDS cannot decode.");
        static byte[] Pixels(BitmapSource image) { var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes; }
        Check(reimported.PixelWidth == artwork.PixelWidth && reimported.PixelHeight == artwork.PixelHeight && Pixels(artwork).SequenceEqual(Pixels(reimported)), "Artwork exports to PNG and returns to DDS with identical pixels");
        var artProject = new SpellProject { RootSha256 = catalog.RootSha256, Spells = [new() { SourceTemplateId = source.Id, TemplateId = source.Id, ImportedArtworkBase64 = Convert.ToBase64String(dds) }] };
        var artOverlay = SpellRules.Generate(catalog, artProject);
        var grayBytes = artOverlay.Single(x => x.File == "GUI/SummonedImages/SpellWorkspace_" + source.Id + "_BW.dds").Data;
        var grayImage = SpellCardView.DecodeImage(grayBytes)!; var grayPixels = Pixels(grayImage); var colorPixels = Pixels(reimported);
        Check(grayImage.PixelWidth == reimported.PixelWidth && grayImage.PixelHeight == reimported.PixelHeight
            && Enumerable.Range(0, grayPixels.Length / 4).All(i => grayPixels[4 * i] == grayPixels[4 * i + 1] && grayPixels[4 * i] == grayPixels[4 * i + 2] && grayPixels[4 * i + 3] == colorPixels[4 * i + 3]),
            "Custom artwork exports the exact _BW DDS requested by unavailable cards, with equal gray channels and unchanged alpha");
        File.WriteAllBytes(Path.Combine(folder, "FireCat_BW.dds"), grayBytes);
        var grayPng = new PngBitmapEncoder(); grayPng.Frames.Add(BitmapFrame.Create(grayImage)); using (var output = File.Create(Path.Combine(folder, "FireCat_BW.png"))) { grayPng.Save(output); }
        var originalIconIndex = source.Template.m_imageIndex;
        var cardOnly = ResourceCatalog.Decode<SpellTemplate>(artOverlay.Single(x => x.File == source.File).Data);
        Check(cardOnly.m_imageIndex == originalIconIndex && !artOverlay.Any(x => x.File.StartsWith("GUI/DeckConfig/")), "Importing card artwork leaves the deck icon unchanged until the separate option is chosen");
        var iconChange = artProject.Spells.Single(); iconChange.UseCardArtworkForDeckIcon = true;
        var iconOverlay = SpellRules.Generate(catalog, artProject);
        var iconSpell = ResourceCatalog.Decode<SpellTemplate>(iconOverlay.Single(x => x.File == source.File).Data);
        var iconAtlas = SpellCardView.DecodeImage(iconOverlay.Single(x => x.File == "GUI/DeckConfig/Spell_Complete_Color21.dds").Data)!;
        var iconTile = new CroppedBitmap(iconAtlas, new System.Windows.Int32Rect(0, 0, 64, 64));
        Check(iconSpell.m_imageIndex == 1280 && Pixels(iconTile).SequenceEqual(Pixels(SpellbookIcons.Preview(catalog, source.Id, iconChange)))
            && iconOverlay.Where(x => x.File.StartsWith("GUI/DeckConfig/")).All(x => x.File.Contains("21.dds")), "Use Card Artwork exports the preview into a fresh 64 by 64 icon slot without replacing any original sheet");
        var grayAtlas = SpellCardView.DecodeImage(iconOverlay.Single(x => x.File == "GUI/DeckConfig/Spell_Complete_BW21.dds").Data)!;
        var grayTile = Pixels(new CroppedBitmap(grayAtlas, new System.Windows.Int32Rect(0, 0, 64, 64)));
        Check(Enumerable.Range(0, grayTile.Length / 4).All(i => grayTile[4 * i] == grayTile[4 * i + 1] && grayTile[4 * i] == grayTile[4 * i + 2]), "Custom deck icons also have a grayscale sprite sheet");
        var bluePixels = new byte[128 * 128 * 4]; for (var i = 0; i < bluePixels.Length; i += 4) { bluePixels[i] = 255; bluePixels[i + 3] = 160; }
        var blueIcon = Convert.ToBase64String(CardArtwork.Encode(BitmapSource.Create(128, 128, 96, 96, PixelFormats.Bgra32, null, bluePixels, 128 * 4)));
        iconChange.UseCardArtworkForDeckIcon = false; iconChange.ImportedDeckIconBase64 = blueIcon;
        var independent = SpellRules.Generate(catalog, artProject);
        var independentPreview = SpellbookIcons.Preview(catalog, source.Id, iconChange); var independentPixels = Pixels(independentPreview); var center = (32 * 64 + 32) * 4;
        Check(independentPreview.PixelWidth == 64 && independentPixels[center] == 255 && independentPixels[center + 1] == 0 && independentPixels[center + 2] == 0 && independentPixels[center + 3] == 160
            && independent.Single(x => x.File == "GUI/SummonedImages/SpellWorkspace_" + source.Id + ".dds").Data.SequenceEqual(dds), "A separate 128 by 128 deck image scales to 64 by 64, retains alpha, and leaves card artwork untouched");
        var iconSibling = SpellRules.Create(catalog, artProject, source, "Independent Icon"); iconSibling.ImportedDeckIconBase64 = blueIcon; artProject.Spells.Add(iconSibling);
        var siblings = SpellRules.Generate(catalog, artProject);
        var firstIndex = ResourceCatalog.Decode<SpellTemplate>(siblings.Single(x => x.File == source.File).Data).m_imageIndex;
        var siblingIndex = ResourceCatalog.Decode<SpellTemplate>(siblings.Single(x => x.File == "Spells/SpellWorkspace/" + iconSibling.TemplateId + ".xml").Data).m_imageIndex;
        Check(firstIndex != siblingIndex && source.Template.m_imageIndex == originalIconIndex, "Spells cloned from the same source receive independent deck icon slots");
        var workerIcons = Task.Run(() => SpellRules.Generate(catalog, artProject)).GetAwaiter().GetResult();
        Check(workerIcons.Single(x => x.File == "GUI/DeckConfig/Spell_Complete_Color21.dds").Data.SequenceEqual(siblings.Single(x => x.File == "GUI/DeckConfig/Spell_Complete_Color21.dds").Data), "Deck icon generation is deterministic on the background build thread");
        var deckVm = new WorkspaceViewModel(new() { ClientExe = catalog.ClientExe }); deckVm.SetCatalog(catalog);
        deckVm.SetDeckIcon(useCardArtwork: true); deckVm.SetDeckIcon(blueIcon); deckVm.Undo();
        Check(deckVm.CurrentModification!.UseCardArtworkForDeckIcon && deckVm.CurrentModification.ImportedDeckIconBase64 is null, "Undo restores the choice to use card artwork for the deck icon");
        deckVm.Redo(); var deckProjectPath = Path.Combine(folder, "deck-icon.spellproject"); ProjectFiles.SaveJson(deckProjectPath, deckVm.Project);
        Check(ProjectFiles.LoadProject(deckProjectPath).Spells.Single().ImportedDeckIconBase64 == blueIcon && !deckVm.CurrentModification!.UseCardArtworkForDeckIcon, "A separately selected deck image survives redo, saving and reopening");
        deckVm.SetDeckIcon(); var originalDeck = SpellRules.Generate(catalog, deckVm.Project);
        Check(ResourceCatalog.Decode<SpellTemplate>(originalDeck.Single(x => x.File == source.File).Data).m_imageIndex == originalIconIndex && !originalDeck.Any(x => x.File.StartsWith("GUI/DeckConfig/")), "Use Original Icon removes the override and restores the original atlas slot");
        Check(!JsonSerializer.Serialize(deckVm.CurrentModification, ProjectFiles.JsonOptions).Contains("DeckIcon"), "Unused deck icon options preserve legacy project serialization");
        var iconPng = new PngBitmapEncoder(); iconPng.Frames.Add(BitmapFrame.Create(independentPreview)); using (var output = File.Create(Path.Combine(folder, "deck-icon-64.png"))) { iconPng.Save(output); }
        var vm = new WorkspaceViewModel(new() { ClientExe = catalog.ClientExe }); vm.SetCatalog(catalog);
        Check(!vm.CanLaunch, "Launch buttons remain unavailable before a successful build");
        vm.SetLaunchReady(true); Check(vm.CanLaunch, "A matching completed build enables launch controls");
        vm.Busy = true; Check(!vm.CanLaunch, "Launch controls disable throughout a build");
        vm.Busy = false; vm.SetLaunchReady(false); Check(!vm.CanLaunch, "A canceled or failed build does not enable launch controls");
        vm.Pips = 4; vm.Accuracy = 90; vm.Undo(); Check(vm.Pips == 4 && vm.Accuracy == source.Accuracy, "Undo restores the selected spell's prior fields"); vm.Redo(); Check(vm.Accuracy == 90, "Redo restores the edit");
        vm.Search = source.Id.ToString(); Check(vm.Library.Any(x => x.Id == source.Id), "Library resolves actual template IDs");
        vm.SpellName = "Renamed Fire Cat";
        Check(vm.SelectedItem!.Name == vm.SpellName, "First rename updates the library row without losing selection");
        vm.DamageMode = "Fixed"; vm.FixedDamage = 100000;
        ProjectFiles.SaveJson(Path.Combine(folder, "fixed-damage.json"), new { vm.DamageMode, vm.DescriptionPreview,
            OriginalDescription = catalog.English(source.Template.m_description), OriginalTrainer = catalog.English(source.Template.m_descriptionTrainer),
            Values = SpellRules.Leaves(vm.CurrentTemplate!.m_effects).Select(x => x.m_effectParam).ToArray() });
        Check(vm.IsFixedDamage && SpellRules.Leaves(vm.CurrentTemplate!.m_effects).Where(x => x.m_effectType == kSpellEffects.kDamage).All(x => x.m_effectParam == 100000)
            && vm.DescriptionPreview.Contains("100000") && !vm.DescriptionPreview.Contains("100000-100000"), "Fixed damage sets every random outcome and displays one amount");
        var saved = Path.Combine(folder, "fixed.spellproject"); ProjectFiles.SaveJson(saved, vm.Project);
        vm.Project = ProjectFiles.LoadProject(saved); vm.ReloadProject();
        Check(vm.IsFixedDamage && vm.FixedDamage == 100000 && vm.SelectedItem!.Name == "Renamed Fire Cat", "Fixed amount, mode and name survive project save and reload");
        vm.DamageMode = "Range"; vm.Minimum = 80; vm.Maximum = 120;
        Check(vm.IsRangeDamage && vm.Minimum == 80 && vm.Maximum == 120 && SpellRules.Leaves(vm.CurrentTemplate!.m_effects).Count == oldLeaves.Count,
            "Damage range 80 to 120 preserves the original effect structure");
        vm.DamageMode = "Fixed"; vm.FixedDamage = 100000; vm.Undo(); vm.Undo();
        Check(vm.IsRangeDamage && vm.Minimum == 80 && vm.Maximum == 120, "Undo restores range values and range mode together");
        vm.Redo(); vm.Redo(); Check(vm.IsFixedDamage && vm.FixedDamage == 100000, "Redo restores fixed damage and mode together");
        vm.Effects.First(x => x.Type == "Damage").Amount = 90000;
        Check(vm.IsRangeDamage && vm.Minimum == 90000 && vm.Maximum == 100000, "Editing a single effect changes the mode to reflect the actual outcomes");
        vm.Undo(); Check(vm.IsFixedDamage && vm.FixedDamage == 100000, "Undoing a direct effect edit restores fixed mode");
        vm.Description = "Custom damage description";
        var cardOverlay = SpellRules.Generate(catalog, vm.Project);
        var cardTable = LanguageTable.Read(cardOverlay.Single(x => x.File == "Locale/en-US/Spells.lang").Data, "Spells");
        var exported = ResourceCatalog.Decode<SpellTemplate>(cardOverlay.Single(x => x.File == source.File).Data);
        string Lookup(string key) => cardTable.Values[key.Split('_')[1]];
        Check(Lookup(exported.m_displayName) == vm.SpellName && Lookup(exported.m_description) == DescriptionIcons.Convert(catalog, vm.Description)
            && exported.m_description == exported.m_descriptionTrainer && exported.m_description == exported.m_descriptionCombatHUD,
            "Built spell name and all card descriptions resolve from actual exported language data");
        vm.MarkSaved(); Check(!vm.HasUnsavedChanges, "Explicit save marks the current card edits as saved");
        vm.Description = "100000 FIRE DAMAGE, then HEAL. Wildfire remains text. $Damage_image$";
        Check(vm.HasUnsavedChanges && vm.Description.Contains("FIRE DAMAGE"), "Typing updates the preview but leaves the description editable and unsaved");
        var iconText = SpellRules.DescriptionText(catalog, vm.CurrentModification!, vm.CurrentTemplate!)!;
        Check(iconText.Contains("#1:$FireSchoolIcon$ #2:$Damage_image$") && iconText.Contains("#3:$Heal_image$") && iconText.Contains("Wildfire remains text.")
            && DescriptionIcons.Convert(catalog, iconText) == iconText, "Icon keywords match whole words and preserve existing tokens without double conversion");
        Check(Imlight.CoreLib.Game.Quests.Management.SpellCardText.Render(vm.CurrentTemplate!).Markup.Contains("{icon:"), "Workspace description preview renders the keyword icons");
        var keywordOverlay = SpellRules.Generate(catalog, vm.Project);
        var keywordTemplate = ResourceCatalog.Decode<SpellTemplate>(keywordOverlay.Single(x => x.File == source.File).Data);
        var keywordTable = LanguageTable.Read(keywordOverlay.Single(x => x.File == "Locale/en-US/Spells.lang").Data, "Spells");
        Check(keywordTable.Values[keywordTemplate.m_description.ToString().Split('_')[1]] == iconText, "The built game description uses the same icon tokens as the workspace preview");
        var startupFile = Path.Combine(folder, "startup.spellproject"); ProjectFiles.SaveJson(startupFile, vm.Project);
        var startup = new WorkspaceViewModel(new() { ClientExe = catalog.ClientExe }) { Project = ProjectFiles.LoadProject(startupFile) };
        startup.ReloadProject();
        var firstPreview = "";
        startup.PreviewChanged += () => {
            if (firstPreview.Length == 0 && startup.CurrentModification?.TemplateId == source.Id && startup.CurrentTemplate is not null) {
                firstPreview = Imlight.CoreLib.Game.Quests.Management.SpellCardText.Render(startup.CurrentTemplate).Markup;
            }
        };
        startup.SetCatalog(catalog);
        Check(firstPreview.Contains("100000") && firstPreview.Contains("{icon:") && startup.DescriptionPreview.Contains("Wildfire remains text."),
            "A project loaded before indexing renders its saved description and keyword icons on the first preview event without an edit");
        Check(!startup.HasUnsavedChanges && startup.Description == vm.Description, "Startup description preparation preserves the saved project and manual-save state");
        var combinations = new WorkspaceViewModel(new() { ClientExe = catalog.ClientExe }); combinations.SetCatalog(catalog);
        combinations.DamageMode = "Fixed"; combinations.FixedDamage = 100;
        combinations.SecondaryKind = "Shield"; combinations.SecondarySchool = "Ice"; combinations.SecondaryAmount = 50;
        var combination = combinations.CurrentTemplate!;
        var combinedLeaves = SpellRules.Leaves(combination.m_effects);
        Check(combination.m_effects.Count == source.Template.m_effects.Count + 1 && combination.m_effects[0].GetType() == source.Template.m_effects[0].GetType()
            && combinedLeaves.Take(oldLeaves.Count).All(x => x.m_effectParam == 100)
            && combination.m_effects.Last().m_effectType == kSpellEffects.kModifyIncomingDamage && combination.m_effects.Last().m_effectParam == -50
            && combination.m_effects.Last().m_effectTarget == kEffectTarget.kSelf && combination.m_effects.Last().m_sDamageType == "Ice",
            "Fire Cat damage stays in its original random group and a caster shield runs afterward outside that group");
        combinations.FixedDamage = 200; combinations.SpellSchool = "Storm";
        Check(combinations.SecondaryAmount == 50 && combinations.SecondarySchool == "Ice" && combinations.Target == "One Enemy",
            "Editing primary damage, school and target does not overwrite the secondary shield");
        Check(combinations.DescriptionPreview.Contains("Then") && combinations.DescriptionPreview.Contains("50%") && combinations.Description.Contains("Then")
            && combinations.Description.Contains("50%") && combinations.CurrentModification!.Description is null,
            "The textbox and card show the secondary line while the original main description remains independent");
        var secondaryLine = SecondaryEffects.Description(combinations.CurrentModification!.SecondaryEffect!);
        combinations.Description = "200 FIRE DAMAGE\nA note\n" + secondaryLine;
        combinations.Description = combinations.Description.Replace("A note", "A note!");
        Check(combinations.CurrentModification!.Description == "200 FIRE DAMAGE\nA note!" && combinations.CurrentModification.SecondaryEffect!.Description is null
            && combinations.Description.Split("Then ").Length == 2 && combinations.DescriptionPreview.Split("Then ").Length == 2,
            "Editing a multiline main description keeps one generated secondary line in both textbox and card");
        combinations.Description = combinations.Description.Replace("A note!", "A note! ");
        Check(combinations.Description.Contains("A note! \n"), "Typing a space before the secondary line preserves the pending main text");
        combinations.Description = combinations.CurrentModification!.Description + "\nThen custom ICE SHIELD";
        Check(combinations.CurrentModification.SecondaryEffect!.Description == "Then custom ICE SHIELD" && combinations.DescriptionPreview.Contains("Then custom Ice Shield")
            && combinations.CurrentTemplate!.m_effects.Last().m_effectParam == -50,
            "Manual secondary wording updates the card icons without changing the real effect");
        var textFile = Path.Combine(folder, "combined-text.spellproject"); ProjectFiles.SaveJson(textFile, combinations.Project);
        var textReload = new WorkspaceViewModel(new() { ClientExe = catalog.ClientExe }) { Project = ProjectFiles.LoadProject(textFile) };
        textReload.ReloadProject(); textReload.SetCatalog(catalog);
        Check(textReload.Description == combinations.Description && textReload.Description.Contains("Then custom ICE SHIELD")
            && textReload.DescriptionPreview.Split("Then ").Length == 2, "Full description wording survives project reopen without duplicating the secondary effect");
        var textOverlay = SpellRules.Generate(catalog, textReload.Project);
        var textTemplate = ResourceCatalog.Decode<SpellTemplate>(textOverlay.Single(x => x.File == source.File).Data);
        var textLocale = LanguageTable.Read(textOverlay.Single(x => x.File == "Locale/en-US/Spells.lang").Data, "Spells");
        var builtText = textLocale.Values[textTemplate.m_description.ToString().Split('_')[1]];
        Check(builtText.Split("Then ").Length == 2 && builtText.Contains("Then custom") && builtText.Contains("$IceSchoolIcon$") && builtText.Contains("$Shield_image$"),
            "The built game description includes the edited secondary wording and icons exactly once");
        combinations.ReloadProject(false);
        combinations.SecondaryAmount = 40;
        Check(combinations.CurrentModification!.SecondaryEffect!.Description is null && combinations.Description.Contains("40%") && !combinations.Description.Contains("Then custom")
            && combinations.CurrentModification.Description!.Contains("A note!"), "Changing an effect control refreshes its line while preserving custom primary wording");
        combinations.Undo(); Check(combinations.Description.Contains("Then custom ICE SHIELD"), "Undo restores manually edited secondary wording");
        combinations.Redo(); Check(combinations.Description.Contains("40%"), "Redo restores the refreshed secondary description");
        combinations.Description = "";
        Check(combinations.Description == "" && combinations.DescriptionPreview == "", "Clearing the full description does not silently append the secondary text again");
        combinations.SecondaryAmount = 50;
        combinations.Description = "200 FIRE DAMAGE\n" + SecondaryEffects.Description(combinations.CurrentModification!.SecondaryEffect!);
        foreach (var kind in SecondaryEffects.Kinds.Where(x => x != "None")) {
            combinations.SecondaryKind = kind;
            var encoded = ResourceCatalog.Encode(combinations.CurrentTemplate!);
            var decoded = ResourceCatalog.Decode<SpellTemplate>(encoded);
            Check(decoded.m_effects.Last().m_effectType == SecondaryEffects.Type(kind)
                && (!SecondaryEffects.OverTime(kind) || decoded.m_effects.Last().m_numRounds == 3), kind + " secondary effect survives the real template encoding");
        }
        combinations.SecondaryKind = "Blade"; combinations.SecondarySchool = "All"; combinations.SecondaryAmount = 25;
        Check(SecondaryEffects.Description(combinations.CurrentModification!.SecondaryEffect!) == "Then +25% to next DAMAGE spell on yourself",
            "Universal blade text describes the next damage spell without an all-schools or blade icon label");
        var allBlade = SpellRules.DescriptionText(catalog, combinations.CurrentModification!, combinations.CurrentTemplate!)!;
        Check(allBlade.Contains("+25% to next") && allBlade.Contains("$Damage_image$") && !allBlade.Contains("$Blade_image$") && !allBlade.Contains("all schools"),
            "Universal blade preview uses the damage icon shown on real game cards");
        combinations.SecondarySchool = "Fire"; combinations.SecondaryAmount = 35;
        var bladeOverlay = SpellRules.Generate(catalog, combinations.Project);
        var bladeTemplate = ResourceCatalog.Decode<SpellTemplate>(bladeOverlay.Single(x => x.File == source.File).Data);
        var bladeTable = LanguageTable.Read(bladeOverlay.Single(x => x.File == "Locale/en-US/Spells.lang").Data, "Spells");
        var bladeText = bladeTable.Values[bladeTemplate.m_description.ToString().Split('_')[1]];
        ProjectFiles.SaveJson(Path.Combine(folder, "blade-description.json"), new { Exported = bladeText, Preview = SpellRules.DescriptionText(catalog, combinations.CurrentModification!, combinations.CurrentTemplate!), Readable = SecondaryEffects.Description(combinations.CurrentModification!.SecondaryEffect!) });
        Check(bladeText.Contains("+35% to next") && bladeText.Contains("$FireSchoolIcon$") && bladeText.Contains("$Damage_image$") && !bladeText.Contains("$Blade_image$")
            && bladeText.Replace("\\n", "\n") == SpellRules.DescriptionText(catalog, combinations.CurrentModification!, combinations.CurrentTemplate!),
            "School blade exports the same school and damage icons as the workspace description");
        Check(bladeTemplate.m_effects.Last().m_effectType == kSpellEffects.kModifyOutgoingDamage && bladeTemplate.m_effects.Last().m_effectParam == 35
            && bladeTemplate.m_effects.Last().m_sDamageType == "Fire" && bladeTemplate.m_effects.Last().m_effectTarget == kEffectTarget.kSelf,
            "Blade wording changes preserve the positive outgoing damage modifier, school and caster target");
        foreach (var school in new[] { "All", "Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance" }) {
            combinations.SecondarySchool = school;
            var visualOverlay = SpellRules.Generate(catalog, combinations.Project);
            var visualSpell = ResourceCatalog.Decode<SpellTemplate>(visualOverlay.Single(x => x.File == source.File).Data);
            var visualCinematic = ResourceCatalog.Decode<CinematicTemplate>(visualOverlay.Single(x => x.File == "Cinematics/" + visualSpell.m_spellBase + ".xml").Data);
            var nativeName = school == "All" ? "Balanceblade" : school + "blade";
            var nativeBlade = catalog.Cinematic(nativeName).m_stages.OfType<AddHangingCinematicStageTemplate>().Single();
            var customBlade = visualCinematic.m_stages.OfType<AddHangingCinematicStageTemplate>().Single(x => x.m_name == "AddHanging");
            Check(customBlade.m_hanging == nativeBlade.m_hanging && customBlade.m_sound == nativeBlade.m_sound && customBlade.m_burnSound == nativeBlade.m_burnSound
                && !customBlade.m_hanging.ToString().Contains("DOT") && visualSpell.m_effects.Last().m_effectParam == 35,
                school + " secondary blade uses its original blade model, school colors and sounds instead of the source spell's DOT visual");
            var originalCinematic = catalog.Cinematic(source.Template.m_spellBase);
            Check(!visualCinematic.m_stages.Any(x => (x.m_name.ToString() is "Outgoing" or "OutgoingFast") && x.m_actions.Count == 0 && x.m_duration == 0),
                school + " blade uses the native charm activation fallback instead of an empty outgoing stage");
            Check(visualCinematic.m_stages.Where(x => x is not AddHangingCinematicStageTemplate).Select(x => Convert.ToBase64String(ResourceCatalog.Encode(x)))
                .SequenceEqual(originalCinematic.m_stages.Where(x => x is not AddHangingCinematicStageTemplate
                    && !((x.m_name.ToString() is "Outgoing" or "OutgoingFast") && x.m_actions.Count == 0 && x.m_duration == 0)).Select(x => Convert.ToBase64String(ResourceCatalog.Encode(x)))),
                school + " blade cinematic preserves the original summon, attack and nonempty actions");
        }
        var elf = catalog.Spells.First(x => x.InternalName == "Fire Elf");
        foreach (var kind in SecondaryEffects.Kinds.Where(x => x != "None")) {
            var secondary = SecondaryEffects.Default(kind, "Fire");
            var change = new SpellModification { SourceTemplateId = source.Id, TemplateId = source.Id, SecondaryEffect = secondary };
            var template = SpellRules.Apply(catalog, change);
            var animation = SecondaryEffects.Cinematic(catalog, change, template);
            if (kind == "Heal") {
                Check(animation is null && template.m_effects.Last().m_effectType == kSpellEffects.kHeal, "Immediate secondary healing retains the original attack cinematic");
                continue;
            }
            var consumption = kind is "Blade" or "Weakness" ? new[] { "Outgoing", "OutgoingFast" } : new[] { "Incoming", "IncomingFast" };
            if (SecondaryEffects.Percent(kind)) {
                Check(!animation!.m_stages.Any(x => consumption.Contains(x.m_name.ToString()) && x.m_actions.Count == 0 && x.m_duration == 0 && x.m_stageRelationships.Count == 0),
                    kind + " cannot override native consumption with an empty cinematic stage");
            }
            if (SecondaryEffects.OverTime(kind)) {
                var act = template.m_effects.Last().m_actNum;
                var activation = animation!.m_stages.Single(x => x.m_name == "ActivateHanging" + (act == 0 ? "" : act.ToString()));
                var sound = kind == "Heal over Time (HOT)" ? "spelllife_HOT_heal1.mp3" : "spellfire_DOT_attack1.mp3";
                Check(activation.m_actions.OfType<ActivateHangingEffectCinematicAction>().Any(x => x.m_activate)
                    && activation.m_actions.OfType<ActivateHangingEffectCinematicAction>().Any(x => !x.m_activate)
                    && activation.m_actions.OfType<PlaySoundCinematicAction>().Any(x => x.m_sound.Any(s => s.ToString().EndsWith(sound, StringComparison.Ordinal))),
                    kind + " exports its original tick activation and sound instead of inheriting the source attack's tick");
            }
            Check(animation!.m_stages.Where(x => x is SummonCinematicStageTemplate or ActCinematicStageTemplate)
                .Select(x => Convert.ToBase64String(ResourceCatalog.Encode(x)))
                .SequenceEqual(catalog.Cinematic(source.Template.m_spellBase).m_stages.Where(x => x is SummonCinematicStageTemplate or ActCinematicStageTemplate)
                    .Select(x => Convert.ToBase64String(ResourceCatalog.Encode(x)))), kind + " preserves the primary attack's summon and damage animations");
        }
        var hotOnDot = new SpellModification { SourceTemplateId = elf.Id, TemplateId = elf.Id,
            SecondaryEffect = new() { Kind = "Heal over Time (HOT)", School = "Life", Target = "Caster", Amount = 90, Rounds = 3 } };
        var combinedTicks = SpellRules.Apply(catalog, hotOnDot);
        var combinedAnimation = SecondaryEffects.Cinematic(catalog, hotOnDot, combinedTicks)!;
        var nativeTick = catalog.Cinematic(elf.Template.m_spellBase).m_stages.Single(x => x.m_name == "ActivateHanging");
        Check(ResourceCatalog.Encode(combinedAnimation.m_stages.Single(x => x.m_name == "ActivateHanging")).SequenceEqual(ResourceCatalog.Encode(nativeTick))
            && combinedAnimation.m_stages.Any(x => x.m_name == "ActivateHanging1" && x.m_actions.OfType<PlaySoundCinematicAction>().Any(a => a.m_sound.Any(s => s.ToString().Contains("HOT_heal")))),
            "A native DOT and a secondary HOT retain separate tick stages and their own activation sounds");
        var elfChange = new SpellModification { SourceTemplateId = elf.Id, TemplateId = elf.Id, SecondaryEffect = new() { Kind = "Blade", School = "Fire" } };
        var elfSpell = SpellRules.Apply(catalog, elfChange); var elfCinematic = SecondaryEffects.Cinematic(catalog, elfChange, elfSpell)!;
        Check(elfSpell.m_effects.Last().m_actNum != SpellRules.Leaves(elf.Template.m_effects).Single(x => x.m_effectType == kSpellEffects.kDamageOverTime).m_actNum
            && elfCinematic.m_stages.OfType<AddHangingCinematicStageTemplate>().Any(x => x.m_name == "AddHanging" && x.m_hanging.ToString().Contains("DOT"))
            && elfCinematic.m_stages.OfType<AddHangingCinematicStageTemplate>().Any(x => x.m_name == "AddHanging1" && x.m_hanging.ToString().Contains("Blade")),
            "Adding a blade to a native DOT spell gives it a separate stage and preserves the native DOT visual");
        combinations.SecondaryKind = "Heal"; combinations.SecondaryAmount = 75; combinations.FixedDamage = 100;
        Check(combinations.FixedDamage == 100 && combinations.SecondaryAmount == 75 && combinations.CurrentTemplate!.m_effects.Last().m_effectParam == 75,
            "Primary fixed damage stays independent from secondary healing");
        var comboFile = Path.Combine(folder, "secondary.spellproject"); ProjectFiles.SaveJson(comboFile, combinations.Project);
        var loadedCombo = ProjectFiles.LoadProject(comboFile);
        Check(loadedCombo.Spells.Single().SecondaryEffect?.Amount == 75, "Secondary effects survive project save and reopen");
        combinations.SecondaryKind = "None"; combinations.Undo();
        Check(combinations.SecondaryKind == "Heal", "Undo restores a removed secondary effect");
        combinations.Redo(); Check(combinations.CurrentTemplate!.m_effects.Count == source.Template.m_effects.Count && combinations.FixedDamage == 100,
            "Removing a secondary effect restores the primary spell without changing its damage");
        combinations.SecondaryKind = "Shield"; combinations.SecondarySchool = "Ice";
        var comboOverlay = SpellRules.Generate(catalog, combinations.Project);
        var exportedCombo = ResourceCatalog.Decode<SpellTemplate>(comboOverlay.Single(x => x.File == source.File).Data);
        var comboText = LanguageTable.Read(comboOverlay.Single(x => x.File == "Locale/en-US/Spells.lang").Data, "Spells").Values[exportedCombo.m_description.ToString().Split('_')[1]];
        Check(exportedCombo.m_effects.Last().m_effectType == kSpellEffects.kModifyIncomingDamage && exportedCombo.m_effects.Last().m_effectParam == -50
            && comboText.Contains("Then") && comboText.Contains("$Shield_image$"), "The real overlay includes the secondary effect and its game icon description together");
        var invalidSecondary = JsonSerializer.Deserialize<SpellProject>(JsonSerializer.Serialize(combinations.Project, ProjectFiles.JsonOptions), ProjectFiles.JsonOptions)!;
        invalidSecondary.Spells.Single().SecondaryEffect = new() { Kind = "Damage over Time (DOT)", School = "Fire", Rounds = 0 };
        Check(SpellRules.Validate(catalog, invalidSecondary).Any(x => x.Contains("invalid secondary")), "A zero-round DOT is rejected before the server can divide by zero");
        var nativeExample = new SpellModification { SourceTemplateId = 1365718327, TemplateId = 1365718327 };
        var nativeProject = new SpellProject { RootSha256 = catalog.RootSha256, Spells = [nativeExample] };
        var nativeVm = new WorkspaceViewModel(new() { ClientExe = catalog.ClientExe }) { Project = nativeProject }; nativeVm.SetCatalog(catalog);
        nativeVm.ShowVariants = true; nativeVm.Search = "1365718327"; nativeVm.SelectedItem = nativeVm.Library.Single();
        var nativeExtras = SpellRules.Leaves(nativeVm.CurrentTemplate!.m_effects).Skip(1).Select(x => x.m_effectParam).ToArray();
        nativeVm.FixedDamage = 100;
        Check(SpellRules.Leaves(nativeVm.CurrentTemplate!.m_effects).Skip(1).Select(x => x.m_effectParam).SequenceEqual(nativeExtras),
            "Editing Brulee Sucre's primary damage preserves its native DOT, heal and HOT amounts");
        var audioFolder = AudioProviders.Export(catalog, folder);
        Check(Directory.GetFiles(audioFolder).All(x => File.ReadAllBytes(x).SequenceEqual(catalog.Read("_Shared-WorldData", "Sound/Miles/" + Path.GetFileName(x)))), "Audio providers are copied exactly from the selected client's read-only resources");
        foreach (var extension in new[] { ".mp3", ".ogg" }) {
            var sample = catalog.GetArchive("_Shared-WorldData").Files.First(x => x.Key.StartsWith("Sound/", StringComparison.Ordinal) && x.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                && x.Value.Value.UncompressedSize is > 0 and < 200000).Key;
            File.WriteAllBytes(Path.Combine(folder, "sample" + extension), catalog.Read("_Shared-WorldData", sample));
        }
        var package = Path.Combine(folder, "Package"); var target = Path.Combine(folder, "Installed"); var backups = Path.Combine(folder, "Backups"); Directory.CreateDirectory(package); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "server.dll"), "original"); File.WriteAllText(Path.Combine(target, "unrelated.txt"), "preserve");
        File.WriteAllText(Path.Combine(package, "server.dll"), "modified"); ProjectFiles.SaveJson(Path.Combine(package, "BuildManifest.json"), new { BuildId = "verification" }); ProjectFiles.SaveJson(Path.Combine(package, "spell_manifest.json"), new SpellProject());
        var snapshot = ServerSnapshots.Install(package, target, backups); ServerSnapshots.Restore(snapshot);
        Check(File.ReadAllText(Path.Combine(target, "server.dll")) == "original" && File.ReadAllText(Path.Combine(target, "unrelated.txt")) == "preserve", "Server snapshot restores changed files and preserves unrelated data");
        ServerSnapshots.Install(package, target, backups); File.WriteAllText(Path.Combine(target, "server.dll"), "external edit");
        try { ServerSnapshots.Restore(snapshot); throw new Exception("Conflicting restore unexpectedly succeeded."); } catch (IOException) { Check(File.ReadAllText(Path.Combine(target, "server.dll")) == "external edit", "Restore conflict preserves newer external changes"); }
        Check(catalog.RootSha256 == SpellRules.Hash(Path.Combine(catalog.GameData, "Root.wad")), "Clean Root.wad remains byte-for-byte unchanged");
        var publishFolder = Path.Combine(folder, "Publish"); Directory.CreateDirectory(publishFolder);
        var one = Path.Combine(publishFolder, "one.json"); var two = Path.Combine(publishFolder, "two.json");
        File.WriteAllText(one, "old one"); File.WriteAllText(two, "old two");
        var update = new BuildUpdates(); update.Text(one, "new one"); update.Text(two, "new two");
        using (var locked = File.Open(two, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            try { update.Publish(CancellationToken.None); throw new InvalidOperationException("Locked publish unexpectedly succeeded."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Check(File.ReadAllText(one) == "old one" && File.ReadAllText(two) == "old two", "A failed multi-file publish restores the previous matching data"); }
        }
        using (var canceled = new CancellationTokenSource()) {
            canceled.Cancel();
            try { update.Publish(canceled.Token); throw new InvalidOperationException("Canceled publish unexpectedly succeeded."); }
            catch (OperationCanceledException) { Check(File.ReadAllText(one) == "old one", "Canceling before publication preserves the ready data"); }
        }
        update.Publish(CancellationToken.None);
        var savedTime = File.GetLastWriteTimeUtc(one); update.Publish(CancellationToken.None);
        Check(File.ReadAllText(one) == "new one" && File.ReadAllText(two) == "new two" && File.GetLastWriteTimeUtc(one) == savedTime,
            "Successful publication updates changed data and skips already identical files");
        var cacheSettings = new WorkspaceSettings { ClientExe = catalog.ClientExe, OutputRoot = Path.Combine(folder, "CacheCheck") };
        var stableProject = new SpellProject { Name = "Stable runtime check" };
        Check(BuildUpdates.ProjectRuntime(cacheSettings, stableProject, "Client", "first") == BuildUpdates.ProjectRuntime(cacheSettings, stableProject, "Client", "second")
            && BuildUpdates.ProjectRuntime(cacheSettings, stableProject, "Server", "first") == BuildUpdates.ProjectRuntime(cacheSettings, stableProject, "Server", "second"), "Code and spell changes keep one client folder and one server folder per project");
        using (var single = new ClientRuntimeCache(cacheSettings, catalog, CancellationToken.None, stableProject)) {
            foreach (var name in new[] { "WizardGraphicalClient.Custom.exe", "SpellWorkspace.SpellMods.dll", "SpellWorkspace.Client.exe" }) { File.WriteAllText(Path.Combine(single.Runtime, name), name); }
            single.Complete();
            File.WriteAllText(Path.Combine(single.Runtime, "spell_overlay.bin"), "updated spell data");
            File.WriteAllText(Path.Combine(single.Runtime, "BuildManifest.json"), "updated manifest");
            Check(single.IsReady(CancellationToken.None), "Spell-data changes reuse project binaries directly without creating a duplicate runtime cache");
            Check(!Directory.Exists(Path.Combine(cacheSettings.OutputRoot, "RuntimeCache")), "Normal project builds keep no shared duplicate compiled client");
        }
        using (var singleServer = new ServerRuntimeCache(definitionSettings, catalog, CancellationToken.None, extracted.Sha256, stableProject)) {
            Directory.CreateDirectory(Path.Combine(singleServer.Runtime, "RavenDBServer"));
            File.WriteAllText(Path.Combine(singleServer.Runtime, "Imlight.Director.exe"), "server fixture");
            File.WriteAllText(Path.Combine(singleServer.Runtime, "RavenDBServer", "Raven.Server.dll"), "engine fixture");
            singleServer.Complete(CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(singleServer.Runtime, "Config"));
            File.WriteAllText(Path.Combine(singleServer.Runtime, "Config", "Imlight.ini"), "changed project configuration");
            Check(singleServer.IsReady(CancellationToken.None), "Project configuration updates reuse the same server binaries without another runtime copy");
        }
        var previous = Path.Combine(Path.GetDirectoryName(BuildUpdates.ProjectRuntime(cacheSettings, stableProject, "Client", ""))!, new string('a', 64));
        Directory.CreateDirectory(previous);
        ProjectFiles.SaveJson(Path.Combine(previous, "BuildManifest.json"), new { Product = "Spell Workspace" });
        ProjectFiles.SaveJson(Path.Combine(previous, "spell_manifest.json"), stableProject);
        File.WriteAllText(Path.Combine(previous, "SpellWorkspace.Client.exe"), "old generated launcher fixture");
        using (var active = File.Open(Path.Combine(previous, "SpellWorkspace.Client.exe"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            BuildUpdates.RemovePreviousBuilds(cacheSettings, stableProject, _ => { });
            Check(Directory.Exists(previous), "Previous generated builds are kept while their executable is in use");
        }
        BuildUpdates.RemovePreviousBuilds(cacheSettings, stableProject, _ => { });
        Check(!Directory.Exists(previous) && Directory.Exists(BuildUpdates.ProjectRuntime(cacheSettings, stableProject, "Client", "")), "After successful replacement, obsolete fingerprint build folders are removed while Current is preserved");
        using (var cached = new ClientRuntimeCache(cacheSettings, catalog, CancellationToken.None)) {
            Check(!cached.IsReady(CancellationToken.None), "A missing client runtime is detected before reuse");
            Directory.CreateDirectory(cached.Runtime);
            foreach (var name in new[] { "WizardGraphicalClient.Custom.exe", "SpellWorkspace.SpellMods.dll", "SpellWorkspace.Client.exe" }) { File.WriteAllText(Path.Combine(cached.Runtime, name), name); }
            cached.Complete(); Check(cached.IsReady(CancellationToken.None), "Complete cached client files pass checksum validation");
            File.WriteAllText(Path.Combine(cached.Runtime, "SpellWorkspace.SpellMods.dll"), "changed");
            Check(!cached.IsReady(CancellationToken.None), "An altered cached DLL is detected instead of reused");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(cached.Runtime)!, "files.json"), "invalid json");
            Check(!cached.IsReady(CancellationToken.None), "A malformed cache record requests a rebuild instead of breaking the build");
        }
        ProjectFiles.SaveJson(reportPath, new { Version = "Beta v0.5", Passed = true, Checks = checks, ArtworkWidth = artwork.PixelWidth, ArtworkHeight = artwork.PixelHeight, Artifacts = folder });
    }
}
