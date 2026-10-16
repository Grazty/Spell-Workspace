// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;

namespace SpellWorkspace.Core;

internal static class SecondaryEffects {
    private static readonly ConditionalWeakTable<ResourceCatalog, Dictionary<(kSpellEffects Type, bool Negative), SpellEffect>> Prototypes = new();
    private sealed record HangingVisual(AddHangingCinematicStageTemplate Add, CinematicStageTemplate? Activate);
    private static readonly ConditionalWeakTable<ResourceCatalog, Dictionary<(string Kind, string School), HangingVisual>> Visuals = new();
    internal static readonly string[] Kinds = ["None", "Shield", "Heal", "Heal over Time (HOT)", "Damage over Time (DOT)", "Blade", "Trap", "Weakness"];
    internal static readonly string[] Targets = ["Caster", "Same target as main spell", "Caster's team", "Enemy team"];
    internal static readonly string[] Schools = ["All", .. SpellRules.Schools];
    internal static bool OverTime(string kind) => kind is "Heal over Time (HOT)" or "Damage over Time (DOT)";
    internal static bool Percent(string kind) => kind is "Shield" or "Blade" or "Trap" or "Weakness";
    internal static SecondaryEffectModification Default(string kind, string school) => new() {
        Kind = kind, Target = kind is "Damage over Time (DOT)" or "Trap" or "Weakness" ? "Same target as main spell" : "Caster",
        School = kind is "Heal" or "Heal over Time (HOT)" ? "Life" : Percent(kind) ? "All" : school,
        Amount = kind == "Shield" ? 50 : Percent(kind) ? 25 : OverTime(kind) ? 300 : 100
    };
    internal static kSpellEffects Type(string kind) => kind switch {
        "Shield" or "Trap" => kSpellEffects.kModifyIncomingDamage,
        "Blade" or "Weakness" => kSpellEffects.kModifyOutgoingDamage,
        "Heal" => kSpellEffects.kHeal,
        "Heal over Time (HOT)" => kSpellEffects.kHealOverTime,
        "Damage over Time (DOT)" => kSpellEffects.kDamageOverTime,
        _ => throw new InvalidDataException("Unknown secondary effect: " + kind)
    };
    internal static SpellEffect Create(ResourceCatalog catalog, SpellTemplate primary, SecondaryEffectModification change) {
        var type = Type(change.Kind);
        var negative = change.Kind is "Shield" or "Weakness";
        var cache = Prototypes.GetOrCreateValue(catalog);
        SpellEffect prototype;
        lock (cache) {
            if (!cache.TryGetValue((type, negative), out prototype!)) {
                var preferred = new uint[] { 1365718327, 311473984, 1252606757 }.Where(catalog.ById.ContainsKey).Select(x => catalog.ById[x]);
                prototype = preferred.Concat(catalog.Spells).SelectMany(x => SpellRules.Leaves(x.Template.m_effects))
                    .FirstOrDefault(x => x.GetType() == typeof(SpellEffect) && x.m_effectType == type && (Percent(change.Kind) ? (x.m_effectParam < 0) == negative : x.m_effectParam >= 0))
                    ?? throw new InvalidDataException("The selected client has no supported prototype for " + change.Kind + ".");
                cache.Add((type, negative), prototype);
            }
        }
        var effect = ResourceCatalog.Clone(prototype);
        effect.m_effectParam = negative ? -change.Amount : change.Amount;
        effect.m_numRounds = OverTime(change.Kind) ? change.Rounds : 0;
        effect.m_sDamageType = change.School;
        // Match original resource effects: the client initializes this runtime cache.
        effect.m_damageType = 0;
        var hangingActs = SpellRules.Leaves(primary.m_effects).Where(x => Hanging(x.m_effectType)).Select(x => x.m_actNum).ToHashSet();
        effect.m_actNum = 0;
        while (hangingActs.Contains(effect.m_actNum)) { effect.m_actNum++; }
        effect.m_effectTarget = change.Target switch {
            "Caster" => kEffectTarget.kSelf,
            "Caster's team" => kEffectTarget.kFriendlyTeam,
            "Enemy team" => kEffectTarget.kEnemyTeam,
            "Same target as main spell" => SpellRules.Leaves(primary.m_effects).Where((_, index) => SpellRules.PrimaryIndexes(primary).Contains(index)).FirstOrDefault()?.m_effectTarget ?? kEffectTarget.kEnemySingle,
            _ => throw new InvalidDataException("Unsupported secondary target.")
        };
        return effect;
    }
    internal static bool Hanging(kSpellEffects type) => type is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyOutgoingDamage
        or kSpellEffects.kDamageOverTime or kSpellEffects.kHealOverTime;

