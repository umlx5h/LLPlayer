using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LLPlayer.Extensions;

// ref: https://stackoverflow.com/questions/44205260/net-core-copy-to-clipboard
public static class WindowsClipboard
{
    public static void SetText(string text)
    {
        OpenClipboard();

        EmptyClipboard();
        IntPtr hGlobal = 0;
        try
        {
            int bytes = (text.Length + 1) * 2;
            hGlobal = Marshal.AllocHGlobal(bytes);

            if (hGlobal == 0)
            {
                ThrowWin32();
            }

            IntPtr target = GlobalLock(hGlobal);

            if (target == 0)
            {
                ThrowWin32();
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            }
            finally
            {
                GlobalUnlock(target);
            }

            if (SetClipboardData(cfUnicodeText, hGlobal) == 0)
            {
                ThrowWin32();
            }

            hGlobal = 0;
        }
        finally
        {
            if (hGlobal != 0)
            {
                Marshal.FreeHGlobal(hGlobal);
            }

            CloseClipboard();
        }
    }

    public static void OpenClipboard()
    {
        int num = 10;
        while (true)
        {
            if (OpenClipboard(0))
            {
                break;
            }

            if (--num == 0)
            {
                ThrowWin32();
            }

            Thread.Sleep(20);
        }
    }

    const uint cfUnicodeText = 13;

    static void ThrowWin32()
    {
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetClipboardData(uint uFormat, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();
}
