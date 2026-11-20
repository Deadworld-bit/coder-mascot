using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CoderMascot.Core;

/// <summary>
/// DPAPI for the current Windows account: the sealed text is useless copied to
/// another account or PC, which is the point — config.json is a plain file that
/// gets synced and pasted around.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretBox : ISecretBox
{
    private static readonly byte[] Entropy = "CoderMascot.LogSources.v1"u8.ToArray();

    public string Seal(string plain) =>
        LogSecrets.Prefix + Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string? Open(string sealedText)
    {
        if (!LogSecrets.IsSealed(sealedText)) return null;

        try
        {
            var bytes = Convert.FromBase64String(sealedText[LogSecrets.Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
