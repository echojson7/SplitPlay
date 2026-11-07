// SPDX-License-Identifier: MIT
// termwrap-probe.exe: runs TermWrap's own patch search (upstream TermWrap\Patch.cpp, built in with this file) on a
// private copy of termsrv.dll and prints what TermWrap reports, so that the SplitPlay service can tell whether a
// termsrv.dll that Windows Update brought can be patched before a desk tries to log on. The running TermService is
// not touched: the DLL is mapped into this process without running its code (DONT_RESOLVE_DLL_REFERENCES), as
// upstream's TermWrapTest does.
//
//   termwrap-probe.exe [path\to\termsrv.dll]
//
// stdout: "probe: termsrv <path>", then one line per TermWrap message (e.g. "DefPolicyPatch not found"), then
// "probe: done". The service decides which messages matter (TermWrapGuard.Classify).
// Exit codes: 0 the search ran to the end, 1 termsrv.dll cannot be loaded, 3 the search crashed.
#include <windows.h>
#include <stdio.h>

extern void patch(HMODULE hMod);

namespace {

HANDLE g_out = INVALID_HANDLE_VALUE;

void Write(const char* text, DWORD length)
{
    DWORD written = 0;
    WriteFile(g_out, text, length, &written, nullptr);
}

void WriteLine(const char* text)
{
    Write(text, lstrlenA(text));
    Write("\n", 1);
}

// TermWrap reports with OutputDebugStringA, which raises DBG_PRINTEXCEPTION_C: print the text and go on. WriteFile,
// not WriteConsole (upstream's test), so that the output also works through a pipe.
LONG NTAPI DebugPrintHandler(PEXCEPTION_POINTERS info)
{
    auto record = info->ExceptionRecord;
    if (record->ExceptionCode == DBG_PRINTEXCEPTION_C && record->NumberParameters >= 2)
    {
        auto length = static_cast<DWORD>(record->ExceptionInformation[0]);
        auto text = reinterpret_cast<const char*>(record->ExceptionInformation[1]);
        if (length > 1 && text)
        {
            // length counts the terminating NUL; TermWrap's messages end with '\n' already.
            Write(text, length - 1);
        }

        return EXCEPTION_CONTINUE_EXECUTION;
    }

#ifndef DBG_PRINTEXCEPTION_WIDE_C
#define DBG_PRINTEXCEPTION_WIDE_C ((DWORD)0x4001000AL)
#endif
    // Windows 10 and later may raise the wide form first; it also carries the ANSI text (parameters 2 and 3).
    if (record->ExceptionCode == DBG_PRINTEXCEPTION_WIDE_C && record->NumberParameters >= 4)
    {
        auto length = static_cast<DWORD>(record->ExceptionInformation[2]);
        auto text = reinterpret_cast<const char*>(record->ExceptionInformation[3]);
        if (length > 1 && text)
        {
            Write(text, length - 1);
        }

        return EXCEPTION_CONTINUE_EXECUTION;
    }

    return EXCEPTION_CONTINUE_SEARCH;
}

int RunPatch(HMODULE module)
{
    __try
    {
        patch(module);
        return 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 3;
    }
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    g_out = GetStdHandle(STD_OUTPUT_HANDLE);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

    wchar_t path[MAX_PATH * 2];
    if (argc >= 2)
    {
        lstrcpynW(path, argv[1], ARRAYSIZE(path));
    }
    else
    {
        UINT n = GetSystemDirectoryW(path, MAX_PATH);
        if (n == 0 || n >= MAX_PATH)
        {
            WriteLine("probe: no system directory");
            return 1;
        }

        lstrcatW(path, L"\\termsrv.dll");
    }

    char narrow[MAX_PATH * 4];
    WideCharToMultiByte(CP_UTF8, 0, path, -1, narrow, sizeof(narrow), nullptr, nullptr);
    Write("probe: termsrv ", 15);
    WriteLine(narrow);

    HMODULE module = LoadLibraryExW(path, nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!module)
    {
        char error[64];
        snprintf(error, sizeof(error), "probe: cannot load termsrv.dll (error %lu)", GetLastError());
        WriteLine(error);
        return 1;
    }

    AddVectoredExceptionHandler(TRUE, DebugPrintHandler);
    int code = RunPatch(module);
    WriteLine(code == 0 ? "probe: done" : "probe: crashed");
    return code;
}
