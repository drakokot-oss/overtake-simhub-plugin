using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Overtake.SimHub.Plugin.Live;

namespace Overtake.SimHub.Plugin.UI
{
    public partial class SettingsControl : UserControl
    {
        private readonly OvertakePlugin _plugin;
        private readonly OvertakeSettings _settings;
        private readonly DispatcherTimer _statusTimer;
        private bool _wasLiveActive;   // detecta a transição ao vivo → encerrado p/ atualizar o rótulo

        private static readonly SolidColorBrush TealBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6));
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6));
        private static readonly SolidColorBrush YellowBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        private static readonly SolidColorBrush BlueBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
        private static readonly SolidColorBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x7B, 0x8C, 0xA3));
        private long _lastExportClickMs;
        private List<EligibleLeague> _eligible;
        private string _pendingSelectRaceId; // race to auto-select after a create/reload

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

            // Live broadcast section
            TxtLiveToken.Text = _settings.LiveBroadcastToken ?? "";
            CmbSessionType.Items.Clear();
            CmbSessionType.Items.Add(MakeItem("Corrida", "race"));
            CmbSessionType.Items.Add(MakeItem("Classificacao", "qualy"));
            CmbSessionType.Items.Add(MakeItem("Sprint", "sprint"));
            CmbSessionType.SelectedIndex = 0;
            UpdateLiveStatus();

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
            UpdateLiveStatus();
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

        // ---- Live cloud broadcast (overtakef1) ----

        private static ComboBoxItem MakeItem(string text, string tag)
        {
            return new ComboBoxItem { Content = text, Tag = tag };
        }

        private static string SelectedTag(ComboBox cmb)
        {
            var it = cmb != null ? cmb.SelectedItem as ComboBoxItem : null;
            return it != null && it.Tag != null ? it.Tag.ToString() : null;
        }

        private void BtnSaveLiveToken_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _plugin == null) return;
            _settings.LiveBroadcastToken = (TxtLiveToken.Text ?? "").Trim();
            SaveSettings();
            _plugin.ConfigureLive();
            LblLiveStatus.Text = "Token salvo.";
            LblLiveStatus.Foreground = TealBrush;
        }

        private void BtnLiveConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            // Persist whatever is typed before listing.
            _settings.LiveBroadcastToken = (TxtLiveToken.Text ?? "").Trim();
            SaveSettings();
            _plugin.ConfigureLive();
            if (string.IsNullOrEmpty(_settings.LiveBroadcastToken))
            {
                LblLiveStatus.Text = "Cole o token primeiro.";
                LblLiveStatus.Foreground = RedBrush;
                return;
            }
            BtnLiveConnect.IsEnabled = false;
            LblLiveStatus.Text = "Conectando...";
            LblLiveStatus.Foreground = DimBrush;
            System.Threading.Tasks.Task.Run(() =>
            {
                List<EligibleLeague> list = _plugin.LiveListEligible();
                string err = _plugin.LiveLastError;
                Dispatcher.Invoke(() =>
                {
                    BtnLiveConnect.IsEnabled = true;
                    if (list == null)
                    {
                        LblLiveStatus.Text = "Falha ao conectar: " + (err ?? "erro desconhecido");
                        LblLiveStatus.Foreground = RedBrush;
                        return;
                    }
                    _eligible = list;
                    CmbLeague.Items.Clear();
                    foreach (var lg in list)
                        CmbLeague.Items.Add(MakeItem(lg.LeagueName ?? lg.LeagueId, lg.LeagueId));
                    if (CmbLeague.Items.Count > 0) CmbLeague.SelectedIndex = 0;
                    LblLiveStatus.Text = list.Count == 0
                        ? "Conectado, mas nenhuma liga elegivel (precisa de papel broadcaster + live_enabled)."
                        : "Conectado. " + list.Count + " liga(s) disponivel(is).";
                    LblLiveStatus.Foreground = list.Count == 0 ? YellowBrush : GreenBrush;
                });
            });
        }

        private EligibleLeague FindLeague(string id)
        {
            if (_eligible == null || id == null) return null;
            foreach (var lg in _eligible) if (lg.LeagueId == id) return lg;
            return null;
        }

        private EligibleGrid FindGrid(EligibleLeague lg, string id)
        {
            if (lg == null || id == null) return null;
            foreach (var g in lg.Grids) if (g.GridId == id) return g;
            return null;
        }

        private void CmbLeague_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CmbGrid.Items.Clear();
            CmbRace.Items.Clear();
            var lg = FindLeague(SelectedTag(CmbLeague));
            if (lg == null) return;
            foreach (var g in lg.Grids)
                CmbGrid.Items.Add(MakeItem(g.GridName ?? g.GridId, g.GridId));
            if (CmbGrid.Items.Count > 0) CmbGrid.SelectedIndex = 0;
        }

        private void CmbGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadScheduledRaces(SelectedTag(CmbGrid));
        }

        // Fetch the grid's SCHEDULED races (PostgREST) and fill the dropdown.
        private void LoadScheduledRaces(string gridId)
        {
            CmbRace.Items.Clear();
            if (_plugin == null || string.IsNullOrEmpty(gridId)) return;
            LblLiveStatus.Text = "Carregando corridas agendadas...";
            LblLiveStatus.Foreground = DimBrush;
            System.Threading.Tasks.Task.Run(() =>
            {
                var races = _plugin.LiveListScheduledRaces(gridId);
                string err = _plugin.LiveLastError;
                Dispatcher.Invoke(() =>
                {
                    if (SelectedTag(CmbGrid) != gridId) return; // grid changed while loading
                    CmbRace.Items.Clear();
                    if (races == null)
                    {
                        LblLiveStatus.Text = "Falha ao listar corridas: " + (err ?? "erro");
                        LblLiveStatus.Foreground = RedBrush;
                        return;
                    }
                    foreach (var r in races)
                    {
                        string label = string.IsNullOrEmpty(r.Name) ? "(sem nome)" : r.Name;
                        if (!string.IsNullOrEmpty(r.RaceDate)) label += " - " + r.RaceDate;
                        if (!string.IsNullOrEmpty(r.Track)) label += " - " + r.Track;
                        CmbRace.Items.Add(MakeItem(label, r.Id));
                    }
                    if (!string.IsNullOrEmpty(_pendingSelectRaceId))
                    {
                        for (int i = 0; i < CmbRace.Items.Count; i++)
                        {
                            var it = CmbRace.Items[i] as ComboBoxItem;
                            if (it != null && (it.Tag as string) == _pendingSelectRaceId) { CmbRace.SelectedIndex = i; break; }
                        }
                        _pendingSelectRaceId = null;
                    }
                    else if (CmbRace.Items.Count > 0) CmbRace.SelectedIndex = 0;
                    LblLiveStatus.Text = races.Count == 0
                        ? "Nenhuma corrida agendada neste grid. Use 'Criar nova corrida'."
                        : races.Count + " corrida(s) agendada(s).";
                    LblLiveStatus.Foreground = races.Count == 0 ? YellowBrush : DimBrush;
                });
            });
        }

        private void BtnRefreshRaces_Click(object sender, RoutedEventArgs e)
        {
            LoadScheduledRaces(SelectedTag(CmbGrid));
        }

        private void BtnToggleNewRace_Click(object sender, RoutedEventArgs e)
        {
            bool show = PanelNewRace.Visibility != Visibility.Visible;
            PanelNewRace.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                if (CmbNewTrack.Items.Count == 0)
                {
                    foreach (var t in LiveBroadcaster.CanonicalTracks) CmbNewTrack.Items.Add(t);
                    CmbNewTrack.SelectedIndex = 0;
                }
                if (DpNewRaceDate.SelectedDate == null) DpNewRaceDate.SelectedDate = DateTime.Today;
            }
        }

        private void BtnCreateRace_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string gridId = SelectedTag(CmbGrid);
            if (string.IsNullOrEmpty(gridId))
            {
                LblLiveStatus.Text = "Selecione o grid (clique CONECTAR).";
                LblLiveStatus.Foreground = RedBrush;
                return;
            }
            string track = CmbNewTrack.SelectedItem as string;
            if (string.IsNullOrEmpty(track))
            {
                LblLiveStatus.Text = "Selecione a pista.";
                LblLiveStatus.Foreground = RedBrush;
                return;
            }
            if (DpNewRaceDate.SelectedDate == null)
            {
                LblLiveStatus.Text = "Selecione a data.";
                LblLiveStatus.Foreground = RedBrush;
                return;
            }
            string dateIso = DpNewRaceDate.SelectedDate.Value.ToString("yyyy-MM-dd");
            string time = (TxtNewRaceTime.Text ?? "").Trim();
            BtnCreateRace.IsEnabled = false;
            LblLiveStatus.Text = "Criando corrida...";
            LblLiveStatus.Foreground = DimBrush;
            System.Threading.Tasks.Task.Run(() =>
            {
                string raceId = _plugin.LiveCreateScheduledRace(gridId, track, dateIso, time);
                string err = _plugin.LiveLastError;
                Dispatcher.Invoke(() =>
                {
                    BtnCreateRace.IsEnabled = true;
                    if (string.IsNullOrEmpty(raceId))
                    {
                        LblLiveStatus.Text = "Falha ao criar corrida: " + (err ?? "erro");
                        LblLiveStatus.Foreground = RedBrush;
                        return;
                    }
                    PanelNewRace.Visibility = Visibility.Collapsed;
                    _pendingSelectRaceId = raceId;
                    LoadScheduledRaces(gridId); // reload + auto-select the new race
                });
            });
        }

        private void BtnGoLive_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string leagueId = SelectedTag(CmbLeague);
            string gridId = SelectedTag(CmbGrid);
            string raceId = SelectedTag(CmbRace);
            if (string.IsNullOrEmpty(leagueId) || string.IsNullOrEmpty(gridId))
            {
                LblLiveStatus.Text = "Selecione liga e grid (clique CONECTAR).";
                LblLiveStatus.Foreground = RedBrush;
                return;
            }
            if (string.IsNullOrEmpty(raceId))
            {
                LblLiveStatus.Text = "Selecione a corrida (ou crie uma nova).";
                LblLiveStatus.Foreground = RedBrush;
                return;
            }
            string sessionType = SelectedTag(CmbSessionType) ?? "race";
            BtnGoLive.IsEnabled = false;
            LblLiveStatus.Text = "Entrando ao vivo...";
            LblLiveStatus.Foreground = DimBrush;
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = _plugin.LiveGoLive(leagueId, gridId, raceId, sessionType);
                string err = _plugin.LiveLastError;
                Dispatcher.Invoke(() =>
                {
                    if (ok)
                    {
                        LblLiveStatus.Text = "AO VIVO. Transmitindo para o portal.";
                        LblLiveStatus.Foreground = GreenBrush;
                        BtnEndLive.IsEnabled = true;
                    }
                    else
                    {
                        LblLiveStatus.Text = "Falha ao entrar ao vivo: " + (err ?? "erro");
                        LblLiveStatus.Foreground = RedBrush;
                        BtnGoLive.IsEnabled = true;
                    }
                });
            });
        }

        private void BtnEndLive_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                _plugin.LiveEnd();
                Dispatcher.Invoke(() =>
                {
                    LblLiveStatus.Text = "Transmissao encerrada.";
                    LblLiveStatus.Foreground = TealBrush;
                    BtnEndLive.IsEnabled = false;
                    BtnGoLive.IsEnabled = true;
                });
            });
        }

        private void UpdateLiveStatus()
        {
            if (_plugin == null || LblLiveStatus == null) return;
            bool active = _plugin.LiveActive;
            BtnEndLive.IsEnabled = active;
            BtnGoLive.IsEnabled = !active;
            if (active)
            {
                string err = _plugin.LiveLastError;
                if (!string.IsNullOrEmpty(err))
                {
                    LblLiveStatus.Text = "AO VIVO (ultimo aviso: " + err + ")";
                    LblLiveStatus.Foreground = YellowBrush;
                }
                else
                {
                    LblLiveStatus.Text = "AO VIVO. Transmitindo para o portal.";
                    LblLiveStatus.Foreground = GreenBrush;
                }
            }
            else if (_wasLiveActive)
            {
                // Transição ao vivo → encerrado (manual OU auto-End). Antes, sem este else, o
                // rótulo ficava preso em "AO VIVO" mesmo após encerrar. Só no tick da transição,
                // para não sobrescrever mensagens transitórias (ex.: "Token salvo.").
                LblLiveStatus.Text = "Transmissao encerrada.";
                LblLiveStatus.Foreground = DimBrush;
            }
            _wasLiveActive = active;
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
