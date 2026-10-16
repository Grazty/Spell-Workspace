// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <string>
#include <vector>
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    wchar_t path[32768]; GetModuleFileNameW(nullptr, path, 32768);
    std::wstring full(path), directory = full.substr(0, full.find_last_of(L"\\/"));
    auto config = directory + L"\\SpellWorkspace.client.ini";
    wchar_t client[32768], working[32768], installed[32768];
    GetPrivateProfileStringW(L"Workspace", L"ClientName", L"", client, 32768, config.c_str());
    GetPrivateProfileStringW(L"Workspace", L"SourceBin", L"", working, 32768, config.c_str());
    GetPrivateProfileStringW(L"Workspace", L"ClientPath", L"", installed, 32768, config.c_str());
    auto executable = installed[0] ? std::wstring(installed) : directory + L"\\" + client;
    auto command = L"\"" + executable + L"\" -L 127.0.0.1 12000";
    std::vector<wchar_t> mutableCommand(command.begin(), command.end()); mutableCommand.push_back(0);
    STARTUPINFOW start{}; start.cb = sizeof(start); PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE, 0, nullptr, working, &start, &process)) {
        MessageBoxW(nullptr, L"Unable to start the custom client. Keep this launcher, client, DLL, configuration, and overlay bundle together.", L"Spell Workspace", MB_OK | MB_ICONERROR);
        return 1;
    }
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return 0;
}
