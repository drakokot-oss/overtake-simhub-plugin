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
using Overtake.SimHub.Plugin.Packets;
using Overtake.SimHub.Plugin.Parsers;
using Overtake.SimHub.Plugin.Store;
using Overtake.SimHub.Plugin.UI;

namespace Overtake.SimHub.Plugin
{
    [PluginDescription("Receives F1 25 UDP telemetry for the Overtake platform")]
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
        private string _latestVersion = "";
        private string _updateDownloadUrl = "";

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

            _store = new SessionStore();

            _receiver = new UdpReceiver();
            _receiver.Start(_settings.UdpPort, _settings.ForwardPort);

            this.AttachDelegate("Overtake.Status", () => _receiver.Status);
            this.AttachDelegate("Overtake.PacketsReceived", () => _displayedPackets);
            this.AttachDelegate("Overtake.SessionType", () => _sessionType);
            this.AttachDelegate("Overtake.ActiveDrivers", () => ActiveDriverCount());
            this.AttachDelegate("Overtake.SessionsCount", () => _store.Sessions.Count);

            global::SimHub.Logging.Current.Info("[Overtake] Plugin initialized");

            System.Threading.Tasks.Task.Run(() => CheckForUpdates());
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            byte[] raw;
            while (_receiver.PacketQueue.TryDequeue(out raw))
            {
                _displayedPackets++;

                var parsed = PacketParser.Dispatch(raw);
                if (parsed == null) continue;

                if (parsed.Session != null)
                {
                    _sessionType = SessionTypeName(parsed.Session.SessionType);
                    _currentSessionTypeId = parsed.Session.SessionType;
                }

                _store.Ingest(parsed);

                if (parsed.Event != null && parsed.Event.Code == "SEND")
                {
                    _sessionEnded = true;
                    if (IsTerminalSession(_currentSessionTypeId))
                        _sessionEndDetected = true;
                }
                if (parsed.Event != null && parsed.Event.Code == "SSTA")
                    _sessionEnded = false;
            }

            if (_sessionEndDetected && _settings.AutoExportJson && _store.Sessions.Count > 0)
            {
                _sessionEndDetected = false;
                TryAutoExport();
            }
        }

        public void End(PluginManager pluginManager)
        {
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

        internal void SaveSettings()
        {
            this.SaveCommonSettings("OvertakeSettings", _settings);
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
        /// Finalizes the accumulated telemetry data and exports it as a JSON file.
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

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = serializer.Serialize(payload);

                string filename = BuildExportFilename();
                string path = Path.Combine(outputDir, filename);
                File.WriteAllText(path, json, System.Text.Encoding.UTF8);

                _lastExportPath = path;
                global::SimHub.Logging.Current.Info(string.Format("[Overtake] Exported league JSON to {0}", path));
                return path;
            }
            catch (Exception ex)
            {
                string msg = string.Format("Export failed: {0}", ex.Message);
                global::SimHub.Logging.Current.Error(string.Format("[Overtake] {0}", msg));
                return msg;
            }
        }

        private void TryAutoExport()
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
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Error(string.Format("[Overtake] Auto-export failed: {0}", ex.Message));
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
            return latest != null ? latest.Drivers.Count : 0;
        }

        private string BuildExportFilename()
        {
            string trackName = "Unknown";
            foreach (var sess in _store.Sessions.Values)
            {
                if (sess.TrackId.HasValue)
                {
                    string tn;
                    if (Lookups.Tracks.TryGetValue((int)sess.TrackId.Value, out tn))
                        trackName = tn;
                    break;
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

            return string.Format("{0}_{1}_{2}.json", trackName, dateTime, shortCode);
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
                    if (UpdateAvailable)
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

        private static bool IsTerminalSession(byte id)
        {
            string name;
            if (Finalizer.Lookups.SessionType.TryGetValue(id, out name))
                return name == "Race" || name == "Race2" || name == "Sprint";
            return false;
        }

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
                case 10: return "Race";
                case 11: return "Race 2";
                case 12: return "Race 3";
                case 13: return "Time Trial";
                default: return string.Format("Session ({0})", id);
            }
        }
    }
}
