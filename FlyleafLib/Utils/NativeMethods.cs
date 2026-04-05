using System.Drawing;
using System.Runtime.InteropServices;

namespace FlyleafLib;

public static partial class Utils
{
    public static class NativeMethods
    {
        public static WindowStyles SetWindowLong(nint hWnd, WindowStyles style)
            => (WindowStyles)SetWindowLong(hWnd, (int)WindowLongFlags.GWL_STYLE, (nint)style);

        public static WindowStylesEx SetWindowLong(nint hWnd, WindowStylesEx style)
            => (WindowStylesEx)SetWindowLong(hWnd, (int)WindowLongFlags.GWL_EXSTYLE, (nint)style);

        public static WindowStyles GetWindowLong(nint hWnd)
            => (WindowStyles)GetWindowLong(hWnd, (int)WindowLongFlags.GWL_STYLE);

        public static WindowStylesEx GetWindowLongEx(nint hWnd)
            => (WindowStylesEx)GetWindowLong(hWnd, (int)WindowLongFlags.GWL_EXSTYLE);

        public static nint GetWindowLong(nint hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);

        public static nint SetWindowLong(nint hWnd, int nIndex, nint dwNewLong)
            => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern nint GetWindowLongPtr32(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern nint SetWindowLongPtr32(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

        //[DllImport("user32.dll")]
        //public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("comctl32.dll")]
        public static extern bool SetWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll")]
        public static extern bool RemoveWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hWnd, WndProcMessages msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, UInt32 uFlags);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int StrCmpLogicalW(string psz1, string psz2);

        [DllImport("user32.dll")]
        public static extern int ShowCursor(bool bShow);

        [DllImport("user32.dll")]
        public static extern int GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT : IEquatable<POINT>
        {
            public static readonly POINT Empty = new();

            public int X;
            public int Y;

            public static bool operator ==(POINT left, POINT right) => left.X == right.X && left.Y == right.Y;
            public static bool operator !=(POINT left, POINT right) => !(left == right);
            public override bool Equals(object obj) => obj is POINT other && this == other;
            public bool Equals(POINT other) => this == other;
            public override int GetHashCode() => HashCode.Combine(X, Y);
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint TimeEndPeriod(uint uMilliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);
        [Flags]
        public enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED    = 0x00000040,
            ES_CONTINUOUS           = 0x80000000,
            ES_DISPLAY_REQUIRED     = 0x00000002,
            ES_SYSTEM_REQUIRED      = 0x00000001
        }

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("User32.dll")]
        public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

