using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Violens.DesktopUi.Controls;
using Violens.DesktopUi.Models;
using Violens.Extensibility.Inspection;
using Violens.ProxyCore.Sessions;

namespace Violens.DesktopUi.ViewModels;

/// <summary>One phase in the Timing tab: its name, duration, and where its bar sits.</summary>
public sealed record TimingPhaseRow(string Label, string Duration, double StartFraction, double LengthFraction, IBrush Brush);

/// <summary>One line in a Statistics breakdown, with a bar showing its share.</summary>
public sealed record StatisticsRow(string Name, string Detail, double Fraction);

/// <summary>A titled group of rows in the Statistics tab.</summary>
public sealed record StatisticsSection(string Title, IReadOnlyList<StatisticsRow> Rows);

/// <summary>
/// The timing views, laid out like a browser's developer tools: the
/// waterfall column in the session list (see <see cref="WaterfallBar"/>),
/// the request summary under it, the Timing tab for the selected session,
/// and the Statistics tab for the sessions the filter shows.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly IBrush StatisticsBarBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x5C, 0x8D, 0xC4));
    private static readonly TimeSpan SummaryDelay = TimeSpan.FromMilliseconds(300);

    private string _requestSummary = "0 requests";
    private string _timingHeader = "Select a session to see its timing.";
    private bool _isStatisticsTabSelected;
    private string _statisticsSummary = string.Empty;
    private bool _statisticsStale = true;
    private bool _computingStatistics;
    private DispatcherTimer? _summaryTimer;
    private RelayCommand? _refreshStatisticsCommand;

    /// <summary>Under the session list, as in developer tools: "12 requests | 1.2 MB transferred | Finish: 2.31 s".</summary>
    public string RequestSummary
    {
        get => _requestSummary;
        private set => SetField(ref _requestSummary, value);
    }

    /// <summary>The Timing tab's heading lines for the selected session.</summary>
    public string TimingHeader
    {
        get => _timingHeader;
        private set => SetField(ref _timingHeader, value);
    }

    public ObservableCollection<TimingPhaseRow> TimingPhases { get; } = [];

    /// <summary>Set by the Statistics tab while it's the one showing; statistics are worked out only then.</summary>
    public bool IsStatisticsTabSelected
    {
        get => _isStatisticsTabSelected;
        set
        {
            if (SetField(ref _isStatisticsTabSelected, value) && value && _statisticsStale)
            {
                _ = RefreshStatisticsAsync();
            }
        }
    }

    public string StatisticsSummary
    {
        get => _statisticsSummary;
        private set => SetField(ref _statisticsSummary, value);
    }

    public ObservableCollection<StatisticsSection> StatisticsSections { get; } = [];

    public RelayCommand RefreshStatisticsCommand => _refreshStatisticsCommand ??= new RelayCommand(() => _ = RefreshStatisticsAsync());

    /// <summary>Called once from the constructor.</summary>
    private void InitializeTimingViews()
    {
        FilteredSessions.CollectionChanged += OnFilteredSessionsChanged;
        WaterfallScale.Shared.Reset([]);
    }

    private void OnFilteredSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (SessionRow row in e.NewItems)
            {
                WaterfallScale.Shared.Include(row.Session);
            }
        }
        else
        {
            WaterfallScale.Shared.Reset(FilteredSessions.Select(row => row.Session));
        }

        _statisticsStale = true;

        // Sessions arrive in bursts; work the summary out once per burst.
        if (_summaryTimer is null)
        {
            _summaryTimer = new DispatcherTimer { Interval = SummaryDelay };
            _summaryTimer.Tick += (_, _) =>
            {
                _summaryTimer.Stop();
                UpdateRequestSummary();
                if (IsStatisticsTabSelected)
                {
                    _ = RefreshStatisticsAsync();
                }
            };
        }

        if (!_summaryTimer.IsEnabled)
        {
            _summaryTimer.Start();
        }
    }

    private void UpdateRequestSummary()
    {
        var count = FilteredSessions.Count;
        var transferred = FilteredSessions.Sum(row => (long)row.Session.Response.Body.Length + row.Session.Request.Body.Length);
        var finish = WaterfallScale.Shared.IsEmpty ? string.Empty : $" | Finish: {TimingPalette.Format(WaterfallScale.Shared.SpanMilliseconds)}";
        var hidden = Sessions.Count - count;
        RequestSummary =
            $"{count} {(count == 1 ? "request" : "requests")}" +
            (hidden > 0 ? $" ({hidden} hidden by the filter)" : string.Empty) +
            $" | {FormatBytes(transferred)} transferred{finish}";
    }

    /// <summary>Fills the Timing tab for the selected session (called when the selection changes).</summary>
    private void RefreshTiming()
    {
        TimingPhases.Clear();
        if (SelectedSessionRow?.Session is not { } session)
        {
            TimingHeader = "Select a session to see its timing.";
            return;
        }

        if (session.Timings is not { } timings)
        {
            TimingHeader = "No timing was recorded for this session (it may have been imported from a file without timings).";
            return;
        }

        var total = Math.Max(timings.Total, 0.001);
        var offset = 0.0;
        foreach (var (_, label, brush, milliseconds) in TimingPalette.Phases(timings))
        {
            TimingPhases.Add(new TimingPhaseRow(label, TimingPalette.Format(milliseconds), offset / total, milliseconds / total, brush));
            offset += milliseconds;
        }

        var lines = new List<string>
        {
            $"Started at {session.StartedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture)}, took {TimingPalette.Format(timings.Total)}.",
        };

        if (timings.AnsweredLocally)
        {
            lines.Add("VioLens answered this request itself, so it never reached a server.");
        }
        else if (timings.ConnectionId > 0)
        {
            lines.Add(
                (timings.ConnectionReused ? $"Reused connection #{timings.ConnectionId}" : $"New connection #{timings.ConnectionId}") +
                (timings.Proxy is null ? " to the server." : $" through the proxy {timings.Proxy}."));
        }

        if (timings.Tls is { } tls)
        {
            if (tls.Protocol is not null)
            {
                lines.Add($"{tls.Protocol}, {tls.CipherSuite}.");
            }

            if (tls.CertificateSubject is not null)
            {
                lines.Add(
                    $"Server certificate: {tls.CertificateSubject}, issued by {tls.CertificateIssuer}" +
                    (tls.CertificateNotAfter is { } notAfter
                        ? $", valid until {notAfter.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}."
                        : "."));
            }

            if (tls.ClientCertificateRequested)
            {
                lines.Add(
                    "The server asked for a client certificate (mutual TLS)" +
                    (tls.AcceptableIssuers.Count > 0 ? $", issued by: {tls.DescribeAcceptableIssuers()}." : ", from any issuer.") +
                    (tls.ClientCertificateSent is { } sent
                        ? $" VioLens sent {sent}."
                        : " VioLens sent none: choose one under Tools > Connections, with this host in its list."));
            }
        }

        if (timings.Paused > 0)
        {
            lines.Add($"Paused at a breakpoint for {TimingPalette.Format(timings.Paused)} (included above).");
        }

        lines.Add("Timed at VioLens: time the browser spent before sending the request isn't included.");
        TimingHeader = string.Join(Environment.NewLine, lines);
    }

    /// <summary>Works the Statistics tab out for the sessions the filter shows (in the background: bodies are decompressed).</summary>
    private async Task RefreshStatisticsAsync()
    {
        if (_computingStatistics)
        {
            return;
        }

        _computingStatistics = true;
        _statisticsStale = false;
        try
        {
            var sessions = FilteredSessions.Select(row => row.Session).ToList();
            var (statistics, bloatBytes, bloatImages) = await Task.Run(() =>
            {
                var computed = SessionStatistics.Compute(sessions);
                var bloat = sessions.Select(s => ImageBloatAnalyzer.AnalyzeResponse(s.Response)).Where(r => r is { BloatBytes: > 0 }).ToList();
                return (computed, bloat.Sum(r => r!.BloatBytes), bloat.Count);
            });

            ShowStatistics(statistics, bloatBytes, bloatImages);
        }
        finally
        {
            _computingStatistics = false;
        }
    }

    private void ShowStatistics(SessionStatistics statistics, long bloatBytes, int bloatImages)
    {
        StatisticsSections.Clear();
        if (statistics.Requests == 0)
        {
            StatisticsSummary = "No sessions to summarise.";
            return;
        }

        var saved = statistics.DecodedResponseBytes - statistics.ResponseBytes;
        StatisticsSummary = string.Join(Environment.NewLine,
            $"{statistics.Requests} {(statistics.Requests == 1 ? "request" : "requests")} over {TimingPalette.Format(statistics.Span.TotalMilliseconds)}" +
            (Sessions.Count > statistics.Requests ? " (the sessions the filter shows)." : "."),
            $"Sent {FormatBytes(statistics.RequestBytes)}; received {FormatBytes(statistics.ResponseBytes)}" +
            (saved > 0 ? $" ({FormatBytes(statistics.DecodedResponseBytes)} decompressed: compression saved {FormatBytes(saved)})." : "."));

        var maxBytes = Math.Max(1, statistics.ByKind.Max(g => g.ResponseBytes));
        StatisticsSections.Add(new StatisticsSection(
            "By type (bytes received)",
            statistics.ByKind.Select(g => new StatisticsRow(g.Name, $"{g.Count} · {FormatBytes(g.ResponseBytes)}", (double)g.ResponseBytes / maxBytes)).ToList()));

        var maxStatus = Math.Max(1, statistics.ByStatus.Max(g => g.Count));
        StatisticsSections.Add(new StatisticsSection(
            "By status",
            statistics.ByStatus.Select(g => new StatisticsRow(g.Name, $"{g.Count}", (double)g.Count / maxStatus)).ToList()));

        var maxHost = Math.Max(1, statistics.ByHost.Max(g => g.Count));
        StatisticsSections.Add(new StatisticsSection(
            "Busiest hosts",
            statistics.ByHost.Select(g => new StatisticsRow(g.Name, $"{g.Count} · {FormatBytes(g.ResponseBytes)} · {TimingPalette.Format(g.TotalMilliseconds)}", (double)g.Count / maxHost)).ToList()));

        if (statistics.Slowest.Count > 0)
        {
            var slowest = Math.Max(1, statistics.Slowest[0].Timings!.Total);
            StatisticsSections.Add(new StatisticsSection(
                "Slowest requests",
                statistics.Slowest.Select(s => new StatisticsRow($"#{s.Id} {SessionUrl.Of(s)}", TimingPalette.Format(s.Timings!.Total), s.Timings.Total / slowest)).ToList()));
        }

        var wins = new List<StatisticsRow>();
        if (statistics.Uncompressed.Count > 0)
        {
            wins.Add(new StatisticsRow(
                $"{statistics.Uncompressed.Count} text {(statistics.Uncompressed.Count == 1 ? "response" : "responses")} sent without compression",
                FormatBytes(statistics.Uncompressed.Sum(s => (long)s.Response.Body.Length)),
                0));
        }

        if (bloatImages > 0)
        {
            wins.Add(new StatisticsRow(
                $"{bloatImages} {(bloatImages == 1 ? "image carries" : "images carry")} bloat (metadata and other bytes that don't change how it looks)",
                FormatBytes(bloatBytes),
                0));
        }

        if (statistics.NotCacheable.Count > 0)
        {
            wins.Add(new StatisticsRow(
                $"{statistics.NotCacheable.Count} successful GET {(statistics.NotCacheable.Count == 1 ? "response has" : "responses have")} no caching headers",
                string.Empty,
                0));
        }

        if (wins.Count > 0)
        {
            StatisticsSections.Add(new StatisticsSection("Easy wins", wins));
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MB",
    };

    /// <summary>The brush the Statistics tab's bars use.</summary>
    public static IBrush StatisticsBar => StatisticsBarBrush;
}
