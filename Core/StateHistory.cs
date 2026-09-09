namespace CoderMascot.Core;

public sealed record HistoryEntry(DateTimeOffset At, MascotState State, string Detail);

/// <summary>
/// The last few things that happened, newest first.
///
/// Answers the question you have when you come back to the desk: did the agent
/// drop while I was out, or did Claude just finish? A badge only ever shows the
/// present tense, so without this the ten minutes you weren't watching are gone.
///
/// Memory only, and deliberately so — this is for the last hour, not an audit
/// log, and the records carry project paths and Claude's prompt text, which is
/// not something to start writing to disk for a convenience feature.
/// </summary>
public sealed class StateHistory
{
    private const int Capacity = 50;
    private readonly LinkedList<HistoryEntry> _entries = new();

    public void Record(MascotState state, string detail)
    {
        // Same state twice running is the poll loop, not an event.
        if (_entries.First?.Value is { } head && head.State == state && head.Detail == detail) return;

        Add(state, detail);
    }

    /// <summary>Add a line that isn't a state change at all, like "Claude finished".</summary>
    public void Note(string detail) => Add(MascotState.Connected, detail);

    private void Add(MascotState state, string detail)
    {
        _entries.AddFirst(new HistoryEntry(DateTimeOffset.Now, state, detail));
        while (_entries.Count > Capacity) _entries.RemoveLast();
    }

    public IReadOnlyList<HistoryEntry> Recent => [.. _entries];
}
