namespace CoderMascot.Core;

/// <summary>Seals a connection string for the config file, and opens it again on this machine.</summary>
public interface ISecretBox
{
    string Seal(string plain);

    /// <summary>The plain text, or null when it isn't sealed or can't be opened here (another account, another PC).</summary>
    string? Open(string sealedText);
}

public static class LogSecrets
{
    public const string Prefix = "dpapi:";

    public static bool IsSealed(string? text) => text?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