    private static HangingVisual Visual(ResourceCatalog catalog, SecondaryEffectModification change) {
        var cache = Visuals.GetOrCreateValue(catalog);
        lock (cache) {
            if (cache.TryGetValue((change.Kind, change.School), out var cached)) { return cached; }
        }
        var preferredName = change.Kind switch {
            "Blade" => change.School == "All" ? "Balanceblade" : change.School + "blade",
            "Shield" => change.School == "All" ? "Tower Shield" : change.School + " Shield",
            "Trap" => change.School == "All" ? "Curse" : change.School + " Trap",
            "Weakness" => "Weakness",
            "Heal over Time (HOT)" => "Sprite",
            "Damage over Time (DOT)" => "Fire Elf",
            _ => ""
        };
        var type = Type(change.Kind); var negative = change.Kind is "Shield" or "Weakness";
        var candidates = catalog.Spells.OrderByDescending(x => x.InternalName == preferredName).ThenBy(x => x.Hidden).ThenBy(x => x.InternalName.Length);
        foreach (var entry in candidates) {
            var effect = SpellRules.Leaves(entry.Template.m_effects).FirstOrDefault(x => x.m_effectType == type
                && (x.m_effectParam < 0) == negative && (x.m_sDamageType.ToString().Equals(change.School, StringComparison.OrdinalIgnoreCase)
                    || change.Kind == "Blade" && change.School == "Balance" && entry.InternalName == "Balanceblade" && x.m_sDamageType == "All"));
            if (effect is null) { continue; }
            CinematicTemplate cinematic;
            try { cinematic = catalog.Cinematic(entry.Template.m_spellBase); } catch (InvalidDataException) { continue; }
            var stageName = "AddHanging" + (effect.m_actNum == 0 ? "" : effect.m_actNum.ToString());
            var stage = cinematic.m_stages.OfType<AddHangingCinematicStageTemplate>().FirstOrDefault(x => x.m_name.ToString() == stageName);
            if (stage is null || string.IsNullOrEmpty(stage.m_hanging)) { continue; }
            if (change.Kind == "Blade" && !stage.m_hanging.ToString().Contains("Blade", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (change.Kind == "Shield" && !stage.m_hanging.ToString().Contains("Ward", StringComparison.OrdinalIgnoreCase)) { continue; }
            var activationName = "ActivateHanging" + (effect.m_actNum == 0 ? "" : effect.m_actNum.ToString());
            var activation = OverTime(change.Kind) ? cinematic.m_stages.FirstOrDefault(x => x.m_name == activationName
                && x.m_actions.OfType<ActivateHangingEffectCinematicAction>().Any()) : null;
            if (OverTime(change.Kind) && activation is null) { continue; }
            var visual = new HangingVisual(stage, activation);
            lock (cache) { cache[(change.Kind, change.School)] = visual; }
            return visual;
        }
        throw new InvalidDataException("This client has no matching " + change.School + " " + change.Kind + " visual. Choose a school with an original effect of that kind.");
    }
    internal static CinematicTemplate? Cinematic(ResourceCatalog catalog, SpellModification modification, SpellTemplate spell) {
        if (modification.SecondaryEffect is not { } extra || extra.Kind == "Heal") { return null; }
        var cinematic = ResourceCatalog.Clone(catalog.Cinematic(spell.m_spellBase));
        if (extra.Kind is "Blade" or "Weakness") {
            // An absent outgoing stage uses the client's standard charm activation.
            cinematic.m_stages.RemoveAll(x => (x.m_name.ToString() is "Outgoing" or "OutgoingFast") && x.m_actions.Count == 0 && x.m_duration == 0 && x.m_stageRelationships.Count == 0);
        }
        if (extra.Kind is "Shield" or "Trap") {
            cinematic.m_stages.RemoveAll(x => (x.m_name.ToString() is "Incoming" or "IncomingFast") && x.m_actions.Count == 0 && x.m_duration == 0 && x.m_stageRelationships.Count == 0);
        }
        var visual = Visual(catalog, extra);
        var stage = ResourceCatalog.Clone(visual.Add);
        var effect = spell.m_effects.Last();
        stage.m_name = "AddHanging" + (effect.m_actNum == 0 ? "" : effect.m_actNum.ToString());
        var index = cinematic.m_stages.FindIndex(x => x.m_name == stage.m_name);
        if (index >= 0) { cinematic.m_stages[index] = stage; } else { cinematic.m_stages.Add(stage); }
        if (visual.Activate is { } nativeActivation) {
            var activation = ResourceCatalog.Clone(nativeActivation);
            activation.m_name = "ActivateHanging" + (effect.m_actNum == 0 ? "" : effect.m_actNum.ToString());
            var activationIndex = cinematic.m_stages.FindIndex(x => x.m_name == activation.m_name);
            if (activationIndex >= 0) { cinematic.m_stages[activationIndex] = activation; } else { cinematic.m_stages.Add(activation); }
        }
        cinematic.m_name = "SpellWorkspace_Cinematic" + modification.TemplateId;
        return cinematic;
    }
    internal static string Description(SecondaryEffectModification effect) {
        if (effect.Description is not null) { return effect.Description; }
        var school = effect.School == "All" ? "all schools" : effect.School.ToUpperInvariant();
        var target = effect.Target switch { "Caster" => "yourself", "Caster's team" => "your team", "Enemy team" => "all enemies", _ => "the same target" };
        var text = effect.Kind switch {
            "Shield" => $"-{effect.Amount}% {school} SHIELD",
            "Blade" => $"+{effect.Amount}% to next {(effect.School == "All" ? "" : school + " ")}DAMAGE spell",
            "Trap" => $"+{effect.Amount}% {school} TRAP",
            "Weakness" => $"-{effect.Amount}% {school} outgoing DAMAGE",
            "Heal" => $"{effect.Amount} HEAL",
            "Heal over Time (HOT)" => $"{effect.Amount} HOT over {effect.Rounds} rounds",
            "Damage over Time (DOT)" => $"{effect.Amount} {school} DOT over {effect.Rounds} rounds",
            _ => ""
        };
        return "Then " + text + " on " + target;
    }
    internal static string CombineDescription(string main, string secondary) => secondary.Length == 0 ? main
        : main.Length == 0 ? secondary : main + "\n" + secondary;
}
