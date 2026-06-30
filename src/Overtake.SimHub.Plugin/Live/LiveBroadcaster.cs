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
    ///  - live-start mode=list -> eligible leagues/grids/races.
    ///  - live-start (leagueId/gridId + raceId|createRace) -> { liveSessionId, channel }.
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
        public string LastError { get; private set; }
        public long LastPushMs { get; private set; }

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private int _pushBusy; // single-in-flight guard (Interlocked) for streaming backpressure

        public LiveBroadcaster()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        private string Endpoint(string fn)
        {
            string b = string.IsNullOrEmpty(BaseUrl) ? DefaultBaseUrl : BaseUrl;
            if (!b.EndsWith("/")) b += "/";
            return b + fn;
        }

        // Synchronous POST. Returns response body; throws on HTTP error (caller maps to LastError).
        private string Post(string fn, string bodyJson, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(Endpoint(fn));
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Headers["apikey"] = AnonKey ?? "";
            req.Headers["Authorization"] = "Bearer " + (AnonKey ?? "");
            req.UserAgent = "OvertakeTelemetry/" + OvertakePlugin.PluginVersion;
            byte[] payload = Encoding.UTF8.GetBytes(bodyJson ?? "");
            req.ContentLength = payload.Length;
            using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var rd = new StreamReader(rs, Encoding.UTF8))
                return rd.ReadToEnd();
        }

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
        /// Go live. Pass raceId for an existing race, OR newRaceName/newRaceTrack to create
        /// one on the fly (leave raceId null/empty). Returns false on error (see LastError).
        /// </summary>
        public bool GoLive(string leagueId, string gridId, string raceId, string newRaceName, string newRaceTrack, string sessionType)
        {
            LastError = null;
            if (string.IsNullOrEmpty(Token)) { LastError = "token vazio"; return false; }
            if (string.IsNullOrEmpty(leagueId) || string.IsNullOrEmpty(gridId)) { LastError = "selecione liga e grid"; return false; }
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"token\":").Append(J(Token));
                sb.Append(",\"leagueId\":").Append(J(leagueId));
                sb.Append(",\"gridId\":").Append(J(gridId));
                if (!string.IsNullOrEmpty(raceId))
                    sb.Append(",\"raceId\":").Append(J(raceId));
                else
                    sb.Append(",\"createRace\":{\"name\":").Append(J(newRaceName))
                      .Append(",\"track\":").Append(J(newRaceTrack)).Append(",\"scheduledTime\":null}");
                sb.Append(",\"sessionType\":").Append(J(string.IsNullOrEmpty(sessionType) ? "race" : sessionType));
                sb.Append('}');
                string resp = Post("live-start", sb.ToString(), 15000);
                var root = _json.DeserializeObject(resp) as Dictionary<string, object>;
                string sid = S(root, "liveSessionId");
                if (string.IsNullOrEmpty(sid)) { LastError = "resposta sem liveSessionId"; return false; }
                LiveSessionId = sid;
                Active = true;
                return true;
            }
            catch (WebException wex) { LastError = Describe(wex); return false; }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        /// <summary>
        /// Fire-and-forget snapshot push with single-in-flight backpressure: if the previous
        /// POST is still in flight, this tick is dropped (keeps the data thread non-blocking).
        /// </summary>
        public void PushSnapshot(string snapshotJson)
        {
            if (!Active || string.IsNullOrEmpty(LiveSessionId) || string.IsNullOrEmpty(snapshotJson)) return;
            if (Interlocked.CompareExchange(ref _pushBusy, 1, 0) != 0) return;
            string body = "{\"token\":" + J(Token) + ",\"liveSessionId\":" + J(LiveSessionId)
                + ",\"snapshot\":" + snapshotJson + ",\"ended\":false}";
            Task.Run(() =>
            {
                try { Post("live-ingest", body, 8000); LastError = null; LastPushMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
                catch (WebException wex) { LastError = Describe(wex); }
                catch (Exception ex) { LastError = ex.Message; }
                finally { Interlocked.Exchange(ref _pushBusy, 0); }
            });
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
