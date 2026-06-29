using System;
using System.Collections.Generic;
using Overtake.SimHub.Plugin.Finalizer;
using Overtake.SimHub.Plugin.Packets;
using Overtake.SimHub.Plugin.Store;

namespace Overtake.SimHub.Plugin.Live
{
    /// <summary>
    /// Builds a read-only live snapshot of the current session for the broadcast UI.
    /// Reads the same <see cref="SessionStore"/> used by the .otk export pipeline but
    /// never mutates it. Output is a plain dictionary tree, serialized to JSON and
    /// pushed over WebSocket by <see cref="RaceWebServer"/>.
    ///
    /// This component is additive: the .otk export path is untouched.
    /// </summary>
    internal static class LiveSnapshotBuilder
    {
        public static Dictionary<string, object> Build(SessionStore store)
        {
            var root = new Dictionary<string, object>();
            if (store == null) { root["ok"] = false; return root; }

            SessionRun sess = LatestSession(store);
            if (sess == null) { root["ok"] = false; return root; }

            int sessionTypeId = sess.SessionType ?? 0;
            bool isQualy = IsQualyLike(sessionTypeId);

            int currentLap = 0;
            foreach (var d in sess.Drivers.Values)
                if (d.LastCurrentLapNum.HasValue && d.LastCurrentLapNum.Value > currentLap)
                    currentLap = d.LastCurrentLapNum.Value;

            string trackName = "Unknown";
            if (sess.TrackId.HasValue)
                Lookups.Tracks.TryGetValue((int)sess.TrackId.Value, out trackName);

            int rainNext = -1;
            if (sess.WeatherForecast != null && sess.WeatherForecast.Count > 0)
            {
                object rp;
                if (sess.WeatherForecast[0].TryGetValue("rainPercentage", out rp) && rp != null)
                    int.TryParse(rp.ToString(), out rainNext);
            }

            root["ok"] = true;
            root["tsMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            root["session"] = new Dictionary<string, object>
            {
                { "game", GameLabel(sess) },
                { "trackId", sess.TrackId },
                { "trackName", trackName },
                { "sessionTypeId", sessionTypeId },
                { "sessionTypeName", SessionTypeName((byte)sessionTypeId) },
                { "mode", isQualy ? "qualy" : "race" },
                { "totalLaps", sess.TotalLaps },
                { "currentLap", currentLap },
                { "timeLeftSec", sess.SessionTimeLeftSec },
                { "weather", sess.Weather },
                { "trackTempC", sess.LatestTrackTempC },
                { "airTempC", sess.LatestAirTempC },
                { "safetyCar", sess.SafetyCarStatus },
                { "rainNextPct", rainNext >= 0 ? (object)rainNext : null },
                { "networkGame", sess.NetworkGame },
                { "numDrivers", sess.Drivers.Count },
            };

            var grid = new List<Dictionary<string, object>>();
            foreach (var kvp in sess.Drivers)
            {
                var d = kvp.Value;
                if (d == null || string.IsNullOrEmpty(d.Tag)) continue;

                ParticipantEntry teamInfo;
                sess.TeamByCarIdx.TryGetValue(d.CarIdx, out teamInfo);

                // Skip AI filler placeholders with no race presence.
                if (teamInfo != null && teamInfo.TeamId == 255
                    && d.CarPosition == 0 && (d.LastCurrentLapNum ?? 0) == 0)
                    continue;

                int bestMs = BestLapMs(d);
                int s1, s2, s3;
                BestSectors(d, out s1, out s2, out s3);

                grid.Add(new Dictionary<string, object>
                {
                    { "carIdx", d.CarIdx },
                    { "pos", (int)d.CarPosition },
                    { "grid", (int)d.GridPosition },
                    { "tag", d.Tag },
                    { "teamId", teamInfo != null ? (object)teamInfo.TeamId : null },
                    { "team", ResolveTeamName(teamInfo) },
                    { "lapsDone", d.LastCurrentLapNum ?? 0 },
                    { "lastLapMs", d.LastSeenLapTimeMs },
                    { "bestLapMs", bestMs },
                    { "curLapMs", d.LiveCurrentLapTimeMs },
                    { "sector", (int)d.LiveSector + 1 },
                    { "s1", s1 }, { "s2", s2 }, { "s3", s3 },
                    { "intervalMs", d.LiveDeltaToCarFrontMs },
                    { "gapMs", d.LiveDeltaToLeaderMs },
                    { "compound", VisualCompoundCode(d.VisualTyreCompound) },
                    { "tyreAge", (int)d.TyresAgeLaps },
                    { "tyreWear", TyreWear(d) },
                    { "damage", Damage(d) },
                    { "stops", d.LastNumPitStops ?? d.PitStops.Count },
                    { "ersPct", (int)Math.Round(d.ErsStorePctLast) },
                    { "ersMode", ErsModeName(d.ErsDeployModeLast) },
                    { "fuelKg", Round1(d.FuelInTankLast) },
                    { "fuelLaps", Round1(d.FuelRemainingLapsLast) },
                    { "penaltiesSec", (int)d.LivePenaltiesSec },
                    { "penaltiesCount", DedupPenaltyCount(d) },
                    { "warnings", d.LastTotalWarnings },
                    { "cornerCutWarnings", d.LastCornerCuttingWarnings },
                    { "pitStatus", (int)d.LivePitStatus },
                    { "status", StatusStr(d) },
                    { "laps", LapList(d) },
                });
            }
            grid.Sort((a, b) =>
            {
                int pa = (int)a["pos"]; if (pa == 0) pa = 999;
                int pb = (int)b["pos"]; if (pb == 0) pb = 999;
                return pa.CompareTo(pb);
            });
            root["grid"] = grid;

            var events = new List<Dictionary<string, object>>();
            int from = Math.Max(0, sess.Events.Count - 25);
            for (int i = sess.Events.Count - 1; i >= from; i--)
                events.Add(sess.Events[i]);
            root["events"] = events;

            return root;
        }

        private static SessionRun LatestSession(SessionStore store)
        {
            SessionRun latest = null;
            long latestTs = -1;
            foreach (var sess in store.Sessions.Values)
            {
                if (sess.LastPacketMs >= latestTs)
                {
                    latestTs = sess.LastPacketMs;
                    latest = sess;
                }
            }
            return latest;
        }

        private static bool IsQualyLike(int id)
        {
            // Race ids (rolling order board): main/sprint races + observed online race ids.
            switch (id)
            {
                case 15: case 16: case 17:
                case 19: case 25: case 26: case 29: case 30: case 36:
                    return false;
                default:
                    return true; // practice / quali / shootout / time trial -> sector board
            }
        }

        private static int BestLapMs(DriverRun d)
        {
            int best = 0;
            for (int i = 0; i < d.Laps.Count; i++)
            {
                int t = d.Laps[i].LapTimeMs;
                if (t > 0 && (best == 0 || t < best)) best = t;
            }
            return best;
        }

        private static List<Dictionary<string, object>> LapList(DriverRun d)
        {
            var list = new List<Dictionary<string, object>>();
            // Sorted by lap number ascending for a clean per-driver history.
            var laps = new List<LapRecord>(d.Laps);
            laps.Sort((a, b) => a.LapNumber.CompareTo(b.LapNumber));
            foreach (var lr in laps)
            {
                if (lr.LapTimeMs <= 0) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "n", lr.LapNumber },
                    { "ms", lr.LapTimeMs },
                    { "s1", lr.Sector1Ms },
                    { "s2", lr.Sector2Ms },
                    { "s3", lr.Sector3Ms },
                });
            }
            return list;
        }

        private static void BestSectors(DriverRun d, out int s1, out int s2, out int s3)
        {
            s1 = s2 = s3 = 0;
            int best = 0;
            LapRecord bestLap = null;
            for (int i = 0; i < d.Laps.Count; i++)
            {
                int t = d.Laps[i].LapTimeMs;
                if (t > 0 && (best == 0 || t < best)) { best = t; bestLap = d.Laps[i]; }
            }
            if (bestLap != null) { s1 = bestLap.Sector1Ms; s2 = bestLap.Sector2Ms; s3 = bestLap.Sector3Ms; }
        }

        private static Dictionary<string, object> TyreWear(DriverRun d)
        {
            var tw = d.LatestTyreWear;
            if (tw == null)
                return new Dictionary<string, object> { { "avg", 0 }, { "fl", 0 }, { "fr", 0 }, { "rl", 0 }, { "rr", 0 } };
            double avg = (tw.FL + tw.FR + tw.RL + tw.RR) / 4.0;
            return new Dictionary<string, object>
            {
                { "avg", Math.Round(avg, 1) },
                { "fl", Math.Round((double)tw.FL, 1) },
                { "fr", Math.Round((double)tw.FR, 1) },
                { "rl", Math.Round((double)tw.RL, 1) },
                { "rr", Math.Round((double)tw.RR, 1) },
            };
        }

        private static Dictionary<string, object> Damage(DriverRun d)
        {
            var dmg = d.LatestDamage;
            if (dmg == null)
                return new Dictionary<string, object> { { "wingFL", 0 }, { "wingFR", 0 }, { "wingRear", 0 }, { "worst", 0 } };
            int worst = Math.Max(Math.Max(dmg.WingFrontLeft, dmg.WingFrontRight), dmg.WingRear);
            return new Dictionary<string, object>
            {
                { "wingFL", dmg.WingFrontLeft },
                { "wingFR", dmg.WingFrontRight },
                { "wingRear", dmg.WingRear },
                { "worst", worst },
            };
        }

        // Live penalty COUNT with reconnect-replay dedup (see backlog v1.1.48):
        // collapse events sharing logical identity (penaltyType+infringement+lap+sec).
        private static int DedupPenaltyCount(DriverRun d)
        {
            var seen = new HashSet<string>();
            int count = 0;
            foreach (var ps in d.PenaltySnapshots)
            {
                if (!ps.PenaltyType.HasValue) continue;
                int pt = ps.PenaltyType.Value;
                if (pt != 0 && pt != 1 && pt != 4) continue; // actionable only (DT/SG/Time)
                string key = pt + "|" + (ps.InfringementType ?? -1) + "|" + (ps.LapNum ?? -1) + "|" + (ps.TimeSec ?? -1);
                if (seen.Add(key)) count++;
            }
            return count;
        }

        private static string VisualCompoundCode(byte v)
        {
            switch (v)
            {
                case 16: return "S";
                case 17: return "M";
                case 18: return "H";
                case 7: return "I";
                case 8: return "W";
                default: return "";
            }
        }

        private static string ErsModeName(byte mode)
        {
            switch (mode)
            {
                case 0: return "Nenhum";
                case 1: return "Medio";
                case 2: return "Hotlap";
                case 3: return "Ultrapassagem";
                default: return "-";
            }
        }

        private static string StatusStr(DriverRun d)
        {
            // ResultStatus: 0 invalid,1 inactive,2 active,3 finished,4 dnf,5 dsq,6 not classified,7 retired
            switch (d.LiveResultStatus)
            {
                case 3: return "Finalizado";
                case 4: return "Abandonou";
                case 5: return "DSQ";
                case 6: return "Nao classificado";
                case 7: return "Retirado";
            }
            if (d.LivePitStatus == 1) return "Pit";
            if (d.LivePitStatus == 2) return "No pit";
            return "Em pista";
        }

        private static string GameLabel(SessionRun sess)
        {
            if (sess.LastGameYear >= 26) return "F1_26";
            if (sess.LastGameYear >= 25) return "F1_25";
            if (sess.LastPacketFormat >= 2026) return "F1_26";
            return "F1_25";
        }

        private static object Round1(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return null;
            return Math.Round((double)v, 1);
        }

        private static string ResolveTeamName(ParticipantEntry team)
        {
            if (team == null) return null;
            if (team.MyTeam || Lookups.IsMyTeamTeamId(team.TeamId)) return "MyTeam";
            return Lookups.LookupOrDefault(Lookups.Teams, team.TeamId, "Team");
        }

        private static string SessionTypeName(byte id)
        {
            switch (id)
            {
                case 1: return "Treino 1";
                case 2: return "Treino 2";
                case 3: return "Treino 3";
                case 4: return "Treino Curto";
                case 5: return "Classificacao 1";
                case 6: return "Classificacao 2";
                case 7: return "Classificacao 3";
                case 8: return "Classificacao Curta";
                case 9: return "Classificacao Unica";
                case 10: case 11: case 12: case 13: case 14: return "Sprint Shootout";
                case 15: return "Corrida";
                case 16: return "Corrida 2";
                case 17: return "Corrida 3";
                case 18: return "Time Trial";
                default: return "Sessao";
            }
        }
    }
}
