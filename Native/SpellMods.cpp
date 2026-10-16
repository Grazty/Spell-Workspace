// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
#define NOMINMAX
#include <winsock2.h>
#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <winhttp.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <map>
#include <mutex>
#include <algorithm>
#include <memory>
#include "vendor/include/MinHook.h"
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ws2_32.lib")

namespace {
using Bytes = std::vector<uint8_t>;
struct Replacement { std::string name; Bytes data; };
struct VirtualArchive { std::wstring source; uint8_t* data = nullptr; size_t size = 0; };
struct OpenFile { std::shared_ptr<VirtualArchive> archive; int64_t position = 0; };
std::vector<std::shared_ptr<VirtualArchive>> archives;
std::map<HANDLE, std::shared_ptr<OpenFile>> files;
std::map<HANDLE, std::shared_ptr<VirtualArchive>> mappings;
std::map<const void*, unsigned> views;
std::recursive_mutex stateLock;
std::wstring directory;
bool openedLogged = false;
HANDLE logHandle = INVALID_HANDLE_VALUE;
decltype(&CreateFileW) realCreateFileW = nullptr;
decltype(&CreateFileA) realCreateFileA = nullptr;
decltype(&ReadFile) realReadFile = nullptr;
decltype(&SetFilePointer) realSetFilePointer = nullptr;
decltype(&SetFilePointerEx) realSetFilePointerEx = nullptr;
decltype(&GetFileSize) realGetFileSize = nullptr;
decltype(&GetFileSizeEx) realGetFileSizeEx = nullptr;
decltype(&CloseHandle) realCloseHandle = nullptr;
decltype(&DuplicateHandle) realDuplicateHandle = nullptr;
decltype(&CreateFileMappingW) realCreateFileMappingW = nullptr;
decltype(&CreateFileMappingA) realCreateFileMappingA = nullptr;
decltype(&MapViewOfFile) realMapViewOfFile = nullptr;
decltype(&UnmapViewOfFile) realUnmapViewOfFile = nullptr;
decltype(&GetFileInformationByHandle) realGetFileInformation = nullptr;
decltype(&connect) realConnect = nullptr;
decltype(&connect) realLegacyConnect = nullptr;
decltype(&WSAConnect) realWSAConnect = nullptr;
using AudioDirectoryCall = const char* (WINAPI*)(const char*);
AudioDirectoryCall realAudioDirectory = nullptr;
std::string audioDirectory;

void Log(const std::string& text) {
    if (logHandle == INVALID_HANDLE_VALUE) { return; }
    auto line = text + "\r\n";
    DWORD written = 0;
    WriteFile(logHandle, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
}
std::wstring Wide(const std::string& text) {
    auto length = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, nullptr, 0);
    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, result.data(), length);
    result.resize(static_cast<size_t>(length - 1));
    return result;
}
std::wstring FullPath(const wchar_t* path) {
    wchar_t buffer[32768];
    auto length = GetFullPathNameW(path, 32768, buffer, nullptr);
    if (!length || length >= 32768) { return L""; }
    std::wstring result(buffer, length);
    std::transform(result.begin(), result.end(), result.begin(), ::towlower);
    return result;
}
std::wstring Setting(const wchar_t* name) {
    wchar_t value[32768];
    GetPrivateProfileStringW(L"Workspace", name, L"", value, 32768, (directory + L"\\SpellWorkspace.client.ini").c_str());
    return value;
}
bool LocalhostArguments(const std::wstring& executable) {
    if (Setting(L"ClientName") == L"OverlayTests.exe") { return true; }
    int count = 0;
    auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    bool local = false;
    if (arguments) {
        for (int i = 1; i + 2 < count; i++) {
            if (wcscmp(arguments[i], L"-L") == 0) { local = wcscmp(arguments[i + 1], L"127.0.0.1") == 0 && wcscmp(arguments[i + 2], L"12000") == 0; break; }
        }
        LocalFree(arguments);
    }
    wchar_t current[32768]; GetCurrentDirectoryW(32768, current);
    if (local && FullPath(current) == FullPath(Setting(L"SourceBin").c_str())) { return true; }
    Log("Restarting this custom client with the localhost connection and original asset working directory.");
    CloseHandle(logHandle); logHandle = INVALID_HANDLE_VALUE;
    auto command = L"\"" + executable + L"\" -L 127.0.0.1 12000";
    std::vector<wchar_t> mutableCommand(command.begin(), command.end()); mutableCommand.push_back(0);
    STARTUPINFOW start{}; start.cb = sizeof(start); PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE, 0, nullptr, Setting(L"SourceBin").c_str(), &start, &process)) { throw std::runtime_error("Unable to launch the localhost client."); }
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return false;
}
bool MatchingServer() {
    if (Setting(L"RequireMatchingServer") != L"1") { return true; }
    auto session = WinHttpOpen(L"Spell Workspace/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY, nullptr, nullptr, 0);
    if (!session) { return false; }
    WinHttpSetTimeouts(session, 2000, 2000, 2000, 2000);
    auto connection = WinHttpConnect(session, L"127.0.0.1", 12181, 0);
    auto request = connection ? WinHttpOpenRequest(connection, L"GET", L"/status", nullptr, nullptr, nullptr, 0) : nullptr;
    std::string body;
    bool okay = request && WinHttpSendRequest(request, nullptr, 0, nullptr, 0, 0, 0) && WinHttpReceiveResponse(request, nullptr);
    if (okay) {
        DWORD got = 0; char buffer[4096];
        while (WinHttpReadData(request, buffer, sizeof(buffer), &got) && got) { body.append(buffer, got); if (body.size() > 65536) { okay = false; break; } }
    }
    if (request) { WinHttpCloseHandle(request); } if (connection) { WinHttpCloseHandle(connection); } WinHttpCloseHandle(session);
    auto expected = Setting(L"ManifestHash"); std::string ascii; for (const auto character : expected) { ascii.push_back(static_cast<char>(character)); }
    auto build = Setting(L"ServerBuildId"); std::string buildAscii; for (const auto character : build) { buildAscii.push_back(static_cast<char>(character)); }
    return okay && body.find("\"ManifestHash\":\"" + ascii + "\"") != std::string::npos && body.find("\"Ready\":true") != std::string::npos &&
        (build.empty() || body.find("\"BuildId\":\"" + buildAscii + "\"") != std::string::npos);
}
Bytes ReadDisk(const std::wstring& path) {
    auto handle = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) { throw std::runtime_error("Cannot open required input file."); }
    LARGE_INTEGER size;
    if (!GetFileSizeEx(handle, &size) || size.QuadPart < 0 || size.QuadPart > 2147483647) { CloseHandle(handle); throw std::runtime_error("Invalid input file size."); }
    Bytes result(static_cast<size_t>(size.QuadPart));
    DWORD got = 0;
    auto okay = ReadFile(handle, result.data(), static_cast<DWORD>(result.size()), &got, nullptr);
    CloseHandle(handle);
    if (!okay || got != result.size()) { throw std::runtime_error("Incomplete input file read."); }
    return result;
}
std::wstring Sha256(const Bytes& bytes) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD length = 0, received = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) { throw std::runtime_error("SHA-256 unavailable."); }
    BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&length), sizeof(length), &received, 0);
    Bytes object(length), digest(32);
    auto status = BCryptCreateHash(algorithm, &hash, object.data(), length, nullptr, 0, 0);
    if (status >= 0) { status = BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), 0); }
    if (status >= 0) { status = BCryptFinishHash(hash, digest.data(), 32, 0); }
    if (hash) { BCryptDestroyHash(hash); }
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status < 0) { throw std::runtime_error("SHA-256 failed."); }
    std::wstring result;
    for (auto value : digest) { wchar_t buffer[3]; swprintf_s(buffer, L"%02x", value); result += buffer; }
    return result;
}
uint32_t U32(const Bytes& data, size_t& at) {
    if (at + 4 > data.size()) { throw std::runtime_error("Truncated data."); }
    uint32_t value; memcpy(&value, data.data() + at, 4); at += 4; return value;
}
std::string Text(const Bytes& data, size_t& at) {
    auto count = U32(data, at);
    if (count > 4096 || at + count > data.size()) { throw std::runtime_error("Invalid resource path."); }
    std::string result(reinterpret_cast<const char*>(data.data() + at), count); at += count; return result;
}
void Add32(Bytes& bytes, uint32_t value) {
    auto at = bytes.size(); bytes.resize(at + 4); memcpy(bytes.data() + at, &value, 4);
}
uint32_t Crc32(const Bytes& bytes) {
    uint32_t value = 0xffffffff;
    for (auto byte : bytes) {
        value ^= byte;
        for (auto bit = 0; bit < 8; bit++) { value = (value >> 1) ^ ((value & 1) ? 0xedb88320 : 0); }
    }
    return ~value;
}
std::shared_ptr<VirtualArchive> Assemble(const std::wstring& path, const std::vector<Replacement>& replacements) {
    auto original = ReadDisk(path);
    if (Sha256(original) != Setting(L"RootSha256")) { throw std::runtime_error("Root.wad changed since this package was built. Rebuild the client package."); }
    if (original.size() < 13 || memcmp(original.data(), "KIWAD", 5)) { throw std::runtime_error("Invalid resource archive."); }
    struct Entry { uint32_t offset, size, stored, crc; uint8_t compressed; std::string name; const Bytes* replacement = nullptr; };
    size_t at = 5;
    auto version = U32(original, at), count = U32(original, at);
    auto versionFlag = version >= 2 ? original.at(at++) : uint8_t(0);
    if (count > 500000 || version > 2) { throw std::runtime_error("Unsupported archive version."); }
    std::vector<Entry> entries;
    std::map<std::string, size_t> names;
    for (uint32_t i = 0; i < count; i++) {
        Entry entry{};
        entry.offset = U32(original, at); entry.size = U32(original, at); entry.stored = U32(original, at);
        entry.compressed = original.at(at++); entry.crc = U32(original, at); entry.name = Text(original, at);
        if (!entry.name.empty() && entry.name.back() == '\0') { entry.name.pop_back(); }
        if (static_cast<uint64_t>(entry.offset) + (entry.compressed ? entry.stored : entry.size) > original.size()) { throw std::runtime_error("Invalid resource offset."); }
        names[entry.name] = entries.size(); entries.push_back(std::move(entry));
    }
    for (const auto& change : replacements) {
        auto it = names.find(change.name);
        if (it == names.end()) { names[change.name] = entries.size(); entries.push_back({ 0, 0, 0, 0, 0, change.name }); }
        auto& entry = entries[names[change.name]];
        entry.replacement = &change.data; entry.size = static_cast<uint32_t>(change.data.size());
        entry.stored = entry.size; entry.compressed = 0; entry.crc = Crc32(change.data);
    }
    size_t tableSize = version >= 2 ? 14 : 13;
    for (const auto& entry : entries) { tableSize += 21 + entry.name.size() + 1; }
    // Streamed sound readers can open the physical WAD through their own CRT.
    // Keep existing payload offsets intact; relocate only data covered by the larger table.
    uint64_t fullSize = std::max(tableSize, original.size());
    for (const auto& entry : entries) {
        if (entry.replacement) { fullSize += entry.replacement->size(); }
        else if (entry.offset < tableSize) { fullSize += entry.compressed ? entry.stored : entry.size; }
    }
    if (fullSize > 2147483647) { throw std::runtime_error("Virtual archive exceeds supported size."); }
    Bytes table; table.insert(table.end(), {'K','I','W','A','D'}); Add32(table, version); Add32(table, static_cast<uint32_t>(entries.size()));
    if (version >= 2) { table.push_back(versionFlag); }
    auto nextData = static_cast<uint32_t>(std::max(tableSize, original.size()));
    for (const auto& entry : entries) {
        auto relocated = entry.replacement || entry.offset < tableSize;
        Add32(table, relocated ? nextData : entry.offset);
        Add32(table, entry.size); Add32(table, entry.stored); table.push_back(entry.compressed); Add32(table, entry.crc);
        Add32(table, static_cast<uint32_t>(entry.name.size() + 1)); table.insert(table.end(), entry.name.begin(), entry.name.end()); table.push_back(0);
        if (relocated) { nextData += entry.replacement ? static_cast<uint32_t>(entry.replacement->size()) : entry.compressed ? entry.stored : entry.size; }
    }
    auto result = std::make_shared<VirtualArchive>(); result->source = FullPath(path.c_str()); result->size = static_cast<size_t>(fullSize);
    result->data = static_cast<uint8_t*>(VirtualAlloc(nullptr, result->size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!result->data) { throw std::runtime_error("Not enough memory for the resource overlay."); }
    memcpy(result->data, original.data(), original.size()); memcpy(result->data, table.data(), table.size());
    auto destination = std::max(tableSize, original.size());
    for (const auto& entry : entries) {
        if (entry.replacement) { memcpy(result->data + destination, entry.replacement->data(), entry.replacement->size()); destination += entry.replacement->size(); }
        else if (entry.offset < tableSize) {
            auto length = entry.compressed ? entry.stored : entry.size;
            memcpy(result->data + destination, original.data() + entry.offset, length); destination += length;
        }
    }
    DWORD previous; VirtualProtect(result->data, result->size, PAGE_READONLY, &previous);
    Log("Virtual Root archive ready: " + std::to_string(entries.size()) + " entries; " + std::to_string(replacements.size()) + " overrides/additions.");
    return result;
}

HANDLE WINAPI HookCreateFileW(LPCWSTR path, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES security, DWORD disposition, DWORD attributes, HANDLE existing) {
    std::lock_guard guard(stateLock);
    auto full = FullPath(path);
    auto found = std::find_if(archives.begin(), archives.end(), [&](const auto& archive) { return archive->source == full; });
    if (found != archives.end() && ((access & (GENERIC_WRITE | DELETE | FILE_WRITE_DATA | FILE_APPEND_DATA)) || disposition != OPEN_EXISTING)) {
        SetLastError(ERROR_ACCESS_DENIED); return INVALID_HANDLE_VALUE;
    }
    auto handle = realCreateFileW(path, access, share, security, disposition, attributes, existing);
    if (handle != INVALID_HANDLE_VALUE && found != archives.end()) {
        files[handle] = std::make_shared<OpenFile>(OpenFile { *found, 0 });
        if (!openedLogged) { openedLogged = true; Log("Client opened virtual Root.wad."); }
    }
    return handle;
}
HANDLE WINAPI HookCreateFileA(LPCSTR path, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES security, DWORD disposition, DWORD attributes, HANDLE existing) {
    return HookCreateFileW(Wide(path).c_str(), access, share, security, disposition, attributes, existing);
}
BOOL WINAPI HookReadFile(HANDLE file, LPVOID buffer, DWORD count, LPDWORD read, LPOVERLAPPED overlap) {
    std::lock_guard guard(stateLock); auto it = files.find(file);
    if (it == files.end()) { return realReadFile(file, buffer, count, read, overlap); }
    auto& state = *it->second;
    auto position = overlap ? (static_cast<uint64_t>(overlap->OffsetHigh) << 32) | overlap->Offset : static_cast<uint64_t>(state.position);
    auto available = position >= state.archive->size ? 0 : std::min<size_t>(count, state.archive->size - static_cast<size_t>(position));
    if (available) { memcpy(buffer, state.archive->data + position, available); }
    if (read) { *read = static_cast<DWORD>(available); }
    if (overlap) { overlap->Internal = 0; overlap->InternalHigh = available; if (overlap->hEvent) { SetEvent(overlap->hEvent); } }
    else { state.position += static_cast<int64_t>(available); }
    return TRUE;
}
BOOL WINAPI HookSetFilePointerEx(HANDLE file, LARGE_INTEGER distance, PLARGE_INTEGER result, DWORD method) {
    std::lock_guard guard(stateLock); auto it = files.find(file);
    if (it == files.end()) { return realSetFilePointerEx(file, distance, result, method); }
    if (method > FILE_END) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    auto base = method == FILE_BEGIN ? 0 : method == FILE_CURRENT ? it->second->position : static_cast<int64_t>(it->second->archive->size);
    auto position = base + distance.QuadPart;
    if (position < 0) { SetLastError(ERROR_NEGATIVE_SEEK); return FALSE; }
    it->second->position = position; if (result) { result->QuadPart = position; } return TRUE;
}
DWORD WINAPI HookSetFilePointer(HANDLE file, LONG low, PLONG high, DWORD method) {
    { std::lock_guard guard(stateLock); if (!files.count(file)) { return realSetFilePointer(file, low, high, method); } }
    LARGE_INTEGER distance, result; distance.LowPart = static_cast<DWORD>(low); distance.HighPart = high ? *high : low < 0 ? -1 : 0;
    if (!HookSetFilePointerEx(file, distance, &result, method)) { return INVALID_SET_FILE_POINTER; }
    if (high) { *high = result.HighPart; } SetLastError(NO_ERROR); return result.LowPart;
}
BOOL WINAPI HookGetFileSizeEx(HANDLE file, PLARGE_INTEGER result) {
    std::lock_guard guard(stateLock); auto it = files.find(file);
    if (it == files.end()) { return realGetFileSizeEx(file, result); }
    result->QuadPart = static_cast<LONGLONG>(it->second->archive->size); return TRUE;
}
DWORD WINAPI HookGetFileSize(HANDLE file, LPDWORD high) {
    { std::lock_guard guard(stateLock); if (!files.count(file)) { return realGetFileSize(file, high); } }
    LARGE_INTEGER size; HookGetFileSizeEx(file, &size); if (high) { *high = size.HighPart; } return size.LowPart;
}
BOOL WINAPI HookGetFileInformation(HANDLE file, LPBY_HANDLE_FILE_INFORMATION information) {
    auto okay = realGetFileInformation(file, information);
    std::lock_guard guard(stateLock); auto it = files.find(file);
    if (okay && it != files.end()) { information->nFileSizeHigh = static_cast<DWORD>(it->second->archive->size >> 32); information->nFileSizeLow = static_cast<DWORD>(it->second->archive->size); }
    return okay;
}
HANDLE WINAPI HookCreateFileMappingW(HANDLE file, LPSECURITY_ATTRIBUTES security, DWORD protection, DWORD high, DWORD low, LPCWSTR name) {
    auto result = realCreateFileMappingW(file, security, protection, high, low, name);
    std::lock_guard guard(stateLock); auto it = files.find(file);
    if (result && it != files.end()) { mappings[result] = it->second->archive; }
    return result;
}
HANDLE WINAPI HookCreateFileMappingA(HANDLE file, LPSECURITY_ATTRIBUTES security, DWORD protection, DWORD high, DWORD low, LPCSTR name) {
    return HookCreateFileMappingW(file, security, protection, high, low, name ? Wide(name).c_str() : nullptr);
}
LPVOID WINAPI HookMapViewOfFile(HANDLE mapping, DWORD access, DWORD high, DWORD low, SIZE_T count) {
    std::lock_guard guard(stateLock); auto it = mappings.find(mapping);
    if (it == mappings.end()) { return realMapViewOfFile(mapping, access, high, low, count); }
    auto offset = (static_cast<uint64_t>(high) << 32) | low;
    if ((access & FILE_MAP_WRITE) || offset >= it->second->size || (count && count > it->second->size - offset)) { SetLastError(ERROR_INVALID_PARAMETER); return nullptr; }
    auto address = it->second->data + offset; views[address]++; return address;
}
BOOL WINAPI HookUnmapViewOfFile(LPCVOID address) {
    std::lock_guard guard(stateLock); auto it = views.find(address);
    if (it == views.end()) { return realUnmapViewOfFile(address); }
    if (!--it->second) { views.erase(it); } return TRUE;
}
BOOL WINAPI HookCloseHandle(HANDLE handle) {
    std::lock_guard guard(stateLock); files.erase(handle); mappings.erase(handle); return realCloseHandle(handle);
}
BOOL WINAPI HookDuplicateHandle(HANDLE sourceProcess, HANDLE source, HANDLE targetProcess, LPHANDLE target, DWORD access, BOOL inherit, DWORD options) {
    std::lock_guard guard(stateLock);
    auto sourceLocal = GetProcessId(sourceProcess) == GetCurrentProcessId();
    auto targetLocal = GetProcessId(targetProcess) == GetCurrentProcessId();
    auto file = sourceLocal && files.count(source) ? files[source] : nullptr;
    auto mapping = sourceLocal && mappings.count(source) ? mappings[source] : nullptr;
    auto okay = realDuplicateHandle(sourceProcess, source, targetProcess, target, access, inherit, options);
    auto error = GetLastError();
    if (sourceLocal && (options & DUPLICATE_CLOSE_SOURCE)) { files.erase(source); mappings.erase(source); }
    if (okay && targetLocal && target) {
        if (file) { files[*target] = file; Log("Duplicated virtual Root archive handle."); }
        if (mapping) { mappings[*target] = mapping; }
    }
    SetLastError(error); return okay;
}
const sockaddr* LocalServerAddress(const sockaddr* address, int length, sockaddr_in& local) {
    // This package only launches with the validated localhost login arguments.
    if (!address || length < static_cast<int>(sizeof(sockaddr_in)) || address->sa_family != AF_INET) { return address; }
    const auto& requested = *reinterpret_cast<const sockaddr_in*>(address);
    auto port = ntohs(requested.sin_port);
    if (port != 12000 && port != 12001 && port != 12500 && (port < 12333 || port > 12335)) { return address; }
    local = requested;
    local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (requested.sin_addr.s_addr != local.sin_addr.s_addr) {
        std::lock_guard guard(stateLock);
        Log("Routing local game connection to 127.0.0.1:" + std::to_string(port) + ".");
    }
    return reinterpret_cast<const sockaddr*>(&local);
}
int WSAAPI HookConnect(SOCKET socket, const sockaddr* address, int length) {
    sockaddr_in local{};
    auto target = LocalServerAddress(address, length, local);
    return realConnect(socket, target, target == address ? length : static_cast<int>(sizeof(local)));
}
int WSAAPI HookLegacyConnect(SOCKET socket, const sockaddr* address, int length) {
    sockaddr_in local{};
    auto target = LocalServerAddress(address, length, local);
    return realLegacyConnect(socket, target, target == address ? length : static_cast<int>(sizeof(local)));
}
int WSAAPI HookWSAConnect(SOCKET socket, const sockaddr* address, int length, LPWSABUF caller, LPWSABUF callee, LPQOS send, LPQOS receive) {
    sockaddr_in local{};
    auto target = LocalServerAddress(address, length, local);
    return realWSAConnect(socket, target, target == address ? length : static_cast<int>(sizeof(local)), caller, callee, send, receive);
}
const char* WINAPI HookAudioDirectory(const char*) {
    return realAudioDirectory(audioDirectory.c_str());
}
template<class T> void Install(const char* name, T detour, T* original, const wchar_t* module = L"kernel32") {
    auto status = MH_CreateHookApi(module, name, reinterpret_cast<LPVOID>(detour), reinterpret_cast<LPVOID*>(original));
    if (status != MH_OK) { throw std::runtime_error(std::string("Cannot install ") + name + ": " + MH_StatusToString(status)); }
}
}

