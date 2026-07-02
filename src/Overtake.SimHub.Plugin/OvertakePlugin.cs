using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameReaderCommon;
using SimHub.Plugins;
using Overtake.SimHub.Plugin.Finalizer;
using Overtake.SimHub.Plugin.Live;
using Overtake.SimHub.Plugin.Packets;
using Overtake.SimHub.Plugin.Parsers;
using Overtake.SimHub.Plugin.Store;
using Overtake.SimHub.Plugin.Security;
using Overtake.SimHub.Plugin.UI;

namespace Overtake.SimHub.Plugin
{
    [PluginDescription("Receives F1 25 / F1 26 UDP telemetry for the Overtake platform")]
    [PluginAuthor("Overtake")]
    [PluginName("Overtake Telemetry")]
    public class OvertakePlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal const string UpdateJsonUrl =
            "https://raw.githubusercontent.com/drakokot-oss/overtake-simhub-plugin/main/version.json";

        private OvertakeSettings _settings;
        private UdpReceiver _receiver;
        private long _displayedPackets;
        private string _sessionType = "";
        private byte _currentSessionTypeId;
        private SessionStore _store;
        private string _lastExportPath = "";
        private string _lastAutoExportMsg = "";
        private bool _sessionEndDetected;
        private bool _sessionEnded;
        private bool _raceFinalClassificationReceived;
        private bool _autoExportArmed; // Once armed, SSTA cannot cancel the pending export
        private long _raceSendAtMs;
        private long _raceFcFirstMs;
        private const long FC_EXPORT_DELAY_MS = 5000;
        private string _latestVersion = "";
        private string _updateDownloadUrl = "";
        private string _latestReleaseNotes = "";
        private string _minSupportedVersion = "";
        private string _installerUrl = "";

