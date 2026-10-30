namespace Violens.ProxyCore.SystemProxy;

/// <summary>
/// The system proxy on whichever platform this is: <see cref="WinInetSystemProxy"/>
/// on Windows, <see cref="MacOSSystemProxy"/> on macOS,
/// <see cref="LinuxSystemProxy"/> (GNOME and KDE settings) on Linux. Elsewhere every
/// method does nothing, so callers needn't check the platform.
/// </summary>
public static class SystemProxyController
{
    /// <summary>Points the system proxy at <c>127.0.0.1:port</c>.</summary>
    public static void Enable(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            WinInetSystemProxy.Enable(port);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacOSSystemProxy.Enable(port);
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxSystemProxy.Enable(port);
        }
    }

    /// <summary>
    /// The proxy this machine was set to use before VioLens registered
    /// itself, for VioLens to forward its own connections through. Call
    /// before <see cref="Enable"/>. Never throws: a failure to read the
    /// settings is reported in the description and means "connect directly".
    /// </summary>
    public static UpstreamProxyDetection DetectUpstream(int ownPort)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WinInetSystemProxy.DetectUpstream(ownPort);
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacOSSystemProxy.DetectUpstream(ownPort);
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxSystemProxy.DetectUpstream(ownPort);
            }
        }
        catch (Exception ex)
        {
            return new UpstreamProxyDetection(null, $"couldn't read the system proxy settings ({ex.Message}), so connecting directly");
        }

        return UpstreamProxyDetection.None;
    }

    /// <summary>Restores the system proxy as it was before <see cref="Enable"/>.</summary>
    public static void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            WinInetSystemProxy.Disable();
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacOSSystemProxy.Disable();
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxSystemProxy.Disable();
        }
    }

    /// <summary>Call once at launch, before anything else touches the system proxy, to undo a crashed run.</summary>
    public static void RecoverFromCrash()
    {
        if (OperatingSystem.IsWindows())
        {
            WinInetSystemProxy.RecoverFromCrash();
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacOSSystemProxy.RecoverFromCrash();
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxSystemProxy.RecoverFromCrash();
        }
    }
}
