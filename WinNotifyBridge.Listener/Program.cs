using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace WinNotifyBridge.Listener
{
    internal static class Program
    {
        private const string DiagnosticsVariableName = "WNB_LISTENER_DIAGNOSTICS";

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly TimeSpan SeenKeyExpiry = TimeSpan.FromHours(12);
        private static readonly string SeenKeysFilePath = Path.Combine(Path.GetTempPath(), "WinNotifyBridge.Listener.seen.txt");
        private static readonly string ListenerLogPath = Path.Combine(Path.GetTempPath(), "WinNotifyBridge.Listener.log");
        private static readonly Dictionary<string, DateTime> SeenNotificationKeys = LoadSeenKeys();
        private static readonly string WpnDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Notifications\wpndatabase.db");
        private static readonly bool DiagnosticsEnabled = ResolveDiagnosticsEnabled();

        private static Dictionary<string, DateTime> LoadSeenKeys()
        {
            var keys = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var cutoff = DateTime.UtcNow - SeenKeyExpiry;
            try
            {
                if (!File.Exists(SeenKeysFilePath))
                {
                    return keys;
                }

                foreach (var line in File.ReadAllLines(SeenKeysFilePath))
                {
                    var sep = line.IndexOf('\t');
                    if (sep < 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, sep);
                    if (long.TryParse(line.Substring(sep + 1), out var ticks))
                    {
                        var ts = new DateTime(ticks, DateTimeKind.Utc);
                        if (ts > cutoff)
                        {
                            keys[key] = ts;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Start fresh if the file is unreadable.
            }

            return keys;
        }

        private static void PersistSeenKeys()
        {
            try
            {
                var cutoff = DateTime.UtcNow - SeenKeyExpiry;
                var lines = SeenNotificationKeys
                    .Where(e => e.Value > cutoff)
                    .Select(e => $"{e.Key}\t{e.Value.Ticks}")
                    .ToArray();
                File.WriteAllLines(SeenKeysFilePath, lines);
            }
            catch (IOException)
            {
                // Best-effort.
            }
        }

        private static bool TryMarkSeen(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (SeenNotificationKeys.ContainsKey(key))
            {
                return false;
            }

            SeenNotificationKeys[key] = DateTime.UtcNow;
            return true;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (!File.Exists(WpnDbPath))
            {
                Log($"Notification database not found: {WpnDbPath}");
                return 1;
            }

            var endpoint = ReadAppSetting("BridgeEndpoint", "http://127.0.0.1:45877/notify/");
            var pollIntervalSeconds = ReadPollIntervalSeconds();

            Log("WinNotifyBridge.Listener started.");
            Log("Listener executable: " + Process.GetCurrentProcess().MainModule.FileName);
            Log($"Diagnostics enabled: {DiagnosticsEnabled} (set machine env {DiagnosticsVariableName}=true)");
            var running = true;
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };

            while (running)
            {
                try
                {
                    var notifications = ReadRecentNotifications(Math.Max(pollIntervalSeconds * 6, 120));
                    var freshNotifications = notifications.Where(notification => !SeenNotificationKeys.ContainsKey(notification.Key)).ToList();

                    if (freshNotifications.Count > 0)
                    {
                        Log($"Found {freshNotifications.Count} new notification(s)");
                    }

                    foreach (var notification in freshNotifications)
                    {
                        if (!TryMarkSeen(notification.Key))
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(notification.Title) && string.IsNullOrWhiteSpace(notification.Body))
                        {
                            continue;
                        }

                        Log($"Forwarding: App='{notification.App}' Title='{notification.Title}' Body='{notification.Body}'");
                        ForwardSync(endpoint, notification.App, notification.Title, notification.Body);
                    }

                    PersistSeenKeys();
                }
                catch (Exception ex)
                {
                    Log($"Listener error: {ex.Message} {ex.StackTrace}");
                }

                Thread.Sleep(TimeSpan.FromSeconds(pollIntervalSeconds));
            }

            return 0;
        }

        private static IReadOnlyList<NotificationRecord> ReadRecentNotifications(int windowSeconds)
        {
            var results = new List<NotificationRecord>();
            var diagnosticsLogCount = 0;
            const int maxDiagnosticsLogsPerPoll = 25;
            var tmpPath = Path.Combine(
                Path.GetTempPath(),
                string.Concat("WinNotifyBridge.wpn.", Process.GetCurrentProcess().Id, ".", Guid.NewGuid().ToString("N"), ".db"));

            try
            {
                File.Copy(WpnDbPath, tmpPath, overwrite: true);
            }
            catch (IOException ex)
            {
                Log($"DB copy failed: {ex.Message}");
                return results;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log($"DB copy access denied: {ex.Message}");
                return results;
            }

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

                    // ArrivalTime is Windows FILETIME (100-ns ticks since 1601-01-01), stored as seconds.
                    var cutoffFiletime = (long)(DateTime.UtcNow.AddSeconds(-windowSeconds)
                        - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds * 10000000L;

                    const string sql = @"
                        SELECT n.[Id], n.[Type], n.[Payload], n.[PayloadType], h.[PrimaryId],
                               (SELECT ha.[AssetValue] FROM [HandlerAssets] ha
                                WHERE ha.[HandlerId] = h.[RecordId] AND ha.[AssetKey] = 'DisplayName'
                                LIMIT 1) AS DisplayName
                        FROM [Notification] n
                        LEFT JOIN [NotificationHandler] h ON h.[RecordId] = n.[HandlerId]
                        WHERE n.[ArrivalTime] >= @cutoff
                        ORDER BY n.[ArrivalTime] ASC";

                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@cutoff", cutoffFiletime);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var id = reader.GetInt64(0);
                                var notificationType = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                                var payloadBytes = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2);
                                var appModelId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                                var displayName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                                var app = string.IsNullOrWhiteSpace(displayName) ? ResolveAppName(appModelId) : displayName;
                                var (title, body) = ExtractTexts(payloadBytes);
                                var rawPreview = GetPayloadPreview(payloadBytes);
                                var isTeamsRelated = IsTeamsRelated(appModelId, displayName, app, title, body);

                                if (DiagnosticsEnabled && diagnosticsLogCount < maxDiagnosticsLogsPerPoll)
                                {
                                    var normalizedType = string.IsNullOrWhiteSpace(notificationType)
                                        ? string.Empty
                                        : notificationType.Trim().ToLowerInvariant();
                                    var likelyTeamsApp = ContainsAny(appModelId, "teams", "msteams", "microsoftteams")
                                                        || ContainsAny(displayName, "teams", "msteams", "microsoft teams")
                                                        || ContainsAny(app, "teams", "msteams", "microsoft teams");

                                    if (normalizedType == "toast" || likelyTeamsApp)
                                    {
                                        Log($"Diag seen: Id='{id}' Type='{notificationType}' AppModelId='{appModelId}' DisplayName='{displayName}' App='{app}' Title='{title}' Body='{body}' Raw='{rawPreview}'");
                                        diagnosticsLogCount++;
                                    }
                                }

                                if (IsPhoneLink(appModelId, displayName, app))
                                {
                                    continue;
                                }

                                if (DiagnosticsEnabled && isTeamsRelated)
                                {
                                    Log($"Teams candidate: Type='{notificationType}' AppModelId='{appModelId}' DisplayName='{displayName}' App='{app}' Title='{title}' Body='{body}' Raw='{rawPreview}'");
                                }

                                if (!ShouldForwardNotification(notificationType, appModelId, app, title, body))
                                {
                                    if (DiagnosticsEnabled && isTeamsRelated)
                                    {
                                        Log($"Teams skipped: Type='{notificationType}' AppModelId='{appModelId}' App='{app}' Title='{title}' Body='{body}' Raw='{rawPreview}'");
                                    }

                                    continue;
                                }

                                results.Add(new NotificationRecord
                                {
                                    Key = $"{appModelId}:{notificationType}:{id}",
                                    Type = notificationType,
                                    App = app,
                                    Title = title,
                                    Body = body
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DB error (tmp='{tmpPath}'): {ex.Message} {ex.StackTrace}");
            }
            finally
            {
                try
                {
                    File.Delete(tmpPath);
                }
                catch (Exception ex)
                {
                    Log($"Temp delete failed (tmp='{tmpPath}'): {ex.Message}");
                }
            }

            return results;
        }

        private static string ResolveAppName(string appModelId)
        {
            if (string.IsNullOrWhiteSpace(appModelId))
            {
                return string.Empty;
            }

            var lowered = appModelId.ToLowerInvariant();
            if (lowered.Contains("microsoft.teams") || lowered.Contains("teams"))
            {
                return "Microsoft Teams";
            }

            if (lowered.Contains("calendar") || lowered.Contains("outlook"))
            {
                return "Calendar";
            }

            if (lowered.Contains("yourphone") || lowered.Contains("phone"))
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

        private static bool IsTeamsRelated(string appModelId, string displayName, string appName, string title, string body)
        {
            return ContainsAny(appModelId, "teams", "microsoft.teams", "outlook", "calendar")
                || ContainsAny(displayName, "teams", "outlook", "calendar")
                || ContainsAny(appName, "teams", "outlook", "calendar")
                || ContainsAny(title, "teams", "meeting", "calendar")
                || ContainsAny(body, "teams", "meeting", "calendar");
        }

        private static bool IsPhoneLink(string appModelId, string displayName, string appName)
        {
            return ContainsAny(appModelId, "yourphone", "phone")
                || ContainsAny(displayName, "phone link", "your phone")
                || ContainsAny(appName, "phone link", "your phone");
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (string.IsNullOrWhiteSpace(value) || terms == null || terms.Length == 0)
            {
                return false;
            }

            var lowered = value.ToLowerInvariant();
            foreach (var term in terms)
            {
                if (!string.IsNullOrWhiteSpace(term) && lowered.Contains(term.ToLowerInvariant()))
                {
                    return true;
                }
            }

            return false;
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

            if (string.IsNullOrWhiteSpace(utf8))
            {
                return string.Empty;
            }

            utf8 = utf8.Replace("\r", " ").Replace("\n", " ").Trim();
            return utf8.Length <= 500 ? utf8 : utf8.Substring(0, 500);
        }

        private static bool ShouldForwardNotification(string notificationType, string appModelId, string appName, string title, string body)
        {
            var hasText = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body);
            if (!hasText)
            {
                return false;
            }

            var normalizedType = string.IsNullOrWhiteSpace(notificationType) ? string.Empty : notificationType.Trim().ToLowerInvariant();
            var normalizedApp = string.IsNullOrWhiteSpace(appName) ? string.Empty : appName.Trim().ToLowerInvariant();
            var normalizedModel = string.IsNullOrWhiteSpace(appModelId) ? string.Empty : appModelId.Trim().ToLowerInvariant();

            if (normalizedType == "toast")
            {
                return true;
            }

            if (IsPhoneLink(appModelId, appName, appName))
            {
                return false;
            }

            if (normalizedType == "tile" || normalizedType == "badge")
            {
                return IsTeamsRelated(normalizedModel, normalizedApp, normalizedApp, title, body);
            }

            return IsTeamsRelated(normalizedModel, normalizedApp, normalizedApp, title, body);
        }

        private static (string Title, string Body) ExtractTexts(byte[] payloadBytes)
        {
            if (payloadBytes == null || payloadBytes.Length == 0)
            {
                return (string.Empty, string.Empty);
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

                if (texts.Length > 0)
                {
                    return (texts[0], texts.Length > 1 ? string.Join(" ", texts.Skip(1)) : string.Empty);
                }

                return TryExtractTextFromJson(xml);
            }
            catch (Exception)
            {
                var utf8 = Encoding.UTF8.GetString(payloadBytes).Trim('\0', '\uFEFF', ' ');
                if (!string.IsNullOrWhiteSpace(utf8))
                {
                    return TryExtractTextFromJson(utf8);
                }

                var unicode = Encoding.Unicode.GetString(payloadBytes).Trim('\0', '\uFEFF', ' ');
                return TryExtractTextFromJson(unicode);
            }
        }

        private static (string Title, string Body) TryExtractTextFromJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (string.Empty, string.Empty);
            }

            var title = ExtractJsonField(raw, "title") ?? ExtractJsonField(raw, "subject");
            var body = ExtractJsonField(raw, "body") ?? ExtractJsonField(raw, "message") ?? ExtractJsonField(raw, "content");
            return (title ?? string.Empty, body ?? string.Empty);
        }

        private static string ExtractJsonField(string raw, string fieldName)
        {
            var marker = "\"" + fieldName + "\"";
            var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }

            var colon = raw.IndexOf(':', idx + marker.Length);
            if (colon < 0)
            {
                return null;
            }

            var firstQuote = raw.IndexOf('"', colon + 1);
            if (firstQuote < 0)
            {
                return null;
            }

            var valueStart = firstQuote + 1;
            var valueEnd = valueStart;
            while (valueEnd < raw.Length)
            {
                if (raw[valueEnd] == '"' && raw[valueEnd - 1] != '\\')
                {
                    break;
                }

                valueEnd++;
            }

            if (valueEnd <= valueStart || valueEnd >= raw.Length)
            {
                return null;
            }

            var value = raw.Substring(valueStart, valueEnd - valueStart)
                .Replace("\\\\", "\\")
                .Replace("\\\"", "\"")
                .Replace("\\n", " ")
                .Replace("\\r", " ")
                .Trim();

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static void ForwardSync(string endpoint, string app, string title, string body)
        {
            try
            {
                using (var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["app"] = app ?? string.Empty,
                    ["title"] = title ?? string.Empty,
                    ["body"] = body ?? string.Empty
                }))
                {
                    HttpClient.PostAsync(endpoint, content).GetAwaiter().GetResult()
                        .EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                Log($"Forward error: {ex.Message} {ex.StackTrace}");
            }
        }

        private static void Log(string message)
        {
            try
            {
                var line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z\t" + message + Environment.NewLine;
                File.AppendAllText(ListenerLogPath, line);
            }
            catch
            {
                // best-effort
            }
        }

        private static string ReadAppSetting(string key, string fallback)
        {
            var value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static int ReadPollIntervalSeconds()
        {
            var value = ConfigurationManager.AppSettings["PollIntervalSeconds"];
            return int.TryParse(value, out var interval) && interval > 0 ? interval : 5;
        }

        private static bool ResolveDiagnosticsEnabled()
        {
#if !DEBUG
            return false;
#else
            var configuredValue = Environment.GetEnvironmentVariable(DiagnosticsVariableName, EnvironmentVariableTarget.Machine);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                configuredValue = Environment.GetEnvironmentVariable(DiagnosticsVariableName, EnvironmentVariableTarget.Process);
            }

            return bool.TryParse(configuredValue, out var enabled) && enabled;
#endif
        }

        private sealed class NotificationRecord
        {
            public string Key { get; set; }
            public string Type { get; set; }
            public string App { get; set; }
            public string Title { get; set; }
            public string Body { get; set; }
        }
    }
}
