using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;

namespace WinNotifyBridge.Tray
{
    public partial class DiagnosticsWindow : Window
    {
        private const string NotificationsLogName = "Microsoft-Windows-PushNotification-Platform/Operational";

        private static readonly string WpnDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Notifications\wpndatabase.db");

        private readonly DispatcherTimer _refreshTimer;
        private readonly ObservableCollection<DiagnosticsEventItem> _events;
        private readonly ObservableCollection<WpnNotificationItem> _wpnRows;

        public DiagnosticsWindow()
        {
            InitializeComponent();

            _events = new ObservableCollection<DiagnosticsEventItem>();
            _wpnRows = new ObservableCollection<WpnNotificationItem>();
            EventsGrid.ItemsSource = _events;
            WpnGrid.ItemsSource = _wpnRows;

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _refreshTimer.Tick += (_, __) => LoadAll();

            Loaded += (_, __) =>
            {
                LoadAll();
                if (AutoRefreshCheckBox.IsChecked == true)
                {
                    _refreshTimer.Start();
                }
            };

            Closed += (_, __) => _refreshTimer.Stop();
        }

        private void RefreshClick(object sender, RoutedEventArgs e)
        {
            LoadAll();
        }

        private void AutoRefreshChanged(object sender, RoutedEventArgs e)
        {
            if (_refreshTimer == null)
            {
                return;
            }

            if (AutoRefreshCheckBox.IsChecked == true)
            {
                _refreshTimer.Start();
                return;
            }

            _refreshTimer.Stop();
        }

        private void LoadAll()
        {
            var eventRows = new List<DiagnosticsEventItem>();
            var wpnRows = new List<WpnNotificationItem>();
            string eventError = null;
            string wpnError = null;

            try
            {
                eventRows = ReadLatestEvents(120);
            }
            catch (EventLogNotFoundException)
            {
                eventError = "Event Viewer channel not found.";
            }
            catch (Exception ex)
            {
                eventError = ex.Message;
            }

            try
            {
                wpnRows = ReadLatestWpnRows(180);
            }
            catch (Exception ex)
            {
                wpnError = ex.Message;
            }

            _events.Clear();
            foreach (var item in eventRows)
            {
                _events.Add(item);
            }

            _wpnRows.Clear();
            foreach (var item in wpnRows)
            {
                _wpnRows.Add(item);
            }

            StatusTextBlock.Text = $"EventViewer: {eventRows.Count} | WPN DB: {wpnRows.Count} | {DateTime.Now:HH:mm:ss}";

            if (!string.IsNullOrWhiteSpace(eventError) || !string.IsNullOrWhiteSpace(wpnError))
            {
                var errors = new[]
                {
                    string.IsNullOrWhiteSpace(eventError) ? null : "EventViewer error: " + eventError,
                    string.IsNullOrWhiteSpace(wpnError) ? null : "WPN DB error: " + wpnError
                }.Where(text => !string.IsNullOrWhiteSpace(text));

                StatusTextBlock.Text += " | " + string.Join("; ", errors);
            }
        }

        private static List<DiagnosticsEventItem> ReadLatestEvents(int maxCount)
        {
            var query = new EventLogQuery(NotificationsLogName, PathType.LogName)
            {
                ReverseDirection = true
            };

            var results = new List<DiagnosticsEventItem>(maxCount);
            using (var reader = new EventLogReader(query))
            {
                for (var i = 0; i < maxCount; i++)
                {
                    using (var record = reader.ReadEvent())
                    {
                        if (record == null)
                        {
                            break;
                        }

                        var message = string.Empty;
                        try
                        {
                            message = record.FormatDescription() ?? string.Empty;
                        }
                        catch
                        {
                            // Some records cannot be formatted depending on provider metadata availability.
                        }

                        var xml = string.Empty;
                        try
                        {
                            xml = record.ToXml() ?? string.Empty;
                        }
                        catch
                        {
                            // Best effort.
                        }

                        var summary = string.IsNullOrWhiteSpace(message) ? xml : message;
                        summary = NormalizeSummary(summary, 280);

                        results.Add(new DiagnosticsEventItem
                        {
                            TimeLocal = record.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                            EventId = record.Id,
                            ProviderName = record.ProviderName ?? string.Empty,
                            Summary = summary
                        });
                    }
                }
            }

            return results;
        }

