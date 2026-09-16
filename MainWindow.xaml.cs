using System.Diagnostics;
using System.IO;
using System.Windows;
using DiscordPurger.Core;
using Microsoft.Web.WebView2.Core;

namespace DiscordPurger;

public partial class MainWindow : Window
{
    private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscordPurger",
            "WebView2");
        Directory.CreateDirectory(userData);

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions());
            await WebView.EnsureCoreWebView2Async(env);

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"),
                CoreWebView2HostResourceAccessKind.Allow);

            var avatarRoot = Path.Combine(Path.GetTempPath(), "DiscordPurger", "avatars");
            Directory.CreateDirectory(avatarRoot);
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AvatarCache.VirtualHost,
                avatarRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            _ = new WebBridge(WebView.CoreWebView2);

            WebView.NavigationCompleted += (_, _) => RevealWindow();
            WebView.CoreWebView2.Navigate("https://app.local/index.html");
        }
        catch (Exception)
        {
            RevealWindow();
            var choice = MessageBox.Show(
                "Discord Purger requires the Microsoft Edge WebView2 runtime, which is not installed on this PC.\n\n" +
                "Click Yes to open the official Microsoft WebView2 installer download page.\n\n" +
                "Run the downloaded setup, then launch Discord Purger again.",
                "WebView2 Runtime Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Yes)
                OpenWebView2Download();
        }
    }

    private void RevealWindow()
    {
        if (Opacity < 1)
        {
            Opacity = 1;
            ShowInTaskbar = true;
        }
    }

    private void OpenWebView2Download()
    {
        try
        {
            Process.Start(new ProcessStartInfo(WebView2BootstrapperUrl)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }
}