        // Race UI (web) — Rota B. Read-only live broadcast server.
        private RaceWebServer _raceWeb;
        private long _lastRaceUiPublishMs;
        private const long RaceUiPublishIntervalMs = 150; // ~6-7 Hz
        private readonly JavaScriptSerializer _liveSerializer =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // Live cloud broadcast (overtakef1) — streams the same snapshot to the portal.
        private LiveBroadcaster _live;
        private long _lastLivePushMs;
        private const long LivePublishIntervalMs = 400; // ~2-3 Hz to the cloud
        private string _lastLiveJson;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream(
                        "Overtake.SimHub.Plugin.Assets.overtake-icon.png"))
                    {
                        if (stream == null) return null;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = stream;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        public string LeftMenuTitle
        {
            get { return "Overtake Telemetry"; }
        }

        public void Init(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[Overtake] Plugin initializing...");

            _settings = this.ReadCommonSettings("OvertakeSettings", () => new OvertakeSettings());

            // Migrate from old default port 20777 to 20778 (relay mode)
            if (_settings.UdpPort == 20777 && _settings.ForwardPort == 0)
            {
                _settings.UdpPort = 20778;
                _settings.ForwardPort = 20777;
                global::SimHub.Logging.Current.Info("[Overtake] Migrated UDP port from 20777 to 20778 (relay mode)");
            }
            if (_settings.ForwardPort == 0)
                _settings.ForwardPort = 20777;

            // v1.1.31 — settings schema migration: existing users come from v1.1.30 with
            // SettingsSchemaVersion=0 (missing in saved JSON). Apply intended defaults for
            // newly added fields and bump version so we don't migrate again on next launch.
            if (_settings.SettingsSchemaVersion < 1)
            {
                _settings.AutoCleanAfterExport = true;
                _settings.SettingsSchemaVersion = 1;
                global::SimHub.Logging.Current.Info(
                    "[Overtake] Migrated settings schema 0 -> 1 (AutoCleanAfterExport=on)");
            }
            if (_settings.SettingsSchemaVersion < 2)
            {
                _settings.RaceUiEnabled = true;
                _settings.RaceUiPort = _settings.RaceUiPort > 0 ? _settings.RaceUiPort : 8088;
                _settings.SettingsSchemaVersion = 2;
                global::SimHub.Logging.Current.Info(
                    "[Overtake] Migrated settings schema 1 -> 2 (Race UI web server defaults)");
            }
            if (_settings.SettingsSchemaVersion < 3)
            {
                if (_settings.LiveBroadcastToken == null) _settings.LiveBroadcastToken = "";
                if (_settings.LiveBroadcastBaseUrl == null) _settings.LiveBroadcastBaseUrl = "";
                _settings.SettingsSchemaVersion = 3;
                global::SimHub.Logging.Current.Info(
                    "[Overtake] Migrated settings schema 2 -> 3 (live cloud broadcast defaults)");
            }

            _store = new SessionStore();

            _receiver = new UdpReceiver();
            _receiver.Start(_settings.UdpPort, _settings.ForwardPort);

            this.AttachDelegate("Overtake.Status", () => _receiver.Status);
            this.AttachDelegate("Overtake.PacketsReceived", () => _displayedPackets);
            this.AttachDelegate("Overtake.SessionType", () => _sessionType);
            this.AttachDelegate("Overtake.ActiveDrivers", () => ActiveDriverCount());
            this.AttachDelegate("Overtake.SessionsCount", () => _store.Sessions.Count);

            // Update advisory — surfaced as dashboard properties so a streamer can
            // put it on the rig screen and notice it WITHOUT opening this panel
            // (the failure mode that bit the v1.1.27 user). UpdateStatus is one of
            // UpToDate / UpdateAvailable / UpdateRequired / UnsupportedFormat.
            this.AttachDelegate("Overtake.PluginVersion", () => PluginVersion);
            this.AttachDelegate("Overtake.LatestVersion", () => _latestVersion);
            this.AttachDelegate("Overtake.UpdateStatus", () => UpdateAdvisor.StatusToken(CurrentUpdateSeverity));

            this.AttachDelegate("Overtake.RaceUiUrl", () => _raceWeb != null && _raceWeb.Running ? _raceWeb.Url : "");
            this.AttachDelegate("Overtake.RaceUiClients", () => _raceWeb != null ? _raceWeb.ClientCount : 0);

            StartRaceWebServer();

            _live = new LiveBroadcaster();
            ConfigureLive();
            this.AttachDelegate("Overtake.LiveBroadcasting", () => _live != null && _live.Active);

            global::SimHub.Logging.Current.Info("[Overtake] Plugin initialized");

            System.Threading.Tasks.Task.Run(() => CheckForUpdates());
        }

        /// <summary>Starts (or restarts) the live race UI web server per current settings.</summary>
        internal void StartRaceWebServer()
        {
            try
            {
                if (_raceWeb != null) { _raceWeb.Stop(); _raceWeb = null; }
                if (_settings == null || !_settings.RaceUiEnabled) return;
                _raceWeb = new RaceWebServer();
                _raceWeb.Start(_settings.RaceUiPort, _settings.RaceUiAllowLan);
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Error(
                    string.Format("[Overtake] Race UI web server failed to start: {0}", ex.Message));
            }
        }

        internal void StopRaceWebServer()
        {
            try { if (_raceWeb != null) { _raceWeb.Stop(); _raceWeb = null; } }
            catch { }
        }

        internal string RaceWebUrl
        {
            get { return _raceWeb != null && _raceWeb.Running ? _raceWeb.Url : ""; }
        }

        internal bool RaceWebRunning
        {
            get { return _raceWeb != null && _raceWeb.Running; }
        }

        private void PublishRaceUi()
        {
            bool wsActive = _raceWeb != null && _raceWeb.Running && _raceWeb.ClientCount > 0;
            bool cloudActive = _live != null && _live.Active;
            if (!wsActive && !cloudActive) return;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastRaceUiPublishMs < RaceUiPublishIntervalMs) return;
            _lastRaceUiPublishMs = nowMs;
            try
            {
                var snap = LiveSnapshotBuilder.Build(_store);
                ExportNumbers.SanitizeForJson(snap);
                string json = _liveSerializer.Serialize(snap);
                _lastLiveJson = json;
                if (wsActive) _raceWeb.Publish(json);
                // Cloud push throttled to ~2-3 Hz (lighter than the local WS cadence).
                if (cloudActive && nowMs - _lastLivePushMs >= LivePublishIntervalMs)
                {
                    _lastLivePushMs = nowMs;
                    _live.PushSnapshot(json);
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Info(
                    string.Format("[Overtake] Race UI publish skipped: {0}", ex.Message));
            }
        }

        // ---- Live cloud broadcast (overtakef1) — driven by the settings panel ----

        /// <summary>Apply token + base URL from settings to the broadcaster.</summary>
        internal void ConfigureLive()
        {
            if (_live == null) _live = new LiveBroadcaster();
            _live.Token = _settings.LiveBroadcastToken ?? "";
            if (!string.IsNullOrWhiteSpace(_settings.LiveBroadcastBaseUrl))
                _live.BaseUrl = _settings.LiveBroadcastBaseUrl.Trim();
        }

        public bool LiveActive { get { return _live != null && _live.Active; } }
        public string LiveSessionId { get { return _live != null ? _live.LiveSessionId : null; } }
        public string LiveLastError { get { return _live != null ? _live.LastError : null; } }

        /// <summary>live-start mode=list — returns eligible leagues/grids/races (null on error).</summary>
        public System.Collections.Generic.List<EligibleLeague> LiveListEligible()
        {
            ConfigureLive();
            return _live.ListEligible();
        }

        /// <summary>Scheduled races of a grid (PostgREST read). Null on error.</summary>
        public System.Collections.Generic.List<ScheduledRace> LiveListScheduledRaces(string gridId)
        {
            ConfigureLive();
            return _live.ListScheduledRaces(gridId);
        }

        /// <summary>Create a scheduled race via the server RPC (option B). Returns raceId or null.</summary>
        public string LiveCreateScheduledRace(string gridId, string track, string raceDateIso, string timeHHMM)
        {
            ConfigureLive();
            return _live.CreateScheduledRace(gridId, track, raceDateIso, timeHHMM);
        }

        /// <summary>Open a live session on an EXISTING race (raceId required).</summary>
        public bool LiveGoLive(string leagueId, string gridId, string raceId, string sessionType)
        {
            ConfigureLive();
            bool ok = _live.GoLive(leagueId, gridId, raceId, sessionType);
            if (ok && _store != null)
            {
                // Stamp the capture so the exported OTK carries the race binding.
                _store.LiveRaceId = raceId;
                _store.LiveLeagueId = leagueId;
                _store.LiveGridId = gridId;
                _store.LiveBroadcastSessionId = _live.LiveSessionId;
            }
            return ok;
        }

        /// <summary>Close the live session (sends ended=true with the last snapshot).</summary>
        public void LiveEnd()
        {
            if (_live != null) _live.End(_lastLiveJson);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            byte[] raw;
            while (_receiver.PacketQueue.TryDequeue(out raw))
            {
                _displayedPackets++;

                var parsed = _store.ParsePacket(raw);
                if (parsed == null) continue;

                if (parsed.Session != null)
                {
                    _sessionType = SessionTypeName(parsed.Session.SessionType);
                    _currentSessionTypeId = parsed.Session.SessionType;
                }

                _store.Ingest(parsed);

                // FinalClassification for Race = tyreStints + pit stops. Export only after it arrives.
                if (parsed.FinalClassification != null)
                {
                    foreach (var sess in _store.Sessions.Values)
                    {
                        if (sess.SessionType.HasValue && IsTerminalSession((byte)sess.SessionType.Value)
                            && sess.FinalClassification != null)
                        {
                            if (!_raceFinalClassificationReceived)
                            {
                                _raceFinalClassificationReceived = true;
                                _raceFcFirstMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            }
                            break;
                        }
                    }
                }

                if (parsed.Event != null && parsed.Event.Code == "SEND")
                {
                    _sessionEnded = true;
                    if (IsTerminalSession(_currentSessionTypeId))
                    {
                        _sessionEndDetected = true;
                        _raceSendAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        // Arm export as soon as Race SEND arrives with FC data.
                        // In online multiplayer SSTA (next session) arrives within
                        // seconds of SEND, so we must arm before it resets flags.
                        if (_raceFinalClassificationReceived)
                        {
                            _autoExportArmed = true;
                        }
                    }
                }
                if (parsed.Event != null && parsed.Event.Code == "SSTA")
                {
                    _sessionEnded = false;
                    if (!_autoExportArmed)
                    {
                        _raceFinalClassificationReceived = false;
                        _raceSendAtMs = 0;
                        _raceFcFirstMs = 0;
                    }
                }
            }

            // Auto-export check: runs after all queued packets are processed.
            // Armed path: wait FC_EXPORT_DELAY_MS (5s) after SEND for last packets to flush.
            // Fallback: 60s after SEND if arming didn't happen (e.g. FC arrived late).
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool armedReady = _autoExportArmed && _raceSendAtMs > 0
                && (nowMs - _raceSendAtMs) >= FC_EXPORT_DELAY_MS;
            bool fcStable = _sessionEndDetected && _raceFinalClassificationReceived
                && _raceFcFirstMs > 0 && (nowMs - _raceFcFirstMs) >= FC_EXPORT_DELAY_MS;
            bool fallbackElapsed = _raceSendAtMs > 0 && (nowMs - _raceSendAtMs) >= 60000;
            bool shouldExport = (armedReady || (_sessionEndDetected && (fcStable || fallbackElapsed)))
                && _settings.AutoExportJson && _store.Sessions.Count > 0;
            if (shouldExport)
            {
                _sessionEndDetected = false;
                _raceFinalClassificationReceived = false;
                _raceSendAtMs = 0;
                _raceFcFirstMs = 0;
                _autoExportArmed = false;
                bool exportOk = TryAutoExport();
                // Camada 2 — auto-rotate AFTER successful auto-export so the next event
                // starts in a fresh capture even if the user never clicks "Nova sessão".
                // Disabled if AutoCleanAfterExport=false in settings.
                if (exportOk && _settings.AutoCleanAfterExport)
                {
                    BeginNewCaptureSession();
                    global::SimHub.Logging.Current.Info(
                        "[Overtake] Capture auto-cleaned after export (AutoCleanAfterExport=on)");
                }
            }

            // Camada 1 — react to AutoRotateRequested raised by SessionStore on track change.
            // The very first Session packet of the new event was rejected by the store
            // (so the new track doesn't pollute the old capture). Now we close out the
            // old capture (export if not already exported) and start fresh.
            if (_store.AutoRotateRequested)
            {
                string reason = _store.AutoRotateReason ?? "track change";
                global::SimHub.Logging.Current.Info(
                    string.Format("[Overtake] Auto-rotate triggered: {0}", reason));

                if (_settings.AutoExportJson && _store.Sessions.Count > 0)
                {
                    bool exportOk = TryAutoExport();
                    if (exportOk)
                        global::SimHub.Logging.Current.Info(
                            "[Overtake] Auto-rotate: previous capture exported");
                }
                BeginNewCaptureSession();
                _store.ClearAutoRotateRequest();
            }

            // Rota B — push the live snapshot to connected broadcast UI clients.
            // Throttled internally to ~6-7 Hz; no-op when no client is connected.
            PublishRaceUi();
        }

        public void End(PluginManager pluginManager)
        {
            try { if (_live != null && _live.Active) _live.End(_lastLiveJson); } catch { }
            StopRaceWebServer();
            if (_receiver != null)
            {
                _receiver.Stop();
                _receiver.Dispose();
            }
            this.SaveCommonSettings("OvertakeSettings", _settings);
            global::SimHub.Logging.Current.Info("[Overtake] Plugin stopped");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this, _settings);
        }

        internal OvertakeSettings Settings
        {
            get { return _settings; }
        }

        internal UdpReceiver Receiver
        {
            get { return _receiver; }
        }

        internal SessionStore Store
        {
            get { return _store; }
        }

        internal string LastExportPath
        {
            get { return _lastExportPath; }
        }

        internal bool SessionEnded
        {
            get { return _sessionEnded; }
        }

        internal string LastAutoExportMsg
        {
            get { return _lastAutoExportMsg; }
        }

        internal static string PluginVersion
        {
            get
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                return string.Format("{0}.{1}.{2}", ver.Major, ver.Minor, ver.Build);
            }
        }

        internal string LatestVersion
        {
            get { return _latestVersion; }
        }

        internal string UpdateDownloadUrl
        {
            get { return _updateDownloadUrl; }
        }

        internal string LatestReleaseNotes
        {
            get { return _latestReleaseNotes; }
        }

        internal bool UpdateAvailable
        {
            get
            {
                if (string.IsNullOrEmpty(_latestVersion)) return false;
                try
                {
                    var current = new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString());
                    var latest = new Version(_latestVersion + ((_latestVersion.Split('.').Length < 4) ? ".0" : ""));
                    return latest > current;
                }
                catch { return false; }
            }
        }

        internal string MinSupportedVersion
        {
            get { return _minSupportedVersion; }
        }

        internal string InstallerUrl
        {
            get { return _installerUrl; }
        }

        /// <summary>
        /// The UDP wire format the current build cannot parse, observed live in the
        /// active capture (0 = none / all good). Pre-2026 builds never set this;
        /// the current build only sets it for formats beyond its support window.
        /// </summary>
        internal int CurrentUnsupportedFormat
        {
            get { return _store != null ? _store.UnsupportedFormatSeen : 0; }
        }

        /// <summary>
        /// How urgently the user should update, combining the version gap with a
        /// live unsupported-wire-format signal. Drives the settings banner and the
        /// <c>Overtake.UpdateStatus</c> dashboard property.
        /// </summary>
        internal UpdateSeverity CurrentUpdateSeverity
        {
            get
            {
                return UpdateAdvisor.Evaluate(
                    PluginVersion, _latestVersion, _minSupportedVersion, CurrentUnsupportedFormat);
            }
        }

        internal void SaveSettings()
        {
            this.SaveCommonSettings("OvertakeSettings", _settings);
        }

        /// <summary>
        /// Clears all telemetry in memory and the UDP queue. Listener stays active.
        /// Call after export before the next session to avoid mixing two races in one capture.
        /// </summary>
        internal void BeginNewCaptureSession()
        {
            if (_receiver != null)
                _receiver.DrainPacketQueue();
            if (_store != null)
                _store.BeginNewCapture();
            _sessionType = "";
            _currentSessionTypeId = 0;
            _sessionEndDetected = false;
            _sessionEnded = false;
            _raceFinalClassificationReceived = false;
            _autoExportArmed = false;
            _raceSendAtMs = 0;
            _raceFcFirstMs = 0;
            _lastAutoExportMsg = "";
            global::SimHub.Logging.Current.Info("[Overtake] Capture cleared for new session (user)");
        }

        internal void RestartReceiver()
        {
            if (_receiver != null)
            {
                _receiver.Stop();
                _receiver.Dispose();
            }
            _store = new SessionStore();
            _receiver = new UdpReceiver();
            _receiver.Start(_settings.UdpPort, _settings.ForwardPort);
        }

        /// <summary>
        /// Finalizes the accumulated telemetry data and exports it as an encrypted .otk file.
        /// Returns the full path to the generated file, or an error message.
        /// </summary>
        internal string ExportLeagueJson(string outputDir)
        {
            try
            {
                if (_store.Sessions.Count == 0)
                    return "No session data to export.";

                Directory.CreateDirectory(outputDir);

                var payload = LeagueFinalizer.Finalize(_store);
                ExportNumbers.SanitizeForJson(payload);

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = serializer.Serialize(payload);

                string filename = BuildExportFilename();
                string otkPath = Path.Combine(outputDir, filename);

                OtkWriter.WriteOtk(json, otkPath);

                _lastExportPath = otkPath;
                global::SimHub.Logging.Current.Info(string.Format("[Overtake] Exported .otk to {0}", otkPath));
                return otkPath;
            }
            catch (Exception ex)
            {
                string msg = string.Format("Export failed: {0}", ex.Message);
                global::SimHub.Logging.Current.Error(string.Format("[Overtake] {0}", msg));
                return msg;
            }
        }

        private bool TryAutoExport()
        {
            try
            {
                string outputDir = _settings.OutputFolder;
                if (string.IsNullOrWhiteSpace(outputDir))
                {
                    outputDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Overtake", "exports");
                    _settings.OutputFolder = outputDir;
                }
                string path = ExportLeagueJson(outputDir);
                if (File.Exists(path))
                {
                    _lastExportPath = path;
                    _lastAutoExportMsg = string.Format("Auto-exported: {0}", System.IO.Path.GetFileName(path));
                    _settings.LastExportPath = path;
                    this.SaveCommonSettings("OvertakeSettings", _settings);
                    global::SimHub.Logging.Current.Info(string.Format("[Overtake] Auto-exported to {0}", path));
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Error(string.Format("[Overtake] Auto-export failed: {0}", ex.Message));
                return false;
            }
        }

        private int ActiveDriverCount()
        {
            Store.SessionRun latest = null;
            long latestTs = 0;
            foreach (var sess in _store.Sessions.Values)
            {
                if (sess.LastPacketMs >= latestTs)
                {
                    latestTs = sess.LastPacketMs;
                    latest = sess;
                }
            }
            if (latest == null) return 0;
            int count = 0;
            foreach (var kvp in latest.Drivers)
            {
                Packets.ParticipantEntry teamInfo;
                latest.TeamByCarIdx.TryGetValue(kvp.Value.CarIdx, out teamInfo);
                if (teamInfo != null && teamInfo.TeamId == 255) continue;
                if (kvp.Key.StartsWith("Driver_") || kvp.Key.StartsWith("Car_")) continue;
                count++;
            }
            return count;
        }

        private string BuildExportFilename()
        {
            // Use track name from the LATEST session (by LastPacketMs) to avoid stale data
            string trackName = "Unknown";
            long latestTs = 0;
            foreach (var sess in _store.Sessions.Values)
            {
                if (sess.TrackId.HasValue && sess.LastPacketMs >= latestTs)
                {
                    string tn;
                    if (Lookups.Tracks.TryGetValue((int)sess.TrackId.Value, out tn))
                    {
                        trackName = tn;
                        latestTs = sess.LastPacketMs;
                    }
                }
            }

            trackName = Regex.Replace(trackName, @"[^a-zA-Z0-9]", "");

            string dateTime = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            string uid;
            if (_store.SessionUid.HasValue)
                uid = _store.SessionUid.Value.ToString("X").PadLeft(8, '0');
            else
                uid = Guid.NewGuid().ToString("N").Substring(0, 8);
            string shortCode = uid.Length > 6 ? uid.Substring(uid.Length - 6) : uid;

            return string.Format("{0}_{1}_{2}.otk", trackName, dateTime, shortCode);
        }

        private void CheckForUpdates()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "OvertakeTelemetry/" + PluginVersion;
                    string json = client.DownloadString(UpdateJsonUrl);
                    var ser = new JavaScriptSerializer();
                    var data = ser.Deserialize<Dictionary<string, object>>(json);
                    object ver;
                    if (data.TryGetValue("version", out ver) && ver != null)
                        _latestVersion = ver.ToString();
                    object url;
                    if (data.TryGetValue("download", out url) && url != null)
                        _updateDownloadUrl = url.ToString();
                    object notes;
                    if (data.TryGetValue("releaseNotes", out notes) && notes != null)
                        _latestReleaseNotes = notes.ToString();
                    object minVer;
                    if (data.TryGetValue("minSupportedVersion", out minVer) && minVer != null)
                        _minSupportedVersion = minVer.ToString();
                    object installer;
                    if (data.TryGetValue("installerUrl", out installer) && installer != null)
                        _installerUrl = installer.ToString();
                    if (CurrentUpdateSeverity == UpdateSeverity.UpdateRequired)
                        global::SimHub.Logging.Current.Info(string.Format(
                            "[Overtake] UPDATE REQUIRED: running v{0} is below minimum supported v{1} (latest v{2}). Exports may be corrupted.",
                            PluginVersion, _minSupportedVersion, _latestVersion));
                    else if (UpdateAvailable)
                        global::SimHub.Logging.Current.Info(
                            string.Format("[Overtake] Update available: v{0} -> v{1}", PluginVersion, _latestVersion));
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Info(
                    string.Format("[Overtake] Update check skipped: {0}", ex.Message));
            }
        }

        /// <summary>
        /// A session is "terminal" (eligible to trigger auto-export) when it is the
        /// LAST race of the weekend.
        ///
        /// Lookups (Finalizer/Lookups.cs) maps session-type IDs to names per the
        /// official F1 25 UDP spec ("Session types" appendix):
        ///   10..14 -> Sprint Shootout (all variants)
        ///   15     -> Race (Main Race)
        ///   16     -> Race 2 (Sprint Race in Sprint Format weekends)
        ///   17     -> Race 3 (rarely seen)
        ///   18     -> Time Trial
        ///   19, 25, 26, 29, 30, 36 -> Race (observed in online lobbies / Career '25)
        /// Only "Race" qualifies as terminal -- so neither Sprint Shootout (10-14)
        /// nor Sprint Race / Race 2 (16) nor Race 3 (17) triggers auto-export
        /// prematurely. The consolidator now keeps the full SS + SQ + Sprint +
        /// Quali + Race Sprint Format weekend in a single .otk.
        ///
        /// v1.1.31: previous logic required raceCount >= 2 whenever a SprintShootout
        /// existed in the capture, which broke captures of "Sprint Format" lobbies that
        /// do NOT include a Sprint Race (e.g. Baku 2026-05-07: SS → OSQ → Race only).
        /// That caused auto-export to never fire for those weekends, which combined
        /// with no auto-rotation produced cross-event captures (Baku + Monaco issue).
        ///
        /// v1.1.45: F1 26 may report the Sprint Race as id=15 ("Race") instead of
        /// id=16 ("Race2"). When the capture already has a Sprint Shootout, defer
        /// terminal export until a Qualifying session (5..9) appears — see
        /// SprintFormatHelper.IsTerminalRaceClosing.
        /// </summary>
        private bool IsTerminalSession(byte id)
        {
            return SprintFormatHelper.IsTerminalRaceClosing(id, _store);
        }

        // Friendly name for the status panel and SimHub log. Must stay aligned
        // with Lookups.SessionType (Finalizer/Lookups.cs). Source of truth is
        // the official F1 25 UDP spec "Session types" appendix.
        // v1.1.38: fixed Sprint Shootout / Race / TimeTrial ID assignments.
        private static string SessionTypeName(byte id)
        {
            switch (id)
            {
                case 0: return "Unknown";
                case 1: return "Practice 1";
                case 2: return "Practice 2";
                case 3: return "Practice 3";
                case 4: return "Short Practice";
                case 5: return "Qualifying 1";
                case 6: return "Qualifying 2";
                case 7: return "Qualifying 3";
                case 8: return "Short Qualifying";
                case 9: return "One-Shot Qualifying";
                case 10: return "Sprint Shootout 1";
                case 11: return "Sprint Shootout 2";
                case 12: return "Sprint Shootout 3";
                case 13: return "Short Sprint Shootout";
                case 14: return "One-Shot Sprint Shootout";
                case 15: return "Race";
                case 16: return "Race 2";
                case 17: return "Race 3";
                case 18: return "Time Trial";
                default: return string.Format("Session ({0})", id);
            }
        }
    }
}
