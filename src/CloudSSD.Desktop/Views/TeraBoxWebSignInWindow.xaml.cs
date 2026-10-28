using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CloudSSD.App;
using CloudSSD.Diagnostics;
using CloudSSD.TeraBox;
using Microsoft.Web.WebView2.Core;

namespace CloudSSD.Desktop.Views;

/// <summary>
/// The user signs in to the TeraBox website inside an embedded Edge (WebView2) window with a private profile. Once the
/// site's session cookie appears, the session (cookie header for the TeraBox site, user agent, page token) is handed to
/// <see cref="TeraBoxService.ConnectWebSessionAsync"/>, which verifies it against TeraBox and stores it with DPAPI. The
/// embedded browser's data is then cleared. Nothing of the session is shown, logged or written elsewhere.
/// </summary>
public partial class TeraBoxWebSignInWindow : Window
{
    private const string StartUrl = "https://www.terabox.com/";

    private readonly AppHost _host;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _connecting;

    public TeraBoxWebSignInWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _poll.Tick += async (_, _) => await TryConnectAsync(automatic: true);
        Closed += (_, _) => _poll.Stop();
    }

    public TeraBoxUser? User { get; private set; }

    private static bool IsTeraBoxHost(string host) => TeraBoxWebApi.IsTeraBoxFamily(host);

    private async void Continue(object sender, RoutedEventArgs e)
    {
        ConsentPanel.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        Placeholder.Text = "Starting the TeraBox website…";
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _host.Paths.TeraBoxSignInProfileDir);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            Web.Visibility = Visibility.Visible;
            Placeholder.Visibility = Visibility.Collapsed;
            Web.CoreWebView2.Navigate(StartUrl);
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("Microsoft Edge WebView2 Runtime is not installed. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and try again.");
        }
        catch (Exception ex) { ShowError("Could not open the sign-in window: " + Redactor.Redact(ex.Message)); }
    }

    private async void ConnectNow(object sender, RoutedEventArgs e) => await TryConnectAsync(automatic: false);

    private async Task TryConnectAsync(bool automatic)
    {
        if (_connecting || Web.CoreWebView2 is null) return;
        var source = Web.CoreWebView2.Source;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps || !IsTeraBoxHost(page.Host))
        {
            if (!automatic) ShowError("Finish signing in on the TeraBox website first (the page must be a TeraBox page).");
            return;
        }
        var origin = $"https://{page.Host}";
        var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(origin);
        if (!cookies.Any(c => c.Name == "ndus" && c.Value.Length > 0))
        {
            if (!automatic) ShowError("TeraBox does not show you as signed in yet. Finish the sign-in on the page below.");
            return;
        }
        _connecting = true;
        _poll.Stop();
        ConnectButton.IsEnabled = false;
        Status.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        Status.Text = "Signed in. Checking the connection with TeraBox…";
        try
        {
            string? jsToken = null;
            try
            {
                var raw = await Web.CoreWebView2.ExecuteScriptAsync("typeof window.jsToken === 'string' ? window.jsToken : ''");
                jsToken = JsonSerializer.Deserialize<string>(raw) is { Length: > 0 } t ? t : null; // otherwise the bridge fetches it
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            var cookieHeader = string.Join("; ", cookies.Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => c.Name + "=" + c.Value));
            var session = new TeraBoxWebSession(origin, cookieHeader, Web.CoreWebView2.Settings.UserAgent, jsToken, DateTimeOffset.UtcNow);
            User = await _host.TeraBox.ConnectWebSessionAsync(session, CancellationToken.None);
            _host.SaveSettings();
            try { await Web.CoreWebView2.Profile.ClearBrowsingDataAsync(); } catch (Exception) { /* best effort; the profile folder is private to this user */ }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError("TeraBox did not accept the session: " + Redactor.Redact(ex.Message) + " Try signing in again.");
            ConnectButton.IsEnabled = true;
            _connecting = false;
        }
    }

    private void ShowError(string text)
    {
        StatusPanel.Visibility = Visibility.Visible;
        Status.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        Status.Text = text;
    }
}
