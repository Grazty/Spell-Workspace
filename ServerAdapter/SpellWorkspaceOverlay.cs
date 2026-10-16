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
 * SPELL WORKSPACE RESOURCE OVERLAY
 * ========================================================================
 *
 * PURPOSE:
 * Supplies verified spell resources in memory from a staged build.
 *
 * USAGE EXAMPLE:
 * s_rootWad = SpellWorkspaceOverlay.Apply(ResourceWad());
 *
 * NOTE:
 * Original WAD files are opened for reading only.
 *
 * TODO:
 *
 * Created by: Spell Workspace contributors
 * Version: KALI 1.0
 * Last Updated: 10/13/2026
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Imcodec.Wad;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Resources;

public static class SpellWorkspaceOverlay {
    private static string s_buildId;
    private static string s_hash;
    private static string s_projectId;
    private static uint[] s_spellIds;
    private static uint[] s_customIds;
    private static ActorSystem s_system;
    private static string s_token;
    private static System.Net.Sockets.TcpListener s_listener;
    internal static Archive ReadLocalArchive(string name) {
        var directory = Environment.GetEnvironmentVariable("IMLIGHT_CLIENT_GAME_DATA_PATH");
        if (string.IsNullOrWhiteSpace(directory)) { throw new InvalidDataException("Spell Workspace requires the selected client's GameData path."); }
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Split('/', '\\').Any(x => x is "." or "..")) { throw new InvalidDataException("Invalid archive name."); }
        var normalized = name.Replace('/', '-').Replace('\\', '-');
        var fileName = normalized.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".wad";
        if (Path.GetFileName(fileName) != fileName) { throw new InvalidDataException("Invalid archive name."); }
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) { Logger.Error("Selected client archive is missing: {ArchivePath}", Logger.Args(path)); return null; }
        return ArchiveParser.Parse(new MemoryStream(File.ReadAllBytes(path))) ?? throw new InvalidDataException("Invalid local archive: " + fileName);
    }
    internal static Archive Apply(Archive original) {
        var folder = Path.Combine(AppContext.BaseDirectory, "Config", "SpellWorkspace");
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) { return original; }
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        if (root.GetProperty("SchemaVersion").GetInt32() != 1) { throw new InvalidDataException("Unsupported Spell Workspace manifest."); }
        var path = root.GetProperty("BaseRootPath").GetString();
        var baseBytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(baseBytes));
        if (!hash.Equals(root.GetProperty("RootSha256").GetString(), StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("Spell Workspace base resources changed. Rebuild client and Imlight.");
        }
        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var input = new BinaryReader(File.OpenRead(Path.Combine(folder, "spell_overlay.bin")), Encoding.UTF8)) {
            if (!input.ReadBytes(8).SequenceEqual("SWOV1\0\0\0"u8.ToArray())) { throw new InvalidDataException("Invalid spell overlay."); }
            var count = input.ReadInt32();
            if (count < 0 || count > 4096) { throw new InvalidDataException("Invalid overlay entry count."); }
            string ReadText() {
                var length = input.ReadInt32();
                if (length < 1 || length > 4096) { throw new InvalidDataException("Invalid overlay path."); }
                return Encoding.UTF8.GetString(input.ReadBytes(length));
            }
            for (var i = 0; i < count; i++) {
                var archive = ReadText(); var name = ReadText(); var size = input.ReadInt32();
                if (archive != "Root" || name.Contains("..", StringComparison.Ordinal) || size < 0 || size > 33554432) { throw new InvalidDataException("Invalid spell resource."); }
                var bytes = input.ReadBytes(size);
                if (bytes.Length != size || !replacements.TryAdd(name, bytes)) { throw new InvalidDataException("Incomplete or duplicate spell resource."); }
            }
            if (input.BaseStream.Position != input.BaseStream.Length) { throw new InvalidDataException("Unexpected trailing overlay data."); }
        }
        using var baseStream = new MemoryStream(baseBytes);
        var archiveBase = ArchiveParser.Parse(baseStream) ?? throw new InvalidDataException("Invalid base resources.");
        var table = archiveBase.Files.Values.Select(x => x.Value).ToDictionary(x => x.FileName, StringComparer.Ordinal);
        foreach (var pair in replacements) { table[pair.Key] = new FileEntry { FileName = pair.Key, UncompressedSize = (uint)pair.Value.Length, CompressedSize = (uint)pair.Value.Length, IsCompressed = false, Crc32 = Crc32(pair.Value) }; }
        var version = BitConverter.ToUInt32(baseBytes, 5);
        var tableSize = (version >= 2 ? 14 : 13) + table.Keys.Sum(name => 21 + Encoding.UTF8.GetByteCount(name) + 1);
        var entries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        var combined = new MemoryStream();
        combined.Write(new byte[tableSize]);
        combined.Write(baseBytes);
        foreach (var pair in table) {
            var entry = pair.Value;
            var offset = (uint)(entry.Offset + tableSize);
            if (replacements.TryGetValue(pair.Key, out var data)) { offset = (uint)combined.Position; combined.Write(data); }
            entries[pair.Key] = new FileEntry { FileName = pair.Key, Offset = offset, UncompressedSize = entry.UncompressedSize, CompressedSize = entry.CompressedSize, IsCompressed = entry.IsCompressed, Crc32 = entry.Crc32 };
        }
        Logger.Information("Spell Workspace build {BuildId}: {Count} memory-only resources loaded.", Logger.Args(root.GetProperty("BuildId").GetString(), replacements.Count));
        s_buildId = root.GetProperty("BuildId").GetString(); s_hash = root.GetProperty("ManifestHash").GetString(); s_projectId = root.GetProperty("ProjectId").GetString();
        s_spellIds = root.GetProperty("Spells").EnumerateArray().Select(x => x.GetProperty("TemplateId").GetUInt32()).ToArray();
        s_customIds = root.GetProperty("Spells").EnumerateArray().Where(x => x.GetProperty("IsNew").GetBoolean()).Select(x => x.GetProperty("TemplateId").GetUInt32()).ToArray();
        s_token = File.ReadAllText(Path.Combine(folder, "control-token.txt")).Trim();
        StartStatusServer();
        return new Archive(entries, combined, version);
    }
    public static void SetReady(ActorSystem system) => s_system = system;

    private static uint Crc32(byte[] bytes) {
        var result = 0xffffffffu;
        foreach (var item in bytes) {
            result ^= item;
            for (var bit = 0; bit < 8; bit++) { result = (result >> 1) ^ ((result & 1) == 0 ? 0 : 0xedb88320); }
        }
        return ~result;
    }

    private static void StartStatusServer() {
        if (s_listener is not null) { return; }
        s_listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 12181);
        s_listener.Start();
        _ = System.Threading.Tasks.Task.Run(async () => {
            while (true) {
                using var client = await s_listener.AcceptTcpClientAsync();
                client.ReceiveTimeout = 2000; client.SendTimeout = 2000;
                try {
                    using var stream = client.GetStream();
                    var input = new byte[16384]; var length = 0; var separator = -1;
                    while (separator < 0 && length < input.Length) {
                        var read = await stream.ReadAsync(input.AsMemory(length)).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                        if (read == 0) { break; } length += read;
                        separator = Encoding.ASCII.GetString(input, 0, length).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    }
                    if (separator < 0) { continue; }
                    var lines = Encoding.ASCII.GetString(input, 0, separator).Split("\r\n");
                    var request = lines[0].Split(' ');
                    var headers = lines.Skip(1).Select(line => line.Split(':', 2)).Where(pair => pair.Length == 2)
                        .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    object result; var code = "200 OK";
                    if (request.Length != 3) { continue; }
                    if (request[0] == "GET" && request[1] == "/status") {
                        result = new { Product = "Spell Workspace", SchemaVersion = 1, BuildId = s_buildId, ManifestHash = s_hash,
                            ProjectId = s_projectId, Ready = s_system is not null, CustomSpellIds = s_customIds };
                    } else if (headers.ContainsKey("Origin") || !headers.TryGetValue("X-Spell-Workspace-Token", out var token) || token != s_token) {
                        code = "403 Forbidden"; result = new { Error = "Local workspace authentication required." };
                    } else if (s_system is null) { code = "503 Service Unavailable"; result = new { Error = "Imlight is still starting." }; }
                    else if (request[0] == "GET" && request[1] == "/characters") { result = SpellWorkspaceGrant.Characters(); }
                    else if (request[0] == "GET" && request[1] == "/spells") { result = SpellWorkspaceGrant.Templates(s_spellIds); }
                    else if (request[0] == "POST" && request[1] == "/grant") {
                        if (!headers.TryGetValue("Content-Length", out var sizeText) || !int.TryParse(sizeText, out var size) || size < 1 || separator + 4 + size > input.Length) { throw new InvalidDataException("Invalid grant request size."); }
                        var end = separator + 4 + size;
                        while (length < end) {
                            var read = await stream.ReadAsync(input.AsMemory(length, end - length)).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                            if (read == 0) { throw new InvalidDataException("Incomplete grant request."); } length += read;
                        }
                        using var document = JsonDocument.Parse(input.AsMemory(separator + 4, size));
                        var value = document.RootElement; var spellId = value.GetProperty("TemplateId").GetUInt32();
                        if (value.GetProperty("ManifestHash").GetString() != s_hash || !s_spellIds.Contains(spellId)) { code = "409 Conflict"; result = new { Error = "Spell is not enabled in this matching build." }; }
                        else { result = await SpellWorkspaceGrant.Grant(s_system, value.GetProperty("CharacterId").GetUInt64(), spellId, value.GetProperty("Treasure").GetBoolean()); }
                    } else { code = "404 Not Found"; result = new { Error = "Unknown workspace endpoint." }; }
                    var body = JsonSerializer.SerializeToUtf8Bytes(result);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + "\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header); await stream.WriteAsync(body);
                } catch (Exception exception) { Logger.Debug("Spell Workspace status connection closed: {Message}", Logger.Args(exception.Message)); }
            }
        });
    }
}
