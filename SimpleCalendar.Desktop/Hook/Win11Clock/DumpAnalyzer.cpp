// 崩溃 dump 分析器（手工解析 minidump 格式，dbghelp 仅用于符号和栈回溯）
// 用法: DumpAnalyzer.exe <dump文件>
#include <windows.h>
#include <dbghelp.h>
#include <stdio.h>
#include <vector>

#pragma comment(lib, "dbghelp.lib")

#define P(...) do { printf(__VA_ARGS__); fflush(stdout); } while (0)

static std::vector<BYTE> g_dump;

#pragma pack(push, 4)
struct MDHeader {
    ULONG Signature, Version, NumberOfStreams, StreamDirectoryRva;
    ULONG CheckSum, TimeDateStamp;
    ULONG64 Flags;
};
struct MDDirectory {
    ULONG StreamType, DataSize, Rva;
};
struct MDException {
    ULONG ExceptionCode, ExceptionFlags;
    ULONG64 ExceptionRecord, ExceptionAddress;
    ULONG NumberParameters, __unused;
    ULONG64 ExceptionInformation[15];
};
struct MDExceptionStream {
    ULONG ThreadId, __alignment;
    MDException ExceptionRecord;
    ULONG ThreadContextSize, ThreadContextRva;
};
#pragma pack(pop)

struct Mem64Desc { ULONG64 start, size; };
struct Mem32Desc { ULONG64 start; ULONG sizeLoc, rvaLoc; };

struct MemRange { DWORD64 start, size, fileRva; };
static std::vector<MemRange> g_ranges;

