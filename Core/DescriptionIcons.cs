// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Imcodec.ObjectProperty.TypeCache;

namespace SpellWorkspace.Core;

internal sealed record DescriptionKeyword(string Word, string Token, BitmapSource? Icon);

internal static class DescriptionIcons {
    private static readonly ConditionalWeakTable<ResourceCatalog, IReadOnlyList<DescriptionKeyword>> Cache = new();
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.OrdinalIgnoreCase) {
        ["FIRE"] = "FireSchoolIcon", ["ICE"] = "IceSchoolIcon", ["STORM"] = "StormSchoolIcon",
        ["MYTH"] = "MythSchoolIcon", ["LIFE"] = "LifeSchoolIcon", ["DEATH"] = "DeathSchoolIcon",
        ["BALANCE"] = "BalanceSchoolIcon", ["SUN"] = "SunSchoolIcon", ["MOON"] = "MoonSchoolIcon",
        ["STAR"] = "StarSchoolIcon", ["SHADOW"] = "ShadowSchoolIcon", ["DAMAGE"] = "Damage_image",
        ["HEAL"] = "Heal_image", ["BLADE"] = "Blade_image", ["TRAP"] = "Trap_image", ["SHIELD"] = "Shield_image",
        ["PIP"] = "Pips_image", ["POWERPIP"] = "PowerPips_image", ["SHADOWPIP"] = "ShadowPips_image",
        ["HOT"] = "OT_Heal_image", ["DOT"] = "OT_Damage_image"
    };
    internal static IReadOnlyList<DescriptionKeyword> Legend(ResourceCatalog catalog) => Cache.GetValue(catalog, source => {
        var definition = ResourceCatalog.Decode<MadlibIconDefinition>(source.Read("Root", "MadlibIcons.xml"));
        var icons = definition.m_madlibIconDataList.Where(x => x is not null)
            .GroupBy(x => x.m_token.ToString().Trim('$'), StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        return Tokens.Where(x => icons.ContainsKey(x.Value)).Select(x => {
            var path = "GUI/Icons/" + icons[x.Value].m_icon.ToString().Replace('\\', '/').Split('/').Last();
            return new DescriptionKeyword(x.Key, "$" + x.Value + "$", source.Texture(path));
        }).ToArray();
    });
    internal static string Convert(ResourceCatalog catalog, string text) {
        var known = Legend(catalog).ToDictionary(x => x.Word, x => x.Token, StringComparer.OrdinalIgnoreCase);
        var converted = Regex.Replace(text, @"\$[^$\r\n]*\$|\b[A-Za-z]+\b", match => known.GetValueOrDefault(match.Value, match.Value));
        var nextArgument = Regex.Matches(converted, @"#(\d+):").Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();
        return Regex.Replace(converted, @"(#-?\d+:)?(\$[A-Za-z0-9_:&]*\$)", match =>
            match.Groups[1].Success ? match.Value : "#" + ++nextArgument + ":" + match.Groups[2].Value);
    }
}
