using System.Drawing;
using System.Runtime.InteropServices;

namespace FlyleafLib;

public static partial class Utils
{
    unsafe public static class NativeMethods
    {
        #region Common
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
        #endregion

        #region DPI
        public static double DpiX
        {
            get;
            internal set
            {
                if (value <= 0)
                    field = 1;
                else
                    field = value;
            }
        } = 1;
        public static double DpiY
        {
            get;
            internal set
            {
                if (value <= 0)
                    field = 1;
                else
                    field = value;
            }
        } = 1;
        public static double DpiXSource
        {
            get;
            internal set
            {
                if (value <= 0)
                    field = 96;
                else
                    field = value;
            }
        } = 96;
        public static double DpiYSource
        {
            get;
            internal set
            {
                if (value <= 0)
                    field = 96;
                else
                    field = value;
            }
        } = 96;
        #endregion

        #region ICC Profile
        const uint PROFILE_FILENAME = 1;
        const uint PROFILE_MEMBUFFER = 2;

        const uint PROFILE_READ = 1;

        const uint FILE_SHARE_READ = 0x00000001;
        const uint OPEN_EXISTING   = 3;

        const uint LCS_sRGB = 0x73524742; // 'sRGB'

        [StructLayout(LayoutKind.Sequential)]
        public struct PROFILE
        {
            public uint  dwType;
            public void* pProfileData;
            public uint  cbDataSize;
        }

        [DllImport("mscms.dll")]
        public static extern nint OpenColorProfileW(PROFILE* pProfile, uint dwDesiredAccess, uint dwShareMode, uint dwCreationMode);

