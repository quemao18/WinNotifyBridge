using System;
using System.Windows;
using WinNotifyBridge.Tray.ViewModels;

namespace WinNotifyBridge.Tray
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();

            var viewModel = new SettingsViewModel();
            viewModel.RequestClose = () =>
            {
                DialogResult = true;
                Close();
            };

            DataContext = viewModel;
        }
    }
}
