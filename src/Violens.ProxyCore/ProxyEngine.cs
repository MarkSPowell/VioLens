namespace Violens.ProxyCore;

/// <summary>
/// Process-wide settings for the proxy engine: who is hosting it (see
/// <see cref="ProxyHostIdentity"/>) and where its log messages go. Call
/// <see cref="Configure"/> once, early in startup, before creating a
/// <c>CertificateAuthority</c> or touching the system proxy.
///
/// Process-wide on purpose: the system proxy, the per-user trust store and
/// the crash-recovery backup are each one per user session, so a single
/// identity per process is all that can work anyway.
/// </summary>
public static class ProxyEngine
{
    private static ProxyHostIdentity s_identity = ProxyHostIdentity.Default;
    private static Action<ProxyLogEntry>? s_log;

    /// <summary>The identity in use: <see cref="ProxyHostIdentity.Default"/> (VioLens) until configured.</summary>
    public static ProxyHostIdentity Identity => Volatile.Read(ref s_identity);

    /// <summary>
    /// Sets the identity and, optionally, where log messages go. Pass
    /// <paramref name="log"/> as null to keep ProxyCore silent (the default).
    /// Messages arrive on whatever thread produced them, often a thread-pool
    /// thread handling a connection, so the handler must be thread-safe and
    /// quick.
    /// </summary>
    public static void Configure(ProxyHostIdentity identity, Action<ProxyLogEntry>? log = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Volatile.Write(ref s_identity, identity);
        Volatile.Write(ref s_log, log);
    }

    /// <summary>
    /// Sends a message to the configured log, if any. A handler that throws
    /// is ignored: logging must never take a connection down.
    /// </summary>
    internal static void Log(string category, string message)
    {
        var log = Volatile.Read(ref s_log);
        if (log is null)
        {
            return;
        }

        try
        {
            log(new ProxyLogEntry(DateTimeOffset.Now, category, EscapeControlCharacters(message)));
        }
        catch
        {
            // See the summary.
        }
    }

    /// <summary>
    /// <paramref name="text"/> with control characters other than tab
    /// written out as <c>\x1b</c> and so on. Log messages carry text from
    /// the network (URLs, header values, a PAC script's alerts), and a line
    /// break or terminal escape in one could fake log lines or rewrite a
    /// terminal showing the log. Internal for tests.
    /// </summary>
    internal static string EscapeControlCharacters(string text)
    {
        if (!text.Any(IsEscaped))
        {
            return text;
        }

        var escaped = new System.Text.StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (IsEscaped(c))
            {
                escaped.Append(c <= 0xFF ? $"\\x{(int)c:x2}" : $"\\u{(int)c:x4}");
            }
            else
            {
                escaped.Append(c);
            }
        }

        return escaped.ToString();

        // C0 except tab, DEL, C1, and the line and paragraph separators.
        static bool IsEscaped(char c) => (char.IsControl(c) && c != '\t') || c is '\u2028' or '\u2029';
    }
}

/// <summary>
/// One log message from ProxyCore. <see cref="Category"/> says which part
/// it came from: <c>proxy</c> (connections and errors), <c>session</c> (one
/// line per completed request), <c>system-proxy</c> or <c>pac</c> (automatic
/// proxy configuration scripts).
/// </summary>
public sealed record ProxyLogEntry(DateTimeOffset Time, string Category, string Message)
{
    /// <summary><c>[category] message</c>, the way VioLens's own console output shows it.</summary>
    public override string ToString() => $"[{Category}] {Message}";
}
