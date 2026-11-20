namespace CoderMascot.UI;

/// <summary>
/// A form that lives in the main window's drawer. It is also a UIElement: the
/// window puts it on screen as it is.
/// </summary>
public interface IDrawerForm
{
    /// <summary>The title in the drawer's header.</summary>
    string Heading { get; }

    /// <summary>One line under the title, or null for none.</summary>
    string? Subheading { get; }

    /// <summary>Width in pixels; the window narrows it to leave a margin on small windows.</summary>
    double PreferredWidth { get; }

    /// <summary>The form is done: true when it saved or confirmed, false when it gave up.</summary>
    event EventHandler<bool>? Finished;

    /// <summary>
    /// The drawer closed without the form finishing (Esc, the close button, or
    /// the window going away). Stop anything still running.
    /// </summary>
    void Abandon();

    /// <summary>Put the keyboard focus on the first thing to fill in. Called once the form is on screen.</summary>
    void FocusFirst();
}

/// <summary>Where forms open. One at a time.</summary>
public interface IDrawerHost
{
    /// <summary>
    /// Show the form and finish with what it finished with, or false if the
    /// drawer was closed on it. Returns false at once, touching nothing, when
    /// another form is already open.
    /// </summary>
    Task<bool> OpenAsync(IDrawerForm form);
}
