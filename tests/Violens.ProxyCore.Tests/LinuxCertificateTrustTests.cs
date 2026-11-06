using Violens.ProxyCore.Certificates;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>
/// Install and Remove run certutil against real browser databases, so only
/// finding the databases, the arguments and the output parsing are tested.
/// </summary>
public class LinuxCertificateTrustTests
{
    [Fact]
    public void Nickname_IsTheNamePrefixAndThumbprint()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1), "DO_NOT_TRUST_ViolensRoot");

        var nickname = LinuxCertificateTrust.Nickname(root);

        Assert.Equal($"DO_NOT_TRUST_ViolensRoot {root.Thumbprint}", nickname);
        Assert.True(LinuxCertificateTrust.IsOurs(nickname, "DO_NOT_TRUST_ViolensRoot", thumbprint: null));
        Assert.True(LinuxCertificateTrust.IsOurs(nickname, "DO_NOT_TRUST_ViolensRoot", root.Thumbprint.ToLowerInvariant()));
        Assert.False(LinuxCertificateTrust.IsOurs(nickname, "DO_NOT_TRUST_ViolensRoot", "0000"));
        Assert.False(LinuxCertificateTrust.IsOurs("Some Other CA", "DO_NOT_TRUST_ViolensRoot", thumbprint: null));
    }

    [Fact]
    public void BuildsCertutilArguments()
    {
        Assert.Equal(
            ["-A", "-d", "sql:/home/u/.pki/nssdb", "-n", "Root ABC", "-t", "C,,", "-i", "/tmp/root.cer"],
            LinuxCertificateTrust.BuildAddArguments("/home/u/.pki/nssdb", "Root ABC", "/tmp/root.cer"));
        Assert.Equal(
            ["-D", "-d", "sql:/db", "-n", "Root ABC"],
            LinuxCertificateTrust.BuildDeleteArguments("/db", "Root ABC"));
        Assert.Equal(["-L", "-d", "sql:/db"], LinuxCertificateTrust.BuildListArguments("/db"));
        Assert.Equal(["-N", "-d", "sql:/db", "--empty-password"], LinuxCertificateTrust.BuildCreateDatabaseArguments("/db"));
    }

    [Fact]
    public void ParsesCertutilListOutput()
    {
        const string output = """

            Certificate Nickname                                         Trust Attributes
                                                                         SSL,S/MIME,JAR/XPI

            DO_NOT_TRUST_ViolensRoot 0123456789ABCDEF0123456789ABCDEF01234567 C,,
            Corporate Root CA (2024)                                     CT,C,C
            someone@example.com                                          u,u,u

            """;

        Assert.Equal(
            ["DO_NOT_TRUST_ViolensRoot 0123456789ABCDEF0123456789ABCDEF01234567", "Corporate Root CA (2024)", "someone@example.com"],
            LinuxCertificateTrust.ParseNicknames(output));
    }

    [Fact]
    public void ParsesAnEmptyDatabase()
    {
        const string output = "\nCertificate Nickname                                         Trust Attributes\n" +
                              "                                                             SSL,S/MIME,JAR/XPI\n\n";

        Assert.Empty(LinuxCertificateTrust.ParseNicknames(output));
    }

    [Fact]
    public void FindsTheSharedDatabaseAndFirefoxProfiles()
    {
        var home = Path.Combine(Path.GetTempPath(), $"violens-home-{Guid.NewGuid():N}");
        try
        {
            void Db(params string[] parts)
            {
                var folder = Path.Combine([home, .. parts]);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "cert9.db"), []);
            }

            Db(".mozilla", "firefox", "abc.default-release");
            Directory.CreateDirectory(Path.Combine(home, ".mozilla", "firefox", "Crash Reports"));
            Db("snap", "firefox", "common", ".mozilla", "firefox", "snap.default");
            Db(".var", "app", "org.mozilla.firefox", ".mozilla", "firefox", "flat.default");
            Db("snap", "chromium", "current", ".pki", "nssdb");

            // No shared database yet: listed to be made, when asked to.
            var found = LinuxCertificateTrust.FindDatabases(home, createDefault: true);
            Assert.Equal(
                [
                    (Path.Combine(home, ".pki", "nssdb"), true),
                    (Path.Combine(home, "snap", "chromium", "current", ".pki", "nssdb"), false),
                    (Path.Combine(home, ".mozilla", "firefox", "abc.default-release"), false),
                    (Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox", "snap.default"), false),
                    (Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox", "flat.default"), false),
                ],
                found.Select(db => (db.Path, db.Create)));
            Assert.Contains("Firefox (snap)", found[3].Description);

            // Removing never makes one.
            Assert.DoesNotContain(
                LinuxCertificateTrust.FindDatabases(home, createDefault: false),
                db => db.Path == Path.Combine(home, ".pki", "nssdb"));

            Db(".pki", "nssdb");
            Assert.False(LinuxCertificateTrust.FindDatabases(home, createDefault: true)[0].Create);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
