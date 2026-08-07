using System.Runtime.InteropServices;

namespace SimpleCalendar.Helpers;

/// <summary>
/// Win32 API 声明，用于窗口定位、任务栏交互和系统时钟控制
/// </summary>
public static class NativeMethods
{
    // === DWM相关（对应ElevenClock的blurwindow.py） ===

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public uint AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// 对应ElevenClock blurwindow.py第49-64行的ExtendFrameIntoClientArea
    /// 让DWM框架覆盖整个客户区（margins = -1表示全覆盖）
    /// </summary>
    public static void ExtendFrameIntoClientArea(IntPtr hwnd)
    {
        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>
    /// 对应ElevenClock blurwindow.py第76-114行的GlobalBlur/ApplyBlur
    /// AccentState=4表示ACCENT_ENABLE_ACRYLICBLURBEHIND
    /// GradientColor格式：AABBGGRR（little-endian）
    /// </summary>
    public static void ApplyAcrylicBlur(IntPtr hwnd, bool dark = true)
    {
        var accent = new ACCENT_POLICY
        {
            AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
            AccentFlags = 2,
            // 对应blurwindow.py第83行的gradientColor
            // dark模式：#21212140 → alpha=0x40, blue=0x21, green=0x21, red=0x21
            // light模式：#eeeeee40 → alpha=0x40, blue=0xee, green=0xee, red=0xee
            GradientColor = (uint)(dark ? 0x40212121 : 0x40EEEEEE),
            AnimationId = 0
        };

        var data = new WINDOWCOMPOSITIONATTRIBDATA
        {
            Attribute = 19, // WCA_ACCENT_POLICY
            Data = Marshal.AllocHGlobal(Marshal.SizeOf(accent)),
            SizeOfData = Marshal.SizeOf(accent)
        };
        Marshal.StructureToPtr(accent, data.Data, false);

        SetWindowCompositionAttribute(hwnd, ref data);

        // 对应blurwindow.py第101-104行的Dark模式设置
        if (dark)
        {
            data.Attribute = 26; // WCA_USEDARKMODECOLORS
            SetWindowCompositionAttribute(hwnd, ref data);
        }

        // 对应blurwindow.py第107-108行的DwmSetWindowAttribute（圆角边框）
        int cornerType = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref cornerType, sizeof(int));

        Marshal.FreeHGlobal(data.Data);
    }

    // 窗口样式
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_SYSMENU = 0x00080000;

    // 消息
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 0x0003;

    // 显示器信息
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // 任务栏边缘
    public enum ABEdge : int
    {
        ABE_LEFT = 0,
        ABE_TOP = 1,
        ABE_RIGHT = 2,
        ABE_BOTTOM = 3
    }

    // 任务栏状态
    public enum ABState : int
    {
        ABS_AUTOHIDE = 0x0000001,
        ABS_ALWAYSONTOP = 0x0000002
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public ABEdge uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll")]
    public static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    public const uint ABM_GETTASKBARPOS = 0x00000005;
    public const uint ABM_GETSTATE = 0x00000004;

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    public static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // 窗口样式常量
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;

    // === GDI 绘制相关 API ===
    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFont(int nHeight, int nWidth, int nEscapement, int nOrientation,
        int fnWeight, uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut,
        uint fdwCharSet, uint fdwOutputPrecision, uint fdwClipPrecision,
        uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);

    [DllImport("gdi32.dll")]
    public static extern bool SetTextColor(IntPtr hdc, int crColor);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr hdc, int iBkMode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool TextOut(IntPtr hdc, int x, int y, string lpString, int nCount);

    [DllImport("gdi32.dll")]
    public static extern bool Rectangle(IntPtr hdc, int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(int crColor);

    [DllImport("user32.dll")]
    public static extern bool FillRect(IntPtr hdc, [In] ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, ref RECT lpRect, bool bErase);

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextAlign(IntPtr hdc, uint fMode);

    // === 分层窗口 API（UpdateLayeredWindow）===
    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend,
        uint dwFlags);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSec, uint dwOffset);

    [DllImport("gdi32.dll")]
    public static extern bool AlphaBlend(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int nWidthSrc, int nHeightSrc, BLENDFUNCTION blendFunction);

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    public const int TRANSPARENT = 1;
    public const int TA_LEFT = 0;
    public const int TA_RIGHT = 2;
    public const int TA_CENTER = 6;
    public const int TA_TOP = 0;
    public const int TA_BOTTOM = 8;
    public const int TA_BASELINE = 24;

