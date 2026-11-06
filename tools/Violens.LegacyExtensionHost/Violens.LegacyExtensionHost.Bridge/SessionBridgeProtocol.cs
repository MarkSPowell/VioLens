using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Violens.LegacyExtensionHost.Bridge;

/// <summary>
/// Pipe name, timeouts and framing for the session bridge, shared by both
/// ends. A local named pipe, which other machines can't reach.
/// </summary>
public static class SessionBridgeProtocol
{
    /// <summary>
    /// Versioned in the name, so an incompatible build fails to connect
    /// rather than misbehaving; the two ends aren't always updated together.
    /// </summary>
    public const string BasePipeName = "Violens.LegacyExtensionHost.SessionBridge.v1";

    private const string SecretFileName = "legacy-host-bridge.id";

    private static readonly Lazy<string> CurrentPipeName = new(() =>
        BasePipeName + "." + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture) + "." + AccountSecret(SecretFolder()));

    /// <summary>
    /// The pipe's name for this account in this logon session, which both
    /// ends work out the same way. Pipe names are shared by every account on
    /// the computer, so a fixed name could be taken first by another
    /// account, leaving this one's extensions without their pipe. The name
    /// ends in a random value kept in this account's own app data, which
    /// other accounts can't read, so they can't know it in advance.
    /// </summary>
    public static string PipeName => CurrentPipeName.Value;

    private static string SecretFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VioLens");

    /// <summary>
    /// The random value in <paramref name="folder"/>, made the first time
    /// either end asks. Internal for tests.
    /// </summary>
    internal static string AccountSecret(string folder)
    {
        var path = Path.Combine(folder, SecretFileName);
        if (TryRead(path) is { } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(folder);
        var bytes = new byte[16];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }

        var secret = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var text = Encoding.ASCII.GetBytes(secret);
            file.Write(text, 0, text.Length);
            return secret;
        }
        catch (IOException)
        {
            // The app and the host started together, and the other one
            // wrote it first; or what's there is damaged, and is replaced.
            if (TryRead(path) is { } madeByTheOtherEnd)
            {
                return madeByTheOtherEnd;
            }

            File.WriteAllText(path, secret);
            return secret;
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.Length == 32 && text.All(Uri.IsHexDigit) ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How long to try connecting before deciding no host is there. Short, so traffic isn't held up.</summary>
    public const int ConnectTimeoutMilliseconds = 250;

    /// <summary>
    /// How long a round trip may take, including the extensions' hooks. Long
    /// enough for a slow extension; a hung one still lets traffic through.
    /// </summary>
    public const int RoundTripTimeoutMilliseconds = 3000;

    /// <summary>The pipe's buffer size; messages can be larger.</summary>
    public const int PipeBufferSize = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Writes one message: a 4-byte little-endian length, then UTF-8 JSON.</summary>
    public static void WriteMessage<T>(Stream stream, T message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        var lengthPrefix = System.BitConverter.GetBytes(payload.Length);
        if (!System.BitConverter.IsLittleEndian)
        {
            System.Array.Reverse(lengthPrefix);
        }

        stream.Write(lengthPrefix, 0, lengthPrefix.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    /// <summary>Reads one message, looping because a read may return fewer bytes than asked for.</summary>
    public static T ReadMessage<T>(Stream stream)
    {
        var lengthPrefix = ReadExactly(stream, 4);
        if (!System.BitConverter.IsLittleEndian)
        {
            System.Array.Reverse(lengthPrefix);
        }

        var length = System.BitConverter.ToInt32(lengthPrefix, 0);
        if (length < 0 || length > 64 * 1024 * 1024)
        {
            // A real message from either end of this protocol is never
            // remotely this large (an inspected HTTP body would have to be
            // enormous) -- rejecting outright here guards against reading a
            // garbled/out-of-sync stream as if it were a legitimate,
            // absurdly large allocation request.
            throw new InvalidDataException($"Session bridge message length {length} is out of the accepted range.");
        }

        var payload = ReadExactly(stream, length);
        var result = JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        return result ?? throw new InvalidDataException("Session bridge message deserialized to null.");
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Session bridge pipe closed before a full message was received.");
            }

            offset += read;
        }

        return buffer;
    }
}
