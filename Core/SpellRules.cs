// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;

namespace SpellWorkspace.Core;

internal static class SpellRules {
    internal static readonly string[] Schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Sun", "Moon", "Star", "Shadow"];
    internal static readonly string[] Targets = Enum.GetNames<kEffectTarget>();
    internal static readonly string[] Categories = ["Damage", "Heal", "Blade", "Weakness", "Shield", "Trap", "Prism", "DOT", "HOT", "Dispel", "Pips", "Reshuffle", "Minion", "Utility", "Enchant"];
    internal static bool Prism(SpellEffect effect) => effect.m_effectType.ToString() is "kModifyIncomingDamageType" or "kModifyOutgoingDamageType";
    internal static bool Numeric(SpellEffect effect) => effect.m_effectType is kSpellEffects.kDamage or kSpellEffects.kHeal or kSpellEffects.kStealHealth;
    internal static string EffectKind(SpellEffect effect) => effect.m_effectType switch {
        kSpellEffects.kDamage or kSpellEffects.kDamagePerTotalPipPower or kSpellEffects.kStealHealth => "Damage",
        kSpellEffects.kHeal => "Heal",
        kSpellEffects.kModifyOutgoingDamage => effect.m_effectParam >= 0 ? "Blade" : "Weakness",
        kSpellEffects.kModifyIncomingDamage => effect.m_effectParam < 0 ? "Shield" : "Trap",
        kSpellEffects.kModifyIncomingDamageType => "Prism",
        kSpellEffects.kDamageOverTime => "DOT",
        kSpellEffects.kHealOverTime => "HOT",
        kSpellEffects.kDispel => "Dispel",
        kSpellEffects.kModifyPips => "Pips",
        kSpellEffects.kReshuffle => "Reshuffle",
        kSpellEffects.kSummonCreature or kSpellEffects.kSpawnCreature => "Minion",
        _ => effect.m_effectType.ToString() == "kModifyOutgoingDamageType" ? "Prism" : "Utility"
    };
    internal static string Category(SpellTemplate spell) {
        var effects = Leaves(spell.m_effects);
        var primary = PrimaryIndexes(spell).ToHashSet();
        var kinds = effects.Where((_, index) => primary.Contains(index)).Select(EffectKind).Distinct().ToArray();
        return kinds.Length == 1 && kinds[0] != "Utility" ? kinds[0] : spell.m_sTypeName == "Enchant" ? "Enchant" : "Utility";
    }

    internal static List<SpellEffect> Leaves(IEnumerable<SpellEffect>? effects) {
        var result = new List<SpellEffect>();
        void Collect(SpellEffect? effect, int depth) {
            if (effect is null || depth > 20) { return; }
            var children = effect.GetType().GetProperty("m_effectList")?.GetValue(effect) as IEnumerable<SpellEffect>;
            if (children is not null) { foreach (var child in children) { Collect(child, depth + 1); } return; }
            if (effect is ConditionalSpellEffect conditional) {
                foreach (var child in conditional.m_elements ?? []) { Collect(child?.m_pEffect, depth + 1); }
                return;
            }
            if (effect is TargetCountSpellEffect counted) {
                foreach (var child in counted.m_effectLists ?? []) { Collect(child, depth + 1); }
                return;
            }
            result.Add(effect);
        }
        foreach (var effect in effects ?? []) { Collect(effect, 0); }
        return result;
    }
    internal static List<int> PrimaryIndexes(SpellTemplate source) {
        var offset = 0;
        foreach (var root in source.m_effects ?? []) {
            var leaves = Leaves([root]);
            if (leaves.Any(x => x.m_effectType is kSpellEffects.kDamage or kSpellEffects.kHeal or kSpellEffects.kStealHealth)) {
                return Enumerable.Range(offset, leaves.Count).ToList();
            }
            offset += leaves.Count;
        }
        return Enumerable.Range(0, offset).ToList();
    }