static BOOL CALLBACK DumpReadMemory(HANDLE, DWORD64 addr, PVOID buffer, DWORD size, LPDWORD bytesRead)
{
    *bytesRead = 0;
    for (auto& r : g_ranges)
    {
        if (addr >= r.start && addr + size <= r.start + r.size)
        {
            memcpy(buffer, g_dump.data() + r.fileRva + (addr - r.start), size);
            *bytesRead = size;
            return TRUE;
        }
    }
    return FALSE;
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc < 2)
    {
        P("usage: DumpAnalyzer.exe <dump.dmp>\n");
        return 1;
    }

    HANDLE f = CreateFileW(argv[1], GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE)
    {
        P("open dump failed: %lu\n", GetLastError());
        return 1;
    }
    LARGE_INTEGER fsize;
    GetFileSizeEx(f, &fsize);
    g_dump.resize((size_t)fsize.QuadPart);
    DWORD read = 0;
    ReadFile(f, g_dump.data(), (DWORD)g_dump.size(), &read, nullptr);
    CloseHandle(f);
    P("dump loaded: %zu bytes\n", g_dump.size());

    auto& hdr = *(MDHeader*)g_dump.data();
    if (hdr.Signature != 0x504D444D)
    {
        P("not a minidump\n");
        return 1;
    }
    P("streams: %lu\n", hdr.NumberOfStreams);

    auto dirs = (MDDirectory*)(g_dump.data() + hdr.StreamDirectoryRva);

    MDExceptionStream* ex = nullptr;
    for (ULONG i = 0; i < hdr.NumberOfStreams; i++)
    {
        ULONG type = dirs[i].StreamType;
        BYTE* data = g_dump.data() + dirs[i].Rva;
        if (type == 6) // ExceptionStream
        {
            ex = (MDExceptionStream*)data;
        }
        else if (type == 9) // Memory64ListStream
        {
            ULONG64 count = *(ULONG64*)data;
            ULONG64 rva = *(ULONG64*)(data + 8);
            auto desc = (Mem64Desc*)(data + 16);
            for (ULONG64 j = 0; j < count; j++)
            {
                g_ranges.push_back({ desc[j].start, desc[j].size, rva });
                rva += desc[j].size;
            }
        }
        else if (type == 4) // ModuleListStream（在后面的模块注册阶段处理）
        {
        }
        else if (type == 5) // MemoryListStream: ULONG count; {ULONG64 start; ULONG32 size; RVA rva;}[]
        {
            ULONG streamBytes = dirs[i].DataSize;
            ULONG count = (streamBytes - 4) / 16;
            auto desc = (Mem32Desc*)(data + 4);
            for (ULONG j = 0; j < count; j++)
                g_ranges.push_back({ desc[j].start, desc[j].sizeLoc, desc[j].rvaLoc });
        }
    }

    if (!ex)
    {
        P("no exception stream\n");
        return 1;
    }

    P("Exception code: 0x%08lx addr: 0x%llx thread: %lu\n",
      ex->ExceptionRecord.ExceptionCode,
      (unsigned long long)ex->ExceptionRecord.ExceptionAddress, ex->ThreadId);
    if (ex->ExceptionRecord.NumberParameters >= 2)
        P("fastfail subcode: %llu\n",
          (unsigned long long)ex->ExceptionRecord.ExceptionInformation[1]);
    P("memory ranges: %zu\n", g_ranges.size());

    HANDLE hProc = GetCurrentProcess();
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS);
    if (!SymInitialize(hProc, nullptr, FALSE))
    {
        P("SymInitialize failed: %lu\n", GetLastError());
        return 1;
    }

    P("SymInitialize ok\n");

    // 手工解析模块列表（type 4 = ModuleListStream）
    for (ULONG i = 0; i < hdr.NumberOfStreams; i++)
    {
        if (dirs[i].StreamType != 4) continue;
        BYTE* data = g_dump.data() + dirs[i].Rva;
        ULONG count = *(ULONG*)data;
        BYTE* mod = data + 4;
        BYTE* end = g_dump.data() + g_dump.size();
        for (ULONG j = 0; j < count; j++, mod += 108)
        {
            if (mod + 108 > end) break;
            ULONG64 base = *(ULONG64*)mod;
            ULONG sizeOfImage = *(ULONG*)(mod + 8);
            ULONG nameRva = *(ULONG*)(mod + 20);
            if (nameRva + 4 >= g_dump.size())
                continue;
            ULONG nameLen = *(ULONG*)(g_dump.data() + nameRva);
            if (nameRva + 4 + nameLen > g_dump.size())
                continue;
            WCHAR name[MAX_PATH] = {};
            ULONG copyLen = min(nameLen, (ULONG)(sizeof(name) - 2));
            memcpy(name, g_dump.data() + nameRva + 4, copyLen);
            P("  mod %lu: %ls base=0x%llx size=0x%lx\n", j, name, (unsigned long long)base, sizeOfImage);
            SymLoadModuleExW(hProc, nullptr, name, nullptr, base, sizeOfImage, nullptr, 0);
        }
        P("modules registered: %lu\n", count);
    }

    CONTEXT ctx = {};
    memcpy(&ctx, g_dump.data() + ex->ThreadContextRva,
           min((DWORD)sizeof(ctx), ex->ThreadContextSize));

    P("RIP=0x%llx RSP=0x%llx RBP=0x%llx\n\nCall stack:\n", ctx.Rip, ctx.Rsp, ctx.Rbp);

    STACKFRAME64 frame = {};
    frame.AddrPC.Offset = ctx.Rip;
    frame.AddrPC.Mode = AddrModeFlat;
    frame.AddrStack.Offset = ctx.Rsp;
    frame.AddrStack.Mode = AddrModeFlat;
    frame.AddrFrame.Offset = ctx.Rbp;
    frame.AddrFrame.Mode = AddrModeFlat;

    for (int i = 0; i < 64; i++)
    {
        if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, hProc, (HANDLE)(ULONG_PTR)ex->ThreadId,
                         &frame, &ctx, DumpReadMemory,
                         SymFunctionTableAccess64, SymGetModuleBase64, nullptr))
            break;

        DWORD64 addr = frame.AddrPC.Offset;
        char symBuf[sizeof(SYMBOL_INFO) + 256] = {};
        auto sym = (SYMBOL_INFO*)symBuf;
        sym->SizeOfStruct = sizeof(SYMBOL_INFO);
        sym->MaxNameLen = 256;
        DWORD64 disp = 0;

        DWORD64 modBase = SymGetModuleBase64(hProc, addr);
        char modName[MAX_PATH] = "?";
        if (modBase)
        {
            IMAGEHLP_MODULE64 mi = { sizeof(mi) };
            if (SymGetModuleInfo64(hProc, addr, &mi))
                strcpy_s(modName, mi.ModuleName);
        }

        if (SymFromAddr(hProc, addr, &disp, sym))
            P("  %02d %s!%s+0x%llx\n", i, modName, sym->Name, (unsigned long long)disp);
        else if (modBase)
            P("  %02d %s+0x%llx\n", i, modName, (unsigned long long)(addr - modBase));
        else
            P("  %02d 0x%llx (no module)\n", i, (unsigned long long)addr);

        if (frame.AddrReturn.Offset == 0)
            break;
    }

    // StackWalk 在 fiber 栈上可能失败，追加原始栈扫描：找出落在模块内的 qword
    P("\nRaw stack scan (qwords pointing into modules):\n");
    for (DWORD64 sp = ctx.Rsp; sp < ctx.Rsp + 0x800; sp += 8)
    {
        DWORD64 val = 0;
        DWORD got = 0;
        if (!DumpReadMemory(hProc, sp, &val, 8, &got) || got != 8)
            continue;

        DWORD64 modBase = SymGetModuleBase64(hProc, val);
        if (!modBase)
            continue;

        char modName[MAX_PATH] = "?";
        IMAGEHLP_MODULE64 mi = { sizeof(mi) };
        if (SymGetModuleInfo64(hProc, val, &mi))
            strcpy_s(modName, mi.ModuleName);

        char symBuf[sizeof(SYMBOL_INFO) + 256] = {};
        auto sym = (SYMBOL_INFO*)symBuf;
        sym->SizeOfStruct = sizeof(SYMBOL_INFO);
        sym->MaxNameLen = 256;
        DWORD64 disp = 0;

        if (SymFromAddr(hProc, val, &disp, sym))
            P("  [RSP+0x%03llx] %s!%s+0x%llx\n", (unsigned long long)(sp - ctx.Rsp),
              modName, sym->Name, (unsigned long long)disp);
        else
            P("  [RSP+0x%03llx] %s+0x%llx\n", (unsigned long long)(sp - ctx.Rsp),
              modName, (unsigned long long)(val - modBase));
    }

    SymCleanup(hProc);
    P("done\n");
    return 0;
}
