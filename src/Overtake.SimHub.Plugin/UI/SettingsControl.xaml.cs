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
        private long _lastExportClickMs;

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

            ChkRaceUi.IsChecked = _settings.RaceUiEnabled;
            ChkRaceUiLan.IsChecked = _settings.RaceUiAllowLan;
            TxtRaceUiPort.Text = (_settings.RaceUiPort > 0 ? _settings.RaceUiPort : 8088).ToString();
            UpdateRaceUiUrl();

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

        private void ChkRaceUi_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _plugin == null) return;
            _settings.RaceUiEnabled = ChkRaceUi.IsChecked == true;
            _settings.RaceUiAllowLan = ChkRaceUiLan.IsChecked == true;
            SaveSettings();
            _plugin.StartRaceWebServer();
            UpdateRaceUiUrl();
        }

        private void BtnApplyRaceUi_Click(object sender, RoutedEventArgs e)
        {
            int port;
            if (int.TryParse(TxtRaceUiPort.Text, out port) && port > 0 && port <= 65535)
            {
                _settings.RaceUiPort = port;
                SaveSettings();
                _plugin.StartRaceWebServer();
                UpdateRaceUiUrl();
            }
            else
            {
                MessageBox.Show("Informe uma porta valida (1-65535).",
                    "Porta invalida", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnOpenRaceUi_Click(object sender, RoutedEventArgs e)
        {
            string url = _plugin != null ? _plugin.RaceWebUrl : "";
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Ative a Race UI primeiro.",
                    "Race UI", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* browser not available */ }
        }

        private void UpdateRaceUiUrl()
        {
            if (LblRaceUiUrl == null || _plugin == null) return;
            string url = _plugin.RaceWebUrl;
            LblRaceUiUrl.Text = string.IsNullOrEmpty(url) ? "desativado" : url;
        }

        private void BtnNewSession_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var r = MessageBox.Show(
                "Isto apaga toda a telemetria em memoria (sessoes, pilotos, caches de nomes).\n\n" +
                "Use depois de ter guardado o export (.otk) e antes da proxima corrida.\n\n" +
                "O listener UDP continua ativo (SimHub nao e afetado).\n\n" +
                "Continuar?",
                "Nova sessao — Overtake",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            _plugin.BeginNewCaptureSession();
            LblExportResult.Text = "Captura limpa. A proxima corrida comeca do zero.";
            LblExportResult.Foreground = TealBrush;
            UpdateStatusLabels();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastExportClickMs < 5000)
                return;
            _lastExportClickMs = nowMs;

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
                    foreach (var dkvp in latestSession.Drivers)
                    {
                        Packets.ParticipantEntry ti;
                        latestSession.TeamByCarIdx.TryGetValue(dkvp.Value.CarIdx, out ti);
                        if (ti != null && ti.TeamId == 255) continue;
                        if (dkvp.Key.StartsWith("Driver_") || dkvp.Key.StartsWith("Car_")) continue;
                        driverCount++;
                    }
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
                LblStatusMain.Text = string.Format("Waiting for F1 25 / F1 26 data on port {0}...", _settings.UdpPort);
                LblHelpText.Text = string.Format(
                    "1) F1 25 / F1 26: Settings > Telemetry > UDP Port = {0}  " +
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

            UpdateRaceUiUrl();

            // Update notification — severity-driven, re-evaluated every tick so a
            // live UnsupportedFormat signal can escalate the banner mid-session.
            RefreshUpdateBanner();
        }

        private void RefreshUpdateBanner()
        {
            UpdateSeverity severity = _plugin.CurrentUpdateSeverity;

            if (severity == UpdateSeverity.UpToDate)
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateBanner.Visibility = Visibility.Visible;

            string current = OvertakePlugin.PluginVersion;
            string latest = _plugin.LatestVersion;
            LblUpdateVersion.Text = string.IsNullOrEmpty(latest)
                ? string.Format("v{0}", current)
                : string.Format("v{0}  \u2192  v{1}", current, latest);

            switch (severity)
            {
                case UpdateSeverity.UnsupportedFormat:
                    ApplyBannerTheme(critical: true);
                    LblUpdateTitle.Text = "\u26A0 Formato UDP nao suportado";
                    LblUpdateWarning.Text =
                        "O jogo esta enviando um formato UDP que ESTA versao nao "
                        + "entende. O arquivo vai sair ILEGIVEL (nomes e equipes "
                        + "embaralhados). Atualize o plugin OU, como solucao "
                        + "imediata, mude a opcao \"UDP Format\" do jogo para 2025.";
                    LblUpdateWarning.Visibility = Visibility.Visible;
                    break;

                case UpdateSeverity.UpdateRequired:
                    ApplyBannerTheme(critical: true);
                    LblUpdateTitle.Text = "\u26A0 Atualizacao necessaria";
                    LblUpdateWarning.Text =
                        "Sua versao esta MUITO desatualizada e pode gerar arquivos "
                        + "corrompidos (ex.: capturas no formato F1 26 saem com nomes "
                        + "e equipes embaralhados). Atualize antes da proxima corrida.";
                    LblUpdateWarning.Visibility = Visibility.Visible;
                    break;

                default: // UpdateAvailable
                    ApplyBannerTheme(critical: false);
                    LblUpdateTitle.Text = "Nova versao disponivel";
                    LblUpdateWarning.Visibility = Visibility.Collapsed;
                    break;
            }

            if (!string.IsNullOrEmpty(_plugin.LatestReleaseNotes))
            {
                LblReleaseNotes.Text = _plugin.LatestReleaseNotes
                    .Replace("### Fixed\r\n", "").Replace("### Fixed\n", "")
                    .Replace("### Added\r\n", "").Replace("### Added\n", "")
                    .Replace("### Changed\r\n", "").Replace("### Changed\n", "")
                    .Trim();
                if (LblReleaseNotes.Text.Length > 200)
                    LblReleaseNotes.Text = LblReleaseNotes.Text.Substring(0, 200) + "...";
                LblReleaseNotes.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrEmpty(_plugin.UpdateDownloadUrl))
            {
                try { LnkUpdate.NavigateUri = new Uri(_plugin.UpdateDownloadUrl); }
                catch { /* invalid url */ }
            }

            // The one-click installer button only makes sense when we actually
            // resolved an installer URL; otherwise fall back to the page link.
            BtnUpdateNow.Visibility = string.IsNullOrEmpty(_plugin.InstallerUrl)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private static readonly SolidColorBrush BannerBgYellow = new SolidColorBrush(Color.FromRgb(0x2D, 0x2A, 0x10));
        private static readonly SolidColorBrush BannerBgRed = new SolidColorBrush(Color.FromRgb(0x3A, 0x16, 0x16));
        private static readonly SolidColorBrush BannerBorderYellow = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
        private static readonly SolidColorBrush BannerBorderRed = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        private static readonly SolidColorBrush BannerTextDark = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E));
        private static readonly SolidColorBrush BannerTextLight = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

        private void ApplyBannerTheme(bool critical)
        {
            UpdateBanner.Background = critical ? BannerBgRed : BannerBgYellow;
            UpdateBanner.BorderBrush = critical ? BannerBorderRed : BannerBorderYellow;
            LblUpdateTitle.Foreground = critical ? BannerBorderRed : BannerBorderYellow;
            LblUpdateVersion.Foreground = critical ? BannerBorderRed : BannerBorderYellow;
            BtnUpdateNow.Background = critical ? BannerBorderRed : BannerBorderYellow;
            BtnUpdateNow.Foreground = critical ? BannerTextLight : BannerTextDark;
        }

        private void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            string installerUrl = _plugin.InstallerUrl;
            if (string.IsNullOrEmpty(installerUrl))
            {
                // No direct installer: open the downloads page instead.
                string page = string.IsNullOrEmpty(_plugin.UpdateDownloadUrl)
                    ? "https://racehub.overtakef1.com/downloads"
                    : _plugin.UpdateDownloadUrl;
                try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
                catch { /* browser not available */ }
                return;
            }

            var confirm = MessageBox.Show(
                "Vamos baixar o instalador da versao mais recente e abri-lo.\n\n"
                + "Feche o SimHub quando o instalador pedir, conclua a instalacao e "
                + "reabra o SimHub.\n\nContinuar?",
                "Atualizar Overtake Telemetry",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            BtnUpdateNow.IsEnabled = false;
            BtnUpdateNow.Content = "Baixando...";

            System.Threading.Tasks.Task.Run(() =>
            {
                string error = null;
                string savedPath = null;
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                    string fileName = "Overtake-Telemetry-Setup.exe";
                    try
                    {
                        string leaf = Path.GetFileName(new Uri(installerUrl).LocalPath);
                        if (!string.IsNullOrEmpty(leaf)) fileName = leaf;
                    }
                    catch { /* keep default name */ }

                    savedPath = Path.Combine(Path.GetTempPath(), fileName);
                    using (var client = new System.Net.WebClient())
                    {
                        client.Headers[System.Net.HttpRequestHeader.UserAgent] =
                            "OvertakeTelemetry/" + OvertakePlugin.PluginVersion;
                        client.DownloadFile(installerUrl, savedPath);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                Dispatcher.Invoke(() =>
                {
                    if (error == null && savedPath != null && File.Exists(savedPath))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(savedPath) { UseShellExecute = true });
                            LblExportResult.Text = "Instalador aberto. Feche o SimHub para concluir.";
                            LblExportResult.Foreground = TealBrush;
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }
                    }

                    if (error != null)
                    {
                        BtnUpdateNow.IsEnabled = true;
                        BtnUpdateNow.Content = "Baixar e atualizar";
                        LblExportResult.Text = "Falha ao baixar: " + error;
                        LblExportResult.Foreground = RedBrush;
                        try
                        {
                            string page = string.IsNullOrEmpty(_plugin.UpdateDownloadUrl)
                                ? "https://racehub.overtakef1.com/downloads"
                                : _plugin.UpdateDownloadUrl;
                            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                        }
                        catch { /* browser not available */ }
                    }
                });
            });
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
