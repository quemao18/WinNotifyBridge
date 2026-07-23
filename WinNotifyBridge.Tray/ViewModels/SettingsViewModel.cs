using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Input;
using WinNotifyBridge.Tray.Infrastructure;

namespace WinNotifyBridge.Tray.ViewModels
{
    internal sealed class SettingsViewModel : INotifyPropertyChanged
    {
        private static readonly string[] ServiceNameCandidates = { "WinNotifyBridge", "Service1" };
        private const string BotTokenVariableName = "WNB_TELEGRAM_BOT_TOKEN";
        private const string ChatIdVariableName = "WNB_TELEGRAM_CHAT_ID";
        private const string BridgePrefixVariableName = "WNB_BRIDGE_PREFIX";
        private const string EnableEventLogWatcherVariableName = "WNB_ENABLE_EVENTLOG_WATCHER";
        private const string AllowedAppsVariableName = "WNB_ALLOWED_APPS";
        private const string KeepSystemAwakeVariableName = "WNB_KEEP_SYSTEM_AWAKE";

        private string _botToken;
        private string _chatId;
        private string _bridgePrefix;
        private string _allowedApps;
        private bool _enableEventLogWatcher;
        private bool _keepSystemAwake;

        public SettingsViewModel()
        {
            SaveCommand = new RelayCommand(() => SaveSettings(restartService: false));
            SaveAndRestartCommand = new RelayCommand(() => SaveSettings(restartService: true));
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke());

            LoadCurrentValues();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public Action RequestClose { get; set; }

        public ICommand SaveCommand { get; }

        public ICommand SaveAndRestartCommand { get; }

        public ICommand CancelCommand { get; }

        public string BotToken
        {
            get => _botToken;
            set => SetField(ref _botToken, value);
        }

        public string ChatId
        {
            get => _chatId;
            set => SetField(ref _chatId, value);
        }

        public string BridgePrefix
        {
            get => _bridgePrefix;
            set => SetField(ref _bridgePrefix, value);
        }

        public string AllowedApps
        {
            get => _allowedApps;
            set => SetField(ref _allowedApps, value);
        }

        public bool EnableEventLogWatcher
        {
            get => _enableEventLogWatcher;
            set => SetField(ref _enableEventLogWatcher, value);
        }

        public bool KeepSystemAwake
        {
            get => _keepSystemAwake;
            set => SetField(ref _keepSystemAwake, value);
        }

        private void LoadCurrentValues()
        {
            BotToken = ReadMachineVariable(BotTokenVariableName);
            ChatId = ReadMachineVariable(ChatIdVariableName);
            BridgePrefix = ReadMachineVariable(BridgePrefixVariableName);
            AllowedApps = ReadMachineVariable(AllowedAppsVariableName)
                .Replace(",", Environment.NewLine)
                .Replace(";", Environment.NewLine);

            var watcherValue = ReadMachineVariable(EnableEventLogWatcherVariableName);
            EnableEventLogWatcher = string.IsNullOrWhiteSpace(watcherValue) ||
                                    (bool.TryParse(watcherValue, out var enabled) && enabled);

            var keepAwakeValue = ReadMachineVariable(KeepSystemAwakeVariableName);
            KeepSystemAwake = bool.TryParse(keepAwakeValue, out var keepAwake) && keepAwake;
        }

        private void SaveSettings(bool restartService)
        {
            try
            {
                WriteMachineVariable(BotTokenVariableName, BotToken);
                WriteMachineVariable(ChatIdVariableName, ChatId);
                WriteMachineVariable(BridgePrefixVariableName, BridgePrefix);
                WriteMachineVariable(AllowedAppsVariableName, AllowedApps);
                WriteMachineVariable(EnableEventLogWatcherVariableName, EnableEventLogWatcher ? "true" : "false");
                WriteMachineVariable(KeepSystemAwakeVariableName, KeepSystemAwake ? "true" : "false");

                if (restartService)
                {
                    RestartService();
                }

                MessageBox.Show("Settings saved.", "WinNotifyBridge", MessageBoxButton.OK, MessageBoxImage.Information);
                RequestClose?.Invoke();
            }
            catch (UnauthorizedAccessException)
            {
                ShowAdminRequiredMessage();
            }
            catch (SecurityException)
            {
                ShowAdminRequiredMessage();
            }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf("registry access is not allowed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ex.Message.IndexOf("cannot open", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ShowAdminRequiredMessage();
                    return;
                }

                MessageBox.Show(ex.Message, "WinNotifyBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void ShowAdminRequiredMessage()
        {
            MessageBox.Show(
                "Saving settings requires administrator permissions because values are stored at machine level for the Windows service.\n\nPlease run WinNotifyBridge.Tray as Administrator and try again.",
                "WinNotifyBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static string ReadMachineVariable(string variableName)
        {
            return Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine) ?? string.Empty;
        }

        private static void WriteMachineVariable(string variableName, string value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Environment.SetEnvironmentVariable(variableName, normalized, EnvironmentVariableTarget.Machine);
        }

        private static void RestartService()
        {
            using (var controller = CreateServiceController())
            {
                controller.Refresh();
                if (controller.Status != ServiceControllerStatus.Stopped && controller.Status != ServiceControllerStatus.StopPending)
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }

                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }

        private static string ResolveServiceName()
        {
            var installedServiceNames = ServiceController.GetServices().Select(service => service.ServiceName).ToArray();
            return ServiceNameCandidates.FirstOrDefault(candidate =>
                installedServiceNames.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)));
        }

        private static ServiceController CreateServiceController()
        {
            var serviceName = ResolveServiceName();
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new InvalidOperationException("WinNotifyBridge service is not installed.");
            }

            return new ServiceController(serviceName);
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SetField<T>(ref T backingField, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(backingField, value))
            {
                return;
            }

            backingField = value;
            OnPropertyChanged(propertyName);
        }
    }
}
