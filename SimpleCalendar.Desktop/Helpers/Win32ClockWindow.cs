using System;
using System.Runtime.InteropServices;
using System.Threading;
using SimpleCalendar.Data;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 纯Win32 API实现的浮动时钟窗口（参考ElevenClock的QWidget实现）
    /// </summary>
    public class Win32ClockWindow : IDisposable
    {
        private const string WINDOW_CLASS = "SimpleCalendarWin32Clock";
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _hInstance = IntPtr.Zero;
        private System.Threading.Timer? _updateTimer;
        private System.Threading.Timer? _raiseTimer;
        private bool _disposed = false;

        // Win32 API声明
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern bool TextOut(IntPtr hdc, int x, int y, string lpString, int c);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateFont(int nHeight, int nWidth, int nEscapement, int nOrientation, 
            int fnWeight, uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet, 
            uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern int SetTextColor(IntPtr hdc, int crColor);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int iBkMode);

        // 常量 - 严格对应ElevenClock的Qt窗口标志
        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        // Qt.WindowStaysOnTopHint → WS_EX_TOPMOST
        private const uint WS_EX_TOPMOST = 0x00000008;
        // Qt.WA_TranslucentBackground → WS_EX_LAYERED
        private const uint WS_EX_LAYERED = 0x00080000;
        // Qt.Tool → WS_EX_TOOLWINDOW
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        // Qt.WA_ShowWithoutActivating → WS_EX_NOACTIVATE
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        // Qt.FramelessWindowHint → WS_POPUP
        private const uint WS_POPUP = 0x80000000;
        private const int SW_SHOW = 5;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_LBUTTONUP = 0x0202;
        private const int TRANSPARENT = 1;
        private readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // 结构体
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        // 委托
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        private WndProcDelegate? _wndProcDelegate;

        public event Action? Clicked;

        public Win32ClockWindow()
        {
            InitializeWindow();
        }

        private void InitializeWindow()
        {
            // ElevenClock: windll.shcore.SetProcessDpiAwareness(c_int(2))
            SetProcessDpiAwareness(2);
            
            _hInstance = Marshal.GetHINSTANCE(typeof(Win32ClockWindow).Module);

            // 注册窗口类
            _wndProcDelegate = WndProc;
            IntPtr pWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);

            WNDCLASS wc = new WNDCLASS();
            wc.style = CS_HREDRAW | CS_VREDRAW;
            wc.lpfnWndProc = pWndProc;
            wc.hInstance = _hInstance;
            wc.lpszClassName = WINDOW_CLASS;

            RegisterClass(ref wc);

            // 创建窗口 - 严格对应ElevenClock的Qt窗口标志：
            // Qt.WindowStaysOnTopHint → WS_EX_TOPMOST
            // Qt.Tool → WS_EX_TOOLWINDOW
            // Qt.WA_ShowWithoutActivating → WS_EX_NOACTIVATE
            // Qt.WA_TranslucentBackground → WS_EX_LAYERED
            // Qt.FramelessWindowHint → WS_POPUP
            uint exStyle = WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            uint style = WS_POPUP;

            _hwnd = CreateWindowEx(
                exStyle, WINDOW_CLASS, "SimpleCalendar Clock",
                style, 0, 0, 200, 50,
                IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create window");
            }

            // 定位窗口到任务栏右下角
            PositionWindow();

            // 显示窗口
            ShowWindow(_hwnd, SW_SHOW);
            UpdateWindow(_hwnd);

            // 启动定时器更新时间（每秒）
            _updateTimer = new System.Threading.Timer(_ => UpdateClockContent(), null, 0, 1000);

            // 启动定时器提升窗口层级（每100ms，参考ElevenClock）
            _raiseTimer = new System.Threading.Timer(_ => RaiseWindow(), null, 100, 100);
        }

        private void PositionWindow()
        {
            var workArea = System.Windows.SystemParameters.WorkArea;
            double screenHeight = System.Windows.SystemParameters.PrimaryScreenHeight;
            double taskbarHeight = screenHeight - workArea.Bottom;
            
            if (taskbarHeight < 10)
                taskbarHeight = 48;

            int width = 200;
            int height = (int)taskbarHeight;
            int x = (int)(workArea.Right - width);
            int y = (int)workArea.Bottom;

            SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE);
        }

        private void RaiseWindow()
        {
            if (_hwnd != IntPtr.Zero)
            {
                BringWindowToTop(_hwnd);
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        private void UpdateClockContent()
        {
            // 触发重绘
            if (_hwnd != IntPtr.Zero)
            {
                InvalidateRect(_hwnd, IntPtr.Zero, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            switch (uMsg)
            {
                case WM_PAINT:
                    OnPaint(hWnd);
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    Clicked?.Invoke();
                    return IntPtr.Zero;

                case WM_DESTROY:
                    return IntPtr.Zero;

                default:
                    return DefWindowProc(hWnd, uMsg, wParam, lParam);
            }
        }

        private void OnPaint(IntPtr hWnd)
        {
            IntPtr hdc = GetDC(hWnd);
            if (hdc == IntPtr.Zero)
                return;

            try
            {
                // 设置透明背景
                SetBkMode(hdc, TRANSPARENT);

                // 获取当前时间
                var now = DateTime.Now;
                string timeStr = now.ToString("HH:mm");
                string dateStr = now.ToString("MM/dd");
                
                var lunar = LunarCalendar.SolarToLunar(now.Year, now.Month, now.Day);
                string lunarStr = lunar.Day == 1 ? lunar.MonthCN : lunar.DayCN;

                // 创建字体
                IntPtr hFont = CreateFont(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
                IntPtr hOldFont = SelectObject(hdc, hFont);

                // 绘制时间（白色）
                SetTextColor(hdc, 0x00FFFFFF); // White
                TextOut(hdc, 10, 5, timeStr, timeStr.Length);

                // 绘制日期
                SetTextColor(hdc, 0x00EEEEEE); // Light gray
                TextOut(hdc, 10, 25, $"{dateStr} {lunarStr}", $"{dateStr} {lunarStr}".Length);

                // 清理
                SelectObject(hdc, hOldFont);
                DeleteObject(hFont);
            }
            finally
            {
                ReleaseDC(hWnd, hdc);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _updateTimer?.Dispose();
                _raiseTimer?.Dispose();
                
                if (_hwnd != IntPtr.Zero)
                {
                    ShowWindow(_hwnd, 0); // SW_HIDE
                    _hwnd = IntPtr.Zero;
                }
                
                _disposed = true;
            }
        }
    }
}
