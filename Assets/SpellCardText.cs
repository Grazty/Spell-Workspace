/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * SPELL CARD TEXT
 * ========================================================================
 *
 * PURPOSE:
 * Turns a spell's card description into the text the game client shows.
 * Descriptions are templates such as "#1:$eA$-#2:$eA5$ #3:$dT_image$#4:$Damage_image$"
 * where $eA5$ is the value of the fifth effect, $dT_image$ the school icon of
 * that effect and $Damage_image$ an icon from MadlibIcons.xml.
 *
 * USAGE EXAMPLE:
 * var card = SpellCardText.Render(spell);
 * card.Markup  "80-120 {icon:GUI/Icons/Icon_Fire.dds}{icon:GUI/Icons/Type_Damage.dds}"
 * card.Plain   "80-120 Fire Damage"
 *
 * NOTE:
 * Effects are numbered depth first through random, variable, conditional,
 * count-based and target-count effects, the same order the client uses.
 *
 * TODO:
 *
 *
 * Created by: Spell Workspace contributors
 * Version: KALI 1.0
 * Last Updated: 10/13/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Quests.Management;

internal readonly record struct SpellCardDescription(string Markup, string Plain);

internal static partial class SpellCardText {

    private const string IconFolder = "GUI/Icons/";

    private static readonly object s_iconLock = new();
    private static Dictionary<string, (string Icon, string Text)> s_icons;

    /// <summary>
    /// Renders the card description of a spell as markup with {icon:path} tokens and as plain text.
    /// </summary>
    internal static SpellCardDescription Render(SpellTemplate spell) {
        var template = FirstText(Text(spell.m_description), Text(spell.m_descriptionTrainer),
            Text(spell.m_descriptionCombatHUD), Text(spell.m_advancedDescription));
        if (template.Length == 0) {
            return new SpellCardDescription(string.Empty, string.Empty);
        }

        var leaves = new List<SpellEffect>();
        foreach (var effect in spell.m_effects ?? []) {
            CollectLeaves(effect, leaves, 0);
        }

        template = BonusBlock().Replace(template, string.Empty);
        template = ArgumentIndex().Replace(template, string.Empty);
        template = template.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\r\n", "\n");
        template = MarkupTag().Replace(template, string.Empty);

        var markup = new StringBuilder();
        var plain = new StringBuilder();
        var last = 0;
        foreach (Match match in Token().Matches(template)) {
            markup.Append(template, last, match.Index - last);
            plain.Append(template, last, match.Index - last);
            last = match.Index + match.Length;
            var (icon, text) = Resolve(match.Groups[1].Value, leaves, spell);
            if (icon is not null) {
                markup.Append("{icon:").Append(icon).Append('}');
                plain.Append(text);
            } else {
                markup.Append(text);
                plain.Append(text);
            }
        }

        markup.Append(template, last, template.Length - last);
        plain.Append(template, last, template.Length - last);
        return new SpellCardDescription(Tidy(markup.ToString()), Tidy(IconSpacing().Replace(plain.ToString(), " ")));
    }

    /// <summary>
    /// Returns the icon path for a school name, or null when the client has no such icon.
    /// </summary>
    internal static string SchoolIcon(string school)
        => string.IsNullOrWhiteSpace(school) ? null : IconFolder + "Icon_" + school + ".dds";

    private static (string Icon, string Text) Resolve(string token, List<SpellEffect> leaves, SpellTemplate spell) {
        if (token is "dTA_image" or "dTB_image") {
            var prism = leaves.FirstOrDefault(SpellWorkspace.Core.SpellRules.Prism);
            if (prism is not null) {
                var school = token == "dTA_image" ? prism.m_sDamageType.ToString()
                    : SpellWorkspace.Core.SpellRules.Schools.FirstOrDefault(name => Imcodec.Cryptography.StringHash.Compute(name) == unchecked((uint)prism.m_effectParam));
                if (school is not null) { return (SchoolIcon(school), school + " "); }
            }
        }
        var value = EffectValue().Match(token);
        if (value.Success) {
            var effect = Leaf(leaves, value.Groups[2].Value);
            if (effect is null) {
                return (null, "?");
            }

            return (null, value.Groups[1].Value == "eARounds"
                ? effect.m_numRounds.ToString()
                : effect.m_effectParam.ToString());
        }

        var damageType = DamageTypeIcon().Match(token);
        if (damageType.Success) {
            var school = Leaf(leaves, damageType.Groups[1].Value)?.m_sDamageType;
            if (string.IsNullOrWhiteSpace(school)) {
                school = spell.m_sMagicSchoolName;
            }

            return (SchoolIcon(school), school + " ");
        }

        switch (token) {
            case "eAPerPip":
            case "eAPerPipAll":
                return (null, leaves.Count > 0 ? leaves[0].m_effectParam.ToString() : "X");
            case "eABonus":
            case "eAExtra":
            case "":
                return (null, string.Empty);
            case "NAME":
                return (null, SummonedName(leaves));
            case ":":
            case "&":
                return (null, token);
        }

        var icons = Icons();
        if (icons.TryGetValue(token, out var entry)) {
            return entry.Icon is null ? (null, entry.Text) : (entry.Icon, entry.Text + " ");
        }

        return (null, Readable(token));
    }