        private static List<WpnNotificationItem> ReadLatestWpnRows(int maxCount)
        {
            var results = new List<WpnNotificationItem>();
            if (!File.Exists(WpnDbPath))
            {
                return results;
            }

            var tmpPath = Path.Combine(
                Path.GetTempPath(),
                string.Concat("WinNotifyBridge.Tray.wpn.", Process.GetCurrentProcess().Id, ".", Guid.NewGuid().ToString("N"), ".db"));

            File.Copy(WpnDbPath, tmpPath, true);
            try
            {
                var connStr = new SQLiteConnectionStringBuilder
                {
                    DataSource = tmpPath,
                    ReadOnly = true,
                    FailIfMissing = true,
                    Pooling = false
                }.ToString();

                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();

                    const string sql = @"
                        SELECT n.[Id], n.[ArrivalTime], n.[Type], n.[Payload], h.[PrimaryId],
                               (SELECT ha.[AssetValue] FROM [HandlerAssets] ha
                                WHERE ha.[HandlerId] = h.[RecordId] AND ha.[AssetKey] = 'DisplayName'
                                LIMIT 1) AS DisplayName
                        FROM [Notification] n
                        LEFT JOIN [NotificationHandler] h ON h.[RecordId] = n.[HandlerId]
                        ORDER BY n.[ArrivalTime] DESC
                        LIMIT @maxRows";

                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@maxRows", maxCount);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var id = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                                var arrivalRaw = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                                var notificationType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                                var payloadBytes = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3);
                                var appModelId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                                var displayName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);

                                var app = string.IsNullOrWhiteSpace(displayName) ? ResolveAppName(appModelId) : displayName;
                                var texts = ExtractTexts(payloadBytes);
                                var title = texts.Item1;
                                var body = texts.Item2;
                                var rawPreview = GetPayloadPreview(payloadBytes);

                                results.Add(new WpnNotificationItem
                                {
                                    Id = id,
                                    ArrivalLocal = FormatArrival(arrivalRaw),
                                    NotificationType = notificationType,
                                    AppModelId = appModelId,
                                    DisplayName = displayName,
                                    App = app,
                                    Title = title,
                                    Body = body,
                                    RawPreview = rawPreview
                                });
                            }
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    File.Delete(tmpPath);
                }
                catch
                {
                    // best effort
                }
            }

            return results;
        }

        private static string FormatArrival(long arrivalRaw)
        {
            try
            {
                if (arrivalRaw <= 0)
                {
                    return string.Empty;
                }

                var fileTimeTicks = arrivalRaw;
                var minFileTime = DateTime.FromFileTimeUtc(0).Ticks;
                if (fileTimeTicks < minFileTime)
                {
                    fileTimeTicks = arrivalRaw * 10000000L;
                }

                return DateTime.FromFileTimeUtc(fileTimeTicks).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return arrivalRaw.ToString();
            }
        }

        private static string ResolveAppName(string appModelId)
        {
            if (string.IsNullOrWhiteSpace(appModelId))
            {
                return string.Empty;
            }

            var name = appModelId;
            var excl = name.IndexOf('!');
            if (excl >= 0)
            {
                name = name.Substring(0, excl);
            }

            var underscore = name.LastIndexOf('_');
            if (underscore > 0)
            {
                name = name.Substring(0, underscore);
            }

            return name;
        }

        private static Tuple<string, string> ExtractTexts(byte[] payloadBytes)
        {
            if (payloadBytes == null || payloadBytes.Length == 0)
            {
                return Tuple.Create(string.Empty, string.Empty);
            }

            try
            {
                var xml = Encoding.UTF8.GetString(payloadBytes).TrimStart('\0', '\uFEFF');
                if (!xml.TrimStart().StartsWith("<", StringComparison.Ordinal))
                {
                    xml = Encoding.Unicode.GetString(payloadBytes).TrimStart('\0', '\uFEFF');
                }

                var doc = XDocument.Parse(xml);
                var texts = doc.Descendants()
                    .Where(e => string.Equals(e.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.Value == null ? string.Empty : t.Value.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToArray();

                if (texts.Length == 0)
                {
                    return Tuple.Create(string.Empty, string.Empty);
                }

                return Tuple.Create(texts[0], texts.Length > 1 ? string.Join(" ", texts.Skip(1)) : string.Empty);
            }
            catch
            {
                return Tuple.Create(string.Empty, string.Empty);
            }
        }

        private static string GetPayloadPreview(byte[] payloadBytes)
        {
            if (payloadBytes == null || payloadBytes.Length == 0)
            {
                return string.Empty;
            }

            var utf8 = Encoding.UTF8.GetString(payloadBytes).TrimStart('\0', '\uFEFF');
            if (string.IsNullOrWhiteSpace(utf8))
            {
                utf8 = Encoding.Unicode.GetString(payloadBytes).TrimStart('\0', '\uFEFF');
            }

            return NormalizeSummary(utf8, 320);
        }

        private static string NormalizeSummary(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Trim();

            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized.Substring(0, maxLength) + "...";
        }

        private sealed class DiagnosticsEventItem
        {
            public string TimeLocal { get; set; }

            public int EventId { get; set; }

            public string ProviderName { get; set; }

            public string Summary { get; set; }
        }

        private sealed class WpnNotificationItem
        {
            public long Id { get; set; }

            public string ArrivalLocal { get; set; }

            public string NotificationType { get; set; }

            public string AppModelId { get; set; }

            public string DisplayName { get; set; }

            public string App { get; set; }

            public string Title { get; set; }

            public string Body { get; set; }

            public string RawPreview { get; set; }
        }
    }
}
