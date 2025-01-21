using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LLPlayer.Services;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // If this is not called first, the constructor of the other control will
        // run before FlyleafHost is initialized, so it will not work.
        DataContext = ((App)Application.Current).Container.Resolve<MainWindowVM>();

        InitializeComponent();

        SetTitleBarDarkMode(this);
    }

    #region Dark Title Bar
    /// <summary>
    /// ref: <see href="https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmsetwindowattribute" />
    /// </summary>
    [DllImport("DwmApi")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void SetTitleBarDarkMode(Window window)
    {
        // Check OS Version
        if (!(Environment.OSVersion.Version >= new Version(10, 0, 18985)))
        {
            return;
        }

        var fl = ((App)Application.Current).Container.Resolve<FlyleafManager>();
        if (!fl.Config.IsDarkTitlebar)
        {
            return;
        }

        bool darkMode = true;

        // Set title bar to dark mode
        // ref: https://stackoverflow.com/questions/71362654/wpf-window-titlebar
        IntPtr hWnd = new WindowInteropHelper(window).EnsureHandle();
        DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, [darkMode ? 1 : 0], 4);
    }
    #endregion
}
