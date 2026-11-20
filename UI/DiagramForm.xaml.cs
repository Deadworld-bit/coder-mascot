using System.Windows;
using System.Windows.Controls;
using CoderMascot.Core;

using UserControl = System.Windows.Controls.UserControl;

namespace CoderMascot.UI;

/// <summary>Link a diagram to a project, or change a link. Only the link is saved.</summary>
public partial class DiagramForm : UserControl, IDrawerForm
{
    /// <summary>The project's other links, by <see cref="DiagramLink.Key"/> → label.</summary>
    private readonly IReadOnlyDictionary<string, string> _taken;

    public DiagramLink? Result { get; private set; }

    public string Heading { get; private set; } = "Link a diagram";

    public string? Subheading =>
        "A draw.io file on this PC, or a web page that shows one. Only the link is saved; the diagram itself stays where it is.";

    public double PreferredWidth => 480;

    public event EventHandler<bool>? Finished;

    public DiagramForm(DiagramLink? editing, IReadOnlyDictionary<string, string> taken)
    {
        _taken = taken;
        InitializeComponent();

        if (editing is not null)
        {
            Heading = "Edit diagram";
            TitleBox.Text = editing.Title ?? string.Empty;
            TargetBox.Text = editing.Target ?? string.Empty;
        }

        Check();
    }

    public void FocusFirst() => TargetBox.Focus();

    // Nothing runs in the background here.
    public void Abandon() { }

    private DiagramPlace? Check()
    {
        var (place, problem) = DiagramLink.Inspect(TargetBox.Text);

        if (place is not null && _taken.TryGetValue(DiagramLink.Key(place), out var already))
        {
            place = null;
            problem = $"Already linked to this project, as “{already}”.";
        }

        // An empty box is where everyone starts; it is not worth a red line.
        var shown = problem is not null && TargetBox.Text.Trim().Length > 0;
        ProblemText.Text = shown ? problem : string.Empty;
        ProblemText.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        BtnSave.IsEnabled = place is not null;

        return place;
    }

    private void OnTargetChanged(object sender, TextChangedEventArgs e) => Check();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a draw.io diagram",
            Filter = "draw.io diagrams|*.drawio;*.dio;*.drawio.svg;*.drawio.png",
            CheckFileExists = true,
        };

        if (DiagramLink.Inspect(TargetBox.Text).Place is { Kind: DiagramKind.File } current)
            picker.InitialDirectory = System.IO.Path.GetDirectoryName(current.Target);

        var owner = Window.GetWindow(this);
        if ((owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) == true) TargetBox.Text = picker.FileName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (Check() is not { } place) return;

        Result = new DiagramLink { Title = DiagramLink.CleanTitle(TitleBox.Text), Target = place.Target };
        Finished?.Invoke(this, true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finished?.Invoke(this, false);
}