    internal static SpellTemplate Apply(ResourceCatalog catalog, SpellModification modification) {
        if (!catalog.ById.TryGetValue(modification.SourceTemplateId, out var source)) { throw new InvalidDataException($"Source spell {modification.SourceTemplateId} is missing."); }
        var spell = ResourceCatalog.Clone(source.Template);
        if (modification.IsNew) {
            // A new learned card is independent of its source's upgrade tree.
            // Remove the upgrade-tree type as well as its links and grouping tags.
            if (spell is TieredSpellTemplate) {
                var standalone = new SpellTemplate();
                foreach (var property in typeof(SpellTemplate).GetProperties().Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)) {
                    property.SetValue(standalone, property.GetValue(spell));
                }
                spell = standalone;
            }
            spell.m_name = CustomIds.ClientName(modification.InternalName, modification.TemplateId);
            spell.m_previousSpellName = "";
            spell.m_purchaseRequirements = null;
            spell.m_displayRequirements = null;
            spell.m_spellFusion = 0;
            spell.m_adjectives = spell.m_adjectives.Where(adjective => !adjective.ToString().StartsWith("Fusion_", StringComparison.Ordinal)).ToList();
        }
        if (modification.DisplayName is not null) { spell.m_displayName = "SpellWorkspace_Name" + modification.TemplateId; }
        if (modification.School is not null) { spell.m_sMagicSchoolName = modification.School; }
        if (modification.Pips is not null) { spell.m_spellRank.m_spellRank = checked((byte)modification.Pips.Value); }
        if (modification.ShadowPips is not null) { spell.m_spellRank.m_shadowPips = checked((byte)modification.ShadowPips.Value); }
        if (modification.XPips is not null) { spell.m_spellRank.m_xPipSpell = modification.XPips.Value; }
        if (modification.SchoolPips is not null) {
            foreach (var pair in modification.SchoolPips) {
                var property = typeof(SpellRank).GetProperty("m_" + pair.Key.ToLowerInvariant() + "Pips") ?? throw new InvalidDataException("Unknown school-pip cost.");
                property.SetValue(spell.m_spellRank, pair.Value);
            }
        }
        if (modification.Accuracy is not null) { spell.m_accuracy = modification.Accuracy.Value; }
        if (modification.Artwork is not null) { spell.m_imageName = Path.GetFileName(modification.Artwork); }
        if (modification.ImportedArtworkBase64 is not null) { spell.m_imageName = "SpellWorkspace_" + modification.TemplateId + ".dds"; }
        if (modification.Treasure is not null) { spell.m_Treasure = modification.Treasure.Value; }
        if (modification.Level is not null) { spell.m_levelRestriction = modification.Level.Value; }
        if (modification.AnimationTemplateId is not null) { spell.m_spellBase = catalog.ById[modification.AnimationTemplateId.Value].Template.m_spellBase; }
        var leaves = Leaves(spell.m_effects);
        var primaryIndexes = PrimaryIndexes(source.Template).ToHashSet();
        foreach (var change in modification.Effects) {
            if (change.Index < 0 || change.Index >= leaves.Count) { throw new InvalidDataException("An effect no longer exists in the source template."); }
            var effect = leaves[change.Index];
            if (change.Amount is not null) { effect.m_effectParam = change.Amount.Value; }
            if (change.Rounds is not null) { effect.m_numRounds = change.Rounds.Value; }
            if (change.Target is not null) { effect.m_effectTarget = Enum.Parse<kEffectTarget>(change.Target); }
            if (change.School is not null || modification.School is not null && primaryIndexes.Contains(change.Index)) {
                effect.m_sDamageType = change.School ?? modification.School;
                effect.m_damageType = 0;
            }
        }
        if (modification.School is not null) {
            foreach (var effect in leaves.Where((e, i) => primaryIndexes.Contains(i) && !string.IsNullOrEmpty(e.m_sDamageType))) {
                effect.m_sDamageType = modification.School;
                effect.m_damageType = 0;
            }
        }
        if (modification.SecondaryEffect is not null) { spell.m_effects.Add(SecondaryEffects.Create(catalog, spell, modification.SecondaryEffect)); }
        if (DescriptionText(catalog, modification, spell) is not null) {
            spell.m_description = "SpellWorkspace_Description" + modification.TemplateId;
            spell.m_descriptionCombatHUD = spell.m_description;
            spell.m_descriptionTrainer = spell.m_description;
        }
        return spell;
    }

    internal static string? DescriptionText(ResourceCatalog catalog, SpellModification modification, SpellTemplate spell) {
        var main = BaseDescriptionText(catalog, modification, spell);
        if (modification.SecondaryEffect is null) { return main; }
        return DescriptionIcons.Convert(catalog, SecondaryEffects.CombineDescription(main ?? OriginalDescription(catalog, modification), SecondaryEffects.Description(modification.SecondaryEffect)));
    }
    internal static string OriginalDescription(ResourceCatalog catalog, SpellModification modification) {
        var source = catalog.ById[modification.SourceTemplateId].Template;
        return new[] { source.m_description, source.m_descriptionTrainer, source.m_descriptionCombatHUD, source.m_advancedDescription }
            .Select(x => catalog.English(x)).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
    }
    internal static string? BaseDescriptionText(ResourceCatalog catalog, SpellModification modification, SpellTemplate spell) {
        if (modification.Description is not null) { return DescriptionIcons.Convert(catalog, modification.Description); }
        var sourceEffects = Leaves(catalog.ById[modification.SourceTemplateId].Template.m_effects);
        if (sourceEffects.Count > 0 && sourceEffects.All(Prism) && (modification.School is not null
            || modification.Effects.Any(x => x.Amount is not null || x.School is not null))) {
            return DescriptionIcons.Convert(catalog, string.Join("\n", Leaves(spell.m_effects).Take(sourceEffects.Count).Select(effect => {
                var destination = Schools.FirstOrDefault(x => StringHash.Compute(x) == unchecked((uint)effect.m_effectParam));
                return "Convert " + effect.m_sDamageType.ToString().ToUpperInvariant() + " DAMAGE to " + (destination ?? "unknown school").ToUpperInvariant() + " DAMAGE";
            })));
        }
        if (modification.RangeDamage == true) { return null; }
        var primaryIndexes = PrimaryIndexes(catalog.ById[modification.SourceTemplateId].Template).ToHashSet();
        if (modification.RangeDamage is null && (!modification.Effects.Any(x => x.Amount is not null && primaryIndexes.Contains(x.Index))
            || Leaves(spell.m_effects).Where((x, i) => primaryIndexes.Contains(i) && x.m_effectType is kSpellEffects.kDamage or kSpellEffects.kHeal or kSpellEffects.kStealHealth)
                .Select(x => x.m_effectParam).Distinct().Count() != 1)) { return null; }
        var original = OriginalDescription(catalog, modification);
        var leaves = Leaves(spell.m_effects);
        var collapsed = Regex.Replace(original, @"(\$eA(\d*)\$)\s*[-–]\s*(?:#-?\d+:)?\$eA(\d*)\$", match => {
            var first = match.Groups[2].Value.Length == 0 ? 1 : int.Parse(match.Groups[2].Value);
            var last = match.Groups[3].Value.Length == 0 ? 1 : int.Parse(match.Groups[3].Value);
            return first >= 1 && last >= 1 && first <= leaves.Count && last <= leaves.Count
                && leaves[first - 1].m_effectParam == leaves[last - 1].m_effectParam ? match.Groups[1].Value : match.Value;
        });
        return collapsed == original ? null : collapsed;
    }

    internal static List<string> Validate(ResourceCatalog catalog, SpellProject project) {
        var errors = new List<string>();
        if (project.RootSha256.Length > 0 && !project.RootSha256.Equals(catalog.RootSha256, StringComparison.OrdinalIgnoreCase)) { errors.Add("This project was made for different base resources. Use its original clean installation or start a new project."); }
        var originalIds = catalog.Manifest.m_serializedTemplates.Select(x => x.m_id).ToHashSet();
        var ids = new HashSet<uint>();
        var hashes = catalog.Spells.Select(x => StringHash.Compute(x.InternalName)).ToHashSet();
        foreach (var mod in project.Spells.Where(x => x.Enabled)) {
            if (!ids.Add(mod.TemplateId)) { errors.Add($"Duplicate template ID: {mod.TemplateId}."); }
            if (!catalog.ById.TryGetValue(mod.SourceTemplateId, out var source)) { errors.Add($"Source spell {mod.SourceTemplateId} not found."); continue; }
            if (mod.IsNew && (originalIds.Contains(mod.TemplateId) || string.IsNullOrWhiteSpace(mod.InternalName) || !hashes.Add(StringHash.Compute(CustomIds.ClientName(mod.InternalName, mod.TemplateId))))) {
                errors.Add($"Custom spell {mod.TemplateId} has a conflicting template ID or internal-name hash.");
            }
            if (!mod.IsNew && mod.TemplateId != mod.SourceTemplateId) { errors.Add("An override must keep its original template ID."); }
            if (mod.Pips is < 0 or > 14 || mod.ShadowPips is < 0 or > 5 || mod.Accuracy is < 0 or > 100) { errors.Add($"Spell {mod.TemplateId}: invalid pip cost or accuracy."); }
            if (mod.School is not null && !Schools.Contains(mod.School)) { errors.Add($"Spell {mod.TemplateId}: unsupported school."); }
            if (mod.SchoolPips?.Any(x => x.Value > 14 || typeof(SpellRank).GetProperty("m_" + x.Key.ToLowerInvariant() + "Pips") is null) == true) { errors.Add($"Spell {mod.TemplateId}: invalid school-pip cost."); }
            if (mod.Artwork is not null && catalog.Texture(mod.Artwork) is null) { errors.Add($"Spell {mod.TemplateId}: card artwork cannot be decoded."); }
            if (mod.SecondaryEffect is { } extra && (!SecondaryEffects.Kinds.Contains(extra.Kind) || extra.Kind == "None" || !SecondaryEffects.Schools.Contains(extra.School)
                || !SecondaryEffects.Targets.Contains(extra.Target) || extra.Amount < 0 || SecondaryEffects.Percent(extra.Kind) && extra.Amount > 100
                || SecondaryEffects.OverTime(extra.Kind) && extra.Rounds is < 1 or > 20)) { errors.Add($"Spell {mod.TemplateId}: invalid secondary effect settings."); }
            else if (mod.SecondaryEffect is not null) {
                try { SecondaryEffects.Cinematic(catalog, mod, Apply(catalog, mod)); }
                catch (InvalidDataException exception) { errors.Add($"Spell {mod.TemplateId}: {exception.Message}"); }
            }
            if (mod.AnimationTemplateId is uint animationId) {
                if (!catalog.ById.TryGetValue(animationId, out var animation)) { errors.Add($"Spell {mod.TemplateId}: animation source missing."); }
                else {
                    var actual = Leaves(Apply(catalog, mod).m_effects.Take(source.Template.m_effects.Count)).Select(x => (x.m_actNum, x.m_effectTarget)).Distinct().ToArray();
                    var supported = Leaves(animation.Template.m_effects).Select(x => (x.m_actNum, x.m_effectTarget)).Distinct().ToHashSet();
                    if (actual.Any(x => !supported.Contains(x))) { errors.Add($"Spell {mod.TemplateId}: this animation does not demonstrate the requested targets/Act stages."); }
                }
            } else if (mod.Effects.Any(x => x.Target is not null && x.Index >= 0 && x.Index < Leaves(source.Template.m_effects).Count
                && x.Target != Leaves(source.Template.m_effects)[x.Index].m_effectTarget.ToString())) {
                errors.Add($"Spell {mod.TemplateId}: choose an animation proven for the changed target mode.");
            }
        }
        return errors;
    }

    internal static SpellModification Create(ResourceCatalog catalog, SpellProject project, SpellEntry source, string name) {
        var taken = catalog.Manifest.m_serializedTemplates.Select(x => x.m_id).Concat(project.Spells.Select(x => x.TemplateId)).ToHashSet();
        var id = CustomIds.Allocate(project, taken);
        var internalName = CustomIds.ClientName("SpellWorkspace_" + project.Id + "_" + id, id);
        return new() { IsNew = true, SourceTemplateId = source.Id, TemplateId = id, InternalName = internalName, DisplayName = name };
    }

    internal static IReadOnlyList<OverlayEntry> Generate(ResourceCatalog catalog, SpellProject project) {
        var errors = Validate(catalog, project);
        if (errors.Count > 0) { throw new InvalidDataException(string.Join(Environment.NewLine, errors)); }
        var entries = new List<OverlayEntry>();
        var locale = LanguageTable.Read(catalog.Read("Root", "Locale/en-US/Spells.lang"), "Spells");
        var originalTextCount = locale.Values.Count;
        LanguageTable? combatLocale = null;
        var manifest = ResourceCatalog.Clone(catalog.Manifest);
        var spellbookIcons = new SpellbookIcons(catalog, project);
        foreach (var mod in project.Spells.Where(x => x.Enabled).OrderBy(x => x.TemplateId)) {
            var spell = Apply(catalog, mod);
            var cinematic = SecondaryEffects.Cinematic(catalog, mod, spell);
            var prisms = Leaves(spell.m_effects).Where(Prism).ToArray();
            if (prisms.Length > 0) {
                cinematic ??= ResourceCatalog.Clone(catalog.Cinematic(spell.m_spellBase));
                combatLocale ??= LanguageTable.Read(catalog.Read("Root", "Locale/en-US/CombatMessages.lang"), "CombatMessages");
                foreach (var prism in prisms) {
                    var stageName = "AddHanging" + (prism.m_actNum == 0 ? "" : prism.m_actNum.ToString());
                    var stage = cinematic.m_stages.OfType<AddHangingCinematicStageTemplate>().FirstOrDefault(x => x.m_name == stageName)
                        ?? throw new InvalidDataException("The selected prism animation is missing " + stageName + ".");
                    var destination = Schools.FirstOrDefault(x => StringHash.Compute(x) == unchecked((uint)prism.m_effectParam))
                        ?? throw new InvalidDataException("The prism's destination school is unsupported.");
                    var announcement = "Convert " + prism.m_sDamageType.ToString().ToUpperInvariant() + " DAMAGE to " + destination.ToUpperInvariant() + " DAMAGE";
                    stage.m_spellText = combatLocale.Add(DescriptionIcons.Convert(catalog, announcement));
                }
                cinematic.m_name = "SpellWorkspace_Cinematic" + mod.TemplateId;
            }
            if (cinematic is not null) {
                spell.m_spellBase = cinematic.m_name;
                entries.Add(new("Root", "Cinematics/" + cinematic.m_name + ".xml", ResourceCatalog.Encode(cinematic)));
            }
            if (mod.DisplayName is not null) { spell.m_displayName = locale.Add(mod.DisplayName.Replace('\n', ' ').Replace('\r', ' ')); }
            if (DescriptionText(catalog, mod, spell) is string description) {
                spell.m_description = locale.Add(description);
                spell.m_descriptionCombatHUD = spell.m_description;
                spell.m_descriptionTrainer = spell.m_description;
            }
            var path = mod.IsNew ? "Spells/SpellWorkspace/" + mod.TemplateId + ".xml" : catalog.ById[mod.SourceTemplateId].File;
            spellbookIcons.Add(mod, spell);
            var encoded = ResourceCatalog.Encode(spell);
            var verification = ResourceCatalog.Decode<SpellTemplate>(encoded);
            if (verification.m_name != spell.m_name || Leaves(verification.m_effects).Count != Leaves(spell.m_effects).Count) { throw new InvalidDataException("Spell round-trip validation failed."); }
            entries.Add(new("Root", path, encoded));
            if (mod.IsNew) { manifest.m_serializedTemplates.Add(new TemplateLocation { m_id = mod.TemplateId, m_filename = path }); }
            if (mod.ImportedArtworkBase64 is not null) {
                var artwork = Convert.FromBase64String(mod.ImportedArtworkBase64);
                entries.Add(new("Root", "GUI/SummonedImages/" + spell.m_imageName, artwork));
                entries.Add(new("Root", "GUI/SummonedImages/SpellWorkspace_" + mod.TemplateId + "_BW.dds", CardArtwork.Grayscale(artwork)));
            }
        }
        if (project.Spells.Any(x => x.Enabled && x.IsNew)) { entries.Add(new("Root", "TemplateManifest.xml", ResourceCatalog.Encode(manifest))); }
        entries.AddRange(spellbookIcons.Entries());
        if (locale.Values.Count > originalTextCount) { entries.Add(new("Root", "Locale/en-US/Spells.lang", locale.Encode())); }
        if (combatLocale is not null) { entries.Add(new("Root", "Locale/en-US/CombatMessages.lang", combatLocale.Encode())); }
        return entries;
    }
    internal static void WriteBundle(string path, IReadOnlyList<OverlayEntry> entries) {
        File.WriteAllBytes(path, Bundle(entries));
    }
    internal static byte[] Bundle(IReadOnlyList<OverlayEntry> entries) {
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream, Encoding.UTF8, true);
        output.Write("SWOV1\0\0\0"u8.ToArray());
        output.Write(entries.Count);
        foreach (var entry in entries) {
            foreach (var text in new[] { entry.Archive, entry.File }) { var bytes = Encoding.UTF8.GetBytes(text); output.Write(bytes.Length); output.Write(bytes); }
            output.Write(entry.Data.Length); output.Write(entry.Data);
        }
        output.Flush(); return stream.ToArray();
    }
    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
}
