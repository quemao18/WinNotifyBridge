using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using System.ServiceProcess;
using System.Windows.Forms;

namespace WinNotifyBridge.Tray
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const string ListenerPathVariableName = "WNB_LISTENER_PATH";

        private static readonly string[] ServiceNameCandidates = { "WinNotifyBridge", "Service1" };

        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _startItem;
        private readonly ToolStripMenuItem _stopItem;
        private readonly ToolStripMenuItem _restartItem;
        private DiagnosticsWindow _diagnosticsWindow;
        private Process _listenerProcess;

        public TrayApplicationContext()
        {
            _statusItem = new ToolStripMenuItem("Status: Unknown") { Enabled = false };
            _startItem = new ToolStripMenuItem("Start service", null, (_, __) => ExecuteServiceAction(StartService));
            _stopItem = new ToolStripMenuItem("Stop service", null, (_, __) => ExecuteServiceAction(StopService));
            _restartItem = new ToolStripMenuItem("Restart service", null, (_, __) => ExecuteServiceAction(RestartService));
            var settingsItem = new ToolStripMenuItem("Settings...", null, (_, __) => OpenSettings());
            var diagnosticsItem = new ToolStripMenuItem("Diagnostics monitor...", null, (_, __) => OpenDiagnosticsMonitor());
            var exitItem = new ToolStripMenuItem("Exit", null, (_, __) => Exit());

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add(_statusItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(_startItem);
            contextMenu.Items.Add(_stopItem);
            contextMenu.Items.Add(_restartItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(settingsItem);
            contextMenu.Items.Add(diagnosticsItem);
            contextMenu.Items.Add(exitItem);
            contextMenu.Opening += (_, __) => RefreshServiceState();

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "WinNotifyBridge",
                Visible = true,
                ContextMenuStrip = contextMenu
            };

            _notifyIcon.DoubleClick += (_, __) => OpenSettings();
            RefreshServiceState();
            EnsureListenerRunningIfServiceIsUp();
        }

        private void EnsureListenerRunningIfServiceIsUp()
        {
            try
            {
                var serviceName = ResolveServiceName();
                if (string.IsNullOrWhiteSpace(serviceName))
                {
                    return;
                }

                using (var controller = new ServiceController(serviceName))
                {
                    if (controller.Status == ServiceControllerStatus.Running)
                    {
                        StartListenerProcess();
                    }
                }
            }
            catch (Exception)
            {
                // Non-critical: if we cannot query the service, skip auto-starting the listener.
            }
        }

        private void ExecuteServiceAction(Action action)
        {
            try
            {
                action();
                RefreshServiceState();
            }
            catch (InvalidOperationException ex) when (ex.Message.IndexOf("cannot open", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MessageBox.Show("Administrator permissions are required to control the Windows service. Run WinNotifyBridge.Tray as Administrator.", "WinNotifyBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "WinNotifyBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartService()
        {
            using (var controller = CreateServiceController())
            {
                controller.Refresh();
                if (controller.Status == ServiceControllerStatus.Running || controller.Status == ServiceControllerStatus.StartPending)
                {
                    return;
                }

                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }

            StartListenerProcess();
        }

        private void StopService()
        {
            using (var controller = CreateServiceController())
            {
                controller.Refresh();
                if (controller.Status == ServiceControllerStatus.Stopped || controller.Status == ServiceControllerStatus.StopPending)
                {
                    return;
                }

                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }

            StopListenerProcess();
        }

        private void RestartService()
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

            StopListenerProcess();
            StartListenerProcess();
        }

        private void OpenSettings()
        {
            var window = new SettingsWindow();
            window.ShowDialog();
            RefreshServiceState();
        }

        private void OpenDiagnosticsMonitor()
        {
            if (_diagnosticsWindow == null || !_diagnosticsWindow.IsLoaded)
            {
                _diagnosticsWindow = new DiagnosticsWindow();
                _diagnosticsWindow.Closed += (_, __) => _diagnosticsWindow = null;
                _diagnosticsWindow.Show();
                return;
            }

            if (_diagnosticsWindow.WindowState == System.Windows.WindowState.Minimized)
            {
                _diagnosticsWindow.WindowState = System.Windows.WindowState.Normal;
            }

            _diagnosticsWindow.Activate();
        }

        private void RefreshServiceState()
        {
            var serviceName = ResolveServiceName();
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                _statusItem.Text = "Status: Not installed";
                _startItem.Enabled = false;
                _stopItem.Enabled = false;
                _restartItem.Enabled = false;
                return;
            }

            try
            {
                using (var controller = new ServiceController(serviceName))
                {
                    controller.Refresh();
                    var status = controller.Status;
                    _statusItem.Text = $"Status: {status} ({serviceName})";
                    _startItem.Enabled = status == ServiceControllerStatus.Stopped;
                    _stopItem.Enabled = status == ServiceControllerStatus.Running;
                    _restartItem.Enabled = status == ServiceControllerStatus.Running || status == ServiceControllerStatus.Stopped;
                }
            }
            catch
            {
                _statusItem.Text = "Status: Not available";
                _startItem.Enabled = false;
                _stopItem.Enabled = false;
                _restartItem.Enabled = false;
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

        private void Exit()
        {
            StopListenerProcess();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            ExitThread();
        }

        private void StartListenerProcess()
        {
            var listenerPath = ResolveListenerPath();
            if (string.IsNullOrWhiteSpace(listenerPath) || !System.IO.File.Exists(listenerPath))
            {
                return;
            }

            var existing = Process.GetProcessesByName("WinNotifyBridge.Listener")
                .FirstOrDefault(process =>
                {
                    try
                    {
                        return string.Equals(process.MainModule?.FileName, listenerPath, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });

            if (existing != null)
            {
                _listenerProcess = existing;
                return;
            }

            StopListenerProcess();

            try
            {
                _listenerProcess = Process.Start(new ProcessStartInfo(listenerPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                try
                {
                    _listenerProcess = Process.Start(new ProcessStartInfo(listenerPath)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    return;
                }
                catch (Exception elevatedEx)
                {
                    MessageBox.Show($"Could not start WinNotifyBridge.Listener as administrator: {elevatedEx.Message}", "WinNotifyBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start WinNotifyBridge.Listener: {ex.Message}", "WinNotifyBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StopListenerProcess()
        {
            if (_listenerProcess == null)
            {
                return;
            }

            try
            {
                if (!_listenerProcess.HasExited)
                {
                    _listenerProcess.Kill();
                    _listenerProcess.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // Process may have already exited.
            }
            finally
            {
                _listenerProcess.Dispose();
                _listenerProcess = null;
            }
        }

        private static Icon LoadTrayIcon()
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null)
                {
                    return icon;
                }
            }
            catch (Exception)
            {
                // Fallback to system icon if extraction fails.
            }

            return SystemIcons.Information;
        }

        private static string ResolveListenerPath()
        {
            var configuredPath = Environment.GetEnvironmentVariable(ListenerPathVariableName, EnvironmentVariableTarget.Machine);
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = Environment.GetEnvironmentVariable(ListenerPathVariableName, EnvironmentVariableTarget.Process);
            }

            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = configuredPath.Trim();
                if (System.IO.Directory.Exists(configuredPath))
                {
                    var fromDir = System.IO.Path.Combine(configuredPath, "WinNotifyBridge.Listener.exe");
                    if (System.IO.File.Exists(fromDir))
                    {
                        return fromDir;
                    }
                }
                else if (System.IO.File.Exists(configuredPath))
                {
                    return configuredPath;
                }
            }

            var assemblyDir = System.IO.Path.GetDirectoryName(typeof(TrayApplicationContext).Assembly.Location);
            if (string.IsNullOrWhiteSpace(assemblyDir))
            {
                return null;
            }

            // When installed via MSI: Service is in \Service\, Tray in \Tray\, Listener in \Listener\
            var installRoot = System.IO.Path.GetDirectoryName(assemblyDir);
            var candidatePaths = new[]
            {
                System.IO.Path.Combine(assemblyDir, "WinNotifyBridge.Listener.exe"),
                installRoot != null ? System.IO.Path.Combine(installRoot, "Listener", "WinNotifyBridge.Listener.exe") : null
            };

            return candidatePaths.FirstOrDefault(path => !string.IsNullOrEmpty(path) && System.IO.File.Exists(path));
        }
    }
}
