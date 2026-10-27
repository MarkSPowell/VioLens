using System.Security.Cryptography.X509Certificates;
using System.Text;
using Violens.ProxyCore.Http;
using Violens.AppCore.Text;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// The Composer tab and Replay (Fiddler Classic's "Reissue"). Both send
/// through VioLens's own listener with <see cref="ComposerClient"/>, so
/// what they send shows up as a new session and goes through the
/// AutoResponder, breakpoints, FiddlerScript and extensions like any other
/// traffic. Capture has to be running.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const string DefaultComposerText =
        "GET https://example.com/ HTTP/1.1\r\n" +
        "User-Agent: VioLens Composer\r\n" +
        "Accept: */*\r\n" +
        "\r\n";

    /// <summary>Where the Composer's request body comes from (<see cref="ComposerBodySourceIndex"/>).</summary>
    public enum ComposerBodySource
    {
        /// <summary>Typed after the headers in the request text.</summary>
        RequestText,

        /// <summary>Hex in the body box, for bytes that aren't text.</summary>
        Hex,

        /// <summary>The bytes of a file, read when the request is sent.</summary>
        File,
    }

    private string _composerText = DefaultComposerText;
    private ComposerBodySource _composerBodySource;
    private string _composerHexBody = string.Empty;
    private string _composerBodyFile = string.Empty;
    private string _composerStatus = "Type or paste a request, then Execute. Start capture first.";
    private bool _isSending;

    /// <summary>Raised when something asks for the Composer tab to be shown (Edit in Composer).</summary>
    public event Action? ComposerShowRequested;

    /// <summary>The request being composed, as raw HTTP: a request line with an https:// URL, headers, a blank line, then any body.</summary>
    public string ComposerText
    {
        get => _composerText;
        set => SetField(ref _composerText, value ?? string.Empty);
    }

    /// <summary>The body source as an index for the ComboBox: 0 request text, 1 hex, 2 file.</summary>
    public int ComposerBodySourceIndex
    {
        get => (int)_composerBodySource;
        set
        {
            var source = Enum.IsDefined(typeof(ComposerBodySource), value) ? (ComposerBodySource)value : ComposerBodySource.RequestText;
            if (_composerBodySource != source)
            {
                _composerBodySource = source;
                RaisePropertyChanged(nameof(ComposerBodySourceIndex));
                RaisePropertyChanged(nameof(IsComposerHexBody));
                RaisePropertyChanged(nameof(IsComposerFileBody));
            }
        }
    }

    public bool IsComposerHexBody => _composerBodySource == ComposerBodySource.Hex;

    public bool IsComposerFileBody => _composerBodySource == ComposerBodySource.File;

    /// <summary>The body as hex, when the body source is Hex.</summary>
    public string ComposerHexBody
    {
        get => _composerHexBody;
        set => SetField(ref _composerHexBody, value ?? string.Empty);
    }

    /// <summary>The file whose bytes are the body, when the body source is File.</summary>
    public string ComposerBodyFile
    {
        get => _composerBodyFile;
        set => SetField(ref _composerBodyFile, value ?? string.Empty);
    }

    /// <summary>What happened to the last request sent from the Composer or by Replay.</summary>
    public string ComposerStatus
    {
        get => _composerStatus;
        private set => SetField(ref _composerStatus, value);
    }

    /// <summary>Sends <see cref="ComposerText"/>.</summary>
    public RelayCommand ExecuteComposerCommand { get; }

    /// <summary>Sends the selected session's request again, unchanged.</summary>
    public RelayCommand ReplaySelectedSessionCommand { get; }

    /// <summary>Copies the selected session's request into the Composer to edit.</summary>
    public RelayCommand EditInComposerCommand { get; }

    private bool CanSend => IsRunning && !_isSending;

    private void RaiseComposerCanExecuteChanged()
    {
        ExecuteComposerCommand?.RaiseCanExecuteChanged();
        ReplaySelectedSessionCommand?.RaiseCanExecuteChanged();
        EditInComposerCommand?.RaiseCanExecuteChanged();
    }

    private async void ExecuteComposer()
    {
        if (!HttpMessageText.TryParseRequest(ComposerText, out var typed, out var parseError))
        {
            ComposerStatus = $"Not sent: {parseError}";
            return;
        }

        switch (_composerBodySource)
        {
            case ComposerBodySource.Hex when typed!.Body.Length > 0:
            case ComposerBodySource.File when typed!.Body.Length > 0:
                ComposerStatus = "Not sent: the request text has a body too. Remove it, or set Body to \"In the request text\".";
                return;
            case ComposerBodySource.Hex:
                if (!TextTransforms.TryParseHex(ComposerHexBody, out var hexBytes, out var hexError))
                {
                    ComposerStatus = $"Not sent: the hex body isn't valid. {hexError}";
                    return;
                }

                typed = typed! with { Body = hexBytes };
                break;
            case ComposerBodySource.File:
                if (string.IsNullOrWhiteSpace(ComposerBodyFile))
                {
                    ComposerStatus = "Not sent: choose the file to send as the body.";
                    return;
                }

                try
                {
                    typed = typed! with { Body = await File.ReadAllBytesAsync(ComposerBodyFile) };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    ComposerStatus = $"Not sent: couldn't read {ComposerBodyFile}: {ex.Message}";
                    return;
                }

                break;
        }

        await SendAsync(typed!, "Composer");
    }

    private async void ReplaySelectedSession()
    {
        if (SelectedSessionRow?.Session is not { } session)
        {
            return;
        }

        var request = session.Request with { Target = SessionUrl.Of(session) };
        await SendAsync(request, $"Replay of #{session.Id}");
        StatusText = ComposerStatus;
    }

    private void EditInComposer()
    {
        if (SelectedSessionRow?.Session is not { } session)
        {
            return;
        }

        var request = session.Request with { Target = SessionUrl.Of(session) };

        // A body that isn't text would be mangled in the text box, so it goes
        // in the hex box instead, byte for byte.
        if (request.Body.Length > 0 && TextTransforms.BytesToText(request.Body, new UTF8Encoding(false)).Note is not null)
        {
            ComposerText = HttpMessageText.FormatHead(request);
            ComposerHexBody = TextTransforms.FormatHex(request.Body);
            ComposerBodySourceIndex = (int)ComposerBodySource.Hex;
            ComposerStatus = $"Loaded #{session.Id}. Its body isn't text, so it's in the hex box below. Edit it, then Execute.";
        }
        else
        {
            ComposerText = HttpMessageText.Format(request);
            ComposerHexBody = string.Empty;
            ComposerBodySourceIndex = (int)ComposerBodySource.RequestText;
            ComposerStatus = $"Loaded #{session.Id}. Edit it, then Execute.";
        }

        ComposerShowRequested?.Invoke();
    }

    private async Task SendAsync(CapturedRequest typed, string what)
    {
        if (_proxy is not { } listener || _authority is null)
        {
            ComposerStatus = $"{what} not sent: start capture first. Requests are sent through VioLens so they're captured.";
            return;
        }

        if (!ComposerClient.TryResolve(typed, out var url, out var toSend, out var error))
        {
            ComposerStatus = $"{what} not sent: {error}";
            return;
        }

        _isSending = true;
        RaiseComposerCanExecuteChanged();
        ComposerStatus = $"{what}: sending {toSend!.Method} {url}...";
        try
        {
            var rootDer = _authority.RootCertificate.Export(X509ContentType.Cert);
            // Generous: a breakpoint may hold the request while someone edits it.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var response = await Task.Run(() => ComposerClient.SendAsync(listener.Port, rootDer, url!, toSend, timeout.Token));
            ComposerStatus = $"{what}: {response.StatusCode} {response.ReasonPhrase} from {toSend.Method} {url}. It's the newest session in the list.";
            if (HttpVersions.IsHttp2(toSend.HttpVersion) && !HttpVersions.IsHttp2(response.HttpVersion))
            {
                ComposerStatus += " It went over HTTP/1.1: turn on Use HTTP/2 with browsers (Tools > Connections) to send HTTP/2.";
            }
        }
        catch (Exception ex)
        {
            ComposerStatus = $"{what} failed: {ex.Message}";
        }
        finally
        {
            _isSending = false;
            RaiseComposerCanExecuteChanged();
        }
    }
}