    public const int FW_NORMAL = 400;
    public const int FW_MEDIUM = 500;
    public const int FW_SEMIBOLD = 600;
    public const int FW_BOLD = 700;

    public const uint DEFAULT_CHARSET = 1;
    public const uint OUT_DEFAULT_PRECIS = 0;
    public const uint CLIP_DEFAULT_PRECIS = 0;
    public const uint CLEARTYPE_QUALITY = 5;
    public const uint DEFAULT_PITCH = 0;
    public const uint VARIABLE_PITCH = 2;

    public const int LOGPIXELSY = 90;

    // WM 消息常量
    public const int WM_PAINT = 0x000F;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_TIMER = 0x0113;
    public const int WM_DESTROY = 0x0002;
    public const int WM_NCCREATE = 0x0081;
    public const int WM_NCDESTROY = 0x0082;
    public const int CS_HREDRAW = 0x0002;
    public const int CS_VREDRAW = 0x0001;
    public const int COLOR_WINDOW = 5;
    public const int IDC_ARROW = 32512;
    public const int GWLP_USERDATA = -21;
    public const int GWLP_WNDPROC = -4;

    // 窗口过程委托类型
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct WNDCLASS
    {
        public int style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    // DPI_AWARENESS_CONTEXT_UNAWARE
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new IntPtr(-1);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    public static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        return new IntPtr(SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    public static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8)
            return GetWindowLongPtr64(hWnd, nIndex);
        return new IntPtr(GetWindowLong(hWnd, nIndex));
    }

