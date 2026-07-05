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
                { "trackLength", sess.TrackLength },
                { "timeLeftSec", sess.SessionTimeLeftSec },
                { "weather", sess.Weather },
                { "trackTempC", sess.LatestTrackTempC },
                { "airTempC", sess.LatestAirTempC },
                { "safetyCar", sess.SafetyCarStatus },
                { "rainNextPct", rainNext >= 0 ? (object)rainNext : null },
                { "forecast", Forecast(sess) },
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

                // Phantom filter — parity with the .otk export. Drops AI grid fillers
                // and empty padded slots (e.g. "Car_18" with 0 laps) the game pads the
                // grid with, so the live UI shows only the real drivers.
                if (LeagueFinalizer.IsPhantomForLive(d.Tag, d, sess, store))
                    continue;

                int bestMs = BestLapMs(d);
                int s1, s2, s3;
                BestSectors(d, out s1, out s2, out s3);
                int pbS1, pbS2, pbS3;
                PersonalBestSectors(d, out pbS1, out pbS2, out pbS3);
                int lastS1, lastS2, lastS3;
                LastLapSectors(d, out lastS1, out lastS2, out lastS3);

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
                    // Live sector splits of the in-progress lap + last completed lap +
                    // personal-best per sector (for F1-style sector colouring).
                    { "liveS1", d.LiveS1Ms }, { "liveS2", d.LiveS2Ms },
                    { "lastS1", lastS1 }, { "lastS2", lastS2 }, { "lastS3", lastS3 },
                    { "pbS1", pbS1 }, { "pbS2", pbS2 }, { "pbS3", pbS3 },
                    { "intervalMs", d.LiveDeltaToCarFrontMs },
                    { "gapMs", d.LiveDeltaToLeaderMs },
                    { "compound", VisualCompoundCode(d.VisualTyreCompound) },
                    { "stints", StintCodes(d) },
                    { "tyreAge", (int)d.TyresAgeLaps },
                    { "tyreWear", TyreWear(d) },
                    { "tyreTempsSurface", TyreTemps(d, true) },
                    { "tyreTempsInner", TyreTemps(d, false) },
                    { "brakeTemps", BrakeTemps(d) },
                    { "engineTemp", d.LiveTelemValid ? (object)d.LiveEngineTemp : null },
                    { "trace", TelemetryTrace(d) },
                    { "damage", Damage(d) },
                    { "stops", d.LastNumPitStops ?? d.PitStops.Count },
                    { "ersPct", (int)Math.Round(d.ErsStorePctLast) },
                    { "ersMode", ErsModeName(d.ErsDeployModeLast) },
                    { "fuelKg", Round1(d.FuelInTankLast) },
                    { "fuelLaps", Round1(d.FuelRemainingLapsLast) },
                    { "penaltiesSec", (int)d.LivePenaltiesSec },
                    { "penaltiesCount", DedupPenaltyCount(d) },
                    { "penalties", PenaltyList(d) },
                    { "warningsDetail", WarningList(d) },
                    { "warnings", d.LastTotalWarnings },
                    { "cornerCutWarnings", d.LastCornerCuttingWarnings },
                    { "pitStatus", (int)d.LivePitStatus },
                    { "status", StatusStr(d) },
                    { "driverStatus", (int)d.LiveDriverStatus },
                    { "lapInvalid", d.LiveCurrentLapInvalid == 1 },
                    { "lapState", LapStatePt(d) },
                    // Track Map (Motion): world position + lap distance + heading.
                    { "x", d.LivePosValid ? (object)Round1(d.LiveWorldX) : null },
                    { "z", d.LivePosValid ? (object)Round1(d.LiveWorldZ) : null },
                    { "yaw", d.LivePosValid ? (object)Math.Round((double)d.LiveYaw, 3) : null },
                    { "lapDist", (int)Math.Max(0, d.LiveLapDistanceM) },
                    { "laps", LapList(d) },
                });
            }
            grid.Sort((a, b) =>
            {
                int pa = (int)a["pos"]; if (pa == 0) pa = 999;
                int pb = (int)b["pos"]; if (pb == 0) pb = 999;
                return pa.CompareTo(pb);
            });

            // Predicted rejoin position if the car pits NOW (race mode only).
            if (!isQualy)
                ComputePitRejoin(grid, (int)sess.TrackId.GetValueOrDefault(-1));

            root["grid"] = grid;

            var events = new List<Dictionary<string, object>>();
            int from = Math.Max(0, sess.Events.Count - 25);
            for (int i = sess.Events.Count - 1; i >= from; i--)
                events.Add(EnrichEvent(sess.Events[i]));
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

        // Only the last 5 completed laps (per request). Ascending by lap number.
        private const int MaxLapsInSnapshot = 5;

        private static List<Dictionary<string, object>> LapList(DriverRun d)
        {
            var laps = new List<LapRecord>();
            foreach (var lr in d.Laps)
                if (lr.LapTimeMs > 0) laps.Add(lr);
            laps.Sort((a, b) => a.LapNumber.CompareTo(b.LapNumber));
            int start = Math.Max(0, laps.Count - MaxLapsInSnapshot);

            var list = new List<Dictionary<string, object>>();
            for (int i = start; i < laps.Count; i++)
            {
                var lr = laps[i];
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

        // Personal best per sector across all completed laps (each sector taken
        // independently — may come from different laps, like the game's purple logic).
        private static void PersonalBestSectors(DriverRun d, out int s1, out int s2, out int s3)
        {
            s1 = s2 = s3 = 0;
            foreach (var lr in d.Laps)
            {
                if (lr.Sector1Ms > 0 && (s1 == 0 || lr.Sector1Ms < s1)) s1 = lr.Sector1Ms;
                if (lr.Sector2Ms > 0 && (s2 == 0 || lr.Sector2Ms < s2)) s2 = lr.Sector2Ms;
                if (lr.Sector3Ms > 0 && (s3 == 0 || lr.Sector3Ms < s3)) s3 = lr.Sector3Ms;
            }
        }

        // Sectors of the most recent completed lap (highest lap number).
        private static void LastLapSectors(DriverRun d, out int s1, out int s2, out int s3)
        {
            s1 = s2 = s3 = 0;
            LapRecord last = null;
            foreach (var lr in d.Laps)
                if (lr.LapTimeMs > 0 && (last == null || lr.LapNumber > last.LapNumber)) last = lr;
            if (last != null) { s1 = last.Sector1Ms; s2 = last.Sector2Ms; s3 = last.Sector3Ms; }
        }

        private static List<Dictionary<string, object>> Forecast(SessionRun sess)
        {
            var list = new List<Dictionary<string, object>>();
            if (sess.WeatherForecast == null) return list;
            int taken = 0;
            foreach (var f in sess.WeatherForecast)
            {
                if (taken >= 8) break; // cap to keep the strip readable + payload small
                object off, w, rain, tt, at;
                f.TryGetValue("timeOffsetMin", out off);
                f.TryGetValue("weather", out w);
                f.TryGetValue("rainPercentage", out rain);
                f.TryGetValue("trackTempC", out tt);
                f.TryGetValue("airTempC", out at);
                list.Add(new Dictionary<string, object>
                {
                    { "offsetMin", off }, { "weather", w }, { "rainPct", rain },
                    { "trackTempC", tt }, { "airTempC", at },
                });
                taken++;
            }
            return list;
        }

        // Actionable penalties, deduplicated, with a human description (PT).
        // Warnings (type 5) go to WarningList instead.
        private static List<Dictionary<string, object>> PenaltyList(DriverRun d)
        {
            var list = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>();
            foreach (var ps in d.PenaltySnapshots)
            {
                // Served DT / Stop-Go (no PenaltyType on the snapshot).
                if (ps.EventCode == "DTSV")
                {
                    string sk = "DTSV|" + (ps.LapNum ?? -1);
                    if (!seen.Add(sk)) continue;
                    list.Add(new Dictionary<string, object>
                    {
                        { "type", 0 }, { "timeSec", 0 }, { "lap", ps.LapNum ?? 0 },
                        { "desc", "Drive-through cumprido" },
                    });
                    continue;
                }
                if (ps.EventCode == "SGSV")
                {
                    string sk = "SGSV|" + (ps.LapNum ?? -1);
                    if (!seen.Add(sk)) continue;
                    list.Add(new Dictionary<string, object>
                    {
                        { "type", 1 }, { "timeSec", 0 }, { "lap", ps.LapNum ?? 0 },
                        { "desc", "Stop & Go cumprido" },
                    });
                    continue;
                }
                if (!ps.PenaltyType.HasValue) continue;
                int pt = ps.PenaltyType.Value;
                if (pt == 5) continue; // warnings -> WarningList
                // DT / SG / grid / reminder / time / DSQ / black flag
                if (pt != 0 && pt != 1 && pt != 2 && pt != 3 && pt != 4 && pt != 6 && pt != 17) continue;
                string key = pt + "|" + (ps.InfringementType ?? -1) + "|" + (ps.LapNum ?? -1) + "|" + (ps.TimeSec ?? -1);
                if (!seen.Add(key)) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "type", pt },
                    { "timeSec", ps.TimeSec ?? 0 },
                    { "lap", ps.LapNum ?? 0 },
                    { "desc", PenaltyDescPt(pt, ps.InfringementType ?? -1, ps.TimeSec ?? 0) },
                });
            }
            return list;
        }

        // Warning events (type 5) with infringement description.
        private static List<Dictionary<string, object>> WarningList(DriverRun d)
        {
            var list = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>();
            foreach (var ps in d.PenaltySnapshots)
            {
                if (!ps.PenaltyType.HasValue || ps.PenaltyType.Value != 5) continue;
                string key = (ps.InfringementType ?? -1) + "|" + (ps.LapNum ?? -1);
                if (!seen.Add(key)) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "lap", ps.LapNum ?? 0 },
                    { "desc", PenaltyDescPt(5, ps.InfringementType ?? -1, 0) },
                });
            }
            return list;
        }

        // Predicted finishing position if the car pits on this lap. Heuristic:
        // it loses ~PitLossSec of track time, so it rejoins behind every car whose
        // gap-to-leader is within (myGap + pitLoss). Spectator-friendly estimate.
        private static void ComputePitRejoin(List<Dictionary<string, object>> grid, int trackId)
        {
            int pitLossMs = PitLossSecForTrack(trackId) * 1000;
            // Build a quick array of gaps (ms) for cars that have a real race position.
            for (int i = 0; i < grid.Count; i++)
            {
                var me = grid[i];
                int myPos = (int)me["pos"];
                if (myPos <= 0) { me["pitRejoinPos"] = null; me["pitLossSec"] = pitLossMs / 1000; continue; }
                int myGap = (int)me["gapMs"];
                long projected = (long)myGap + pitLossMs;
                int ahead = 0;
                for (int j = 0; j < grid.Count; j++)
                {
                    if (j == i) continue;
                    int oPos = (int)grid[j]["pos"];
                    if (oPos <= 0) continue;
                    int oGap = (int)grid[j]["gapMs"];
                    if (oGap <= projected) ahead++;
                }
                me["pitRejoinPos"] = ahead + 1;
                me["pitLossSec"] = pitLossMs / 1000;
            }
        }

        // Rough pit-lane time loss in seconds. We don't have per-track pit deltas,
        // so use a single conservative average; clearly labelled as an estimate in UI.
        private static int PitLossSecForTrack(int trackId)
        {
            return 22;
        }

        // Attach a human-readable PT description to penalty/retirement events for the feed.
        private static Dictionary<string, object> EnrichEvent(Dictionary<string, object> ev)
        {
            if (ev == null) return ev;
            object codeObj;
            if (!ev.TryGetValue("code", out codeObj)) return ev;
            string code = codeObj as string;
            object dataObj;
            ev.TryGetValue("data", out dataObj);
            var data = dataObj as Dictionary<string, object>;

            if (code == "PENA" && data != null)
            {
                int pt = AsInt(data, "penaltyType", -1);
                int inf = AsInt(data, "infringementType", -1);
                int sec = AsInt(data, "timeSec", 0);
                data["desc"] = PenaltyDescPt(pt, inf, sec);
            }
            return ev;
        }

        private static int AsInt(Dictionary<string, object> d, string k, int def)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                int v;
                if (int.TryParse(o.ToString(), out v)) return v;
            }
            return def;
        }

        private static string PenaltyTypePt(int pt)
        {
            switch (pt)
            {
                case 0: return "Drive-through";
                case 1: return "Stop & Go";
                case 2: return "Punicao de grid";
                case 3: return "Lembrete de punicao";
                case 4: return "Punicao de tempo";
                case 5: return "Aviso";
                case 6: return "Desqualificado";
                case 17: return "Bandeira preta";
                default: return "Punicao";
            }
        }

        private static string InfringementPt(int inf)
        {
            switch (inf)
            {
                case 0: case 1: return "bloqueio";
                case 3: return "colisao grave";
                case 4: return "colisao leve";
                case 5: case 6: return "nao devolveu posicao";
                case 7: case 8: case 9: return "corte de curva com ganho";
                case 10: return "cruzou a saida do pit";
                case 11: return "ignorou bandeira azul";
                case 12: return "ignorou bandeira amarela";
                case 13: case 14: case 15: case 16: return "drive-through nao cumprido";
                case 17: return "excesso de velocidade no pit";
                case 18: return "parado tempo demais";
                case 25: case 26: case 27: case 28: case 29: case 30: return "volta invalidada";
                case 33: return "bloqueando o pit lane";
                case 34: return "queima de largada";
                case 35: case 36: case 37: return "infracao sob safety car";
                case 38: return "excesso de ritmo sob VSC";
                case 45: return "stop&go nao cumprido";
                case 46: return "drive-through nao cumprido";
                case 52: return "ganho ilegal de tempo";
                case 53: return "pit obrigatorio nao cumprido";
                default: return "";
            }
        }

        private static string PenaltyDescPt(int pt, int inf, int sec)
        {
            string head = PenaltyTypePt(pt);
            if (pt == 4 && sec > 0) head += " (+" + sec + "s)";
            string tail = InfringementPt(inf);
            return string.IsNullOrEmpty(tail) ? head : head + " - " + tail;
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

        // Tyre temperatures (Car Telemetry packet 6). null until the packet arrives,
        // so the UI can show "aguardando" instead of a misleading 0.
        private static Dictionary<string, object> TyreTemps(DriverRun d, bool surface)
        {
            if (!d.LiveTelemValid) return null;
            return surface
                ? new Dictionary<string, object> {
                    { "fl", d.LiveTyreSurfFL }, { "fr", d.LiveTyreSurfFR },
                    { "rl", d.LiveTyreSurfRL }, { "rr", d.LiveTyreSurfRR } }
                : new Dictionary<string, object> {
                    { "fl", d.LiveTyreInnerFL }, { "fr", d.LiveTyreInnerFR },
                    { "rl", d.LiveTyreInnerRL }, { "rr", d.LiveTyreInnerRR } };
        }

        // Telemetry trace (speed/throttle/brake/gear by lap distance) for the charts:
        // current lap vs previous lap. Downsampled to keep the payload bounded.
        private static Dictionary<string, object> TelemetryTrace(DriverRun d)
        {
            var cur = TraceArray(d.TraceCur);
            var prev = TraceArray(d.TracePrev);
            if (cur == null && prev == null) return null;
            return new Dictionary<string, object> { { "cur", cur }, { "prev", prev } };
        }

        private static List<object> TraceArray(Dictionary<int, int[]> trace)
        {
            if (trace == null || trace.Count == 0) return null;
            var keys = new List<int>(trace.Keys);
            keys.Sort();
            const int max = 70;
            int step = keys.Count > max ? (int)Math.Ceiling(keys.Count / (double)max) : 1;
            var outp = new List<object>();
            for (int i = 0; i < keys.Count; i += step)
            {
                int b = keys[i];
                int[] v = trace[b];
                // [distanceM, speedKmh, throttlePct, brakePct, gear] (+ ersPct, ersModeCode no traço v2)
                if (v.Length > 5)
                    outp.Add(new object[] { b * 25, v[0], v[1], v[2], v[3], v[4], v[5] });
                else
                    outp.Add(new object[] { b * 25, v[0], v[1], v[2], v[3] });
            }
            return outp;
        }

        private static Dictionary<string, object> BrakeTemps(DriverRun d)
        {
            if (!d.LiveTelemValid) return null;
            return new Dictionary<string, object> {
                { "fl", d.LiveBrakeFL }, { "fr", d.LiveBrakeFR },
                { "rl", d.LiveBrakeRL }, { "rr", d.LiveBrakeRR } };
        }

        private static Dictionary<string, object> TyreWear(DriverRun d)
        {
            var tw = d.LatestTyreWear;
            if (tw == null)
                return new Dictionary<string, object> { { "avg", 0 }, { "max", 0 }, { "fl", 0 }, { "fr", 0 }, { "rl", 0 }, { "rr", 0 } };
            double avg = (tw.FL + tw.FR + tw.RL + tw.RR) / 4.0;
            double max = Math.Max(Math.Max(tw.FL, tw.FR), Math.Max(tw.RL, tw.RR));
            return new Dictionary<string, object>
            {
                { "avg", Math.Round(avg, 1) },
                { "max", Math.Round(max, 1) },
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
                return new Dictionary<string, object> {
                    { "wingFL", 0 }, { "wingFR", 0 }, { "wingRear", 0 },
                    { "tyreFL", 0 }, { "tyreFR", 0 }, { "tyreRL", 0 }, { "tyreRR", 0 }, { "worst", 0 } };
            int worst = Math.Max(Math.Max(dmg.WingFrontLeft, dmg.WingFrontRight), dmg.WingRear);
            return new Dictionary<string, object>
            {
                { "wingFL", dmg.WingFrontLeft },
                { "wingFR", dmg.WingFrontRight },
                { "wingRear", dmg.WingRear },
                { "tyreFL", dmg.TyreDmgFL },
                { "tyreFR", dmg.TyreDmgFR },
                { "tyreRL", dmg.TyreDmgRL },
                { "tyreRR", dmg.TyreDmgRR },
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

        private static List<string> StintCodes(DriverRun d)
        {
            // d.TyreStints comes from SessionHistory/FinalClassification packets and
            // holds the real stint list; each entry has a "tyreVisual" compound id.
            var list = new List<string>();
            if (d.TyreStints != null)
            {
                foreach (var stint in d.TyreStints)
                {
                    object tv;
                    if (stint == null || !stint.TryGetValue("tyreVisual", out tv) || tv == null) continue;
                    int code;
                    if (!int.TryParse(tv.ToString(), out code)) continue;
                    string c = VisualCompoundCode((byte)code);
                    if (!string.IsNullOrEmpty(c)) list.Add(c);
                }
            }
            // Fallback: SessionHistory may not have arrived yet (early in a stint /
            // online lobbies that throttle it) — show the current live compound.
            if (list.Count == 0)
            {
                string cur = VisualCompoundCode(d.VisualTyreCompound);
                if (!string.IsNullOrEmpty(cur)) list.Add(cur);
            }
            return list;
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
                case 2: return "Volta Rapida";
                case 3: return "Boost";
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

        // Lap context (qualifying board): out lap / flying lap / in lap, based on
        // DriverStatus (LapData). Pit lane wins. Used to label "Volta de saida" /
        // "Em volta" on the Classificacao tab.
        private static string LapStatePt(DriverRun d)
        {
            if (d.LivePitStatus == 1) return "Box";
            switch (d.LiveDriverStatus)
            {
                case 0: return "Garagem";
                case 1: return "Em volta";          // flying lap
                case 2: return "Volta de entrada";  // in lap
                case 3: return "Volta de saida";    // out lap
                case 4: return "Em pista";
            }
            return "Em pista";
        }

        private static string GameLabel(SessionRun sess)
        {
            // Content first (parity with .otk export): the F1 26 "2026 Season Pack"
            // runs on the 2025 wire format, so packetFormat alone reports F1_25 even
            // when the content is 2026. Detect via team ids (220-230) or Madring (42).
            bool content2026 = false;
            if (sess.TrackId.HasValue && sess.TrackId.Value == Packets.GameInfo.F1_26TrackIdMadring)
                content2026 = true;
            if (!content2026)
            {
                foreach (var te in sess.TeamByCarIdx.Values)
                {
                    if (te != null && Packets.GameInfo.IsF1_26TeamId(te.TeamId))
                    {
                        content2026 = true;
                        break;
                    }
                }
            }
            if (content2026) return "F1_26";
            if (sess.LastPacketFormat != 0)
                return Packets.GameInfo.GameNameFromPacketFormat(sess.LastPacketFormat);
            if (sess.LastGameYear >= 26) return "F1_26";
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
