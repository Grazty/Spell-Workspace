// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Imcodec.ObjectProperty.TypeCache;
using SpellWorkspace.UI;

namespace SpellWorkspace.Core;

internal sealed class SpellbookIcons {
    private const int TileSize = 64;
    private const int Columns = 8;
    private const int SlotsPerSheet = Columns * Columns;
    private readonly ResourceCatalog _catalog;
    private readonly Dictionary<uint, int> _indexes;
    private readonly SortedDictionary<int, WriteableBitmap> _sheets = [];
    internal static bool Customized(SpellModification? modification) => modification is not null && (modification.UseCardArtworkForDeckIcon || modification.ImportedDeckIconBase64 is not null);

    internal SpellbookIcons(ResourceCatalog catalog, SpellProject project) {
        _catalog = catalog;
        var originalSheets = catalog.GetArchive("Root").Files.Keys.Count(x => x.StartsWith("GUI/DeckConfig/Spell_Complete_Color", StringComparison.Ordinal) && x.EndsWith(".dds", StringComparison.Ordinal));
        if (originalSheets < 1 || catalog.Spells.Any(x => x.Template.m_imageIndex >= originalSheets * SlotsPerSheet)) { throw new InvalidDataException("The selected client's spellbook icon layout is unsupported."); }
        _indexes = project.Spells.Where(x => x.Enabled && Customized(x)).OrderBy(x => x.TemplateId)
            .Select((spell, index) => (spell.TemplateId, Index: originalSheets * SlotsPerSheet + index)).ToDictionary(x => x.TemplateId, x => x.Index);
    }
    internal static BitmapSource Original(ResourceCatalog catalog, uint sourceId) {
        var index = catalog.ById[sourceId].Template.m_imageIndex;
        var sheet = catalog.Texture("GUI/DeckConfig/Spell_Complete_Color" + (index / SlotsPerSheet + 1).ToString("D2") + ".dds")
            ?? throw new InvalidDataException("The original spellbook icon sheet is missing.");
        if (index < 0 || sheet.PixelWidth != 512 || sheet.PixelHeight != 512) { throw new InvalidDataException("The spellbook icon sheet has an unsupported size."); }
        var tile = new CroppedBitmap(sheet, new Int32Rect(index % Columns * TileSize, index % SlotsPerSheet / Columns * TileSize, TileSize, TileSize));
        tile.Freeze(); return tile;
    }
    internal static BitmapSource Preview(ResourceCatalog catalog, uint sourceId, SpellModification? modification) {
        var frame = Original(catalog, sourceId);
        if (!Customized(modification)) { return frame; }
        var selected = modification!.UseCardArtworkForDeckIcon ? modification.ImportedArtworkBase64 : modification.ImportedDeckIconBase64;
        var artwork = selected is not null ? SpellCardView.DecodeImage(Convert.FromBase64String(selected))
            : catalog.Texture(modification.Artwork ?? catalog.ById[sourceId].Artwork);
        if (artwork is null) { throw new InvalidDataException("The custom deck icon image cannot be decoded."); }
        var pixels = new byte[TileSize * TileSize * 4]; new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, TileSize * 4, 0);
        const int inset = 3; var size = TileSize - inset * 2;
        var scale = Math.Max((double)size / artwork.PixelWidth, (double)size / artwork.PixelHeight);
        var resized = new FormatConvertedBitmap(new TransformedBitmap(artwork, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
        var cropped = new byte[size * size * 4];
        resized.CopyPixels(new Int32Rect((resized.PixelWidth - size) / 2, (resized.PixelHeight - size) / 2, size, size), cropped, size * 4, 0);
        for (var y = 0; y < size; y++) { Buffer.BlockCopy(cropped, y * size * 4, pixels, ((y + inset) * TileSize + inset) * 4, size * 4); }
        var thumbnail = BitmapSource.Create(TileSize, TileSize, 96, 96, PixelFormats.Bgra32, null, pixels, TileSize * 4); thumbnail.Freeze(); return thumbnail;
    }
    internal void Add(SpellModification modification, SpellTemplate spell) {
        if (!_indexes.TryGetValue(modification.TemplateId, out var index)) { return; }
        var thumbnail = Preview(_catalog, modification.SourceTemplateId, modification);
        var pixels = new byte[TileSize * TileSize * 4]; new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, TileSize * 4, 0);
        var sheetNumber = index / SlotsPerSheet + 1;
        if (!_sheets.TryGetValue(sheetNumber, out var sheet)) { sheet = new WriteableBitmap(512, 512, 96, 96, PixelFormats.Bgra32, null); _sheets.Add(sheetNumber, sheet); }
        sheet.WritePixels(new Int32Rect(index % Columns * TileSize, index % SlotsPerSheet / Columns * TileSize, TileSize, TileSize), pixels, TileSize * 4, 0);
        spell.m_imageIndex = index;
    }
    internal IEnumerable<OverlayEntry> Entries() {
        foreach (var pair in _sheets) {
            var color = CardArtwork.Encode(pair.Value);
            yield return new("Root", "GUI/DeckConfig/Spell_Complete_Color" + pair.Key.ToString("D2") + ".dds", color);
            yield return new("Root", "GUI/DeckConfig/Spell_Complete_BW" + pair.Key.ToString("D2") + ".dds", CardArtwork.Grayscale(color));
        }
    }
}
