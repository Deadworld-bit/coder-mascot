using System.Diagnostics;
using System.Text;

namespace CoderMascot.Core;

/// <summary>
/// Making a child process's output arrive as the text it actually is.
///
/// .NET decodes a redirected stream using the console's code page, which on a
/// Vietnamese Windows install is CP1258 and on most others is CP437 or CP1252 —
/// none of which is what git writes. Git writes UTF-8. Read one as the other and
/// every non-ASCII character arrives as two or three wrong ones: a branch called
/// "Fix-một-số-lỗi-phương-thức-ký" comes back as "Fix-má»™t-sá»‘-lá»—i…" and
/// stays that way through the window, the filter and the merge matching.
///
/// It is one line per process and it has to be on *every* one of them, which is
/// why it lives here rather than being remembered at each call site.
/// </summary>
public static class ProcessText
{
    /// <summary>UTF-8 with no byte-order mark: a BOM written to stdin is data git would read.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static ProcessStartInfo ReadAsUtf8(this ProcessStartInfo psi)
    {
        if (psi.RedirectStandardOutput) psi.StandardOutputEncoding = Utf8;
        if (psi.RedirectStandardError) psi.StandardErrorEncoding = Utf8;
        if (psi.RedirectStandardInput) psi.StandardInputEncoding = Utf8;

        return psi;
    }
}
