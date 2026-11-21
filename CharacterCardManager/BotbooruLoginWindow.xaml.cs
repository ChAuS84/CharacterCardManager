using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CharacterCardManager;

public partial class BotbooruLoginWindow : Window
{
    public string? ResultCookieHeader { get; private set; }
    public string? ResultUserAgent { get; private set; }
    public string? ResultBearerToken { get; private set; }
    public bool WasCleared { get; private set; }

    public BotbooruLoginWindow(string? existingCookie)
    {
        InitializeComponent();
        TxtManualCookie.Text = existingCookie ?? string.Empty;
        Loaded += BotbooruLoginWindow_Loaded;
    }

    private async void BotbooruLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string userData = Path.Combine(AppContext.BaseDirectory, "webview_data");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await WebView.EnsureCoreWebView2Async(env);
            WebView.CoreWebView2.Navigate("https://botbooru.com/account");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 failed to initialize ({ex.Message}).\nYou can still paste your browser's Botbooru Cookie header at the bottom.",
                "WebView2 Notice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async void SaveSession_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is null)
        {
            MessageBox.Show("Browser is not initialized yet. Use the manual cookie box below if needed.", "Not Ready");
            return;
        }

        var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync("https://botbooru.com");
        string? token = null;
        try
        {
            string rawTokenJson = await WebView.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('token')");
            if (!string.IsNullOrWhiteSpace(rawTokenJson) && rawTokenJson != "null")
                token = JsonSerializer.Deserialize<string>(rawTokenJson);
        }
        catch
        {
            // Ignore script errors
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show(
                "No Botbooru login token found in browser localStorage yet.\nPlease sign in on the page until you see your account, then click 'Save Session & Close'.",
                "Not Signed In",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ResultCookieHeader = cookies.Count > 0 ? string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")) : null;
        ResultUserAgent = WebView.CoreWebView2.Settings.UserAgent;
        ResultBearerToken = token;
        DialogResult = true;
        Close();
    }

    private void UseManualCookie_Click(object sender, RoutedEventArgs e)
    {
        string raw = TxtManualCookie.Text.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            MessageBox.Show("Paste a Bearer token or Cookie header first, or use the browser above.", "Empty");
            return;
        }

        if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            ResultBearerToken = raw["Bearer ".Length..].Trim();
        else if (!raw.Contains('=') && raw.Contains('.'))
            ResultBearerToken = raw;
        else
            ResultCookieHeader = raw;

        DialogResult = true;
        Close();
    }

    private async void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is not null)
        {
            WebView.CoreWebView2.CookieManager.DeleteAllCookies();
            try { await WebView.CoreWebView2.ExecuteScriptAsync("localStorage.removeItem('token')"); } catch { }
        }

        ResultCookieHeader = null;
        ResultBearerToken = null;
        WasCleared = true;
        DialogResult = true;
        Close();
    }
}
