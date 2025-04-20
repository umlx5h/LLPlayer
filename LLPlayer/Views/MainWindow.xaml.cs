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

        SetWindowSize();
        SetTitleBarDarkMode(this);
    }

    private void SetWindowSize()
    {
        // 16:9 size list
        List<Size> candidateSizes =
        [
            new(1280, 720),
            new(1024, 576),
            new(960, 540),
            new(800, 450),
            new(640, 360),
            new(480, 270),
            new(320, 180)
        ];

        // Get available screen width / height
        double availableWidth = SystemParameters.WorkArea.Width;
        double availableHeight = SystemParameters.WorkArea.Height;

        // Get the largest size that will fit on the screen
        Size selectedSize = candidateSizes.FirstOrDefault(
            s => s.Width <= availableWidth && s.Height <= availableHeight,
            candidateSizes[^1]);

        // Set
        Width = selectedSize.Width;
        Height = selectedSize.Height;
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
