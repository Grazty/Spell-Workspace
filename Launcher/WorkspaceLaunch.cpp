// Copyright (C) 2026 Revive101. AGPL-3.0-or-later.
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <string>
#include <vector>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR arguments, int show) {
    wchar_t path[32768];
    const auto length = GetModuleFileNameW(nullptr, path, 32768);
    if (!length || length == 32768) { return 1; }
    const std::wstring full(path, length);
    const auto root = full.substr(0, full.find_last_of(L"\\/"));
    const auto executable = root + L"\\Application\\SpellWorkspace.exe";
    auto command = L"\"" + executable + L"\"";
    if (arguments && *arguments) { command += L" "; command += arguments; }
    std::vector<wchar_t> mutableCommand(command.begin(), command.end());
    mutableCommand.push_back(0);
    STARTUPINFOW start{};
    start.cb = sizeof(start);
    start.dwFlags = STARTF_USESHOWWINDOW;
    start.wShowWindow = static_cast<WORD>(show);
    PROCESS_INFORMATION process{};
    // Keep the caller's working directory so relative command-line paths retain their meaning.
    if (!CreateProcessW(executable.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &start, &process)) {
        MessageBoxW(nullptr, L"Unable to start Spell Workspace. Extract the complete ZIP and keep the Application folder beside SpellWorkspace.exe.", L"Spell Workspace", MB_OK | MB_ICONERROR);
        return 1;
    }
    CloseHandle(process.hThread);
    // Pump messages while waiting; diagnostics and scripts receive the actual app exit code.
    while (MsgWaitForMultipleObjects(1, &process.hProcess, FALSE, INFINITE, QS_ALLINPUT) == WAIT_OBJECT_0 + 1) {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
    }
    DWORD result = 1;
    GetExitCodeProcess(process.hProcess, &result);
    CloseHandle(process.hProcess);
    return static_cast<int>(result);
}
