namespace Overtake.SimHub.Plugin
{
    public class OvertakeSettings
    {
        public int UdpPort;
        public int ForwardPort;
        public string OutputFolder;
        public bool AutoExportJson;

        // Camada 2 (v1.1.31): when true, the SessionStore is wiped right after a successful
        // auto-export so the next race starts in a clean capture. Pair with AutoExportJson.
        // Stays opt-out (defaults to true) — narrators/streamers benefit the most because they
        // rarely click "Nova sessão" between back-to-back events. Power users who export
        // manually mid-event can disable it.
        public bool AutoCleanAfterExport;

        public string LastExportPath;

        // Settings schema version. Used by OvertakePlugin.Init to migrate saved settings
        // when new fields are introduced (so missing-from-save fields like AutoCleanAfterExport
        // get the intended default, not C# zero-value).
        // 0 = pre-v1.1.31, 1 = v1.1.31 (added AutoCleanAfterExport),
        // 2 = test build (added Race UI web server settings),
        // 3 = test build (added live cloud-broadcast settings).
        public int SettingsSchemaVersion;

        // Race UI (web) — Rota B. Local web server + WebSocket serving the live
        // broadcast page. Read-only over the capture; does not affect .otk export.
        public bool RaceUiEnabled;
        public int RaceUiPort;
        public bool RaceUiAllowLan;

        // Live cloud broadcast (overtakef1) — sends the same read-only snapshot to the
        // portal via the live-ingest Edge Function. Token is generated in the portal
        // (Perfil > Transmissao / SimHub). BaseUrl empty = use the built-in default.
        // Does not touch the .otk pipeline.
        public string LiveBroadcastToken;
        public string LiveBroadcastBaseUrl;

        public OvertakeSettings()
        {
            UdpPort = 20778;
            ForwardPort = 20777;
            OutputFolder = "";
            AutoExportJson = true;
            AutoCleanAfterExport = true;
            LastExportPath = "";
            SettingsSchemaVersion = 3;
            RaceUiEnabled = true;
            RaceUiPort = 8088;
            RaceUiAllowLan = false;
            LiveBroadcastToken = "";
            LiveBroadcastBaseUrl = "";
        }
    }
}
