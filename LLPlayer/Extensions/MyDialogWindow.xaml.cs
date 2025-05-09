using System.Windows;
using LLPlayer.Views;

namespace LLPlayer.Extensions;

public partial class MyDialogWindow : Window, IDialogWindow
{
    public IDialogResult? Result { get; set; }

    public MyDialogWindow()
    {
        InitializeComponent();

        MainWindow.SetTitleBarDarkMode(this);
    }
}
