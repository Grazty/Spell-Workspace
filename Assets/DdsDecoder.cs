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
 * DDS DECODER
 * ========================================================================
 *
 * PURPOSE:
 * Decodes DXT1, DXT3, DXT5 and 32-bit uncompressed DDS textures to RGBA
 * for the admin model viewer.
 *
 * USAGE EXAMPLE:
 * DdsDecoder.TryDecode(bytes, 512, out var width, out var height, out var rgba, out var error);
 *
 * NOTE:
 * Picks the largest mip level no wider than the requested size.
 *
 * TODO:
 *
 *
 * Created by: Spell Workspace contributors
 * Version: KALI 1.0
 * Last Updated: 09/23/2026
 */

using System;
using System.Buffers.Binary;

namespace Imlight.CoreLib.Game.Quests.Management;

internal static class DdsDecoder {

    private const int HeaderSize = 128;

    public static bool TryDecode(byte[] data, int maxSize, out int width, out int height, out byte[] rgba, out string error) {
        width = height = 0;
        rgba = null;
        if (data is null || data.Length < HeaderSize || data[0] != 'D' || data[1] != 'D' || data[2] != 'S') {
            error = "not a DDS file";
            return false;
        }

        height = (int) BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        width = (int) BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var mipCount = Math.Max(1, (int) BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28)));
        var pixelFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
        var fourCc = System.Text.Encoding.ASCII.GetString(data, 84, 4);
        var bitCount = (int) BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(88));

        var blockBytes = fourCc switch {
            "DXT1" => 8,
            "DXT3" or "DXT5" => 16,
            _ => 0
        };
        var compressed = (pixelFlags & 0x4) != 0;
        if (compressed && blockBytes == 0) {
            error = $"unsupported DDS format {fourCc}";
            return false;
        }

        if (!compressed && bitCount != 32) {
            error = $"unsupported uncompressed DDS with {bitCount} bits per pixel";
            return false;
        }

        var offset = HeaderSize;
        var level = 0;
        while (level + 1 < mipCount && width > maxSize) {
            offset += LevelSize(width, height, compressed, blockBytes);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            level++;
        }

        if (offset + LevelSize(width, height, compressed, blockBytes) > data.Length) {
            error = "DDS data is truncated";
            return false;
        }

        rgba = new byte[width * height * 4];
        if (!compressed) {
            var rMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(92));
            for (var i = 0; i < width * height; i++) {
                var pixel = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4));
                var redFirst = rMask == 0x000000FF;
                rgba[i * 4] = (byte) (redFirst ? pixel : pixel >> 16);
                rgba[i * 4 + 1] = (byte) (pixel >> 8);
                rgba[i * 4 + 2] = (byte) (redFirst ? pixel >> 16 : pixel);
                rgba[i * 4 + 3] = (byte) (pixel >> 24);
            }

            error = null;
            return true;
        }

        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[16];
        for (var by = 0; by < blocksHigh; by++) {
            for (var bx = 0; bx < blocksWide; bx++) {
                var block = offset + (by * blocksWide + bx) * blockBytes;
                var colorBlock = fourCc == "DXT1" ? block : block + 8;
                alphas.Fill(255);
                if (fourCc == "DXT3") {
                    for (var i = 0; i < 16; i++) {
                        var nibble = (data[block + i / 2] >> ((i % 2) * 4)) & 0xF;
                        alphas[i] = (byte) (nibble * 17);
                    }
                } else if (fourCc == "DXT5") {
                    DecodeDxt5Alpha(data.AsSpan(block, 8), alphas);
                }

                var palette = DecodeColorPalette(data.AsSpan(colorBlock, 4), fourCc == "DXT1");
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(colorBlock + 4));
                for (var i = 0; i < 16; i++) {
                    var x = bx * 4 + i % 4;
                    var y = by * 4 + i / 4;
                    if (x >= width || y >= height) {
                        continue;
                    }

                    var entry = (int) ((bits >> (i * 2)) & 0x3);
                    var target = (y * width + x) * 4;
                    rgba[target] = palette[entry * 4];
                    rgba[target + 1] = palette[entry * 4 + 1];
                    rgba[target + 2] = palette[entry * 4 + 2];
                    rgba[target + 3] = fourCc == "DXT1" ? palette[entry * 4 + 3] : alphas[i];
                }
            }
        }

        error = null;
        return true;
    }

    private static int LevelSize(int width, int height, bool compressed, int blockBytes)
        => compressed
            ? Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes
            : width * height * 4;

    private static byte[] DecodeColorPalette(ReadOnlySpan<byte> block, bool dxt1) {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        var p = new byte[16];
        Expand565(c0, p, 0);
        Expand565(c1, p, 4);
        if (!dxt1 || c0 > c1) {
            for (var k = 0; k < 3; k++) {
                p[8 + k] = (byte) ((2 * p[k] + p[4 + k]) / 3);
                p[12 + k] = (byte) ((p[k] + 2 * p[4 + k]) / 3);
            }

            p[11] = 255;
            p[15] = 255;
        } else {
            for (var k = 0; k < 3; k++) {
                p[8 + k] = (byte) ((p[k] + p[4 + k]) / 2);
            }

            p[11] = 255;
            p[15] = 0;
        }

        return p;
    }

    private static void Expand565(ushort color, byte[] target, int offset) {
        var r = (color >> 11) & 0x1F;
        var g = (color >> 5) & 0x3F;
        var b = color & 0x1F;
        target[offset] = (byte) ((r << 3) | (r >> 2));
        target[offset + 1] = (byte) ((g << 2) | (g >> 4));
        target[offset + 2] = (byte) ((b << 3) | (b >> 2));
        target[offset + 3] = 255;
    }

    private static void DecodeDxt5Alpha(ReadOnlySpan<byte> block, Span<byte> alphas) {
        int a0 = block[0];
        int a1 = block[1];
        Span<int> table = stackalloc int[8];
        table[0] = a0;
        table[1] = a1;
        if (a0 > a1) {
            for (var i = 1; i < 7; i++) {
                table[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            }
        } else {
            for (var i = 1; i < 5; i++) {
                table[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            }

            table[6] = 0;
            table[7] = 255;
        }

        ulong bits = 0;
        for (var i = 0; i < 6; i++) {
            bits |= (ulong) block[2 + i] << (8 * i);
        }

        for (var i = 0; i < 16; i++) {
            alphas[i] = (byte) table[(int) ((bits >> (3 * i)) & 0x7)];
        }
    }

}
