using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// Which account a loopback connection to VioLens comes from. Any program on
/// this computer can connect to 127.0.0.1, including another user's, and
/// VioLens signs in to proxies and sends client certificates as the user who
/// runs it, so it lends that identity only to that user's own programs.
/// <list type="bullet">
/// <item>Windows: the TCP table names the process at the client's end
/// (GetExtendedTcpTable), and that process's token names its account.</item>
/// <item>Linux: <c>/proc/net/tcp</c> and <c>tcp6</c> name each connection's
/// user directly.</item>
/// <item>macOS: there's no lookup, so the answer is "don't know".</item>
/// </list>
/// </summary>
internal static class LoopbackConnectionOwner
{
    /// <summary>
    /// Whether the connection from <paramref name="client"/> to
    /// <paramref name="server"/> (both as the server sees them) was opened by
    /// a program running as this process's account. False when it wasn't,
    /// or when it can't be found or its owner can't be read; null when this
    /// platform has no way to tell.
    /// </summary>
    public static bool? IsCurrentUser(IPEndPoint client, IPEndPoint server)
    {
        client = Unmapped(client);
        server = Unmapped(server);
        if (OperatingSystem.IsWindows())
        {
            return WindowsOwnerIsCurrentUser(client, server);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxOwnerIsCurrentUser(client, server);
        }

        return null;
    }

    // An IPv4 connection can show as ::ffff:a.b.c.d, both to the listener and
    // in the tables: a dual-mode socket's (.NET's TcpClient makes one by
    // default) is listed with the IPv6 ones, at least on Linux. So both
    // tables are searched, and addresses compared unwrapped.
    private static IPEndPoint Unmapped(IPEndPoint endPoint) =>
        endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;

    [SupportedOSPlatform("windows")]
    private static bool WindowsOwnerIsCurrentUser(IPEndPoint client, IPEndPoint server)
    {
        // The client's own row: its end is the local one there.
        if (FindWindowsOwner(client, server) is not { } pid)
        {
            return false;
        }

        if (pid == Environment.ProcessId)
        {
            return true;
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            return identity.User is { } user && user == current.User;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? FindWindowsOwner(IPEndPoint local, IPEndPoint remote) =>
        FindWindowsOwner(local, remote, ipv6: local.AddressFamily == AddressFamily.InterNetworkV6) ??
        FindWindowsOwner(local, remote, ipv6: local.AddressFamily != AddressFamily.InterNetworkV6);

    [SupportedOSPlatform("windows")]
    private static int? FindWindowsOwner(IPEndPoint local, IPEndPoint remote, bool ipv6)
    {
        var table = ReadWindowsTable(ipv6 ? AfInet6 : AfInet);
        if (table is null)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        var rowSize = ipv6 ? 56 : 24;
        for (var i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + (i * rowSize), rowSize);
            var (rowLocal, rowRemote, pid) = ipv6
                ? (new IPEndPoint(new IPAddress(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..])), Port(row[20..])),
                   new IPEndPoint(new IPAddress(row[24..40], BinaryPrimitives.ReadUInt32LittleEndian(row[40..])), Port(row[44..])),
                   BinaryPrimitives.ReadInt32LittleEndian(row[52..]))
                : (new IPEndPoint(new IPAddress(row[4..8]), Port(row[8..])),
                   new IPEndPoint(new IPAddress(row[12..16]), Port(row[16..])),
                   BinaryPrimitives.ReadInt32LittleEndian(row[20..]));

            if (SameEndPoint(Unmapped(rowLocal), local) && SameEndPoint(Unmapped(rowRemote), remote))
            {
                return pid;
            }
        }

        return null;
    }

    // A port is the low 16 bits of a DWORD, in network byte order.
    private static int Port(ReadOnlySpan<byte> field) => (field[0] << 8) | field[1];

    // Scope IDs aside: a loopback address has none worth comparing.
    private static bool SameEndPoint(IPEndPoint a, IPEndPoint b) =>
        a.Port == b.Port && a.Address.GetAddressBytes().AsSpan().SequenceEqual(b.Address.GetAddressBytes());

    [SupportedOSPlatform("windows")]
    private static byte[]? ReadWindowsTable(int addressFamily)
    {
        // The table can grow between asking its size and reading it.
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidConnections, 0);
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result != 0)
                {
                    return null;
                }

                var table = new byte[size];
                Marshal.Copy(buffer, table, 0, size);
                return table;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static bool LinuxOwnerIsCurrentUser(IPEndPoint client, IPEndPoint server)
    {
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // no IPv6, say
            }

            // "sl local_address rem_address st tx:rx tr:when retrnsmt uid ..."
            foreach (var line in lines.Skip(1))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 7 &&
                    ParseLinuxEndPoint(fields[1]) is { } local && SameEndPoint(Unmapped(local), client) &&
                    ParseLinuxEndPoint(fields[2]) is { } remote && SameEndPoint(Unmapped(remote), server))
                {
                    return uint.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid == geteuid();
                }
            }
        }

        return false;
    }

    /// <summary>
    /// "0100007F:1F90": the address as 32-bit words printed in the
    /// kernel's own byte order, then the port in hex.
    /// </summary>
    internal static IPEndPoint? ParseLinuxEndPoint(string text)
    {
        var colon = text.IndexOf(':');
        if (colon is not (8 or 32) ||
            !int.TryParse(text.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        var bytes = new byte[colon / 2];
        for (var word = 0; word < colon / 8; word++)
        {
            if (!uint.TryParse(text.AsSpan(word * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            BitConverter.TryWriteBytes(bytes.AsSpan(word * 4, 4), value);
        }

        return new IPEndPoint(new IPAddress(bytes), port);
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("libc")]
    private static extern uint geteuid();
}
