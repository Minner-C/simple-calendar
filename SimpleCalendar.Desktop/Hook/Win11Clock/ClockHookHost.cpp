// SimpleCalendar Win11 时钟 Hook 注入器
// 用法：
//   ClockHookHost.exe           将 ClockHookDll.dll 注入当前会话的 explorer.exe
//   ClockHookHost.exe -u        发送停止信号，卸载 Hook（时钟在下次系统刷新时恢复）

#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>

static DWORD FindExplorerPid()
{
    // 注入目标必须是任务栏（Shell_TrayWnd）的属主 explorer 进程
    HWND hTaskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
    if (hTaskbar)
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(hTaskbar, &pid);
        if (pid)
        {
            wchar_t exePath[MAX_PATH] = {};
            HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (hProc)
            {
                DWORD size = MAX_PATH;
                QueryFullProcessImageNameW(hProc, 0, exePath, &size);
                CloseHandle(hProc);
                const wchar_t* name = wcsrchr(exePath, L'\\');
                name = name ? name + 1 : exePath;
                if (_wcsicmp(name, L"explorer.exe") == 0)
                    return pid;
            }
        }
    }

    // 回退：当前会话的第一个 explorer.exe
    DWORD mySession = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &mySession);

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE)
        return 0;

    DWORD pid = 0;
    PROCESSENTRY32W pe{ sizeof(pe) };
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            if (_wcsicmp(pe.szExeFile, L"explorer.exe") == 0)
            {
                DWORD session = 0;
                ProcessIdToSessionId(pe.th32ProcessID, &session);
                if (session == mySession)
                {
                    pid = pe.th32ProcessID;
                    break;
                }
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return pid;
}

static int Uninstall()
{
    HANDLE ev = OpenEventW(EVENT_MODIFY_STATE, FALSE, L"SimpleClockHook_Stop");
    if (!ev)
    {
        wprintf(L"未找到运行中的 Hook（事件不存在，可能未注入）\n");
        return 1;
    }
    SetEvent(ev);
    CloseHandle(ev);
    wprintf(L"已发送停止信号，Hook 将自行卸载\n");
    return 0;
}

static int InstallWith(const wchar_t* dllPath, CLSID tapClsid, const wchar_t* xamlDiagPath)
{
    DWORD pid = FindExplorerPid();
    if (!pid)
    {
        wprintf(L"找不到 explorer.exe\n");
        return 1;
    }
    wprintf(L"目标 explorer.exe pid=%lu\n", pid);

    // 官方调试器模式：从外部进程调用 InitializeXamlDiagnosticsEx，
    // XAML 诊断子系统会自己把 TAP DLL 注入目标进程并激活。
    HMODULE wux = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr,
                                 LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!wux)
    {
        wprintf(L"加载 Windows.UI.Xaml.dll 失败: %lu\n", GetLastError());
        return 1;
    }

    typedef HRESULT(WINAPI* InitializeXamlDiagnosticsEx_t)(
        LPCWSTR endPointName, DWORD pid, LPCWSTR wszDllXamlDiagnostics,
        LPCWSTR wszTAPDllName, CLSID tapClsid, LPCWSTR wszInitializationData);

    auto ixde = reinterpret_cast<InitializeXamlDiagnosticsEx_t>(
        GetProcAddress(wux, "InitializeXamlDiagnosticsEx"));
    if (!ixde)
    {
        wprintf(L"找不到 InitializeXamlDiagnosticsEx 导出\n");
        return 1;
    }

    HRESULT hr = E_FAIL;
    for (int i = 0; i < 10000; i++)
    {
        wchar_t connectionName[256];
        swprintf_s(connectionName, L"VisualDiagConnection%d", i + 1);
        hr = ixde(connectionName, pid, xamlDiagPath, dllPath, tapClsid, nullptr);
        if (hr != HRESULT_FROM_WIN32(ERROR_NOT_FOUND))
            break;
    }

    if (FAILED(hr))
    {
        wprintf(L"InitializeXamlDiagnosticsEx 失败: 0x%08lx\n", hr);
        return 1;
    }

    wprintf(L"注入请求成功（XAML 诊断子系统将把 DLL 载入 explorer）\n");
    wprintf(L"用 ClockHookHost.exe -u 卸载\n");
    return 0;
}

static int Install(const wchar_t* dllPath)
{
    // 与 ClockHookDll.cpp 中的 CLSID_SimpleClockTAP 一致
    // {3F6A2C1E-9B4D-4A7F-8C5E-1D2B3A4C5E6F}
    CLSID tapClsid = { 0x3f6a2c1e, 0x9b4d, 0x4a7f,
                       { 0x8c, 0x5e, 0x1d, 0x2b, 0x3a, 0x4c, 0x5e, 0x6f } };
    return InstallWith(dllPath, tapClsid, L"");
}

// 对照实验：注入 VS 官方 UwpTap（机制是否可用的判定性测试）
static int InstallVsTap()
{
    // VS 官方 TAP 的默认 CLSID（来自 XAML 源码 XamlDiagnostics.cpp）
    // {28CB4DF8-85EB-46EE-8D71-C614C2305F74}
    CLSID vsClsid = { 0x28cb4df8, 0x85eb, 0x46ee,
                      { 0x8d, 0x71, 0xc6, 0x14, 0xc2, 0x30, 0x5f, 0x74 } };
    const wchar_t* vsTap =
        L"C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\Common7\\IDE\\CommonExtensions\\Microsoft\\XamlDiagnostics\\x64\\Microsoft.VisualStudio.DesignTools.UwpTap.dll";
    const wchar_t* xamlDiag =
        L"C:\\Program Files (x86)\\Windows Kits\\10\\bin\\x64\\XamlDiagnostics\\xamldiagnostics.dll";
    return InstallWith(vsTap, vsClsid, xamlDiag);
}

// 对照实验：我们自己的 DLL，但用 ASCII 路径 + 官方 xamldiagnostics.dll
static int InstallOursClean()
{
    CLSID tapClsid = { 0x3f6a2c1e, 0x9b4d, 0x4a7f,
                       { 0x8c, 0x5e, 0x1d, 0x2b, 0x3a, 0x4c, 0x5e, 0x6f } };
    const wchar_t* xamlDiag =
        L"C:\\Program Files (x86)\\Windows Kits\\10\\bin\\x64\\XamlDiagnostics\\xamldiagnostics.dll";
    return InstallWith(L"C:\\ClockHookTest\\ClockHookDll.dll", tapClsid, xamlDiag);
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc > 1 && (wcscmp(argv[1], L"-u") == 0 || wcscmp(argv[1], L"/u") == 0))
        return Uninstall();
    if (argc > 1 && wcscmp(argv[1], L"-vstest") == 0)
        return InstallVsTap();
    if (argc > 1 && wcscmp(argv[1], L"-clean") == 0)
        return InstallOursClean();

    // 默认 DLL 与宿主同目录
    wchar_t dllPath[MAX_PATH];
    GetModuleFileNameW(nullptr, dllPath, MAX_PATH);
    wchar_t* slash = wcsrchr(dllPath, L'\\');
    if (slash) *(slash + 1) = L'\0';
    wcscat_s(dllPath, L"ClockHookDll.dll");

    if (GetFileAttributesW(dllPath) == INVALID_FILE_ATTRIBUTES)
    {
        wprintf(L"找不到 DLL: %ls\n", dllPath);
        return 1;
    }

    return Install(dllPath);
}