    /// <summary>
    /// 对应ElevenClock tools.py第104-105行：isTaskbarDark()
    /// 通过注册表判断任务栏是否使用深色主题
    /// </summary>
    public static bool IsTaskbarDark()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            key?.Close();
            return value is 0;  // 0=深色，1=浅色
        }
        catch { return true; }  // 默认深色
    }

    /// <summary>
    /// 获取任务栏位置信息
    /// </summary>
    public static (ABEdge Edge, RECT Bounds) GetTaskbarPosition()
    {
        var data = new APPBARDATA();
        data.cbSize = Marshal.SizeOf(data);
        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        return (data.uEdge, data.rc);
    }

    /// <summary>
    /// 隐藏/显示 Windows 系统托盘时钟
    /// 同时修改 ShowClock 和 ShowSystrayClock 注册表键，并重启 Explorer 使其生效
    /// </summary>
    public static void SetSystemClockVisible(bool visible)
    {
        var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        if (key != null)
        {
            int val = visible ? 1 : 0;
            key.SetValue("ShowClock", val, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("ShowSystrayClock", val, Microsoft.Win32.RegistryValueKind.DWord);
            key.Close();
        }

        // 重启 Explorer 使注册表变更生效
        RestartExplorer();
    }

    /// <summary>
    /// 检查系统时钟是否可见
    /// </summary>
    public static bool IsSystemClockVisible()
    {
        var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        var value = key?.GetValue("ShowClock");
        key?.Close();
        return value is not 0;
    }

    /// <summary>
    /// 重启 Explorer.exe 使注册表变更生效
    /// </summary>
    public static void RestartExplorer()
    {
        try
        {
            // 找到 Explorer 进程
            var explorers = System.Diagnostics.Process.GetProcessesByName("explorer");
            if (explorers.Length > 0)
            {
                // 优雅地关闭 Explorer
                foreach (var proc in explorers)
                {
                    proc.CloseMainWindow();
                }
                System.Threading.Thread.Sleep(500);

                // 如果还没退出，强制结束
                foreach (var proc in explorers)
                {
                    if (!proc.HasExited)
                        proc.Kill();
                }
            }

            // 重新启动 Explorer
            System.Threading.Thread.Sleep(1000);
            System.Diagnostics.Process.Start("explorer.exe");
        }
        catch
        {
            // 如果重启失败，尝试直接启动
            try { System.Diagnostics.Process.Start("explorer.exe"); } catch { }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_SETTINGCHANGE = 0x001A;

    /// <summary>
    /// 通知 Explorer 设置已更改，使其刷新
    /// </summary>
    private static void NotifyExplorerRefresh()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero)
        {
            PostMessage(taskbar, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // === EnumChildWindows 递归枚举 ===
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// 查找系统时钟窗口句柄
    /// Win11 24H2：枚举所有子窗口，找到位于任务栏右端最小的子窗口
    /// 系统时钟通常是Shell_TrayWnd右端的一个小矩形子窗口
    /// </summary>
    public static IntPtr FindSystemClockWindow()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
            taskbar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
        if (taskbar == IntPtr.Zero)
            return IntPtr.Zero;
    
        // 方案1：直接查找已知类名（Win10/早期Win11）
        string[] classNames = { "TrayClockWClass", "ClockButton" };
        foreach (var className in classNames)
        {
            var clock = FindWindowEx(taskbar, IntPtr.Zero, className, null);
            if (clock != IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine($"[Native] 找到时钟窗口: class={className}, hwnd={clock}");
                return clock;
            }
        }

        // 方案1.5：找TrayNotifyWnd（托盘通知区域），系统时钟在它的左边
        var trayNotify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify != IntPtr.Zero)
        {
            GetWindowRect(trayNotify, out var trayRect);
            System.Diagnostics.Debug.WriteLine($"[Native] 找到TrayNotifyWnd: L={trayRect.Left} T={trayRect.Top} R={trayRect.Right} B={trayRect.Bottom}");
            // 返回TrayNotifyWnd，上层代码会根据它的Left来定位时钟
            return trayNotify;
        }

        // 方案2：递归枚举所有子窗口，寻找包含clock/time的
        IntPtr foundClock = IntPtr.Zero;
        EnumChildWindows(taskbar, (child, lParam) =>
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(child, sb, sb.Capacity);
            var cls = sb.ToString();
            GetWindowRect(child, out var rect);
            System.Diagnostics.Debug.WriteLine($"[Native] 子窗口: class={cls}, pos=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}), size={rect.Width}x{rect.Height}");

            if (cls.IndexOf("clock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Native] 识别为时钟窗口: {cls}, size={rect.Width}x{rect.Height}");
                foundClock = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (foundClock != IntPtr.Zero)
            return foundClock;

        // 方案3：枚举所有子窗口，找位于任务栏右端且最小的子窗口
        // 系统时钟是右端最小的子窗口（通常约80x48像素），不像TrayNotifyWnd那样很大
        GetWindowRect(taskbar, out var taskbarRect);
        IntPtr bestClock = IntPtr.Zero;
        int bestClockWidth = int.MaxValue;
        int bestClockRight = 0;

        EnumChildWindows(taskbar, (child, lParam) =>
        {
            GetWindowRect(child, out var rect);
            // 只考虑位于任务栏右端的子窗口（Right >= 任务栏Right - 10像素）
            if (rect.Right >= taskbarRect.Right - 10 && rect.Width > 0 && rect.Height > 0)
            {
                // 找右端最小的子窗口——系统时钟通常比TrayNotifyWnd小得多
                if (rect.Right > bestClockRight || (rect.Right == bestClockRight && rect.Width < bestClockWidth))
                {
                    bestClockRight = rect.Right;
                    bestClockWidth = rect.Width;
                    bestClock = child;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (bestClock != IntPtr.Zero)
        {
            GetWindowRect(bestClock, out var rect);
            var sb = new System.Text.StringBuilder(256);
            GetClassName(bestClock, sb, sb.Capacity);
            System.Diagnostics.Debug.WriteLine($"[Native] 右端最小子窗口: class={sb.ToString()}, pos=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}), size={rect.Width}x{rect.Height}");
        }

        return bestClock;
    }

    /// <summary>
    /// 获取窗口的矩形位置（屏幕坐标）
    /// </summary>
    public static RECT GetWindowRectangle(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out var rect);
        return rect;
    }

    /// <summary>
    /// 隐藏系统时钟窗口
    /// </summary>
    public static bool HideSystemClock()
    {
        var clockHwnd = FindSystemClockWindow();
        if (clockHwnd != IntPtr.Zero)
        {
            return ShowWindow(clockHwnd, SW_HIDE);
        }
        return false;
    }

    /// <summary>
    /// 显示系统时钟窗口
    /// </summary>
    public static bool ShowSystemClock()
    {
        var clockHwnd = FindSystemClockWindow();
        if (clockHwnd != IntPtr.Zero)
        {
            return ShowWindow(clockHwnd, SW_SHOW);
        }
        return false;
    }
}
