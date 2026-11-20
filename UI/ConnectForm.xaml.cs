using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CoderMascot.Core;

using UserControl = System.Windows.Controls.UserControl;

namespace CoderMascot.UI;

/// <summary>
/// First run, without a text editor.
///
/// The flow is deliberately the order the user has to do it in: say where Coder
/// is, press a button that opens the page holding a token, paste it back. The
/// address has to come first because the token page is on that host — asking for
/// a token before knowing where to send someone for one is how the old README
/// instructions read, and it is why people gave up at step one.
/// </summary>
public partial class ConnectForm : UserControl, IDrawerForm
{
    private readonly CoderConfig _cfg;
    private IReadOnlyList<string> _workspaces = [];
    private CancellationTokenSource? _probe;

    /// <summary>Finished, or abandoned by the drawer: either way it must not report again.</summary>
    private bool _closed;

    /// <summary>Connected, saved, and ready to poll.</summary>
    public event EventHandler? Connected;

    public string Heading => "Point the mascot at your workspace";

    public string? Subheading =>
        "Two things: where Coder lives, and a session token so it can ask about your workspace. Nothing is saved until it has been checked.";

    public double PreferredWidth => 480;

    public event EventHandler<bool>? Finished;

    public ConnectForm(CoderConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();

        // Whatever we already know goes in the box. A machine with the CLI
        // installed usually knows the address even when it has no usable token,
        // and re-typing something the app already has is busywork.
        UrlBox.Text = cfg.Url ?? cfg.RejectedUrl ?? string.Empty;

        ShowUrlState();
    }

    public void FocusFirst()
    {
        if (UrlBox.Text.Length == 0) UrlBox.Focus();
        else TokenBox.Focus();
    }

    public void Abandon()
    {
        _closed = true;
        _probe?.Cancel();
    }

    private void Finish(bool done)
    {
        if (_closed) return;

        Abandon();
        Finished?.Invoke(this, done);
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e) => ShowUrlState();

    private void OnTokenChanged(object sender, RoutedEventArgs e) => ShowUrlState();

    /// <summary>Keep the two buttons honest about what is possible right now.</summary>
    private void ShowUrlState()
    {
        var clean = CoderConfig.CleanUrl(UrlBox.Text);

        BtnOpen.IsEnabled = clean is not null;
        BtnConnect.IsEnabled = clean is not null && TokenBox.Password.Trim().Length > 0;

        UrlNote.Text = UrlBox.Text.Trim().Length == 0
            ? "For example: https://coder.example.com"
            : clean is null
                ? "That address can't be used — it has to be https:// (or http:// on this machine)."
                : $"Will connect to {clean}";
    }

    private void OnOpenTokenPage(object sender, RoutedEventArgs e)
    {
        if (CoderConfig.TokenPage(UrlBox.Text) is not { } page)
        {
            Report("Fill in the address first.", ok: false);
            return;
        }

        try
        {
            // Parsed, not pasted: UseShellExecute asks the shell what a string
            // is, and it will run things that are not web pages.
            var uri = new Uri(page, UriKind.Absolute);
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

            OpenNote.Text = "Signed in? Copy the token from that page and paste it below.";
            TokenBox.Focus();
        }
        catch (Exception ex)
        {
            Report($"Couldn't open the browser — {ex.Message}. The page is: {page}", ok: false);
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        _probe?.Cancel();
        _probe = new CancellationTokenSource();

        Busy(true);
        Report("Checking…", ok: true);

        SetupResult result;
        try
        {
            result = await CoderSetup.ApplyAsync(
                _cfg, UrlBox.Text, TokenBox.Password, Chosen(), _probe.Token);
        }
        catch (Exception ex)
        {
            // ApplyAsync is written not to throw; if it ever does, the setup
            // form is the last place that should disappear on the user.
            CrashLog.Write("setup", ex);
            Report(ex.Message, ok: false);
            Busy(false);
            return;
        }

        Busy(false);

        if (!result.Ok)
        {
            Report(result.Problem, ok: false);
            return;
        }

        // More than one workspace and no choice yet: ask, rather than watching
        // whichever one the API happened to list first.
        if (result.Workspaces.Count > 1 && string.IsNullOrWhiteSpace(Chosen()))
        {
            _workspaces = result.Workspaces;
            WorkspaceBox.ItemsSource = _workspaces;
            WorkspacePick.Visibility = Visibility.Visible;

            Report($"Connected as {result.Username ?? "you"}. Pick the workspace to watch.", ok: true);
            return;
        }

        Report($"Connected as {result.Username ?? "you"}. Saved — the mascot is watching"
               + (_cfg.Workspace is { Length: > 0 } w ? $" {w}." : "."), ok: true);

        Connected?.Invoke(this, EventArgs.Empty);

        // Left on screen just long enough to read, then out of the way — unless
        // it was closed in the meantime.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Finish(true);
    }

    private string? Chosen() => WorkspaceBox.SelectedItem as string;

    private void OnWorkspaceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (Chosen() is null) return;

        // Picking one is the answer to the question, so it re-runs itself rather
        // than making the user press Connect a second time.
        OnConnect(sender, e);
    }

    private void OnLater(object sender, RoutedEventArgs e) => Finish(false);

    private void Busy(bool busy)
    {
        BtnConnect.IsEnabled = !busy;
        BtnConnect.Content = busy ? "Checking…" : "Connect";
        UrlBox.IsEnabled = !busy;
        TokenBox.IsEnabled = !busy;
    }

    private void Report(string message, bool ok)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.Foreground = ok
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.LightSalmon;
    }
}
