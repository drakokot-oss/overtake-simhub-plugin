using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Overtake.SimHub.Plugin.Live
{
    public class EligibleRace { public string RaceId; public string Name; public string Track; public string Status; }
    public class ScheduledRace { public string Id; public string Name; public string Track; public string RaceDate; public string ScheduledTime; }
    public class EligibleGrid { public string GridId; public string GridName; public List<EligibleRace> Races = new List<EligibleRace>(); }
    public class EligibleLeague { public string LeagueId; public string LeagueName; public List<EligibleGrid> Grids = new List<EligibleGrid>(); }

    /// <summary>
    /// Cloud live-broadcast client. Streams the (read-only) live snapshot to the
    /// overtakef1 portal via the live-start / live-ingest Edge Functions. Fully
    /// independent of the .otk export pipeline.
    ///
    /// Contract: §7 of docs/LIVE-AO-VIVO-IMPLEMENTACAO.md in f1-race-hub.
    ///  - apikey header = public anon key (required by the Supabase gateway).
    ///  - credential = the broadcast token (otklive_…), sent in the JSON body.
    ///  - live-start mode=list -> eligible leagues/grids.
    ///  - live-start (leagueId/gridId + raceId) -> { liveSessionId, channel }. Always an EXISTING race.
    ///  - GET /rest/v1/races?grid_id=eq..&status=eq.scheduled -> scheduled races (public read).
    ///  - POST /rest/v1/rpc/plugin_create_scheduled_race -> creates a scheduled race (option B).
    ///  - live-ingest { token, liveSessionId, snapshot, ended } at ~2-3 Hz; ended=true closes.
    /// </summary>
    public class LiveBroadcaster
    {
        public const string DefaultBaseUrl = "https://zjmnwmfbvqrzayousfxr.supabase.co/functions/v1/";
        // Public anon key (same VITE_SUPABASE_PUBLISHABLE_KEY the site ships in its bundle).
        public const string DefaultAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InpqbW53bWZidnFyemF5b3VzZnhyIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODAyMzY3NDgsImV4cCI6MjA5NTgxMjc0OH0.WUwaqfhfBvjXq7nPxLM8yEwaSHBOXacafOsrw98O6iA";

        public string BaseUrl = DefaultBaseUrl;
        public string AnonKey = DefaultAnonKey;
        public string Token = "";

        public bool Active { get; private set; }
        public string LiveSessionId { get; private set; }
        public string BoundRaceId { get; private set; }
        public string BoundLeagueId { get; private set; }
        public string BoundGridId { get; private set; }
        public string LastError { get; private set; }
        public long LastPushMs { get; private set; }

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private int _pushBusy; // single-in-flight guard (Interlocked) for streaming backpressure
        private int _consecutiveFails; // circuit-breaker: falhas seguidas no push
        private long _backoffUntilMs;  // até quando pausar o push (backoff exponencial)

        public LiveBroadcaster()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        // Project origin (https://<ref>.supabase.co) derived from BaseUrl, so we can hit
        // both /functions/v1/ (edge functions) and /rest/v1/ (PostgREST + rpc).
        private string Origin()
        {
            string b = string.IsNullOrEmpty(BaseUrl) ? DefaultBaseUrl : BaseUrl;
            try { return new Uri(b).GetLeftPart(UriPartial.Authority); }
            catch { return "https://zjmnwmfbvqrzayousfxr.supabase.co"; }
        }
        private string FnEndpoint(string fn) { return Origin() + "/functions/v1/" + fn; }
        private string RestEndpoint(string path) { return Origin() + "/rest/v1/" + path; }

        // Synchronous request. Returns response body; throws on HTTP error (caller maps it).
        private string Send(string method, string url, string bodyJson, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Headers["apikey"] = AnonKey ?? "";
            req.Headers["Authorization"] = "Bearer " + (AnonKey ?? "");
            req.UserAgent = "OvertakeTelemetry/" + OvertakePlugin.PluginVersion;
            if (method != "GET" && bodyJson != null)
            {
                req.ContentType = "application/json";
                byte[] payload = Encoding.UTF8.GetBytes(bodyJson);
                req.ContentLength = payload.Length;
                using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var rd = new StreamReader(rs, Encoding.UTF8))
                return rd.ReadToEnd();
        }

        private string Post(string fn, string bodyJson, int timeoutMs)
        { return Send("POST", FnEndpoint(fn), bodyJson, timeoutMs); }

        // Minimal JSON string escaping (token/ids/names embedded by hand).
        private static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string S(Dictionary<string, object> d, string k)
        { object v; return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : null; }

        /// <summary>live-start mode=list. Returns null on error (see LastError).</summary>
        public List<EligibleLeague> ListEligible()
        {
            LastError = null;
            try
            {
                string body = "{\"token\":" + J(Token) + ",\"mode\":\"list\"}";
                string resp = Post("live-start", body, 15000);
                var root = _json.DeserializeObject(resp) as Dictionary<string, object>;
                var list = new List<EligibleLeague>();
                var eligible = root != null && root.ContainsKey("eligible") ? root["eligible"] as object[] : null;
                if (eligible == null) return list;
                foreach (var le in eligible)
                {
                    var lm = le as Dictionary<string, object>; if (lm == null) continue;
                    var L = new EligibleLeague { LeagueId = S(lm, "leagueId"), LeagueName = S(lm, "leagueName") };
                    var grids = lm.ContainsKey("grids") ? lm["grids"] as object[] : null;
                    if (grids != null) foreach (var ge in grids)
                    {
                        var gm = ge as Dictionary<string, object>; if (gm == null) continue;
                        var G = new EligibleGrid { GridId = S(gm, "gridId"), GridName = S(gm, "gridName") };
                        var races = gm.ContainsKey("races") ? gm["races"] as object[] : null;
                        if (races != null) foreach (var re in races)
                        {
                            var rm = re as Dictionary<string, object>; if (rm == null) continue;
                            G.Races.Add(new EligibleRace { RaceId = S(rm, "raceId"), Name = S(rm, "name"), Track = S(rm, "track"), Status = S(rm, "status") });
                        }
                        L.Grids.Add(G);
                    }
                    list.Add(L);
                }
                return list;
            }
            catch (WebException wex) { LastError = Describe(wex); return null; }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        /// <summary>
        /// Go live on an EXISTING race (raceId required — no more freeform createRace).
        /// Use ListScheduledRaces / CreateScheduledRace to obtain the raceId first.
        /// Returns false on error (see LastError).
        /// </summary>
        public bool GoLive(string leagueId, string gridId, string raceId, string sessionType)
        {
            LastError = null;
            if (string.IsNullOrEmpty(Token)) { LastError = "token vazio"; return false; }
            if (string.IsNullOrEmpty(leagueId) || string.IsNullOrEmpty(gridId)) { LastError = "selecione liga e grid"; return false; }
            if (string.IsNullOrEmpty(raceId)) { LastError = "selecione (ou crie) uma corrida"; return false; }
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"token\":").Append(J(Token));
                sb.Append(",\"leagueId\":").Append(J(leagueId));
                sb.Append(",\"gridId\":").Append(J(gridId));
                sb.Append(",\"raceId\":").Append(J(raceId));
                sb.Append(",\"sessionType\":").Append(J(string.IsNullOrEmpty(sessionType) ? "race" : sessionType));
                sb.Append('}');
                string resp = Post("live-start", sb.ToString(), 15000);
                var root = _json.DeserializeObject(resp) as Dictionary<string, object>;
                string sid = S(root, "liveSessionId");
                if (string.IsNullOrEmpty(sid)) { LastError = "resposta sem liveSessionId"; return false; }
                LiveSessionId = sid;
                BoundRaceId = raceId;
                BoundLeagueId = leagueId;
                BoundGridId = gridId;
                Active = true;
                return true;
            }
            catch (WebException wex) { LastError = Describe(wex); return false; }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        /// <summary>
        /// List the grid's SCHEDULED races (public read via PostgREST). Returns null on error.
        /// Contract 1: GET /rest/v1/races?grid_id=eq.{gridId}&amp;status=eq.scheduled&amp;select=...&amp;order=race_date.asc
        /// </summary>
        public List<ScheduledRace> ListScheduledRaces(string gridId)
        {
            LastError = null;
            if (string.IsNullOrEmpty(gridId)) { LastError = "grid vazio"; return null; }
            try
            {
                string url = RestEndpoint("races?grid_id=eq." + Uri.EscapeDataString(gridId)
                    + "&status=eq.scheduled&select=id,name,track,race_date,scheduled_time&order=race_date.asc");
                string resp = Send("GET", url, null, 15000);
                var arr = _json.DeserializeObject(resp) as object[];
                var list = new List<ScheduledRace>();
                if (arr == null) return list;
                foreach (var it in arr)
                {
                    var m = it as Dictionary<string, object>; if (m == null) continue;
                    list.Add(new ScheduledRace
                    {
                        Id = S(m, "id"),
                        Name = S(m, "name"),
                        Track = S(m, "track"),
                        RaceDate = S(m, "race_date"),
                        ScheduledTime = S(m, "scheduled_time")
                    });
                }
                return list;
            }
            catch (WebException wex) { LastError = Describe(wex); return null; }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        /// <summary>
        /// Create a new scheduled race via the server RPC (option B). Returns the new raceId,
        /// or null on error. Contract 3: POST /rest/v1/rpc/plugin_create_scheduled_race
        /// body { _token, _grid_id, _track, _race_date (ISO date), _scheduled_time "HH:MM" }.
        /// NOTE: the RPC is provided by the site; until it exists this returns null with a 404.
        /// </summary>
        public string CreateScheduledRace(string gridId, string track, string raceDateIso, string timeHHMM)
        {
            LastError = null;
            if (string.IsNullOrEmpty(Token)) { LastError = "token vazio"; return null; }
            if (string.IsNullOrEmpty(gridId)) { LastError = "grid vazio"; return null; }
            if (string.IsNullOrEmpty(track)) { LastError = "selecione a pista"; return null; }
            if (string.IsNullOrEmpty(raceDateIso)) { LastError = "informe a data"; return null; }
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"_token\":").Append(J(Token));
                sb.Append(",\"_grid_id\":").Append(J(gridId));
                sb.Append(",\"_track\":").Append(J(track));
                sb.Append(",\"_race_date\":").Append(J(raceDateIso));
                sb.Append(",\"_scheduled_time\":").Append(J(string.IsNullOrEmpty(timeHHMM) ? null : timeHHMM));
                sb.Append('}');
                string resp = Send("POST", RestEndpoint("rpc/plugin_create_scheduled_race"), sb.ToString(), 15000);
                // RPC may return {"race_id":"uuid"}, a bare "uuid", or [{"race_id":"uuid"}].
                var obj = _json.DeserializeObject(resp);
                string id = ExtractRaceId(obj);
                if (string.IsNullOrEmpty(id)) { LastError = "resposta sem race_id"; return null; }
                return id;
            }
            catch (WebException wex) { LastError = Describe(wex); return null; }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        private static string ExtractRaceId(object obj)
        {
            var d = obj as Dictionary<string, object>;
            if (d != null) return S(d, "race_id") ?? S(d, "id");
            var arr = obj as object[];
            if (arr != null && arr.Length > 0) return ExtractRaceId(arr[0]);
            return obj as string;
        }

        /// <summary>Canonical track names — MUST match the site list exactly (tracksMatch).</summary>
        public static readonly string[] CanonicalTracks = new[]
        {
            "Bahrain International Circuit", "Jeddah Corniche Circuit", "Albert Park Circuit",
            "Suzuka International Racing Course", "Shanghai International Circuit", "Miami International Autodrome",
            "Autodromo Enzo e Dino Ferrari", "Circuit de Monaco", "Circuit de Barcelona-Catalunya", "Madring (Madrid)",
            "Circuit Gilles Villeneuve", "Red Bull Ring", "Red Bull Ring Reverse", "Silverstone Circuit", "Silverstone Reverse",
            "Hungaroring", "Circuit de Spa-Francorchamps", "Circuit Zandvoort", "Zandvoort Reverse", "Autodromo Nazionale Monza",
            "Marina Bay Street Circuit", "Lusail International Circuit", "Circuit of the Americas",
            "Autodromo Hermanos Rodriguez", "Interlagos", "Las Vegas Strip Circuit", "Yas Marina Circuit"
        };

        /// <summary>
        /// Fire-and-forget snapshot push with single-in-flight backpressure: if the previous
        /// POST is still in flight, this tick is dropped (keeps the data thread non-blocking).
        /// </summary>
        public void PushSnapshot(string snapshotJson)
        {
            if (!Active || string.IsNullOrEmpty(LiveSessionId) || string.IsNullOrEmpty(snapshotJson)) return;
            // Backoff após falhas transientes: não martelar o servidor (evita thundering-herd
            // quando o backend dá um soluço — o 500 transiente que já observamos).
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _backoffUntilMs) return;
            if (Interlocked.CompareExchange(ref _pushBusy, 1, 0) != 0) return;
            string body = "{\"token\":" + J(Token) + ",\"liveSessionId\":" + J(LiveSessionId)
                + ",\"snapshot\":" + snapshotJson + ",\"ended\":false}";
            Task.Run(() =>
            {
                try
                {
                    Post("live-ingest", body, 8000);
                    LastError = null;
                    LastPushMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _consecutiveFails = 0;
                    _backoffUntilMs = 0;
                }
                catch (WebException wex) { LastError = Describe(wex); HandlePushFailure(wex); }
                catch (Exception ex) { LastError = ex.Message; HandlePushFailure(null); }
                finally { Interlocked.Exchange(ref _pushBusy, 0); }
            });
        }

        /// <summary>
        /// Circuit-breaker do stream: em erro de AUTH (401/403/404) para de vez (token revogado /
        /// sem permissão / sessão sumiu — re-tentar não adianta). Em erro transiente (5xx/timeout)
        /// aplica backoff exponencial (400ms→…→10s) e desiste após ~8 falhas seguidas. Evita que
        /// um broadcaster problemático fique martelando o edge indefinidamente.
        /// </summary>
        private void HandlePushFailure(WebException wex)
        {
            int code = 0;
            if (wex != null) { var r = wex.Response as HttpWebResponse; if (r != null) code = (int)r.StatusCode; }
            if (code == 401 || code == 403 || code == 404) { Active = false; return; }
            _consecutiveFails++;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long delay = (long)Math.Min(10000, 400 * Math.Pow(2, Math.Min(_consecutiveFails, 6)));
            _backoffUntilMs = now + delay;
            if (_consecutiveFails >= 8) Active = false;
        }

        /// <summary>
        /// Auto-upload do .otk pós-live: envia os bytes do arquivo gerado pro servidor, que faz
        /// STAGING do resultado na corrida vinculada (aguardando confirmação do admin). Retorna
        /// true no sucesso. Contract: POST /functions/v1/live-otk-ingest { token, liveSessionId, otk(base64) }.
        /// </summary>
        public bool PostOtk(string liveSessionId, byte[] otkBytes)
        {
            LastError = null;
            if (string.IsNullOrEmpty(Token)) { LastError = "token vazio"; return false; }
            if (string.IsNullOrEmpty(liveSessionId) || otkBytes == null || otkBytes.Length == 0) { LastError = "otk/sessao vazios"; return false; }
            try
            {
                string b64 = Convert.ToBase64String(otkBytes);
                var sb = new StringBuilder();
                sb.Append("{\"token\":").Append(J(Token));
                sb.Append(",\"liveSessionId\":").Append(J(liveSessionId));
                sb.Append(",\"otk\":").Append(J(b64));
                sb.Append('}');
                Post("live-otk-ingest", sb.ToString(), 30000); // OTK ~100-200KB em base64 → timeout maior
                return true;
            }
            catch (WebException wex) { LastError = Describe(wex); return false; }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        /// <summary>Final ingest with ended=true (freezes the result on the portal), then go idle.</summary>
        public void End(string snapshotJson)
        {
            if (!Active || string.IsNullOrEmpty(LiveSessionId)) { Active = false; LiveSessionId = null; return; }
            string snap = string.IsNullOrEmpty(snapshotJson) ? "null" : snapshotJson;
            string body = "{\"token\":" + J(Token) + ",\"liveSessionId\":" + J(LiveSessionId)
                + ",\"snapshot\":" + snap + ",\"ended\":true}";
            try { Post("live-ingest", body, 8000); }
            catch (WebException wex) { LastError = Describe(wex); }
            catch (Exception ex) { LastError = ex.Message; }
            Active = false;
            LiveSessionId = null;
        }

        private static string Describe(WebException wex)
        {
            try
            {
                var r = wex.Response as HttpWebResponse;
                if (r != null)
                {
                    int code = (int)r.StatusCode;
                    if (code == 401) return "401 - token invalido/revogado (ou apikey)";
                    if (code == 403) return "403 - sem permissao para transmitir, ou liga sem live_enabled";
                    if (code == 400) return "400 - requisicao invalida";
                    if (code == 404) return "404 - sessao nao encontrada";
                    return code + " - erro do servidor";
                }
            }
            catch { }
            return wex.Message;
        }
    }
}
