// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <map>
#include <algorithm>
#include <io.h>
#pragma comment(lib, "ws2_32.lib")
int TestNetwork() {
    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data)) { return 20; }
    auto legacyModule = LoadLibraryW(L"wsock32.dll");
    auto legacyConnect = legacyModule ? reinterpret_cast<decltype(&connect)>(GetProcAddress(legacyModule, "connect")) : nullptr;
    if (!legacyConnect) { return 21; }
    for (int method = 0; method < 3; method++) {
        auto client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        sockaddr_in target{}; target.sin_family = AF_INET; target.sin_port = htons(12000); InetPtonA(AF_INET, "169.254.83.107", &target.sin_addr);
        auto result = method == 0 ? connect(client, reinterpret_cast<sockaddr*>(&target), sizeof(target)) :
            method == 1 ? legacyConnect(client, reinterpret_cast<sockaddr*>(&target), sizeof(target)) :
            WSAConnect(client, reinterpret_cast<sockaddr*>(&target), sizeof(target), nullptr, nullptr, nullptr, nullptr);
        sockaddr_in peer{}; int peerSize = sizeof(peer);
        auto okay = result == 0 && getpeername(client, reinterpret_cast<sockaddr*>(&peer), &peerSize) == 0 &&
            peer.sin_addr.s_addr == htonl(INADDR_LOOPBACK) && ntohs(peer.sin_port) == 12000;
        DWORD timeout = 3000; setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
        char response[1024]; auto received = okay ? recv(client, response, sizeof(response), 0) : -1;
        closesocket(client);
        if (!okay || received <= 0) { printf("FAIL: socket method %d, result %d, response %d.\n", method, result, received); return 22 + method; }
        printf("PASS: socket method %d reached 127.0.0.1:12000 and received the server handshake (%d bytes).\n", method, received);
    }
    auto listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in local{}; local.sin_family = AF_INET; local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(listener, reinterpret_cast<sockaddr*>(&local), sizeof(local)) || listen(listener, 1)) { return 25; }
    int localSize = sizeof(local); getsockname(listener, reinterpret_cast<sockaddr*>(&local), &localSize);
    auto client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    auto okay = connect(client, reinterpret_cast<sockaddr*>(&local), sizeof(local)) == 0;
    closesocket(client); closesocket(listener); WSACleanup();
    if (!okay) { return 26; }
    printf("PASS: unrelated socket port %u remains unchanged.\n", ntohs(local.sin_port));
    return 0;
}
uint32_t Number(const uint8_t* data, size_t& at) { uint32_t value; memcpy(&value, data + at, 4); at += 4; return value; }
struct AudioResource { std::wstring root; uint32_t offset = 0, size = 0; bool compressed = false; std::vector<uint8_t> prefix; };
struct AudioFile { FILE* file = nullptr; AudioResource resource; int64_t position = 0; };
std::map<std::string, AudioResource> audioResources;
std::vector<uint8_t> physicalRoot;
std::wstring physicalRootPath;
int WINAPI OpenAudioFile(const char* name, uintptr_t* handle) {
    auto found = audioResources.find(name);
    if (found == audioResources.end()) { return 0; }
    if (!physicalRoot.empty() && found->second.root == physicalRootPath) {
        *handle = reinterpret_cast<uintptr_t>(new AudioFile { nullptr, found->second, found->second.offset }); return 1;
    }
    FILE* original = nullptr;
    if (_wfopen_s(&original, found->second.root.c_str(), L"rb") || !original) { return 0; }
    auto descriptor = _dup(_fileno(original)); fclose(original);
    if (descriptor < 0) { return 0; }
    auto file = _fdopen(descriptor, "rb");
    if (!file) { _close(descriptor); return 0; }
    _fseeki64(file, found->second.offset, SEEK_SET);
    auto state = new AudioFile { file, found->second };
    *handle = reinterpret_cast<uintptr_t>(state); return 1;
}
void WINAPI CloseAudioFile(uintptr_t handle) { auto state = reinterpret_cast<AudioFile*>(handle); if (state->file) { fclose(state->file); } delete state; }
int WINAPI SeekAudioFile(uintptr_t handle, int offset, unsigned type) {
    auto state = reinterpret_cast<AudioFile*>(handle);
    auto position = type == 0 ? static_cast<int64_t>(state->resource.offset) + offset : type == 2 ? static_cast<int64_t>(state->resource.offset) + state->resource.size - offset : offset;
    if (!state->file) { state->position = type == 1 ? state->position + offset : position; return static_cast<int>(state->position - state->resource.offset); }
    _fseeki64(state->file, position, type == 1 ? SEEK_CUR : SEEK_SET);
    return static_cast<int>(_ftelli64(state->file) - state->resource.offset);
}
unsigned WINAPI ReadAudioFile(uintptr_t handle, void* data, unsigned count) {
    auto state = reinterpret_cast<AudioFile*>(handle);
    if (state->file) { return static_cast<unsigned>(fread(data, 1, count, state->file)); }
    if (state->position < 0 || static_cast<uint64_t>(state->position) >= physicalRoot.size()) { return 0; }
    auto got = static_cast<unsigned>(std::min<size_t>(count, physicalRoot.size() - static_cast<size_t>(state->position)));
    memcpy(data, physicalRoot.data() + state->position, got); state->position += got; return got;
}
int TestResourcePayloads(const uint8_t* view, const std::wstring& folder) {
    struct Entry { uint32_t offset, size, stored; uint8_t compressed; };
    auto index = [](const uint8_t* data) {
        std::map<std::string, Entry> result;
        size_t at = 5; auto version = Number(data, at), count = Number(data, at); if (version >= 2) { at++; }
        for (uint32_t i = 0; i < count; i++) {
            Entry entry{}; entry.offset = Number(data, at); entry.size = Number(data, at); entry.stored = Number(data, at); entry.compressed = data[at++]; Number(data, at);
            auto length = Number(data, at); std::string name(reinterpret_cast<const char*>(data + at), length - 1); at += length;
            result[name] = entry;
        }
        return result;
    };
    auto original = index(physicalRoot.data()), actual = index(view);
    FILE* input = nullptr; _wfopen_s(&input, (folder + L"\\spell_overlay.bin").c_str(), L"rb"); if (!input) { return 50; }
    _fseeki64(input, 0, SEEK_END); auto length = _ftelli64(input); _fseeki64(input, 0, SEEK_SET);
    std::vector<uint8_t> bundle(static_cast<size_t>(length)); auto got = fread(bundle.data(), 1, bundle.size(), input); fclose(input); if (got != bundle.size()) { return 51; }
    size_t at = 8; auto count = Number(bundle.data(), at);
    auto text = [&]() { auto size = Number(bundle.data(), at); std::string value(reinterpret_cast<const char*>(bundle.data() + at), size); at += size; return value; };
    std::map<std::string, std::vector<uint8_t>> replacements;
    for (uint32_t i = 0; i < count; i++) {
        auto archive = text(), name = text(); auto size = Number(bundle.data(), at);
        if (archive != "Root") { return 52; }
        replacements[name] = std::vector<uint8_t>(bundle.begin() + at, bundle.begin() + at + size); at += size;
    }
    for (const auto& pair : actual) {
        const auto& entry = pair.second;
        auto replacement = replacements.find(pair.first);
        if (replacement != replacements.end()) {
            if (entry.compressed || entry.size != replacement->second.size() || memcmp(view + entry.offset, replacement->second.data(), entry.size)) { printf("FAIL: overlay bytes differ: %s.\n", pair.first.c_str()); return 53; }
        } else {
            auto source = original.find(pair.first); if (source == original.end()) { return 54; }
            const auto& expected = source->second;
            auto size = entry.compressed ? entry.stored : entry.size;
            if (entry.size != expected.size || entry.stored != expected.stored || entry.compressed != expected.compressed
                || memcmp(view + entry.offset, physicalRoot.data() + expected.offset, size)) { printf("FAIL: original bytes differ: %s.\n", pair.first.c_str()); return 55; }
        }
    }
    for (const auto& pair : original) { if (!actual.count(pair.first)) { return 56; } }
    for (const auto& pair : replacements) { if (!actual.count(pair.first)) { return 57; } }
    printf("PASS: all %zu original resources retained; %zu replacements/additions match the bundle byte-for-byte; %zu virtual entries.\n", original.size(), replacements.size(), actual.size()); return 0;
}
int TestArchiveStreams(HMODULE miles, const std::wstring& folder, bool world = false) {
    wchar_t root[32768]; GetPrivateProfileStringW(L"Workspace", L"RootPath", L"", root, 32768, (folder + L"\\SpellWorkspace.client.ini").c_str());
    size_t audioArchives = 0;
    auto index = [&](const std::wstring& path, const std::string& label) {
        auto file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        auto mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        auto view = static_cast<const uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
        if (!view) { if (mapping) { CloseHandle(mapping); } if (file != INVALID_HANDLE_VALUE) { CloseHandle(file); } return; }
        auto before = audioResources.size();
        if (!memcmp(view, "KIWAD", 5)) {
            size_t at = 5; auto version = Number(view, at), entries = Number(view, at); if (version >= 2) { at++; }
            for (uint32_t i = 0; i < entries; i++) {
                auto offset = Number(view, at), length = Number(view, at), stored = Number(view, at); auto compressed = view[at++]; Number(view, at);
                auto nameLength = Number(view, at); std::string name(reinterpret_cast<const char*>(view + at), nameLength - 1); at += nameLength;
                auto extension = name.size() >= 4 ? name.substr(name.size() - 4) : "";
                auto selected = world ? extension == ".mp3" || extension == ".ogg" || extension == ".wav" : name == "Sound/Title.mp3" || name == "Sound/Menu.mp3"
                    || name == "Sound/Dialogue_Creation/CC_Ambrose_01.mp3" || name == "Sound/Combat/FX_CastingSound_Fire.mp3"
                    || name == "Sound/Combat/spellfire_firecat_summon.mp3" || name == "Sound/Combat/spellfire_firecat_attack2.mp3";
                if (selected && (world || !compressed)) {
                    auto size = compressed ? stored : length;
                    audioResources[label + name] = { path, offset, size, compressed != 0, std::vector<uint8_t>(view + offset, view + offset + (size < 32 ? size : 32)) };
                }
            }
        }
        if (audioResources.size() > before) { audioArchives++; }
        UnmapViewOfFile(view); CloseHandle(mapping); CloseHandle(file);
    };
    index(root, world ? "Root.wad/" : "");
    if (world) {
        std::wstring rootPath(root); auto directory = rootPath.substr(0, rootPath.find_last_of(L"\\/"));
        WIN32_FIND_DATAW found{}; auto search = FindFirstFileW((directory + L"\\*.wad").c_str(), &found);
        if (search != INVALID_HANDLE_VALUE) {
            do {
                if (wcscmp(found.cFileName, L"Root.wad") == 0) { continue; }
                std::wstring wideName(found.cFileName);
                auto length = WideCharToMultiByte(CP_UTF8, 0, wideName.c_str(), static_cast<int>(wideName.size()), nullptr, 0, nullptr, nullptr);
                std::string label(static_cast<size_t>(length), '\0');
                WideCharToMultiByte(CP_UTF8, 0, wideName.c_str(), static_cast<int>(wideName.size()), label.data(), length, nullptr, nullptr);
                index(directory + L"\\" + wideName, label + "/");
            } while (FindNextFileW(search, &found));
            FindClose(search);
        }
    }
    auto setDirectory = reinterpret_cast<const char* (WINAPI*)(const char*)>(GetProcAddress(miles, "AIL_set_redist_directory"));
    auto startup = reinterpret_cast<int (WINAPI*)()>(GetProcAddress(miles, "AIL_startup"));
    auto shutdown = reinterpret_cast<void (WINAPI*)()>(GetProcAddress(miles, "AIL_shutdown"));
    auto callbacks = reinterpret_cast<void (WINAPI*)(decltype(&OpenAudioFile), decltype(&CloseAudioFile), decltype(&SeekAudioFile), decltype(&ReadAudioFile))>(GetProcAddress(miles, "AIL_set_file_callbacks"));
    auto openDriver = reinterpret_cast<void* (WINAPI*)(unsigned, int, int, unsigned)>(GetProcAddress(miles, "AIL_open_digital_driver"));
    auto closeDriver = reinterpret_cast<void (WINAPI*)(void*)>(GetProcAddress(miles, "AIL_close_digital_driver"));
    auto openStream = reinterpret_cast<void* (WINAPI*)(void*, const char*, int)>(GetProcAddress(miles, "AIL_open_stream"));
    auto closeStream = reinterpret_cast<void (WINAPI*)(void*)>(GetProcAddress(miles, "AIL_close_stream"));
    auto error = reinterpret_cast<const char* (WINAPI*)()>(GetProcAddress(miles, "AIL_last_error"));
    if (!callbacks || !openDriver || !closeDriver || !openStream || !closeStream || !startup || !shutdown || !setDirectory || !error || (!world && audioResources.size() != 6) || audioResources.empty()) { return 41; }
    auto providerPath = folder + L"\\Audio\\Miles";
    auto providerLength = WideCharToMultiByte(CP_ACP, 0, providerPath.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string providers(static_cast<size_t>(providerLength), '\0');
    WideCharToMultiByte(CP_ACP, 0, providerPath.c_str(), -1, providers.data(), providerLength, nullptr, nullptr);
    setDirectory(providers.c_str()); if (!startup()) { return 42; }
    callbacks(OpenAudioFile, CloseAudioFile, SeekAudioFile, ReadAudioFile);
    auto driver = openDriver(44100, 16, 2, 0);
    if (!driver) { printf("Driver error: %s\n", error()); shutdown(); return 43; }
    int result = 0;
    size_t checked = 0, opened = 0, streamFailures = 0;
    std::map<std::pair<std::wstring, std::string>, bool> formats;
    for (const auto& resource : audioResources) {
        uintptr_t diagnostic = 0;
        if (OpenAudioFile(resource.first.c_str(), &diagnostic)) {
            SeekAudioFile(diagnostic, 0, 0); uint8_t head[16]{}; auto got = ReadAudioFile(diagnostic, head, sizeof(head));
            if (got != (resource.second.size < sizeof(head) ? resource.second.size : sizeof(head)) || memcmp(head, resource.second.prefix.data(), got)) {
                printf("FAIL: incorrect audio bytes for %s.\n", resource.first.c_str()); CloseAudioFile(diagnostic); result = 45; break;
            }
            if (world) {
                checked++;
                if (checked % 5000 == 0) { printf("Checked %zu audio resources...\n", checked); fflush(stdout); }
            } else { printf("Archive prefix %s (%u bytes):", resource.first.c_str(), got); for (auto value : head) { printf(" %02x", value); } printf("\n"); }
            auto state = reinterpret_cast<AudioFile*>(diagnostic);
            LARGE_INTEGER size{}; if (state->file) { GetFileSizeEx(reinterpret_cast<HANDLE>(_get_osfhandle(_fileno(state->file))), &size); }
            if (!world) { printf("Archive reader: %s; CRT size: %lld\n", state->file ? "hooked CRT" : "physical archive without hooks", size.QuadPart); } CloseAudioFile(diagnostic);
        } else { printf("FAIL: cannot open %s.\n", resource.first.c_str()); result = 46; break; }
        if (world) {
            if (resource.second.compressed || resource.second.size < 32) { continue; }
            auto key = std::make_pair(resource.second.root, resource.first.substr(resource.first.size() - 4));
            if (formats[key]) { continue; } formats[key] = true;
        }
        auto stream = openStream(driver, resource.first.c_str(), 0);
        if (!stream) { printf("STREAM FAILURE: %s: %s\n", resource.first.c_str(), error()); streamFailures++; if (!world) { result = 44; break; } continue; }
        opened++;
        printf("PASS: archive stream %s opens through %s.\n", resource.first.c_str(), physicalRoot.empty() ? "duplicated CRT file handles" : "physical archive reader for Root and CRT for other archives"); closeStream(stream);
    }
    if (world) { printf("WORLD AUDIO: %zu resource headers verified across %zu audio archives; %zu representative streams opened; %zu stream failures; result %d.\n", checked, audioArchives, opened, streamFailures, result); }
    closeDriver(driver); shutdown(); return result;
}
int TestAudio(HMODULE miles, const std::wstring& folder) {
    auto setDirectory = reinterpret_cast<const char* (WINAPI*)(const char*)>(GetProcAddress(miles, "AIL_set_redist_directory"));
    auto startup = reinterpret_cast<int (WINAPI*)()>(GetProcAddress(miles, "AIL_startup"));
    auto shutdown = reinterpret_cast<void (WINAPI*)()>(GetProcAddress(miles, "AIL_shutdown"));
    auto decompress = reinterpret_cast<int (WINAPI*)(const void*, unsigned, const char*, void**, unsigned*, void*)>(GetProcAddress(miles, "AIL_decompress_ASI"));
    auto release = reinterpret_cast<void (WINAPI*)(void*)>(GetProcAddress(miles, "AIL_mem_free_lock"));
    if (!setDirectory || !startup || !shutdown || !decompress || !release) { return 30; }
    auto selected = setDirectory("Z:\\missing-audio-decoders");
    if (!selected || strstr(selected, "missing-audio-decoders") || !startup()) { return 31; }
    for (const auto extension : { "mp3", "ogg" }) {
        std::wstring wideExtension(extension, extension + strlen(extension));
        auto path = folder + L"\\sample." + wideExtension;
        auto file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) { shutdown(); return 32; }
        auto length = GetFileSize(file, nullptr); std::vector<uint8_t> sample(length); DWORD read = 0;
        auto okay = ReadFile(file, sample.data(), length, &read, nullptr); CloseHandle(file);
        if (!okay || read != length) { shutdown(); return 33; }
        void* pcm = nullptr; unsigned pcmLength = 0; std::string format = std::string(".") + extension;
        auto result = decompress(sample.data(), length, format.c_str(), &pcm, &pcmLength, nullptr);
        if (!result || !pcm || !pcmLength) { shutdown(); printf("FAIL: %s decoder did not produce audio.\n", extension); return 34; }
        release(pcm); printf("PASS: %s decoder produced %u bytes of audio using the redirected provider directory.\n", extension, pcmLength);
    }
    shutdown(); return 0;
}
int wmain(int count, wchar_t** arguments) {
    wchar_t current[32768]; GetModuleFileNameW(nullptr, current, 32768);
    std::wstring exe(current), folder = exe.substr(0, exe.find_last_of(L'\\'));
    auto originalWorld = count == 2 && wcscmp(arguments[1], L"--world-audio-original") == 0;
    auto installed = count == 2 && wcscmp(arguments[1], L"--installed-resources") == 0;
    if (installed) { folder += L"\\Spell Workshop Data"; }
    auto resources = installed || (count == 2 && wcscmp(arguments[1], L"--resources") == 0);
    auto physical = count == 2 && (wcscmp(arguments[1], L"--streams-physical") == 0 || wcscmp(arguments[1], L"--world-audio-physical") == 0);
    auto world = count == 2 && (wcscmp(arguments[1], L"--world-audio") == 0 || wcscmp(arguments[1], L"--world-audio-physical") == 0 || originalWorld);
    auto streams = count == 2 && (wcscmp(arguments[1], L"--streams") == 0 || physical || world);
    auto audio = count == 2 && (wcscmp(arguments[1], L"--audio") == 0 || streams);
    auto miles = audio ? LoadLibraryW((folder + L"\\mss64.dll").c_str()) : nullptr;
    if (audio && !miles) { return 29; }
    if (physical || resources) {
        wchar_t root[32768]; GetPrivateProfileStringW(L"Workspace", L"RootPath", L"", root, 32768, (folder + L"\\SpellWorkspace.client.ini").c_str());
        physicalRootPath = root; FILE* source = nullptr; _wfopen_s(&source, root, L"rb");
        if (!source) { return 47; } _fseeki64(source, 0, SEEK_END); auto length = _ftelli64(source); _fseeki64(source, 0, SEEK_SET);
        if (length <= 0 || length > 2147483647) { fclose(source); return 48; }
        physicalRoot.resize(static_cast<size_t>(length)); auto got = fread(physicalRoot.data(), 1, physicalRoot.size(), source); fclose(source);
        if (got != physicalRoot.size()) { return 49; }
    }
    auto module = LoadLibraryW((folder + L"\\SpellWorkspace.SpellMods.dll").c_str());
    auto initialize = module ? reinterpret_cast<BOOL(*)()>(GetProcAddress(module, "SpellWorkspaceInitialize")) : nullptr;
    if (!originalWorld && (!initialize || !initialize())) { return 2; }
    if (audio) { return streams ? TestArchiveStreams(miles, folder, world) : TestAudio(miles, folder); }
    if (count == 2 && wcscmp(arguments[1], L"--network") == 0) { return TestNetwork(); }
    wchar_t root[32768];
    GetPrivateProfileStringW(L"Workspace", L"RootPath", L"", root, 32768, (folder + L"\\SpellWorkspace.client.ini").c_str());
    auto file = CreateFileW(root, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) { return 3; }
    LARGE_INTEGER size; if (!GetFileSizeEx(file, &size)) { return 4; }
    auto mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
    auto view = mapping ? static_cast<const uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0)) : nullptr;
    if (!view || memcmp(view, "KIWAD", 5)) { return 5; }
    if (resources) {
        auto result = TestResourcePayloads(view, folder); UnmapViewOfFile(view); CloseHandle(mapping); CloseHandle(file); return result;
    }
    size_t at = 5; auto version = Number(view, at), entries = Number(view, at); if (version >= 2) { at++; }
    uint32_t testOffset = 0, testSize = 0;
    for (uint32_t i = 0; i < entries; i++) {
        auto offset = Number(view, at), length = Number(view, at); Number(view, at); at++; Number(view, at);
        auto nameLength = Number(view, at); std::string name(reinterpret_cast<const char*>(view + at), nameLength - 1); at += nameLength;
        if (name == "SpellWorkspaceTest.txt") { testOffset = offset; testSize = length; }
    }
    if (!testOffset || testSize != 20 || memcmp(view + testOffset, "memory overlay works", 20)) { return 6; }
    LARGE_INTEGER position; position.QuadPart = testOffset;
    if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN)) { return 7; }
    char text[32]{}; DWORD got = 0;
    if (!ReadFile(file, text, testSize, &got, nullptr) || got != testSize || memcmp(text, "memory overlay works", 20)) { return 8; }
    HANDLE duplicate = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), file, GetCurrentProcess(), &duplicate, 0, FALSE, DUPLICATE_SAME_ACCESS)) { return 11; }
    position.QuadPart = testOffset;
    if (!SetFilePointerEx(duplicate, position, nullptr, FILE_BEGIN) || !ReadFile(duplicate, text, 5, &got, nullptr) || got != 5 || memcmp(text, "memor", 5)) { return 12; }
    position.QuadPart = 0;
    if (!SetFilePointerEx(file, position, &position, FILE_CURRENT) || position.QuadPart != testOffset + 5) { return 13; }
    if (!ReadFile(file, text, 15, &got, nullptr) || got != 15 || memcmp(text, "y overlay works", 15)) { return 14; }
    HANDLE moved = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), duplicate, GetCurrentProcess(), &moved, 0, FALSE, DUPLICATE_SAME_ACCESS | DUPLICATE_CLOSE_SOURCE)) { return 15; }
    position.QuadPart = testOffset;
    if (!SetFilePointerEx(moved, position, nullptr, FILE_BEGIN) || !ReadFile(moved, text, testSize, &got, nullptr) || got != testSize || memcmp(text, "memory overlay works", 20) || !CloseHandle(moved)) { return 16; }
    HANDLE mapDuplicate = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), mapping, GetCurrentProcess(), &mapDuplicate, 0, FALSE, DUPLICATE_SAME_ACCESS)) { return 17; }
    auto extraView = static_cast<const uint8_t*>(MapViewOfFile(mapDuplicate, FILE_MAP_READ, 0, 0, 0));
    if (!extraView || memcmp(extraView + testOffset, "memory overlay works", 20) || !UnmapViewOfFile(extraView) || !CloseHandle(mapDuplicate)) { return 18; }
    SetLastError(0); auto write = CreateFileW(root, GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (write != INVALID_HANDLE_VALUE || GetLastError() != ERROR_ACCESS_DENIED) { return 9; }
    auto okay = UnmapViewOfFile(view) && CloseHandle(mapping) && CloseHandle(file);
    printf("PASS: normal and mapped reads, duplicated handles, shared seeks, close-source duplicates, new entries, virtual sizes, close, and denied WAD writes. Entries: %u.\n", entries);
    return okay ? 0 : 10;
}