        [DllImport("User32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, ref RECT rectangle);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, ref RECT rectangle);

        [DllImport("user32.dll")]
        public static extern bool GetWindowInfo(IntPtr hwnd, ref WINDOWINFO pwi);

        [DllImport("user32.dll")]
        public static extern void SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);


        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWINFO
        {
            public uint cbSize;
            public RECT rcWindow;
            public RECT rcClient;
            public uint dwStyle;
            public uint dwExStyle;
            public uint dwWindowStatus;
            public uint cxWindowBorders;
            public uint cyWindowBorders;
            public ushort atomWindowType;
            public ushort wCreatorVersion;

            // Allows automatic initialization of "cbSize" with "new WINDOWINFO(null/true/false)".
            public WINDOWINFO(Boolean? filler) : this()
                => cbSize = (UInt32)Marshal.SizeOf(typeof(WINDOWINFO));

        }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [Flags]
        public enum SetWindowPosFlags : uint
        {
            SWP_ASYNCWINDOWPOS = 0x4000,
            SWP_DEFERERASE = 0x2000,
            SWP_DRAWFRAME = 0x0020,
            SWP_FRAMECHANGED = 0x0020,
            SWP_HIDEWINDOW = 0x0080,
            SWP_NOACTIVATE = 0x0010,
            SWP_NOCOPYBITS = 0x0100,
            SWP_NOMOVE = 0x0002,
            SWP_NOOWNERZORDER = 0x0200,
            SWP_NOREDRAW = 0x0008,
            SWP_NOREPOSITION = 0x0200,
            SWP_NOSENDCHANGING = 0x0400,
            SWP_NOSIZE = 0x0001,
            SWP_NOZORDER = 0x0004,
            SWP_SHOWWINDOW = 0x0040,
        }

        [Flags]
        public enum WindowLongFlags : int
        {
            GWL_EXSTYLE = -20,
            GWLP_HINSTANCE = -6,
            GWLP_HWNDPARENT = -8,
            GWL_ID = -12,
            GWL_STYLE = -16,
            GWL_USERDATA = -21,
            GWL_WNDPROC = -4,
            DWLP_USER = 0x8,
            DWLP_MSGRESULT = 0x0,
            DWLP_DLGPROC = 0x4
        }

        [Flags]
        public enum WindowStyles : uint
        {
            WS_BORDER = 0x800000,
            WS_CAPTION = 0xc00000,
            WS_CHILD = 0x40000000,
            WS_CLIPCHILDREN = 0x2000000,
            WS_CLIPSIBLINGS = 0x4000000,
            WS_DISABLED = 0x8000000,
            WS_DLGFRAME = 0x400000,
            WS_GROUP = 0x20000,
            WS_HSCROLL = 0x100000,
            WS_MAXIMIZE = 0x1000000,
            WS_MAXIMIZEBOX = 0x10000,
            WS_MINIMIZE = 0x20000000,
            WS_MINIMIZEBOX = 0x20000,
            WS_OVERLAPPED = 0x0,
            WS_OVERLAPPEDWINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_SIZEFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX,
            WS_POPUP = 0x80000000,
            WS_POPUPWINDOW = WS_POPUP | WS_BORDER | WS_SYSMENU,
            WS_SIZEFRAME = 0x40000,
            WS_SYSMENU = 0x80000,
            WS_TABSTOP = 0x10000,
            WS_THICKFRAME = 0x00040000,
            WS_VISIBLE = 0x10000000,
            WS_VSCROLL = 0x200000
        }

        [Flags]
        public enum WindowStylesEx : uint
        {
            WS_EX_ACCEPTFILES = 0x00000010,
            WS_EX_APPWINDOW = 0x00040000,
            WS_EX_CLIENTEDGE = 0x00000200,
            WS_EX_COMPOSITED = 0x02000000,
            WS_EX_CONTEXTHELP = 0x00000400,
            WS_EX_CONTROLPARENT = 0x00010000,
            WS_EX_DLGMODALFRAME = 0x00000001,
            WS_EX_LAYERED = 0x00080000,
            WS_EX_LAYOUTRTL = 0x00400000,
            WS_EX_LEFT = 0x00000000,
            WS_EX_LEFTSCROLLBAR = 0x00004000,
            WS_EX_LTRREADING = 0x00000000,
            WS_EX_MDICHILD = 0x00000040,
            WS_EX_NOACTIVATE = 0x08000000,
            WS_EX_NOINHERITLAYOUT = 0x00100000,
            WS_EX_NOPARENTNOTIFY = 0x00000004,
            WS_EX_NOREDIRECTIONBITMAP = 0x00200000,
            WS_EX_OVERLAPPEDWINDOW = WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE,
            WS_EX_PALETTEWINDOW = WS_EX_WINDOWEDGE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            WS_EX_RIGHT = 0x00001000,
            WS_EX_RIGHTSCROLLBAR = 0x00000000,
            WS_EX_RTLREADING = 0x00002000,
            WS_EX_STATICEDGE = 0x00020000,
            WS_EX_TOOLWINDOW = 0x00000080,
            WS_EX_TOPMOST = 0x00000008,
            WS_EX_TRANSPARENT = 0x00000020,
            WS_EX_WINDOWEDGE = 0x00000100
        }

        public enum ShowWindowCommands : uint
        {
            SW_HIDE = 0,
            SW_SHOWNORMAL = 1,
            SW_NORMAL = 1,
            SW_SHOWMINIMIZED = 2,
            SW_SHOWMAXIMIZED = 3,
            SW_MAXIMIZE = 3,
            SW_SHOWNOACTIVATE = 4,
            SW_SHOW = 5,
            SW_MINIMIZE = 6,
            SW_SHOWMINNOACTIVE = 7,
            SW_SHOWNA = 8,
            SW_RESTORE = 9,
            SW_SHOWDEFAULT = 10,
            SW_FORCEMINIMIZE = 11,
            SW_MAX = 11
        }

        public enum WndProcMessages : uint
        {
            WM_MOVE         = 0x0003,
            WM_SIZE         = 0x0005,
            WM_DISPLAYCHANGE= 0x007E,
            WM_NCDESTROY    = 0x0082
        }

        //public delegate nint WndProcDelegate(nint hWnd, WndProcMessages msg, nint wParam, nint lParam);
        public delegate nint SubclassWndProc(nint hWnd, WndProcMessages msg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

        public static int SignedHIWORD(nint n) => SignedHIWORD(unchecked((int)(long)n));
        public static int SignedLOWORD(nint n) => SignedLOWORD(unchecked((int)(long)n));
        public static int SignedHIWORD(int n) => (short)((n >> 16) & 0xffff);
        public static int SignedLOWORD(int n) => (short)(n & 0xFFFF);

        #region DPI
        public static double DpiX = 0, DpiY = 0;
        public static double DpiXSource = 96, DpiYSource = 96;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(Point pt, MonitorOptions dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(nint hwnd, MonitorOptions dwFlags);

        [Flags]
        public enum MonitorOptions : uint
        {
            MONITOR_DEFAULTTONULL   = 0x00000000,
            MONITOR_DEFAULTTOPRIMARY= 0x00000001,
            MONITOR_DEFAULTTONEAREST= 0x00000002
        }

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const int MDT_EFFECTIVE_DPI = 0;
        public static (double dpiX, double dpiY) GetDpiAtPoint(Point point)
        {
            IntPtr monitor = MonitorFromPoint(point,  MonitorOptions.MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
                return (dpiX / 96.0, dpiY / 96.0);

            // Fallback to primary monitor DPI
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return (g.DpiX / 96.0, g.DpiY / 96.0);
        }
        #endregion

        // Currently not used (mainly for refresh rate?*)
        //[StructLayout(LayoutKind.Sequential)]
        //public struct DEVMODE
        //{
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string dmDeviceName;
        //    public ushort dmSpecVersion;
        //    public ushort dmDriverVersion;
        //    public ushort dmSize;
        //    public ushort dmDriverExtra;
        //    public int dmFields;
        //    public int dmPositionX;
        //    public int dmPositionY;
        //    public int dmDisplayOrientation;
        //    public int dmDisplayFixedOutput;
        //    public short dmColor;
        //    public short dmDuplex;
        //    public short dmYResolution;
        //    public short dmTTOption;
        //    public short dmCollate;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string dmFormName;
        //    public ushort dmLogPixels;
        //    public int dmBitsPerPel;
        //    public int dmPelsWidth;
        //    public int dmPelsHeight;
        //    public int dmDisplayFlags;
        //    public int dmDisplayFrequency; // not accurate should be ratio / float (e.g. 59.95 will be 59)
        //    public int dmICMMethod;
        //    public int dmICMIntent;
        //    public int dmMediaType;
        //    public int dmDitherType;
        //    public int dmReserved1;
        //    public int dmReserved2;
        //    public int dmPanningWidth;
        //    public int dmPanningHeight;

        //    public static DEVMODE Get(string deviceName)
        //    {
        //        DEVMODE dev = new();
        //        dev.dmSize = (ushort)Marshal.SizeOf(dev);
        //        EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dev);
        //        return dev;
        //    }
        //}

        //[DllImport("user32.dll", CharSet = CharSet.Ansi)]
        //private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        //private const int ENUM_CURRENT_SETTINGS = -1;


        //[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        //public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

        //[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        //public struct MONITORINFOEXW
        //{
        //    public uint cbSize;
        //    public RECT rcMonitor;
        //    public RECT rcWork;
        //    public MonitorInfoFlags dwFlags;

        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string szDevice;

        //    public static MONITORINFOEXW Create()
        //        => new() { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEXW)), szDevice = string.Empty };
        //}

        //[Flags]
        //public enum MonitorInfoFlags : uint
        //{
        //    MONITORINFOF_PRIMARY = 0x00000001
        //}

        //[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        //public struct DISPLAY_DEVICE
        //{
        //    public int cb;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string DeviceName;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        //    public string DeviceString;
        //    public int StateFlags;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        //    public string DeviceID;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        //    public string DeviceKey;

        //    public static DISPLAY_DEVICE Create()
        //    {
        //        var display = new DISPLAY_DEVICE();
        //        display.cb = Marshal.SizeOf(display);
        //        return display;
        //    }
        //}

        //[DllImport("user32.dll", CharSet = CharSet.Ansi)]
        //public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        //[DllImport("user32.dll", SetLastError = true)]
        //public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_DEVICE_INFO_HEADER requestPacket);
    }
}
