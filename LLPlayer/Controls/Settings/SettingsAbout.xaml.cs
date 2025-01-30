using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using LLPlayer.Extensions;

namespace LLPlayer.Controls.Settings;

public partial class SettingsAbout : UserControl
{
    public SettingsAbout()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsAboutVM>();
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

public class SettingsAboutVM : Bindable
{
    public SettingsAboutVM()
    {
        AppName = App.Name;

        Version = App.Version;
        CommitHash = App.CommitHash;

        Libraries =
        [
            new LibraryInfo
            {
                Name = "SuRGeoNix/Flyleaf",
                Description = "Media Player .NET Library for WinUI 3/WPF/WinForms (based on FFmpeg/DirectX)",
                Url = "https://github.com/SuRGeoNix/Flyleaf"
            },

            new LibraryInfo
            {
                Name = "SuRGeoNix/Flyleaf.FFmpeg",
                Description = "FFmpeg Bindings for C#/.NET",
                Url = "https://github.com/SuRGeoNix/Flyleaf.FFmpeg"
            },

            new LibraryInfo
            {
                Name = "sandrohanea/whisper.net",
                Description = "Dotnet bindings for OpenAI Whisper (whisper.cpp)",
                Url = "https://github.com/sandrohanea/whisper.net"
            },

            new LibraryInfo
            {
                Name = "Sicos1977/TesseractOCR",
                Description = ".NET wrapper for Tesseract OCR",
                Url = "https://github.com/Sicos1977/TesseractOCR"
            },

            new LibraryInfo
            {
                Name = "MaterialDesignInXamlToolkit ",
                Description = "Google's Material Design in XAML & WPF",
                Url = "https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit"
            },

            new LibraryInfo
            {
                Name = "searchpioneer/lingua-dotnet",
                Description = "Natural language detection library for .NET",
                Url = "https://github.com/searchpioneer/lingua-dotnet"
            },

            new LibraryInfo
            {
                Name = "CharsetDetector/UTF-unknowns",
                Description = "Character set detector build in C#",
                Url = "https://github.com/CharsetDetector/UTF-unknown"
            },

            new LibraryInfo
            {
                Name = "PrismLibrary/Prism",
                Description = "A framework for building MVVM application",
                Url = "https://github.com/PrismLibrary/Prism"
            },

            new LibraryInfo
            {
                Name = "amerkoleci/Vortice.Windows",
                Description = ".NET bindings for Direct3D11, XAudio, etc.",
                Url = "https://github.com/amerkoleci/Vortice.Windows"
            },

            new LibraryInfo
            {
                Name = "sskodje/WpfColorFont",
                Description = " A WPF font and color dialog",
                Url = "https://github.com/sskodje/WpfColorFont"
            },
        ];

    }
    public string AppName { get; }
    public string Version { get; }
    public string CommitHash { get; }
    public ObservableCollection<LibraryInfo> Libraries { get; }

    [field: AllowNull, MaybeNull]
    public DelegateCommand CmdCopyVersion => field ??= new(() =>
    {
        Clipboard.SetText($"Version: {App.Version}, CommitHash: {App.CommitHash}");
    });
}

public class LibraryInfo
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string Url { get; set; }
}
