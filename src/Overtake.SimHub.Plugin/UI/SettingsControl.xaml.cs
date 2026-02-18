using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Overtake.SimHub.Plugin.UI
{
    public partial class SettingsControl : UserControl
    {
        private readonly OvertakePlugin _plugin;
        private readonly OvertakeSettings _settings;
        private readonly DispatcherTimer _statusTimer;

        private static readonly SolidColorBrush TealBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6));
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6));
        private static readonly SolidColorBrush YellowBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        private static readonly SolidColorBrush BlueBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
        private static readonly SolidColorBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x7B, 0x8C, 0xA3));

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(OvertakePlugin plugin, OvertakeSettings settings)
            : this()
        {
            _plugin = plugin;
            _settings = settings;

            _statusTimer = new DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.Tick += StatusTimer_Tick;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            TxtPort.Text = _settings.UdpPort.ToString();
            TxtOutputFolder.Text = string.IsNullOrWhiteSpace(_settings.OutputFolder)
                ? DefaultOutputFolder() : _settings.OutputFolder;
            ChkAutoExport.IsChecked = _settings.AutoExportJson;

            LblVersion.Text = "v" + OvertakePlugin.PluginVersion;

            UpdateStatusLabels();
            if (_statusTimer != null)
                _statusTimer.Start();
        }

        private void BtnApplyPort_Click(object sender, RoutedEventArgs e)
        {
            int port;
            if (int.TryParse(TxtPort.Text, out port) && port > 0 && port <= 65535)
            {
                _settings.UdpPort = port;
                _plugin.RestartReceiver();
                SaveSettings();
            }
            else
            {
                MessageBox.Show("Please enter a valid port number (1-65535).",
                    "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select output folder for JSON exports";
                if (!string.IsNullOrEmpty(_settings.OutputFolder))
                    dialog.SelectedPath = _settings.OutputFolder;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _settings.OutputFolder = dialog.SelectedPath;
                    TxtOutputFolder.Text = dialog.SelectedPath;
                    SaveSettings();
                }
            }
        }

        private void ChkAutoExport_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.AutoExportJson = ChkAutoExport.IsChecked == true;
            SaveSettings();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            string outputDir = ResolveOutputFolder();
            string result = _plugin.ExportLeagueJson(outputDir);

            if (File.Exists(result))
            {
                LblExportResult.Text = "Exported: " + Path.GetFileName(result);
                LblExportResult.Foreground = GreenBrush;
                LblLastExport.Text = Path.GetFileName(result);
                LblLastExport.Foreground = GreenBrush;
                _settings.LastExportPath = result;
                SaveSettings();
            }
            else
            {
                LblExportResult.Text = result;
                LblExportResult.Foreground = RedBrush;
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = ResolveOutputFolder();
            if (Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
            else
                MessageBox.Show("Output folder does not exist yet. Run an export first.",
                    "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            UpdateStatusLabels();
        }

        private void UpdateStatusLabels()
        {
            if (_plugin == null || _plugin.Receiver == null) return;

            string receiverStatus = _plugin.Receiver.Status;
            long packets = _plugin.Receiver.PacketsReceived;
            string error = _plugin.Receiver.LastError;

            var store = _plugin.Store;
            int driverCount = 0;
            string sessionType = "";
            string trackName = "";
            int sessionCount = 0;

            if (store != null)
            {
                sessionCount = store.Sessions.Count;
                Store.SessionRun latestSession = null;
                long latestTs = 0;
                foreach (var sess in store.Sessions.Values)
                {
                    if (sess.LastPacketMs >= latestTs)
                    {
                        latestTs = sess.LastPacketMs;
                        latestSession = sess;
                    }
                }
                if (latestSession != null)
                {
                    driverCount = latestSession.Drivers.Count;
                    if (latestSession.SessionType.HasValue)
                    {
                        string stName;
                        if (Finalizer.Lookups.SessionType.TryGetValue(latestSession.SessionType.Value, out stName))
                            sessionType = stName;
                    }
                    if (latestSession.TrackId.HasValue)
                    {
                        string trName;
                        if (Finalizer.Lookups.Tracks.TryGetValue(latestSession.TrackId.Value, out trName))
                            trackName = trName;
                    }
                }
            }

            // Status banner
            if (receiverStatus == "Error")
            {
                StatusDot.Fill = RedBrush;
                LblStatusMain.Text = "Connection error";
                LblHelpText.Text = string.IsNullOrEmpty(error) ? "" : error;
                LblHelpText.Foreground = RedBrush;
            }
            else if (packets > 0 && driverCount > 0 && _plugin.SessionEnded)
            {
                StatusDot.Fill = BlueBrush;
                LblStatusMain.Text = string.Format("Session ended: {0} - {1}", trackName, sessionType);
                string autoMsg = _plugin.LastAutoExportMsg;
                LblHelpText.Text = !string.IsNullOrEmpty(autoMsg)
                    ? autoMsg
                    : "Session data captured. Click EXPORT LEAGUE JSON to save.";
                LblHelpText.Foreground = BlueBrush;
            }
            else if (packets > 0 && driverCount > 0)
            {
                StatusDot.Fill = GreenBrush;
                LblStatusMain.Text = string.Format("Capturing: {0} - {1}", trackName, sessionType);
                LblHelpText.Text = "Data is being captured. Your SimHub dashboards continue to work normally.";
                LblHelpText.Foreground = DimBrush;
            }
            else if (packets > 0)
            {
                StatusDot.Fill = GreenBrush;
                LblStatusMain.Text = "Receiving data...";
                LblHelpText.Text = "Packets arriving. Waiting for session details.";
                LblHelpText.Foreground = DimBrush;
            }
            else if (receiverStatus == "Listening")
            {
                StatusDot.Fill = YellowBrush;
                LblStatusMain.Text = string.Format("Waiting for F1 25 data on port {0}...", _settings.UdpPort);
                LblHelpText.Text = string.Format(
                    "1) F1 25: Settings > Telemetry > UDP Port = {0}  " +
                    "2) SimHub: Home > F1 25 > Game config > UDP Port = {1}  " +
                    "See Setup Guide below.",
                    _settings.UdpPort, _settings.ForwardPort);
                LblHelpText.Foreground = YellowBrush;
            }
            else
            {
                StatusDot.Fill = RedBrush;
                LblStatusMain.Text = "Listener stopped";
                LblHelpText.Text = "Click Apply on the port setting to restart.";
                LblHelpText.Foreground = YellowBrush;
            }

            // Counters
            LblTrack.Text = string.IsNullOrEmpty(trackName) ? "\u2014" : trackName;
            LblSession.Text = string.IsNullOrEmpty(sessionType) ? "\u2014" : sessionType;
            LblDrivers.Text = driverCount.ToString();
            LblPackets.Text = packets.ToString("N0");
            LblSessions.Text = sessionCount.ToString();
            LblStatus.Text = receiverStatus;

            // Last export
            string lastExport = _plugin.LastExportPath;
            if (!string.IsNullOrEmpty(lastExport) && File.Exists(lastExport))
            {
                LblLastExport.Text = Path.GetFileName(lastExport);
                LblLastExport.Foreground = GreenBrush;
            }

            // Auto-export message
            string autoExportMsg = _plugin.LastAutoExportMsg;
            if (!string.IsNullOrEmpty(autoExportMsg) && string.IsNullOrEmpty(LblExportResult.Text))
            {
                LblExportResult.Text = autoExportMsg;
                LblExportResult.Foreground = GreenBrush;
            }

            // Update notification
            if (_plugin.UpdateAvailable && LblUpdateAvailable.Visibility != Visibility.Visible)
            {
                LblUpdateAvailable.Visibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(_plugin.UpdateDownloadUrl))
                {
                    try { LnkUpdate.NavigateUri = new Uri(_plugin.UpdateDownloadUrl); }
                    catch { /* invalid url */ }
                }
            }
        }

        private string ResolveOutputFolder()
        {
            string dir = _settings.OutputFolder;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = DefaultOutputFolder();
                _settings.OutputFolder = dir;
                TxtOutputFolder.Text = dir;
                SaveSettings();
            }
            return dir;
        }

        private static string DefaultOutputFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Overtake", "exports");
        }

        private void SaveSettings()
        {
            if (_plugin != null)
                _plugin.SaveSettings();
        }

        private void Hyperlink_RequestNavigate(object sender,
            System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* browser not available */ }
            e.Handled = true;
        }
    }
}
