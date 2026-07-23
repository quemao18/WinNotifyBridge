using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace WinNotifyBridge
{
    public partial class Service1 : ServiceBase
    {
        private const string BotTokenVariableName = "WNB_TELEGRAM_BOT_TOKEN";
        private const string ChatIdVariableName = "WNB_TELEGRAM_CHAT_ID";
        private const string BridgePrefixVariableName = "WNB_BRIDGE_PREFIX";
        private const string EnableEventLogWatcherVariableName = "WNB_ENABLE_EVENTLOG_WATCHER";
        private const string AllowedAppsVariableName = "WNB_ALLOWED_APPS";
        private const string KeepSystemAwakeVariableName = "WNB_KEEP_SYSTEM_AWAKE";
        private const string NotificationsLogName = "Microsoft-Windows-PushNotification-Platform/Operational";
        private const string DefaultBridgePrefix = "http://127.0.0.1:45877/notify/";

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly HashSet<int> AllowedNotificationEventIds = new HashSet<int> { 2416 };
        private static readonly TimeSpan DuplicateSuppressionWindow = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, DateTime> _recentNotificationMessages = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _recentNotificationsLock = new object();

        private readonly string _botToken;
        private readonly string _chatId;
        private readonly string _bridgePrefix;
        private readonly bool _enableEventLogWatcher;
        private readonly bool _keepSystemAwake;
        private readonly HashSet<string> _allowedApps;

        private EventLogWatcher _notificationsWatcher;
        private HttpListener _bridgeListener;
        private CancellationTokenSource _bridgeCancellation;
        private Task _bridgeTask;

        public Service1()
        {
            InitializeComponent();
            _botToken = Environment.GetEnvironmentVariable(BotTokenVariableName, EnvironmentVariableTarget.Machine);
            _chatId = Environment.GetEnvironmentVariable(ChatIdVariableName, EnvironmentVariableTarget.Machine);
            _bridgePrefix = ResolveBridgePrefix();
            _enableEventLogWatcher = ResolveEnableEventLogWatcher();
            _keepSystemAwake = ResolveKeepSystemAwake();
            _allowedApps = ResolveAllowedApps();
        }

        // Allow running the service logic in a console for debugging.
        public void RunAsConsole()
        {
            OnStart(null);
            Console.WriteLine("Service running in console mode. Press Enter to stop.");
            Console.ReadLine();
            OnStop();
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                StartBridgeListener();
            }
            catch (Exception ex)
            {
                LogError(ex, "Error starting local bridge listener.");
            }

            if (_enableEventLogWatcher)
            {
                try
                {
                    StartNotificationsWatcher();
                }
                catch (Exception ex)
                {
                    LogError(ex, "Error starting Windows notifications watcher.");
                }
            }

            if (_keepSystemAwake)
            {
                try
                {
                    ApplyKeepAwakeRequest();
                }
                catch (Exception ex)
                {
                    LogError(ex, "Error enabling keep-awake mode.");
                }
            }

            ObserveTask(
                SendToTelegramAsync($"{Environment.MachineName}: WinNotifyBridge started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
                "Error sending start notification.");
        }

        protected override void OnStop()
        {
            StopBridgeListener();
            StopNotificationsWatcher();

            if (_keepSystemAwake)
            {
                try
                {
                    ClearKeepAwakeRequest();
                }
                catch (Exception ex)
                {
                    LogError(ex, "Error clearing keep-awake mode.");
                }
            }

            try
            {
                SendToTelegramAsync($"{Environment.MachineName}: WinNotifyBridge stopped at {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                LogError(ex, "Error sending stop notification.");
            }
        }

        public Task ForwardNotificationAsync(string app, string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            var appName = string.IsNullOrWhiteSpace(app) ? "Windows" : app.Trim();
            if (!IsAllowedApp(appName))
            {
                return Task.CompletedTask;
            }

            var messageText = string.IsNullOrWhiteSpace(text) ? "(empty notification)" : text.Trim();
            return SendToTelegramAsync($"[{appName}] {messageText}", cancellationToken);
        }

        private async Task SendToTelegramAsync(string message, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId) || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var endpoint = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            using (var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = _chatId,
                ["text"] = message
            }))
            using (var response = await HttpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
            }
        }

        private void StartBridgeListener()
        {
            _bridgeCancellation = new CancellationTokenSource();
            _bridgeListener = new HttpListener();
            _bridgeListener.Prefixes.Add(_bridgePrefix);
            _bridgeListener.Start();
            _bridgeTask = Task.Run(() => RunBridgeListenerAsync(_bridgeCancellation.Token));
        }

        private void StopBridgeListener()
        {
            if (_bridgeListener == null)
            {
                return;
            }

            _bridgeCancellation.Cancel();
            _bridgeListener.Stop();
            _bridgeListener.Close();

            try
            {
                _bridgeTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                LogError(ex, "Error stopping local bridge listener.");
            }

            _bridgeTask = null;
            _bridgeCancellation.Dispose();
            _bridgeCancellation = null;
            _bridgeListener = null;
        }

        private async Task RunBridgeListenerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await _bridgeListener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    throw;
                }

                try
                {
                    await ProcessBridgeRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
                    await WriteBridgeResponseAsync(context.Response, HttpStatusCode.Accepted).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogError(ex, "Error processing bridge request.");
                    await WriteBridgeResponseAsync(context.Response, HttpStatusCode.InternalServerError).ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessBridgeRequestAsync(HttpListenerRequest request, CancellationToken cancellationToken)
        {
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string payload;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                payload = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var fields = ParseFormPayload(payload);
            fields.TryGetValue("app", out var app);
            fields.TryGetValue("title", out var title);
            fields.TryGetValue("body", out var body);

            string message;
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
            {
                message = null;
            }
            else if (string.IsNullOrWhiteSpace(body))
            {
                message = title;
            }
            else if (string.IsNullOrWhiteSpace(title))
            {
                message = body;
            }
            else
            {
                message = $"{title}{Environment.NewLine}{body}";
            }

            await ForwardNotificationAsync(app, message, cancellationToken).ConfigureAwait(false);
            try
            {
                EventLog.WriteEntry($"Bridge POST received: app='{app ?? ""}' title='{(title ?? string.Empty).Replace('\n', ' ')}' bodyLength={(body?.Length ?? 0)}", EventLogEntryType.Information);
            }
            catch
            {
                // Best-effort logging
            }
        }

        private static Dictionary<string, string> ParseFormPayload(string payload)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return values;
            }

            var pairs = payload.Split('&');
            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair))
                {
                    continue;
                }

                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair.Substring(0, separator);
                var value = separator < 0 ? string.Empty : pair.Substring(separator + 1);

                key = Uri.UnescapeDataString(key.Replace('+', ' '));
                value = Uri.UnescapeDataString(value.Replace('+', ' '));
                values[key] = value;
            }

            return values;
        }

        private static async Task WriteBridgeResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode)
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "text/plain";
            var buffer = Encoding.UTF8.GetBytes("ok");
            response.ContentLength64 = buffer.Length;
            using (response.OutputStream)
            {
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            }
        }

        private string ResolveBridgePrefix()
        {
            var configuredPrefix = Environment.GetEnvironmentVariable(BridgePrefixVariableName, EnvironmentVariableTarget.Machine);
            return string.IsNullOrWhiteSpace(configuredPrefix) ? DefaultBridgePrefix : configuredPrefix.Trim();
        }

        private bool ResolveEnableEventLogWatcher()
        {
            var configuredValue = Environment.GetEnvironmentVariable(EnableEventLogWatcherVariableName, EnvironmentVariableTarget.Machine);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                // Disabled by default: the push-notification platform event log (EventID 2416)
                // only carries AppUserModelId with no toast payload, producing only noise.
                // Enable explicitly by setting WNB_ENABLE_EVENTLOG_WATCHER=true.
                return false;
            }

            return bool.TryParse(configuredValue, out var enabled) && enabled;
        }

        private bool ResolveKeepSystemAwake()
        {
            var configuredValue = Environment.GetEnvironmentVariable(KeepSystemAwakeVariableName, EnvironmentVariableTarget.Machine);
            return bool.TryParse(configuredValue, out var enabled) && enabled;
        }

        private HashSet<string> ResolveAllowedApps()
        {
            var configuredValue = Environment.GetEnvironmentVariable(AllowedAppsVariableName, EnvironmentVariableTarget.Machine);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return null;
            }

            var values = configuredValue
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (values.Length == 0)
            {
                return null;
            }

            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }

        private bool IsAllowedApp(string appName)
        {
            if (_allowedApps == null || _allowedApps.Count == 0)
            {
                return true;
            }

            if (_allowedApps.Contains(appName))
            {
                return true;
            }

            return _allowedApps.Any(allowedApp =>
                appName.IndexOf(allowedApp, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void StartNotificationsWatcher()
        {
            var query = new EventLogQuery(NotificationsLogName, PathType.LogName);
            _notificationsWatcher = new EventLogWatcher(query);
            _notificationsWatcher.EventRecordWritten += OnNotificationEventRecordWritten;
            _notificationsWatcher.Enabled = true;
        }

        private void StopNotificationsWatcher()
        {
            if (_notificationsWatcher == null)
            {
                return;
            }

            _notificationsWatcher.EventRecordWritten -= OnNotificationEventRecordWritten;
            _notificationsWatcher.Enabled = false;
            _notificationsWatcher.Dispose();
            _notificationsWatcher = null;
        }

        private void OnNotificationEventRecordWritten(object sender, EventRecordWrittenEventArgs eventArgs)
        {
            if (eventArgs.EventException != null)
            {
                LogError(eventArgs.EventException, "Error reading Windows notification event.");
                return;
            }

            if (eventArgs.EventRecord == null)
            {
                return;
            }

            using (var eventRecord = eventArgs.EventRecord)
            {
                var source = string.IsNullOrWhiteSpace(eventRecord.ProviderName) ? "Windows.Notifications" : eventRecord.ProviderName;
                if (!TryBuildNotificationPayload(eventRecord, out var appName, out var message))
                {
                    return;
                }

                var effectiveAppName = string.IsNullOrWhiteSpace(appName) ? source : appName;
                if (IsAppOnlyMessage(message) || IsLowValuePlatformMessage(source, message))
                {
                    return;
                }

                if (ShouldSuppressDuplicate($"{effectiveAppName}|{NormalizeMessageForKey(message)}"))
                {
                    return;
                }

                ObserveTask(
                    ForwardNotificationAsync(effectiveAppName, message),
                    "Error forwarding Windows notification to Telegram.");
            }
        }

        private static bool TryBuildNotificationPayload(EventRecord eventRecord, out string appName, out string message)
        {
            appName = null;
            message = null;

            var description = TryGetDescription(eventRecord);
            var textLines = TryGetToastTextLines(eventRecord);
            var notificationType = ResolveNotificationType(eventRecord, description);

            if (!ShouldForward(eventRecord.Id, description, notificationType, textLines.Length > 0))
            {
                return false;
            }

            var appUserModelId = TryGetEventDataValue(eventRecord, "AppUserModelId")
                ?? ExtractBracketFieldValue(description, "AppUserModelId")
                ?? TryExtractAppUserModelIdFromDescription(description);
            appName = BuildFriendlyAppName(appUserModelId) ?? appUserModelId;

            var title = textLines.Length > 0 ? textLines[0] : null;
            var body = textLines.Length > 1 ? string.Join(" ", textLines.Skip(1)) : null;

            var normalizedAppName = string.IsNullOrWhiteSpace(appName) ? null : appName.Trim();
            var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            var normalizedBody = string.IsNullOrWhiteSpace(body) ? null : body.Trim();

            var hasMeaningfulTitle = !string.IsNullOrWhiteSpace(normalizedTitle) &&
                                     !string.Equals(normalizedTitle, normalizedAppName, StringComparison.OrdinalIgnoreCase);
            var hasMeaningfulBody = !string.IsNullOrWhiteSpace(normalizedBody) &&
                                    !string.Equals(normalizedBody, normalizedAppName, StringComparison.OrdinalIgnoreCase);

            if (!hasMeaningfulTitle && !hasMeaningfulBody)
            {
                return false;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(normalizedAppName))
            {
                parts.Add($"App: {normalizedAppName}");
            }

            if (hasMeaningfulTitle)
            {
                parts.Add($"Title: {normalizedTitle}");
            }

            if (hasMeaningfulBody)
            {
                parts.Add($"Message: {normalizedBody}");
            }

            var builtMessage = string.Join(Environment.NewLine, parts);
            if (string.IsNullOrWhiteSpace(builtMessage))
            {
                return false;
            }

            message = builtMessage.Length <= 3500 ? builtMessage : builtMessage.Substring(0, 3500);
            return true;
        }

        private static bool ShouldForward(int eventId, string description, string notificationType, bool hasTextContent)
        {
            if (string.Equals(notificationType, "badge", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (hasTextContent)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                return false;
            }

            var normalized = description.ToLowerInvariant();
            return AllowedNotificationEventIds.Contains(eventId) &&
                   normalized.Contains("toast notification") &&
                   normalized.Contains("notification was received");
        }

        private static string ResolveNotificationType(EventRecord eventRecord, string description)
        {
            var notificationType = TryGetEventDataValue(eventRecord, "NotificationType")
                ?? ExtractBracketFieldValue(description, "NotificationType");

            if (!string.IsNullOrWhiteSpace(notificationType))
            {
                return notificationType.Trim().ToLowerInvariant();
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            var normalized = description.ToLowerInvariant();
            if (normalized.Contains("badge"))
            {
                return "badge";
            }

            if (normalized.Contains("toast"))
            {
                return "toast";
            }

            return null;
        }

        private static bool IsAppOnlyMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            var lines = message
                .Replace("\r\n", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            return lines.Length == 1 && lines[0].StartsWith("App:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLowValuePlatformMessage(string source, string message)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            if (source.IndexOf("PushNotifications-Platform", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var normalizedMessage = message.ToLowerInvariant();
            return normalizedMessage.IndexOf("title:", StringComparison.Ordinal) < 0 &&
                   normalizedMessage.IndexOf("message:", StringComparison.Ordinal) < 0;
        }

        private static string NormalizeMessageForKey(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            return string.Join("|", message
                .Replace("\r\n", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line)))
                .ToLowerInvariant();
        }

        private bool ShouldSuppressDuplicate(string messageKey)
        {
            if (string.IsNullOrWhiteSpace(messageKey))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            lock (_recentNotificationsLock)
            {
                var expiredKeys = _recentNotificationMessages
                    .Where(entry => now - entry.Value > DuplicateSuppressionWindow)
                    .Select(entry => entry.Key)
                    .ToArray();

                foreach (var expiredKey in expiredKeys)
                {
                    _recentNotificationMessages.Remove(expiredKey);
                }

                if (_recentNotificationMessages.TryGetValue(messageKey, out var lastSeenUtc) && now - lastSeenUtc <= DuplicateSuppressionWindow)
                {
                    return true;
                }

                _recentNotificationMessages[messageKey] = now;
                return false;
            }
        }

        private static string TryGetDescription(EventRecord eventRecord)
        {
            try
            {
                return eventRecord.FormatDescription();
            }
            catch (EventLogException)
            {
                return null;
            }
        }

        private static string TryGetEventDataValue(EventRecord eventRecord, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            try
            {
                var xml = XDocument.Parse(eventRecord.ToXml());
                var node = xml
                    .Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "Data" &&
                        string.Equals((string)element.Attribute("Name"), fieldName, StringComparison.OrdinalIgnoreCase));

                var value = node?.Value;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string[] TryGetToastTextLines(EventRecord eventRecord)
        {
            try
            {
                var xml = XDocument.Parse(eventRecord.ToXml());
                var values = xml
                    .Descendants()
                    .Where(node => node.Name.LocalName == "Data")
                    .Select(node => node.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();

                foreach (var value in values)
                {
                    var lines = TryParseToastTextLines(value);
                    if (lines.Length > 0)
                    {
                        return lines;
                    }
                }

                return Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string[] TryParseToastTextLines(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<string>();
            }

            var decoded = WebUtility.HtmlDecode(payload).Trim();
            var toastStart = decoded.IndexOf("<toast", StringComparison.OrdinalIgnoreCase);
            if (toastStart < 0)
            {
                return Array.Empty<string>();
            }

            var toastPayload = decoded.Substring(toastStart);

            try
            {
                var payloadXml = XDocument.Parse(toastPayload);
                return payloadXml
                    .Descendants()
                    .Where(node => node.Name.LocalName == "text")
                    .Select(node => node.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string ExtractBracketFieldValue(string description, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            var marker = $"[{fieldName}]";
            var markerIndex = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= 0)
            {
                return null;
            }

            var valueEnd = markerIndex;
            var valueStart = description.LastIndexOf(' ', valueEnd - 1);
            if (valueStart < 0)
            {
                valueStart = 0;
            }

            var value = description.Substring(valueStart, valueEnd - valueStart).Trim(' ', ':', ',', '.');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string TryExtractAppUserModelIdFromDescription(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            var markers = new[]
            {
                "received from ",
                "submitted to threadpool: ",
                "is being delivered to "
            };

            foreach (var marker in markers)
            {
                var start = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    continue;
                }

                start += marker.Length;
                var end = description.IndexOf(' ', start);
                if (end < 0)
                {
                    end = description.Length;
                }

                var candidate = description.Substring(start, end - start).Trim(' ', ':', ',', '.', ';');
                if (IsLikelyAppUserModelId(candidate))
                {
                    return candidate;
                }
            }

            var tokens = description.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var candidate = token.Trim(' ', ':', ',', '.', ';', '(', ')', '[', ']');
                if (IsLikelyAppUserModelId(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsLikelyAppUserModelId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf('!') > 0 &&
                   value.IndexOf(' ') < 0;
        }

        private static string BuildFriendlyAppName(string appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return null;
            }

            var value = appUserModelId;
            var exclamationIndex = value.IndexOf('!');
            if (exclamationIndex >= 0)
            {
                value = value.Substring(0, exclamationIndex);
            }

            var underscoreIndex = value.IndexOf('_');
            if (underscoreIndex > 0)
            {
                value = value.Substring(0, underscoreIndex);
            }

            var dotIndex = value.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < value.Length - 1)
            {
                value = value.Substring(dotIndex + 1);
            }

            return value.Replace("Desktop", " Desktop").Trim();
        }

        private void ObserveTask(Task task, string context)
        {
            task.ContinueWith(
                t =>
                {
                    if (t.Exception != null)
                    {
                        LogError(t.Exception.GetBaseException(), context);
                    }
                },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private void ApplyKeepAwakeRequest()
        {
            var result = SetThreadExecutionState(
                ExecutionState.Continuous |
                ExecutionState.SystemRequired |
                ExecutionState.AwayModeRequired);
            if (result == 0)
            {
                throw new InvalidOperationException("Windows rejected the keep-awake request.");
            }
        }

        private void ClearKeepAwakeRequest()
        {
            var result = SetThreadExecutionState(ExecutionState.Continuous);
            if (result == 0)
            {
                throw new InvalidOperationException("Windows rejected the keep-awake reset request.");
            }
        }

        private void LogError(Exception exception, string context)
        {
            EventLog.WriteEntry($"{context} {exception.Message}", EventLogEntryType.Error);
        }

        [Flags]
        private enum ExecutionState : uint
        {
            SystemRequired = 0x00000001,
            AwayModeRequired = 0x00000040,
            Continuous = 0x80000000
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);
    }
}
