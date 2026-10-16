// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Imlight.CoreLib.Game.Quests.Management;

namespace SpellWorkspace.Core;

internal static class CardArtwork {
    internal static byte[] Encode(BitmapSource image) {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return Encode(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }
    private static byte[] Encode(int width, int height, byte[] pixels) {
        var result = new byte[128 + pixels.Length]; "DDS "u8.CopyTo(result);
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), value);
        Write(4, 124); Write(8, 0x100f); Write(12, (uint)height); Write(16, (uint)width); Write(20, (uint)(width * 4));
        Write(76, 32); Write(80, 0x41); Write(88, 32); Write(92, 0x00ff0000); Write(96, 0x0000ff00); Write(100, 0x000000ff); Write(104, 0xff000000); Write(108, 0x1000);
        pixels.CopyTo(result, 128); return result;
    }
    internal static byte[] Grayscale(byte[] dds) {
        if (!DdsDecoder.TryDecode(dds, 4096, out var width, out var height, out var pixels, out _)) { throw new InvalidDataException("Custom artwork cannot be decoded to make its unavailable-card image."); }
        for (var i = 0; i < pixels.Length; i += 4) {
            var gray = (byte)((299 * pixels[i] + 587 * pixels[i + 1] + 114 * pixels[i + 2] + 500) / 1000);
            pixels[i] = gray; pixels[i + 1] = gray; pixels[i + 2] = gray;
        }
        return Encode(width, height, pixels);
    }
}