extern "C" __declspec(dllexport) BOOL SpellWorkspaceInitialize() {
    try {
        wchar_t executable[32768]; GetModuleFileNameW(nullptr, executable, 32768);
        std::wstring full(executable);
        HMODULE ownModule = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&SpellWorkspaceInitialize), &ownModule)) { throw std::runtime_error("Cannot locate the mod data directory."); }
        wchar_t modulePath[32768]; GetModuleFileNameW(ownModule, modulePath, 32768);
        std::wstring modPath(modulePath); directory = modPath.substr(0, modPath.find_last_of(L"\\/"));
        logHandle = CreateFileW((directory + L"\\runtime.log").c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (full.substr(full.find_last_of(L"\\/") + 1) != Setting(L"ClientName") || Sha256(ReadDisk(full)) != Setting(L"ClientSha256")) { throw std::runtime_error("Client fingerprint mismatch. Rebuild this package for the selected executable."); }
        if (!LocalhostArguments(full)) { return FALSE; }
        if (!MatchingServer()) { throw std::runtime_error("Start the matching Spell Workspace Imlight build before launching this modified client. The running server manifest did not match."); }
        auto bundle = ReadDisk(directory + L"\\spell_overlay.bin");
        if (bundle.size() < 12 || memcmp(bundle.data(), "SWOV1\0\0\0", 8)) { throw std::runtime_error("Invalid spell overlay bundle."); }
        size_t at = 8; auto count = U32(bundle, at); if (count > 4096) { throw std::runtime_error("Too many overlay entries."); }
        std::vector<Replacement> replacements;
        for (uint32_t i = 0; i < count; i++) {
            auto archive = Text(bundle, at), name = Text(bundle, at); auto length = U32(bundle, at);
            if (archive != "Root" || name.find("..") != std::string::npos || name.empty() || name.front() == '/' || length > 33554432 || at + length > bundle.size()) { throw std::runtime_error("Invalid overlay resource."); }
            replacements.push_back({ name, Bytes(bundle.begin() + at, bundle.begin() + at + length) }); at += length;
        }
        if (at != bundle.size()) { throw std::runtime_error("Unexpected trailing overlay data."); }
        archives.push_back(Assemble(Setting(L"RootPath"), replacements));
        if (MH_Initialize() != MH_OK) { throw std::runtime_error("Cannot initialize resource hooks."); }
        Install("CreateFileW", HookCreateFileW, &realCreateFileW); Install("CreateFileA", HookCreateFileA, &realCreateFileA);
        Install("ReadFile", HookReadFile, &realReadFile); Install("SetFilePointer", HookSetFilePointer, &realSetFilePointer);
        Install("SetFilePointerEx", HookSetFilePointerEx, &realSetFilePointerEx); Install("GetFileSize", HookGetFileSize, &realGetFileSize);
        Install("GetFileSizeEx", HookGetFileSizeEx, &realGetFileSizeEx); Install("CloseHandle", HookCloseHandle, &realCloseHandle);
        Install("DuplicateHandle", HookDuplicateHandle, &realDuplicateHandle);
        Install("CreateFileMappingW", HookCreateFileMappingW, &realCreateFileMappingW); Install("CreateFileMappingA", HookCreateFileMappingA, &realCreateFileMappingA);
        Install("MapViewOfFile", HookMapViewOfFile, &realMapViewOfFile); Install("UnmapViewOfFile", HookUnmapViewOfFile, &realUnmapViewOfFile);
        Install("GetFileInformationByHandle", HookGetFileInformation, &realGetFileInformation);
        auto providers = Setting(L"AudioProviderDirectory");
        if (!providers.empty() && GetModuleHandleW(L"mss64.dll")) {
            if (GetFileAttributesW((providers + L"\\mss64mp3.asi").c_str()) == INVALID_FILE_ATTRIBUTES
                || GetFileAttributesW((providers + L"\\mss64ogg.asi").c_str()) == INVALID_FILE_ATTRIBUTES) {
                throw std::runtime_error("The custom client's audio decoders are missing. Keep the Audio folder with the client package.");
            }
            BOOL substituted = FALSE;
            auto length = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, providers.c_str(), -1, nullptr, 0, nullptr, &substituted);
            if (!length || substituted || length >= 512) { throw std::runtime_error("The audio provider directory cannot be used by this client."); }
            audioDirectory.resize(static_cast<size_t>(length));
            WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, providers.c_str(), -1, audioDirectory.data(), length, nullptr, &substituted);
            audioDirectory.resize(static_cast<size_t>(length - 1));
            Install("AIL_set_redist_directory", HookAudioDirectory, &realAudioDirectory, L"mss64.dll");
            Log("Audio provider directory redirected to the custom package's MP3 and OGG decoders.");
        }
        Install("connect", HookConnect, &realConnect, L"ws2_32");
        Install("WSAConnect", HookWSAConnect, &realWSAConnect, L"ws2_32");
        auto legacy = LoadLibraryW(L"wsock32.dll");
        if (!legacy) { throw std::runtime_error("Cannot load legacy socket library."); }
        if (GetProcAddress(legacy, "connect") != GetProcAddress(GetModuleHandleW(L"ws2_32.dll"), "connect")) {
            Install("connect", HookLegacyConnect, &realLegacyConnect, L"wsock32");
        }
        if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK) { throw std::runtime_error("Cannot enable resource hooks."); }
        Log("Spell Workspace initialized. Resource changes are memory-only. No WAD writes permitted.");
        return TRUE;
    } catch (const std::exception& exception) {
        Log(std::string("FAILED: ") + exception.what());
        MH_DisableHook(MH_ALL_HOOKS);
        MessageBoxW(nullptr, Wide(exception.what()).c_str(), L"Spell Workspace runtime", MB_OK | MB_ICONERROR);
        return FALSE;
    }
}
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
