namespace CoderMascot.Core;

/// <summary>A system-wide shortcut, in the form RegisterHotKey wants.</summary>
public sealed record HotkeySpec(uint Modifiers, uint Key, string Text);

/// <summary>
/// Parsing "Ctrl+Alt+N" into the two numbers Windows needs.
///
/// Kept away from the P/Invoke and away from WPF's Key enum so the parsing —
/// which is the part with rules — can be tested off-Windows. The rules: at
/// least one modifier, exactly one key, and nothing else accepted.
///
/// A modifier is required because a bare hotkey is registered *globally*: bind
/// N on its own and every other application on the machine stops receiving the
/// letter N. That is not a preference, it is a way to make someone's computer
/// unusable from a config file typo.
/// </summary>
public static class Hotkey
{
    public const string Default = "Ctrl+Alt+N";

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>Windows repeats a held hotkey; one note per press is plenty.</summary>
    public const uint NoRepeat = 0x4000;

    public static HotkeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        uint modifiers = 0;
        uint? key = null;
        var parts = new List<string>();

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; parts.Add("Ctrl"); continue;
                case "alt": modifiers |= ModAlt; parts.Add("Alt"); continue;
                case "shift": modifiers |= ModShift; parts.Add("Shift"); continue;
                case "win" or "super" or "cmd": modifiers |= ModWin; parts.Add("Win"); continue;
            }

            // A second key is a typo, not a two-key chord — Windows has no such
            // thing, and picking one of them silently would bind the wrong one.
            if (key is not null) return null;

            key = VirtualKey(part);
            if (key is null) return null;

            parts.Add(part.ToUpperInvariant());
        }

        if (key is null || modifiers == 0) return null;

        // Modifiers first, in a fixed order, so the text we show back doesn't
        // depend on the order they were typed in.
        var ordered = parts.Where(p => p is "Ctrl" or "Alt" or "Shift" or "Win")
            .OrderBy(p => p switch { "Ctrl" => 0, "Alt" => 1, "Shift" => 2, _ => 3 })
            .Append(parts[^1]);

        return new HotkeySpec(modifiers, key.Value, string.Join("+", ordered));
    }

    private static uint? VirtualKey(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
            return null;
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') &&
            int.TryParse(name[1..], out var n) && n is >= 1 and <= 12)
            return (uint)(0x70 + n - 1);

        return name.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "insert" => 0x2D,
            "delete" => 0x2E,
            _ => null,
        };
    }
}
