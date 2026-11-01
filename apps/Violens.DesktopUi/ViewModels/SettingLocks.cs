using Violens.ProxyCore.Preferences;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// Which settings the user may change: false for one machine policy locks
/// (see PolicyPreferenceStore). The window binds each control's IsEnabled
/// to these, and the view model's setters check them too, so QuickExec and
/// extensions can't change a locked setting either.
/// </summary>
public sealed class SettingLocks(PolicyPreferenceStore preferences)
{
    public bool AcceptExecCommands => !preferences.IsLocked(PreferenceKeys.AcceptExecCommands);

    public bool CheckForUpdates => !preferences.IsLocked(PreferenceKeys.CheckForUpdates);

    /// <summary>Help &gt; Check for Updates, which policy turns off along with the start-up check.</summary>
    public bool CheckForUpdatesNow =>
        CheckForUpdates || preferences.GetBoolPref(PreferenceKeys.CheckForUpdates, true);

    public bool Port => !preferences.IsLocked(PreferenceKeys.PortAutomatic) && !preferences.IsLocked(PreferenceKeys.Port);

    public bool SessionsToKeep => !preferences.IsLocked(PreferenceKeys.SessionsToKeep);

    public bool UpstreamProxy => !preferences.IsLocked(PreferenceKeys.UpstreamProxy);

    public bool UpstreamProxyUserName => !preferences.IsLocked(PreferenceKeys.UpstreamProxyUserName);

    public bool AllowRemoteClients => !preferences.IsLocked(PreferenceKeys.AllowRemoteClients);

    public bool Http2 => !preferences.IsLocked(PreferenceKeys.Http2);

    public bool SkipDecryptionHosts => !preferences.IsLocked(PreferenceKeys.SkipDecryptionHosts);

    public bool ClientCertificate =>
        !preferences.IsLocked(PreferenceKeys.ClientCertificate) && !preferences.IsLocked(PreferenceKeys.ClientCertificateHosts);

    public bool AutoLaunchLegacyHost => !preferences.IsLocked(PreferenceKeys.AutoLaunchLegacyHost);

    public bool FiddlerScriptPath => !preferences.IsLocked(PreferenceKeys.FiddlerScriptPath);

    public bool Any => preferences.Policy.Locked.Count > 0;
}