        [DllImport("mscms.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseColorProfile(nint hProfile);

        [DllImport("mscms.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetStandardColorSpaceProfileW(char* pMachineName, uint dwSCS, char* pBuffer, uint* pcbSize);

        public static nint OpenColorProfile(byte[] data)
        {
            fixed (byte* ptr = data)
                return OpenColorProfile(ptr, data.Length);
        }
        public static nint OpenColorProfile(void* data, int size)
        {
            if (data == null || size <= 0)
                return 0;

            var profile = new PROFILE
            {
                dwType      = PROFILE_MEMBUFFER,
                pProfileData= data,
                cbDataSize  = (uint)size
            };

            return OpenColorProfileW(&profile, PROFILE_READ, 0, 0);
        }
        public static nint OpenSRgbProfile()
        {
            uint size = 0;

            // First call: get required buffer size in bytes.
            _ = GetStandardColorSpaceProfileW(null, LCS_sRGB, null, &size);

            if (size == 0)
                return 0;

            byte* buffer = (byte*)NativeMemory.Alloc(size);

            try
            {
                uint actualSize = size;

                if (!GetStandardColorSpaceProfileW(null, LCS_sRGB, (char*)buffer, &actualSize))
                    return 0;

                PROFILE profile = new()
                {
                    dwType       = PROFILE_FILENAME,
                    pProfileData = buffer,
                    cbDataSize   = actualSize
                };

                return OpenColorProfileW(&profile, PROFILE_READ, FILE_SHARE_READ, OPEN_EXISTING);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        // HTRANSFORM
        const uint INTENT_PERCEPTUAL            = 0;
        //const uint INTENT_RELATIVE_COLORIMETRIC = 1;
        //const uint INTENT_SATURATION            = 2;
        //const uint INTENT_ABSOLUTE_COLORIMETRIC = 3;
        //const uint USE_RELATIVE_COLORIMETRIC = 0x00020000;

        const uint BEST_MODE       = 0x00000003;
        const uint INDEX_DONT_CARE = 0;

        [DllImport("mscms.dll")]
        public static extern nint CreateMultiProfileTransform(nint* pahProfiles, uint nProfiles, uint* padwIntent, uint nIntents, uint dwFlags, uint indexPreferredCMM);

        [DllImport("mscms.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteColorTransform(nint hTransform);

        public static nint CreateTransform(nint sourceProfile, nint destinationProfile)
        {
            nint* profiles = stackalloc nint[2]
            {
                sourceProfile,
                destinationProfile
            };

            uint intent = INTENT_PERCEPTUAL;

            return CreateMultiProfileTransform(profiles, 2, &intent, 1, BEST_MODE, INDEX_DONT_CARE);
        }

        // LUT
        const uint BM_16b_RGB = 0x000A;

        [DllImport("mscms.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateBitmapBits(nint hColorTransform, void* pSrcBits, uint bmInput, uint dwWidth, uint dwHeight, uint dwInputStride, void* pDestBits, uint bmOutput, uint dwOutputStride, nint pfnCallBack, nint ulCallbackData);

        public static ushort[] CreateIccLut(nint transform)
        {
            // TranslateBitmapBits/BM_16b_RGB behaves as BGR16 on the
            // tested Windows ICM implementation, despite BMFORMAT documenting RGB.
            // Swap R/B on both input and output.
            //
            // Do not validate this with an sRGB->sRGB transform alone:
            // the two R/B swaps cancel and hide the issue.

            const int size = 33;

            int width  = size * size;
            int height = size;
            int count  = width * height;

            // 48-bit RGB: ushort R,G,B
            ushort[] input  = new ushort[count * 3];
            ushort[] output = new ushort[count * 3];

            int i = 0;

            for (int g = 0; g < size; g++)
            {
                ushort gv = (ushort)(g * 65535 / (size - 1));

                for (int b = 0; b < size; b++)
                {
                    ushort bv = (ushort)(b * 65535 / (size - 1));

                    for (int r = 0; r < size; r++)
                    {
                        // Input to TranslateBitmapBits
                        input[i++] = bv;
                        input[i++] = gv;
                        input[i++] = (ushort)(r * 65535 / (size - 1));
                    }
                }
            }

            uint stride = (uint)(width * 3 * sizeof(ushort));

            fixed (ushort* src = input)
            fixed (ushort* dst = output)
            {
                if (!TranslateBitmapBits(transform, src, BM_16b_RGB, (uint)width, (uint)height, stride, dst, BM_16b_RGB, stride, 0, 0))
                return [];
            }

            ushort[] rgba = new ushort[count * 4];

            for (int p = 0; p < count; p++)
            {
                int src = p * 3;
                int dst = p * 4;

                // Output from TranslateBitmapBits -> RGBA texture
                rgba[dst    ] = output[src + 2]; // R
                rgba[dst + 1] = output[src + 1]; // G
                rgba[dst + 2] = output[src    ]; // B
                rgba[dst + 3] = ushort.MaxValue;
            }

            return rgba;
        }
        #endregion

        #region Monitor / ICC Profiles
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

        #region Display Config
        public static class DisplayConfig
        {
            const uint QDC_ONLY_ACTIVE_PATHS    = 0x00000002;
            const int ERROR_SUCCESS             = 0;
            const int ERROR_INSUFFICIENT_BUFFER = 122;

            public readonly record struct DisplayHDRInfo(bool Supported, bool Enabled, float? SDRWhiteNits);

            public static bool TryGetHDRInfo(string deviceName, out DisplayHDRInfo info)
            {
                info = default;

                while (true)
                {
                    int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);

                    if (result != ERROR_SUCCESS)
                        return false;

                    var paths = new PathInfo[pathCount];
                    var modes = new ModeInfo[modeCount];

                    result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

                    if (result == ERROR_INSUFFICIENT_BUFFER)
                        continue;

                    if (result != ERROR_SUCCESS)
                        return false;

                    for (int i = 0; i < pathCount; i++)
                    {
                        ref var path = ref paths[i];

                        var source = new SourceDeviceName
                        {
                            header = CreateHeader(DeviceInfoType.GetSourceName, path.sourceInfo.adapterId, path.sourceInfo.id)
                        };

                        if (DisplayConfigGetDeviceInfo(ref source) != ERROR_SUCCESS)
                            continue;

                        if (!string.Equals( source.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        return TryGetHDRInfo(path.targetInfo.adapterId, path.targetInfo.id, out info);
                    }

                    return false;
                }
            }

            static bool TryGetHDRInfo(LUID adapterId, uint targetId, out DisplayHDRInfo info)
            {
                info = default;

                bool supported;
                bool enabled;

                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                {   // Windows 11 24H2 (build 26100)+.
                    var color = new GetAdvancedColorInfo2
                    {
                        header = CreateHeader(DeviceInfoType.GetAdvancedColorInfo2, adapterId, targetId)
                    };

                    if (DisplayConfigGetDeviceInfo(ref color) == ERROR_SUCCESS)
                    {
                        supported   = color.flags.HasFlag(AdvancedColorMode2.HighDynamicRangeSupported);
                        enabled     = color.activeColorMode == AdvancedColorMode.HDR;

                        return FinishHDRInfo(adapterId, targetId, supported, enabled, out info);
                    }

                    // Unexpected failure of the new API: try legacy one.
                }

                var legacy = new GetAdvancedColorInfo
                {
                    header = CreateHeader(DeviceInfoType.GetAdvancedColorInfo, adapterId, targetId)
                };

                if (DisplayConfigGetDeviceInfo(ref legacy) != ERROR_SUCCESS)
                    return false;

                // Pre-24H2:
                //
                // HDR display:
                //   AdvancedColorSupported = true
                //   WideColorEnforced      = false
                //
                // Advanced-Color SDR display (Win11 22H2+):
                //   AdvancedColorSupported = true
                //   WideColorEnforced      = true
                //
                supported =
                     legacy.flags.HasFlag(AdvancedColorFlags.AdvancedColorSupported) &&
                    !legacy.flags.HasFlag(AdvancedColorFlags.WideColorEnforced);

                enabled =
                    supported &&
                    legacy.flags.HasFlag(AdvancedColorFlags.AdvancedColorEnabled);

                return FinishHDRInfo(adapterId, targetId, supported, enabled, out info);
            }

            static bool FinishHDRInfo(LUID adapterId, uint targetId, bool supported, bool enabled, out DisplayHDRInfo info)
            {
                float? sdrWhiteNits = null;

                if (enabled)
                {
                    var white = new SDRWhiteLevel
                    {
                        header = CreateHeader(DeviceInfoType.GetSDRWhiteLevel, adapterId, targetId)
                    };

                    if (DisplayConfigGetDeviceInfo(ref white) == ERROR_SUCCESS)
                        sdrWhiteNits = white.Level * (80.0f / 1000.0f);
                }

                info = new DisplayHDRInfo(supported, enabled, sdrWhiteNits);

                return true;
            }

            static DeviceInfoHeader CreateHeader(DeviceInfoType type, LUID adapterId, uint id) => new()
                {
                    type      = type,
                    size      = type switch
                    {
                        DeviceInfoType.GetSourceName            => (uint)Marshal.SizeOf<SourceDeviceName>(),
                        DeviceInfoType.GetAdvancedColorInfo     => (uint)Marshal.SizeOf<GetAdvancedColorInfo>(),
                        DeviceInfoType.GetAdvancedColorInfo2    => (uint)Marshal.SizeOf<GetAdvancedColorInfo2>(),
                        DeviceInfoType.GetSDRWhiteLevel         => (uint)Marshal.SizeOf<SDRWhiteLevel>(),
                        _ => (uint)Marshal.SizeOf<DeviceInfoHeader>()
                    },
                    adapterId = adapterId,
                    id        = id
                };


            public enum DeviceInfoType : uint
            {
                GetSourceName           = 1,
                GetAdvancedColorInfo    = 9,
                GetSDRWhiteLevel        = 11,
                GetAdvancedColorInfo2   = 15,
            }

            public enum ColorEncoding : uint
            {
                RGB       = 0,
                YCbCr444  = 1,
                YCbCr422  = 2,
                YCbCr420  = 3,
                Intensity = 4,
            }

            [Flags]
            public enum AdvancedColorFlags : uint
            {
                None                       = 0,
                AdvancedColorSupported     = 1 << 0,
                AdvancedColorEnabled       = 1 << 1,
                WideColorEnforced          = 1 << 2,
                AdvancedColorForceDisabled = 1 << 3,
            }

            [Flags]
            public enum AdvancedColorMode2 : uint
            {
                None                         = 0,
                AdvancedColorSupported       = 1 << 0,
                AdvancedColorActive          = 1 << 1,
                AdvancedColorLimitedByPolicy = 1 << 3,
                HighDynamicRangeSupported    = 1 << 4,
                HighDynamicRangeUserEnabled  = 1 << 5,
                WideColorSupported           = 1 << 6,
                WideColorUserEnabled         = 1 << 7,
            }

            public enum AdvancedColorMode : int
            {
                SDR = 0,
                WCG = 1,
                HDR = 2,
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct LUID
            {
                public uint LowPart;
                public int HighPart;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Rational
            {
                public uint Numerator;
                public uint Denominator;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PathSourceInfo
            {
                public LUID adapterId;
                public uint id;
                public uint modeInfoIdx;
                public uint statusFlags;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PathTargetInfo
            {
                public LUID adapterId;
                public uint id;
                public uint modeInfoIdx;

                public uint outputTechnology;
                public uint rotation;
                public uint scaling;

                public Rational refreshRate;

                public uint scanLineOrdering;
                public int targetAvailable; // BOOL
                public uint statusFlags;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PathInfo
            {
                public PathSourceInfo sourceInfo;
                public PathTargetInfo targetInfo;
                public uint flags;
            }

            // We only need correctly-sized storage for QueryDisplayConfig().
            [StructLayout(LayoutKind.Explicit, Size = 64)]
            public struct ModeInfo { }

            [StructLayout(LayoutKind.Sequential)]
            public struct DeviceInfoHeader
            {
                public DeviceInfoType type;
                public uint size;
                public LUID adapterId;
                public uint id;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct SourceDeviceName
            {
                public DeviceInfoHeader header;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string viewGdiDeviceName;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct GetAdvancedColorInfo
            {
                public DeviceInfoHeader header;
                public AdvancedColorFlags flags;
                public ColorEncoding colorEncoding;
                public uint bitsPerColorChannel;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct GetAdvancedColorInfo2
            {
                public DeviceInfoHeader header;
                public AdvancedColorMode2 flags;
                public ColorEncoding colorEncoding;
                public uint bitsPerColorChannel;
                public AdvancedColorMode activeColorMode;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SDRWhiteLevel
            {
                public DeviceInfoHeader header;
                public uint Level;
            }

            [DllImport("user32.dll", ExactSpelling = true)]
            public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

            [DllImport("user32.dll", ExactSpelling = true)]
            public static extern int QueryDisplayConfig(
                uint flags,
                ref uint numPathArrayElements,
                [Out] PathInfo[] pathArray,
                ref uint numModeInfoArrayElements,
                [Out] ModeInfo[] modeInfoArray,
                IntPtr currentTopologyId);

            [DllImport("user32.dll", ExactSpelling = true)]
            public static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

            [DllImport("user32.dll", ExactSpelling = true)]
            public static extern int DisplayConfigGetDeviceInfo(ref GetAdvancedColorInfo request);

            [DllImport("user32.dll", ExactSpelling = true)]
            public static extern int DisplayConfigGetDeviceInfo(ref GetAdvancedColorInfo2 request);

            [DllImport("user32.dll", ExactSpelling = true)]
            public static extern int DisplayConfigGetDeviceInfo(ref SDRWhiteLevel request);
        }
        #endregion
    }
}
