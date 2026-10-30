namespace Violens.ProxyCore.Certificates;

/// <summary>A trust-store command (macOS <c>security</c>, Linux <c>certutil</c>) failed; carries its exit code and stderr.</summary>
public sealed class CertificateTrustException : Exception
{
    public int ExitCode { get; }

    public string StandardError { get; }

    public CertificateTrustException(string message, int exitCode, string standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }
}
