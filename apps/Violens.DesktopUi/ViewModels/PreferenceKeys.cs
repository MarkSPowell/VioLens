namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// Every preference the desktop app uses (see the Preferences Design).
/// Breakpoints and the AutoResponder switch are deliberately not saved:
/// re-arming them at launch would silently affect every app on the machine.
/// </summary>
internal static class PreferenceKeys
{
    public const string PortAutomatic = "violens.proxy.port.auto";
    public const string Port = "violens.proxy.port";

    public const string ShowFiddlerScriptPanel = "violens.ui.panels.fiddlerscript";
    public const string ShowExtensionsPanel = "violens.ui.panels.extensions";
    public const string ShowLegacyExtensionHostPanel = "violens.ui.panels.legacyhost";
    public const string ShowAlsoBreakOnRow = "violens.ui.panels.alsobreakon";

    public const string FilterText = "violens.ui.filter.text";

    /// <summary>How many sessions to keep, oldest removed first; 0 keeps every session.</summary>
    public const string SessionsToKeep = "violens.ui.sessionlist.keep";

    public const string FiddlerScriptPath = "violens.fiddlerscript.path";
    public const string AutoLaunchLegacyHost = "violens.extensions.legacyhost.autolaunch";

    public const string ShowConnectionsRow = "violens.ui.panels.connections";
    public const string UpstreamProxy = "violens.proxy.upstream";
    public const string AllowRemoteClients = "violens.proxy.allowremote";

    /// <summary>Whether browsers may use HTTP/2 with VioLens.</summary>
    public const string Http2 = "violens.proxy.http2.enabled";

    /// <summary>
    /// Hosts whose HTTPS traffic is tunnelled without decrypting it, for apps
    /// that pin certificates. A list preference (semicolon-separated, with
    /// <c>*</c> wildcards), per the Project Plan's extensible config lists.
    /// </summary>
    public const string SkipDecryptionHosts = "violens.config.https.skipdecryption";

    /// <summary>The thumbprint of the client certificate (mutual TLS) to send, from the personal store. Empty sends none.</summary>
    public const string ClientCertificate = "violens.proxy.clientcertificate";

    /// <summary>The hosts the client certificate is sent to (a list preference, <c>*</c> wildcards).</summary>
    public const string ClientCertificateHosts = "violens.config.https.clientcertificatehosts";

    /// <summary>Whether violens-exec may send QuickExec commands over the pipe.</summary>
    public const string AcceptExecCommands = "violens.automation.exec";

    /// <summary>Whether VioLens checks for a newer release at start-up.</summary>
    public const string CheckForUpdates = "violens.update.check";

    /// <summary>The user name for an upstream proxy that asks for a sign-in. The password is never saved.</summary>
    public const string UpstreamProxyUserName = "violens.proxy.upstream.username";

    /// <summary>The main window's placement: position in screen pixels, size in the window's own units.</summary>
    public const string WindowLeft = "violens.ui.window.left";
    public const string WindowTop = "violens.ui.window.top";
    public const string WindowWidth = "violens.ui.window.width";
    public const string WindowHeight = "violens.ui.window.height";
    public const string WindowMaximized = "violens.ui.window.maximized";

    /// <summary>
    /// Session-list columns the user hid, by key (see
    /// MainWindowViewModel.Columns.cs): the header for built-in columns,
    /// <c>script:</c>Title and <c>flag:</c>Name for script and extension
    /// columns. Empty means every column is shown.
    /// </summary>
    public const string HiddenSessionColumns = "violens.ui.sessionlist.hiddencolumns";

    /// <summary>
    /// The session list's column order and widths, one <c>width|key</c>
    /// entry per column in display order (see ColumnLayout). Unset means
    /// the order and widths in MainWindow.axaml.
    /// </summary>
    public const string SessionColumnLayout = "violens.ui.sessionlist.columns";

    /// <summary>TextWizard's last conversion and character encoding, by the names it shows.</summary>
    public const string TextWizardTransform = "violens.ui.textwizard.transform";
    public const string TextWizardEncoding = "violens.ui.textwizard.encoding";

    /// <summary>The Import/Export via Extension picker's last choice (see FormatChoice.Key), highlighted next time.</summary>
    public const string LastImportFormat = "violens.extensions.import.lastformat";
    public const string LastExportFormat = "violens.extensions.export.lastformat";
}