    private static SpellEffect Leaf(List<SpellEffect> leaves, string number) {
        var index = number.Length == 0 ? 1 : int.Parse(number);
        return index >= 1 && index <= leaves.Count ? leaves[index - 1] : null;
    }

    private static void CollectLeaves(SpellEffect effect, List<SpellEffect> leaves, int depth) {
        if (effect is null || depth > 12) {
            return;
        }

        switch (effect) {
            case ConditionalSpellEffect conditional:
                foreach (var element in conditional.m_elements ?? []) {
                    CollectLeaves(element?.m_pEffect, leaves, depth + 1);
                }

                return;
            case TargetCountSpellEffect targetCount:
                foreach (var list in targetCount.m_effectLists ?? []) {
                    CollectLeaves(list, leaves, depth + 1);
                }

                return;
            case RandomSpellEffect random:
                CollectAll(random.m_effectList, leaves, depth);
                return;
            case VariableSpellEffect variable:
                CollectAll(variable.m_effectList, leaves, depth);
                return;
            case CountBasedSpellEffect countBased:
                CollectAll(countBased.m_effectList, leaves, depth);
                return;
            case EffectListSpellEffect list:
                CollectAll(list.m_effectList, leaves, depth);
                return;
            default:
                leaves.Add(effect);
                return;
        }
    }

    private static void CollectAll(List<SpellEffect> effects, List<SpellEffect> leaves, int depth) {
        foreach (var child in effects ?? []) {
            CollectLeaves(child, leaves, depth + 1);
        }
    }

    private static string SummonedName(List<SpellEffect> leaves) {
        var summon = leaves.FirstOrDefault(effect => effect.m_effectType == kSpellEffects.kSummonCreature);
        if (summon is null) {
            return "a creature";
        }

        try {
            if (CoreObjectFactory.GetCoreTemplate((ulong) (uint) summon.m_effectParam) is GameObjectTemplate creature) {
                return FirstText(Text(creature.m_displayName), creature.m_objectName, "a creature");
            }
        }
        catch (Exception) {
            Logger.Debug("Summoned creature {0} could not be loaded for a card description.", Logger.Args(summon.m_effectParam));
        }

        return "a creature";
    }

    private static Dictionary<string, (string Icon, string Text)> Icons() {
        lock (s_iconLock) {
            if (s_icons is not null) {
                return s_icons;
            }

            s_icons = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            try {
                var definition = RootArchiveLoader.GetFile<MadlibIconDefinition>("MadlibIcons.xml");
                foreach (var data in definition?.m_madlibIconDataList ?? []) {
                    if (data is null || string.IsNullOrWhiteSpace(data.m_token)) {
                        continue;
                    }

                    var token = data.m_token.ToString().Trim('$');
                    var icon = (string) data.m_icon;
                    var text = Text((string) data.m_text);
                    if (text.Contains(':')) {
                        text = text[..text.IndexOf(':')];
                    }

                    s_icons[token] = (
                        string.IsNullOrWhiteSpace(icon) ? null : IconFolder + icon.Replace('\\', '/').Split('/').Last(),
                        FirstText(text, Readable(token)));
                }

                Logger.Information("Loaded {0} card description icons from MadlibIcons.xml.", Logger.Args(s_icons.Count));
            }
            catch (Exception ex) {
                Logger.Warning("MadlibIcons.xml could not be read; card descriptions show icon names as words. {0}", Logger.Args(ex.Message));
            }

            return s_icons;
        }
    }

    private static string Readable(string token) {
        return token.Replace("_image", string.Empty).Replace("SchoolIcon", string.Empty).Replace('_', ' ').Trim();
    }

    private static string Tidy(string text)
        => MultipleSpaces().Replace(text, " ").Replace(" \n", "\n").Replace("\n ", "\n").Trim();

    private static string Text(string key) {
        if (string.IsNullOrEmpty(key)) {
            return string.Empty;
        }

        try {
            var text = Locale.GetEnglishName(key);
            if (string.IsNullOrEmpty(text) && key.IndexOf('_') is var split and > 0) {
                text = Locale.GetEnglishName(key[..split], key[(split + 1)..]);
            }

            return text ?? string.Empty;
        }
        catch (Exception) {
            return string.Empty;
        }
    }

    private static string FirstText(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    [GeneratedRegex(@"\$([A-Za-z0-9_:&]*)\$")]
    private static partial Regex Token();

    [GeneratedRegex(@"^(eA|eARounds)(\d*)$")]
    private static partial Regex EffectValue();

    [GeneratedRegex(@"^dT_image(\d*)$")]
    private static partial Regex DamageTypeIcon();

    [GeneratedRegex(@"#-?\d+:")]
    private static partial Regex ArgumentIndex();

    [GeneratedRegex(@"#-?\d+:\{[^}]*\}|\{[^}]*\$eA(Bonus|Extra)\$[^}]*\}")]
    private static partial Regex BonusBlock();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex MarkupTag();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex IconSpacing();

}
