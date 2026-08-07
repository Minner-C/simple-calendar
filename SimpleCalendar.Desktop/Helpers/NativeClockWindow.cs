// NativeClockWindow.cs - 原生Win32时钟窗口
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimpleCalendar.Helpers
{
    public class NativeClockWindow
    {
        private const string CLASS_NAME = "SimpleCalendarNativeClock";
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        
        [DllImport("user32.dll")]
        private static extern bool RegisterClass(ref WNDCLASS lpWndClass);
        
        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        
        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        
        [StructLayout(LayoutKind.Sequential)]
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
        
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;
        
        private IntPtr _hwnd;
        private Thread _messageThread;
        
        public void Create()
        {
            _messageThread = new Thread(() =>
            {
                var wndClass = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(new WndProcDelegate(WindowProc)),
                    hInstance = Marshal.GetHINSTANCE(typeof(NativeClockWindow).Module),
                    lpszClassName = CLASS_NAME
                };
                
                RegisterClass(ref wndClass);
                
                // 查找任务栏窗口
                IntPtr hTaskbar = FindWindow("Shell_TrayWnd", null);
                
                if (hTaskbar != IntPtr.Zero)
                {
                    // 创建子窗口
                    _hwnd = CreateWindowEx(
                        WS_EX_LAYERED,
                        CLASS_NAME,
                        null,
                        WS_CHILD | WS_VISIBLE,
                        0, 0, 80, 48,
                        hTaskbar,  // 父窗口为任务栏
                        IntPtr.Zero,
                        wndClass.hInstance,
                        IntPtr.Zero);
                    
                    if (_hwnd != IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NativeClock] 窗口创建成功: {_hwnd}");
                        
                        // 设置为最顶层
                        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    }
                }
                
                // 消息循环
                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            });
            
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
        }
        
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProcDelegate _wndProc = WindowProc;
        
        private static IntPtr WindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            return DefWindowProc(hWnd, uMsg, wParam, lParam);
        }
        
        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);
        
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
    }
}
