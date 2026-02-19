using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Overtake.SimHub.Plugin.Packets;
using Overtake.SimHub.Plugin.Store;

namespace Overtake.SimHub.Plugin.Finalizer
{
    /// <summary>
    /// Transforms the accumulated SessionStore into a front-ready JSON schema.
    /// Ported from Python league_finalizer.py (1270 LOC).
    /// </summary>
    public static class LeagueFinalizer
    {
        private static readonly Regex GhostCarRe = new Regex(@"^Car\d+$");
        private static readonly Regex EarlyRegRe = new Regex(@"^Car_\d+$");

        /// <summary>
        /// Determines if an entry is a phantom/empty car slot that should be excluded.
        /// Team 255 = invalid team, Driver_X/Car_X with 0 laps = empty slot.
        /// </summary>
        private static bool IsPhantomEntry(string tag, DriverRun dr, SessionRun sess)
        {
            ParticipantEntry teamInfo;
            sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo);
            bool invalidTeam = teamInfo == null || teamInfo.TeamId == 255;
            if (teamInfo != null && teamInfo.TeamId == 255 && dr.Laps.Count == 0)
                return true;
            if (tag.StartsWith("Driver_") && dr.Laps.Count == 0 && invalidTeam)
                return true;
            if (EarlyRegRe.IsMatch(tag) && dr.Laps.Count == 0 && invalidTeam)
                return true;
            return false;
        }

        public static Dictionary<string, object> Finalize(SessionStore store)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var capture = new Dictionary<string, object>
            {
                { "sessionUID", store.SessionUid.HasValue ? store.SessionUid.Value.ToString() : "unknown" },
                { "startedAtMs", store.StartedAtMs },
                { "endedAtMs", nowMs },
                { "source", new Dictionary<string, object>() },
            };

            var allParticipants = new List<string>();
            var seenTags = new HashSet<string>();
            foreach (var sess in store.Sessions.Values)
            {
                foreach (var kvp in sess.TagsByCarIdx)
                {
                    string tag = kvp.Value;
                    if (string.IsNullOrEmpty(tag) || !seenTags.Add(tag)) continue;
                    DriverRun dr;
                    sess.Drivers.TryGetValue(tag, out dr);
                    if (dr != null && IsPhantomEntry(tag, dr, sess)) continue;
                    allParticipants.Add(tag);
                }
            }

            // Session dedup: keep only the last session of each sessionType
            var sessionList = new List<KeyValuePair<string, SessionRun>>(
                store.Sessions.Select(kvp => new KeyValuePair<string, SessionRun>(kvp.Key, kvp.Value)));
            var lastByType = new Dictionary<int, int>();
            for (int i = 0; i < sessionList.Count; i++)
            {
                int? st = sessionList[i].Value.SessionType;
                if (st.HasValue) lastByType[st.Value] = i;
            }
            var deduped = new List<KeyValuePair<string, SessionRun>>();
            for (int i = 0; i < sessionList.Count; i++)
            {
                int? st = sessionList[i].Value.SessionType;
                if (!st.HasValue || lastByType[st.Value] == i)
                    deduped.Add(sessionList[i]);
            }

            // Cross-session team lookup: tag -> ParticipantEntry (first known)
            var globalTeamByTag = new Dictionary<string, ParticipantEntry>();
            foreach (var sess in store.Sessions.Values)
            {
                foreach (var kvp2 in sess.TeamByCarIdx)
                {
                    string tag;
                    if (sess.TagsByCarIdx.TryGetValue(kvp2.Key, out tag) && !string.IsNullOrEmpty(tag))
                    {
                        if (!globalTeamByTag.ContainsKey(tag))
                            globalTeamByTag[tag] = kvp2.Value;
                    }
                }
            }

            var sessionsOut = new List<object>();
            var sessionTypeNames = new List<string>();
            var seenSt = new HashSet<int>();

            foreach (var kvp in deduped)
            {
                string sid = kvp.Key;
                SessionRun sess = kvp.Value;

                // Skip phantom sessions (UID=0, or no type with no meaningful data)
                if (sid == "0" || (!sess.SessionType.HasValue && sess.Drivers.Count == 0))
                    continue;

                if (sess.SessionType.HasValue && seenSt.Add(sess.SessionType.Value))
                    sessionTypeNames.Add(((Dictionary<string, object>)Lookups.Label(Lookups.SessionType, sess.SessionType, "SessionType"))["name"].ToString());

                sessionsOut.Add(FinalizeSession(sid, sess, sessionsOut, globalTeamByTag));
            }

            capture["sessionTypesInCapture"] = sessionTypeNames;

            var result = new Dictionary<string, object>
            {
                { "schemaVersion", "league-1.0" },
                { "game", "F1_25" },
                { "capture", capture },
                { "participants", allParticipants },
                { "sessions", sessionsOut },
            };

            var pktCounts = new Dictionary<string, object>();
            foreach (var pc in store.PacketCounts)
                pktCounts[pc.Key.ToString()] = pc.Value;

            result["_debug"] = new Dictionary<string, object>
            {
                { "packetIdCounts", pktCounts },
                { "notes", store.Notes },
                { "diagnostics", new Dictionary<string, object>
                    {
                        { "sessionHistory", new Dictionary<string, object>
                            {
                                { "received", store.DiagShReceived },
                                { "noDriver", store.DiagShNoDriver },
                                { "earlyRegister", store.DiagShEarlyRegister },
                                { "dedup", store.DiagShDedup },
                                { "lapsAccepted", store.DiagShLapsAccepted },
                                { "lapsFiltered", store.DiagShLapsFiltered },
                            }
                        },
                        { "lapData", new Dictionary<string, object>
                            {
                                { "lapRecorded", store.DiagLdLapRecorded },
                                { "noDriver", store.DiagLdNoDriver },
                                { "earlyRegister", store.DiagLdEarlyRegister },
                                { "alreadyExists", store.DiagLdAlreadyExists },
                                { "sanityFail", store.DiagLdSanityFail },
                            }
                        },
                        { "participants", new Dictionary<string, object>
                            {
                                { "received", store.DiagParticipantsReceived },
                                { "numActive", store.DiagParticipantsNumActive },
                                { "playerCarIdx", store.DiagPlayerCarIdx },
                                { "playerRecoveredFromOverflow", store.DiagPlayerRecoveredFromOverflow },
                            }
                        },
                    }
                },
            };

            return result;
        }

        private static Dictionary<string, object> FinalizeSession(
            string sid, SessionRun sess, List<object> previousSessions,
            Dictionary<string, ParticipantEntry> globalTeamByTag = null)
        {
            // Build carIdx->tag lookup
            var idxToTag = new Dictionary<int, string>(sess.TagsByCarIdx);
            if (sess.FinalClassification != null && sess.FinalClassification.Classification != null)
            {
                foreach (var row in sess.FinalClassification.Classification)
                {
                    if (row != null && !idxToTag.ContainsKey(row.CarIdx))
                    {
                        string tag;
                        if (sess.TagsByCarIdx.TryGetValue(row.CarIdx, out tag))
                            idxToTag[row.CarIdx] = tag;
                    }
                }
            }

            // Best lap by tag
            var bestByTag = new Dictionary<string, int>();
            foreach (var dkvp in sess.Drivers)
            {
                string tag = dkvp.Key;
                DriverRun dr = dkvp.Value;
                int bestMs = 0;
                foreach (var lap in dr.Laps)
                    if (lap.LapTimeMs > 0 && (bestMs == 0 || lap.LapTimeMs < bestMs))
                        bestMs = lap.LapTimeMs;
                if (bestMs == 0 && dr.Best.ContainsKey("bestLapTimeMs") && dr.Best["bestLapTimeMs"] != null)
                {
                    int shBest;
                    if (int.TryParse(dr.Best["bestLapTimeMs"].ToString(), out shBest) && shBest > 0)
                        bestMs = shBest;
                }
                if (bestMs == 0 && dr.LastSeenLapTimeMs > 0)
                    bestMs = dr.LastSeenLapTimeMs;
                if (bestMs > 0)
                    bestByTag[tag] = bestMs;
            }

            // Build results from FinalClassification
            var resultsOut = new List<Dictionary<string, object>>();
            if (sess.FinalClassification != null && sess.FinalClassification.Classification != null)
            {
                var seenResultTags = new HashSet<string>();
                foreach (var row in sess.FinalClassification.Classification)
                {
                    if (row == null || row.Position <= 0) continue;
                    string tag;
                    if (!sess.TagsByCarIdx.TryGetValue(row.CarIdx, out tag))
                        tag = string.Format("Car{0}", row.CarIdx);

                    if (GhostCarRe.IsMatch(tag) && !sess.Drivers.ContainsKey(tag)) continue;
                    if (!seenResultTags.Add(tag)) continue;
                    if (!sess.Drivers.ContainsKey(tag)) continue;

                    var drCheck = sess.Drivers[tag];
                    if (IsPhantomEntry(tag, drCheck, sess)) continue;

                    int bestMs;
                    bestByTag.TryGetValue(tag, out bestMs);
                    if (bestMs == 0 && row.BestLapTimeMs > 0)
                        bestMs = (int)row.BestLapTimeMs;

                    int? totalMs = row.TotalRaceTimeSec > 0 ? (int?)(int)(row.TotalRaceTimeSec * 1000) : null;

                    string statusStr;
                    Lookups.ResultStatus.TryGetValue(row.ResultStatus, out statusStr);
                    if (statusStr == null) statusStr = "FinishedOrUnknown";

                    var drData = sess.Drivers.ContainsKey(tag) ? sess.Drivers[tag] : null;
                    ParticipantEntry teamInfo;
                    sess.TeamByCarIdx.TryGetValue(row.CarIdx, out teamInfo);
                    if (teamInfo == null && globalTeamByTag != null)
                        globalTeamByTag.TryGetValue(tag, out teamInfo);

                    string teamName = ResolveTeamName(teamInfo);

                    resultsOut.Add(new Dictionary<string, object>
                    {
                        { "position", (int)row.Position },
                        { "tag", tag },
                        { "teamId", teamInfo != null ? (object)(int)teamInfo.TeamId : null },
                        { "teamName", teamName },
                        { "grid", row.GridPosition > 0 ? (object)(int)row.GridPosition : null },
                        { "numLaps", (int)row.NumLaps },
                        { "bestLapTimeMs", bestMs > 0 ? (object)bestMs : null },
                        { "bestLapTime", bestMs > 0 ? MsToStr(bestMs) : "" },
                        { "totalTimeMs", totalMs },
                        { "totalTime", totalMs.HasValue ? MsToStr(totalMs.Value) : "" },
                        { "penaltiesTimeSec", (int)row.PenaltiesTimeSec },
                        { "pitStops", (int)row.NumPitStops },
                        { "status", statusStr },
                        { "numPenalties", (int)row.NumPenalties },
                    });
                }
            }

            // Fallback: reconstruct race results from lap data
            string stName = sess.SessionType.HasValue ? Lookups.LookupOrDefault(Lookups.SessionType, sess.SessionType.Value, "S") : "";
            bool isRace = stName.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                       || stName.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isQuali = stName.IndexOf("Qualifying", StringComparison.OrdinalIgnoreCase) >= 0
                        || stName.IndexOf("Shootout", StringComparison.OrdinalIgnoreCase) >= 0;

            if (resultsOut.Count == 0 && isRace && sess.Drivers.Count > 0)
                resultsOut = BuildRaceFallbackResults(sess, bestByTag, idxToTag, previousSessions, globalTeamByTag);

            if (resultsOut.Count == 0 && isQuali && sess.Drivers.Count > 0)
                resultsOut = BuildQualiFallbackResults(sess, bestByTag, globalTeamByTag);

            // Fill in missing grid positions: qualifying results -> LapData GridPosition -> sequential
            if (isRace)
            {
                var qualiGrid = new Dictionary<string, int>();
                if (previousSessions.Count > 0)
                {
                    foreach (var prevObj in previousSessions)
                    {
                        var prev = prevObj as Dictionary<string, object>;
                        if (prev == null) continue;
                        var stObj = prev.ContainsKey("sessionType") ? prev["sessionType"] as Dictionary<string, object> : null;
                        if (stObj == null) continue;
                        string prevStName = stObj.ContainsKey("name") ? stObj["name"].ToString() : "";
                        if (prevStName.IndexOf("Qualifying", StringComparison.OrdinalIgnoreCase) < 0
                            && prevStName.IndexOf("Shootout", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var prevResults = prev.ContainsKey("results") ? prev["results"] as List<Dictionary<string, object>> : null;
                        if (prevResults == null) continue;
                        foreach (var r in prevResults)
                        {
                            object tagObj, posObj;
                            if (r.TryGetValue("tag", out tagObj) && r.TryGetValue("position", out posObj)
                                && tagObj != null && posObj != null)
                                qualiGrid[tagObj.ToString()] = (int)posObj;
                        }
                    }
                }

                // LapData GridPosition as second fallback
                var lapDataGrid = new Dictionary<string, int>();
                foreach (var drKvp in sess.Drivers)
                {
                    if (drKvp.Value.GridPosition > 0)
                        lapDataGrid[drKvp.Key] = drKvp.Value.GridPosition;
                }

                int maxKnownGrid = 0;
                foreach (var gp in qualiGrid.Values)
                    if (gp > maxKnownGrid) maxKnownGrid = gp;
                foreach (var gp in lapDataGrid.Values)
                    if (gp > maxKnownGrid) maxKnownGrid = gp;

                int nextGrid = maxKnownGrid + 1;
                foreach (var res in resultsOut)
                {
                    if (res["grid"] == null)
                    {
                        string tag = res["tag"].ToString();
                        int qPos;
                        if (qualiGrid.TryGetValue(tag, out qPos))
                            res["grid"] = qPos;
                        else if (lapDataGrid.TryGetValue(tag, out qPos))
                            res["grid"] = qPos;
                        else
                            res["grid"] = nextGrid++;
                    }
                }
            }

            // Re-number positions
            resultsOut.Sort((a, b) => ((int)a["position"]).CompareTo((int)b["position"]));
            for (int i = 0; i < resultsOut.Count; i++)
                resultsOut[i]["position"] = i + 1;

            // Build drivers payload (filter out phantom/empty car slots)
            var driversOut = new Dictionary<string, object>();
            foreach (var dkvp in sess.Drivers)
            {
                string tag = dkvp.Key;
                DriverRun dr = dkvp.Value;
                if (IsPhantomEntry(tag, dr, sess)) continue;
                driversOut[tag] = FinalizeDriver(tag, dr, sess, idxToTag, globalTeamByTag);
            }

            // Events
            var eventsOut = FinalizeEvents(sess, idxToTag);

            // Weather timeline
            var weatherTimelineOut = new List<object>();
            foreach (var wt in sess.WeatherTimeline)
            {
                weatherTimelineOut.Add(new Dictionary<string, object>
                {
                    { "tsMs", wt.TsMs },
                    { "weather", Lookups.Label(Lookups.Weather, wt.Weather, "Weather") },
                    { "trackTempC", wt.TrackTempC },
                    { "airTempC", wt.AirTempC },
                });
            }

            // Safety — compute lapsUnderSC / lapsUnderVSC via timestamp interpolation
            var scLaps = ComputeLapsUnderSafetyCar(eventsOut, resultsOut);

            var safetyOut = new Dictionary<string, object>
            {
                { "status", Lookups.Label(Lookups.SafetyCarStatus, sess.SafetyCarStatus.HasValue ? (int?)sess.SafetyCarStatus.Value : null, "SafetyCar") },
                { "fullDeploys", sess.NumSafetyCarDeployments },
                { "vscDeploys", sess.NumVSCDeployments },
                { "redFlagPeriods", sess.NumRedFlagPeriods },
                { "lapsUnderSC", scLaps["sc"] },
                { "lapsUnderVSC", scLaps["vsc"] },
            };

            // Awards
            var awards = ComputeAwards(resultsOut, eventsOut, driversOut);

            return new Dictionary<string, object>
            {
                { "sessionUID", sid },
                { "sessionType", Lookups.Label(Lookups.SessionType, sess.SessionType, "SessionType") },
                { "track", Lookups.Label(Lookups.Tracks, sess.TrackId.HasValue ? (int?)(int)sess.TrackId.Value : null, "Track") },
                { "weather", Lookups.Label(Lookups.Weather, sess.Weather.HasValue ? (int?)(int)sess.Weather.Value : null, "Weather") },
                { "trackTempC", sess.LatestTrackTempC },
                { "airTempC", sess.LatestAirTempC },
                { "weatherTimeline", weatherTimelineOut },
                { "weatherForecast", FinalizeWeatherForecast(sess) },
                { "lastPacketMs", sess.LastPacketMs },
                { "sessionEndedAtMs", sess.SessionEndedAtMs },
                { "safetyCar", safetyOut },
                { "networkGame", sess.NetworkGame == 1 },
                { "awards", awards },
                { "results", resultsOut },
                { "drivers", driversOut },
                { "events", eventsOut },
            };
        }

        private static Dictionary<string, object> FinalizeDriver(
            string tag, DriverRun dr, SessionRun sess, Dictionary<int, string> idxToTag,
            Dictionary<string, ParticipantEntry> globalTeamByTag = null)
        {
            // Laps (sorted)
            var sortedLaps = new List<LapRecord>(dr.Laps);
            sortedLaps.Sort((a, b) => a.LapNumber.CompareTo(b.LapNumber));
            var lapsOut = new List<object>();
            foreach (var lap in sortedLaps)
            {
                lapsOut.Add(new Dictionary<string, object>
                {
                    { "lapNumber", lap.LapNumber },
                    { "lapTimeMs", lap.LapTimeMs },
                    { "lapTime", !string.IsNullOrEmpty(lap.LapTime) ? lap.LapTime : MsToStr(lap.LapTimeMs) },
                    { "sector1Ms", lap.Sector1Ms },
                    { "sector2Ms", lap.Sector2Ms },
                    { "sector3Ms", lap.Sector3Ms },
                    { "valid", (lap.ValidFlags & 0x01) != 0 },
                    { "flags", (lap.ValidFlags & 0x01) != 0 ? new List<string> { "Valid" } : new List<string> { "InvalidOrNotSet" } },
                    { "tsMs", lap.TsMs },
                });
            }

            // Tyre stints (filter out invalid entries with endLap=255)
            var tyreOut = new List<object>();
            foreach (var ts in dr.TyreStints)
            {
                object endLapObj;
                int endLap = ts.TryGetValue("endLap", out endLapObj) && endLapObj != null ? Convert.ToInt32(endLapObj) : 0;
                if (endLap >= 255) continue;

                object ta, tv;
                ts.TryGetValue("tyreActual", out ta);
                ts.TryGetValue("tyreVisual", out tv);
                int taId = ta != null ? Convert.ToInt32(ta) : -1;
                int tvId = tv != null ? Convert.ToInt32(tv) : -1;
                tyreOut.Add(new Dictionary<string, object>
                {
                    { "endLap", endLap },
                    { "tyreActualId", taId >= 0 ? (object)taId : null },
                    { "tyreActual", taId >= 0 ? Lookups.LookupOrDefault(Lookups.TyreActual, taId, "Tyre") : "Unknown" },
                    { "tyreVisualId", tvId >= 0 ? (object)tvId : null },
                    { "tyreVisual", tvId >= 0 ? Lookups.LookupOrDefault(Lookups.TyreVisual, tvId, "Tyre") : "Unknown" },
                });
            }

            // Penalties vs collisions
            var penTl = new List<object>();
            var collTl = new List<object>();
            foreach (var ps in dr.PenaltySnapshots)
            {
                if (ps.EventCode == "COLL")
                {
                    var collEntry = new Dictionary<string, object> { { "tsMs", ps.TsMs }, { "type", "collision" } };
                    if (ps.OtherVehicleIdx.HasValue)
                    {
                        string otherTag;
                        if (idxToTag.TryGetValue(ps.OtherVehicleIdx.Value, out otherTag))
                            collEntry["otherVehicleTag"] = otherTag;
                    }
                    collTl.Add(collEntry);
                    continue;
                }
                var entry = new Dictionary<string, object> { { "tsMs", ps.TsMs } };
                if (ps.PenaltyType.HasValue)
                {
                    entry["penaltyType"] = ps.PenaltyType.Value;
                    entry["penaltyTypeName"] = Lookups.LookupOrDefault(Lookups.PenaltyType, ps.PenaltyType.Value, "PenaltyType");
                    int pt = ps.PenaltyType.Value;
                    entry["category"] = pt == 5 ? "warning" : pt == 6 ? "disqualification"
                        : pt == 16 ? "retired" : (pt == 0 || pt == 1 || pt == 4) ? "penalty" : "other";
                }
                if (ps.InfringementType.HasValue)
                {
                    entry["infringementType"] = ps.InfringementType.Value;
                    entry["infringementTypeName"] = Lookups.LookupOrDefault(Lookups.InfringementType, ps.InfringementType.Value, "Infringement");
                }
                if (ps.TimeSec.HasValue && ps.TimeSec.Value != 255)
                    entry["timeSec"] = ps.TimeSec.Value;
                if (ps.LapNum.HasValue) entry["lapNum"] = ps.LapNum.Value;
                if (ps.OtherVehicleIdx.HasValue)
                {
                    string otherTag;
                    if (idxToTag.TryGetValue(ps.OtherVehicleIdx.Value, out otherTag))
                        entry["otherDriver"] = otherTag;
                }
                penTl.Add(entry);
            }

            // Pit stops timeline
            var pitTl = new List<object>();
            foreach (var ps in dr.PitStops)
            {
                pitTl.Add(new Dictionary<string, object>
                {
                    { "numPitStops", ps.NumPitStops },
                    { "tsMs", ps.TsMs },
                    { "lapNum", ps.LapNum },
                });
            }

            // TyreWear per lap (sorted)
            var twSorted = new List<TyreWearSnapshot>(dr.TyreWearPerLap);
            twSorted.Sort((a, b) => a.LapNumber.CompareTo(b.LapNumber));
            var twOut = new List<object>();
            foreach (var tw in twSorted)
            {
                twOut.Add(new Dictionary<string, object>
                {
                    { "lapNumber", tw.LapNumber },
                    { "rl", tw.RL }, { "rr", tw.RR }, { "fl", tw.FL }, { "fr", tw.FR }, { "avg", tw.Avg },
                });
            }

            // Damage per lap (sorted) + wing repairs
            var dmgSorted = new List<DamageSnapshot>(dr.DamagePerLap);
            dmgSorted.Sort((a, b) => a.LapNumber.CompareTo(b.LapNumber));
            var dmgOut = new List<object>();
            foreach (var d in dmgSorted)
            {
                dmgOut.Add(new Dictionary<string, object>
                {
                    { "lapNumber", d.LapNumber },
                    { "wingFL", d.WingFL }, { "wingFR", d.WingFR }, { "wingRear", d.WingRear },
                    { "tyreDmgRL", d.TyreDmgRL }, { "tyreDmgRR", d.TyreDmgRR },
                    { "tyreDmgFL", d.TyreDmgFL }, { "tyreDmgFR", d.TyreDmgFR },
                });
            }

            var wingRepairs = new List<object>();
            for (int i = 1; i < dmgSorted.Count; i++)
            {
                var prev = dmgSorted[i - 1];
                var curr = dmgSorted[i];
                CheckWingRepair(wingRepairs, prev.WingFL, curr.WingFL, curr.LapNumber, "frontLeftWing");
                CheckWingRepair(wingRepairs, prev.WingFR, curr.WingFR, curr.LapNumber, "frontRightWing");
                CheckWingRepair(wingRepairs, prev.WingRear, curr.WingRear, curr.LapNumber, "rearWing");
            }

            // Team info (with cross-session fallback)
            ParticipantEntry teamInfo;
            sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo);
            if (teamInfo == null && globalTeamByTag != null)
                globalTeamByTag.TryGetValue(dr.Tag, out teamInfo);
            string teamName = ResolveTeamName(teamInfo);

            return new Dictionary<string, object>
            {
                { "position", 0 },
                { "teamId", teamInfo != null ? (object)(int)teamInfo.TeamId : null },
                { "teamName", teamName },
                { "myTeam", teamInfo != null && teamInfo.MyTeam },
                { "raceNumber", teamInfo != null ? (object)(int)teamInfo.RaceNumber : null },
                { "aiControlled", teamInfo != null ? (object)teamInfo.AiControlled : null },
                { "isPlayer", sess.PlayerCarIndex >= 0 && dr.CarIdx == sess.PlayerCarIndex },
                { "platform", teamInfo != null ? Lookups.LookupOrDefault(Lookups.Platforms, teamInfo.Platform, "Platform") : null },
                { "showOnlineNames", teamInfo != null && teamInfo.ShowOnlineNames != 0 },
                { "yourTelemetry", teamInfo != null && teamInfo.YourTelemetry == 1 ? "public" : "restricted" },
                { "nationality", teamInfo != null ? (object)(int)teamInfo.Nationality : 0 },
                { "laps", lapsOut },
                { "tyreStints", tyreOut },
                { "tyreWearPerLap", twOut },
                { "damagePerLap", dmgOut },
                { "wingRepairs", wingRepairs },
                { "best", dr.Best },
                { "pitStopsTimeline", pitTl },
                { "penaltiesTimeline", penTl },
                { "collisionsTimeline", collTl },
                { "totalWarnings", dr.LastTotalWarnings },
                { "cornerCuttingWarnings", dr.LastCornerCuttingWarnings },
            };
        }

        private static List<object> FinalizeWeatherForecast(SessionRun sess)
        {
            var result = new List<object>();
            if (sess.WeatherForecast == null) return result;
            foreach (var fc in sess.WeatherForecast)
            {
                object wObj;
                int? weatherId = fc.TryGetValue("weather", out wObj) && wObj != null ? (int?)wObj : null;
                result.Add(new Dictionary<string, object>
                {
                    { "timeOffsetMin", fc.ContainsKey("timeOffsetMin") ? fc["timeOffsetMin"] : null },
                    { "weather", Lookups.Label(Lookups.Weather, weatherId, "Weather") },
                    { "trackTempC", fc.ContainsKey("trackTempC") ? fc["trackTempC"] : null },
                    { "airTempC", fc.ContainsKey("airTempC") ? fc["airTempC"] : null },
                    { "rainPercentage", fc.ContainsKey("rainPercentage") ? fc["rainPercentage"] : null },
                });
            }
            return result;
        }

        private static List<Dictionary<string, object>> FinalizeEvents(
            SessionRun sess, Dictionary<int, string> idxToTag)
        {
            var eventsOut = new List<Dictionary<string, object>>();
            foreach (var ev in sess.Events)
            {
                object codeObj;
                if (!ev.TryGetValue("code", out codeObj) || codeObj == null) continue;
                string code = codeObj.ToString();
                string evName;
                if (!Lookups.EventNames.TryGetValue(code, out evName))
                    evName = string.Format("UnknownEvent({0})", code);

                var entry = new Dictionary<string, object>
                {
                    { "tsMs", ev.ContainsKey("tsMs") ? ev["tsMs"] : null },
                    { "code", code },
                    { "name", evName },
                };

                object rawData;
                if (ev.TryGetValue("data", out rawData) && rawData is Dictionary<string, object>)
                {
                    var src = (Dictionary<string, object>)rawData;
                    var enriched = new Dictionary<string, object>(src);

                    // Resolve vehicle indices to driver tags
                    string[] idxKeys = { "vehicleIdx", "vehicle1Idx", "vehicle2Idx",
                                         "overtakerIdx", "overtakenIdx", "otherVehicleIdx" };
                    foreach (var key in idxKeys)
                    {
                        object vidxObj;
                        if (enriched.TryGetValue(key, out vidxObj) && vidxObj != null)
                        {
                            string tag;
                            if (idxToTag.TryGetValue((int)vidxObj, out tag))
                                enriched[key.Replace("Idx", "Tag")] = tag;
                        }
                    }

                    // Enrich PENA events with type names
                    if (code == "PENA")
                    {
                        object ptObj, itObj;
                        if (enriched.TryGetValue("penaltyType", out ptObj) && ptObj != null)
                            enriched["penaltyTypeName"] = Lookups.LookupOrDefault(
                                Lookups.PenaltyType, (int)ptObj, "PenaltyType");
                        if (enriched.TryGetValue("infringementType", out itObj) && itObj != null)
                            enriched["infringementTypeName"] = Lookups.LookupOrDefault(
                                Lookups.InfringementType, (int)itObj, "Infringement");
                    }

                    entry["data"] = enriched;
                }

                eventsOut.Add(entry);
            }
            return eventsOut;
        }

        private static List<Dictionary<string, object>> BuildRaceFallbackResults(
            SessionRun sess, Dictionary<string, int> bestByTag,
            Dictionary<int, string> idxToTag, List<object> previousSessions,
            Dictionary<string, ParticipantEntry> globalTeamByTag = null)
        {
            var retiredTags = new HashSet<string>();
            foreach (var ev in sess.Events)
            {
                object code;
                if (ev.TryGetValue("code", out code) && code != null && code.ToString() == "RTMT")
                {
                    // RTMT event doesn't carry vehicleIdx in our simplified event dict
                }
            }

            int maxLaps = 0;
            foreach (var dr in sess.Drivers.Values)
                if (dr.Laps.Count > maxLaps) maxLaps = dr.Laps.Count;

            // Qualifying grid from previous sessions
            var qualiGrid = new Dictionary<string, int>();
            foreach (var prevObj in previousSessions)
            {
                var prev = prevObj as Dictionary<string, object>;
                if (prev == null) continue;
                var stObj = prev.ContainsKey("sessionType") ? prev["sessionType"] as Dictionary<string, object> : null;
                if (stObj == null) continue;
                string stName = stObj.ContainsKey("name") ? stObj["name"].ToString() : "";
                if (stName.IndexOf("Qualifying", StringComparison.OrdinalIgnoreCase) < 0
                    && stName.IndexOf("Shootout", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var results = prev.ContainsKey("results") ? prev["results"] as List<Dictionary<string, object>> : null;
                if (results == null) continue;
                foreach (var r in results)
                {
                    object tagObj, posObj;
                    if (r.TryGetValue("tag", out tagObj) && r.TryGetValue("position", out posObj)
                        && tagObj != null && posObj != null)
                        qualiGrid[tagObj.ToString()] = (int)posObj;
                }
            }

            var entries = new List<Dictionary<string, object>>();
            foreach (var dkvp in sess.Drivers)
            {
                string tag = dkvp.Key;
                DriverRun dr = dkvp.Value;
                if (IsPhantomEntry(tag, dr, sess)) continue;

                int nLaps = dr.Laps.Count;
                int totalMs = 0;
                foreach (var lap in dr.Laps) totalMs += lap.LapTimeMs;

                int bestMs;
                bestByTag.TryGetValue(tag, out bestMs);

                bool isRetired = retiredTags.Contains(tag) || (maxLaps > 0 && nLaps < maxLaps - 1);
                string status = isRetired ? "DidNotFinish" : "Finished";

                ParticipantEntry teamInfo;
                sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo);
                if (teamInfo == null && globalTeamByTag != null)
                    globalTeamByTag.TryGetValue(tag, out teamInfo);
                string teamName = ResolveTeamName(teamInfo);

                int gridPos;
                qualiGrid.TryGetValue(tag, out gridPos);
                if (gridPos <= 0 && dr.GridPosition > 0)
                    gridPos = dr.GridPosition;

                entries.Add(new Dictionary<string, object>
                {
                    { "position", 0 },
                    { "tag", tag },
                    { "teamId", teamInfo != null ? (object)(int)teamInfo.TeamId : null },
                    { "teamName", teamName },
                    { "grid", gridPos > 0 ? (object)gridPos : null },
                    { "numLaps", nLaps },
                    { "bestLapTimeMs", bestMs > 0 ? (object)bestMs : null },
                    { "bestLapTime", bestMs > 0 ? MsToStr(bestMs) : "" },
                    { "totalTimeMs", totalMs > 0 ? (object)totalMs : null },
                    { "totalTime", totalMs > 0 ? MsToStr(totalMs) : "" },
                    { "penaltiesTimeSec", 0 },
                    { "pitStops", dr.PitStops.Count },
                    { "status", status },
                    { "numPenalties", 0 },
                });
            }

            entries.Sort((a, b) =>
            {
                int cmp = -((int)a["numLaps"]).CompareTo((int)b["numLaps"]);
                if (cmp != 0) return cmp;
                int aTime = a["totalTimeMs"] != null ? (int)a["totalTimeMs"] : 999999999;
                int bTime = b["totalTimeMs"] != null ? (int)b["totalTimeMs"] : 999999999;
                return aTime.CompareTo(bTime);
            });

            for (int i = 0; i < entries.Count; i++)
                entries[i]["position"] = i + 1;

            return entries;
        }

        private static List<Dictionary<string, object>> BuildQualiFallbackResults(
            SessionRun sess, Dictionary<string, int> bestByTag,
            Dictionary<string, ParticipantEntry> globalTeamByTag = null)
        {
            var entries = new List<Dictionary<string, object>>();
            foreach (var dkvp in sess.Drivers)
            {
                string tag = dkvp.Key;
                DriverRun dr = dkvp.Value;
                if (IsPhantomEntry(tag, dr, sess)) continue;

                int bestMs;
                bestByTag.TryGetValue(tag, out bestMs);

                ParticipantEntry teamInfo;
                sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo);
                if (teamInfo == null && globalTeamByTag != null)
                    globalTeamByTag.TryGetValue(tag, out teamInfo);
                string teamName = ResolveTeamName(teamInfo);

                entries.Add(new Dictionary<string, object>
                {
                    { "position", 0 },
                    { "tag", tag },
                    { "teamId", teamInfo != null ? (object)(int)teamInfo.TeamId : null },
                    { "teamName", teamName },
                    { "grid", null },
                    { "bestLapTimeMs", bestMs > 0 ? (object)bestMs : null },
                    { "bestLapTime", bestMs > 0 ? MsToStr(bestMs) : "" },
                    { "totalTimeMs", null },
                    { "totalTime", "" },
                    { "pitStops", null },
                    { "status", bestMs > 0 ? "Finished" : "NoTime" },
                });
            }

            entries.Sort((a, b) =>
            {
                bool aNone = a["bestLapTimeMs"] == null;
                bool bNone = b["bestLapTimeMs"] == null;
                if (aNone != bNone) return aNone ? 1 : -1;
                if (aNone) return 0;
                return ((int)a["bestLapTimeMs"]).CompareTo((int)b["bestLapTimeMs"]);
            });

            for (int i = 0; i < entries.Count; i++)
                entries[i]["position"] = i + 1;

            return entries;
        }

        private static Dictionary<string, object> ComputeAwards(
            List<Dictionary<string, object>> results,
            List<Dictionary<string, object>> events,
            Dictionary<string, object> drivers)
        {
            var awards = new Dictionary<string, object>();

            // Fastest Lap: from the LAST FTLP event (overall fastest lap) or best result
            Dictionary<string, object> lastFtlp = null;
            foreach (var ev in events)
            {
                object code;
                if (ev.TryGetValue("code", out code) && code != null && code.ToString() == "FTLP")
                    lastFtlp = ev;
            }

            if (lastFtlp != null)
            {
                object dataObj;
                var ftlpData = lastFtlp.TryGetValue("data", out dataObj) ? dataObj as Dictionary<string, object> : null;
                if (ftlpData != null)
                {
                    object tagObj, secObj;
                    string ftTag = ftlpData.TryGetValue("vehicleTag", out tagObj) && tagObj != null ? tagObj.ToString() : null;
                    float ftSec = ftlpData.TryGetValue("lapTimeSec", out secObj) && secObj != null ? Convert.ToSingle(secObj) : 0;
                    int ftMs = ftSec > 0 ? (int)(ftSec * 1000) : 0;
                    if (ftTag != null && ftMs > 0)
                        awards["fastestLap"] = new Dictionary<string, object>
                        {
                            { "tag", ftTag },
                            { "timeMs", ftMs },
                            { "time", MsToStr(ftMs) },
                        };
                    else
                        awards["fastestLap"] = null;
                }
                else
                    awards["fastestLap"] = null;
            }
            else
            {
                // Fallback: use best result bestLapTimeMs
                Dictionary<string, object> bestR = null;
                int bestMs = int.MaxValue;
                foreach (var r in results)
                {
                    object msObj;
                    if (r.TryGetValue("bestLapTimeMs", out msObj) && msObj != null)
                    {
                        int ms = (int)msObj;
                        if (ms > 0 && ms < bestMs) { bestMs = ms; bestR = r; }
                    }
                }
                if (bestR != null)
                    awards["fastestLap"] = new Dictionary<string, object>
                    {
                        { "tag", bestR["tag"] },
                        { "timeMs", bestMs },
                        { "time", MsToStr(bestMs) },
                    };
                else
                    awards["fastestLap"] = null;
            }

            // Most Positions Gained
            var gains = new List<Dictionary<string, object>>();
            foreach (var r in results)
            {
                object gridObj, posObj, statusObj;
                if (!r.TryGetValue("grid", out gridObj) || gridObj == null) continue;
                if (!r.TryGetValue("position", out posObj) || posObj == null) continue;
                int grid = (int)gridObj;
                int pos = (int)posObj;
                if (grid <= 0 || pos <= 0) continue;
                int gained = grid - pos;
                r.TryGetValue("status", out statusObj);
                string status = statusObj != null ? statusObj.ToString() : "";
                if (status != "Finished" && status != "FinishedOrUnknown") continue;
                gains.Add(new Dictionary<string, object>
                {
                    { "tag", r["tag"] },
                    { "grid", grid },
                    { "finish", pos },
                    { "gained", gained },
                });
            }
            if (gains.Count > 0)
            {
                gains.Sort((a, b) =>
                {
                    int cmp = -((int)a["gained"]).CompareTo((int)b["gained"]);
                    if (cmp != 0) return cmp;
                    return ((int)a["finish"]).CompareTo((int)b["finish"]);
                });
                var winner = gains[0];
                awards["mostPositionsGained"] = (int)winner["gained"] > 0 ? winner : null;
            }
            else
            {
                awards["mostPositionsGained"] = null;
            }

            // Most Consistent: lowest std deviation (min 5 clean laps, top 50%)
            awards["mostConsistent"] = ComputeMostConsistent(results, drivers);

            return awards;
        }

        private static object ComputeMostConsistent(
            List<Dictionary<string, object>> results,
            Dictionary<string, object> drivers)
        {
            if (results.Count == 0 || drivers.Count == 0) return null;

            var finishedPositions = new List<int>();
            foreach (var r in results)
            {
                object statusObj, posObj;
                r.TryGetValue("status", out statusObj);
                r.TryGetValue("position", out posObj);
                string s = statusObj != null ? statusObj.ToString() : "";
                if ((s == "Finished" || s == "FinishedOrUnknown") && posObj != null)
                    finishedPositions.Add((int)posObj);
            }
            finishedPositions.Sort();
            int cutoff = finishedPositions.Count > 0 ? finishedPositions[finishedPositions.Count / 2] : 999;

            var posByTag = new Dictionary<string, int>();
            foreach (var r in results)
            {
                object tagObj, posObj;
                r.TryGetValue("tag", out tagObj);
                r.TryGetValue("position", out posObj);
                if (tagObj != null && posObj != null)
                    posByTag[tagObj.ToString()] = (int)posObj;
            }

            Dictionary<string, object> best = null;
            int bestStdDev = int.MaxValue;

            foreach (var dkvp in drivers)
            {
                string tag = dkvp.Key;
                var driverDict = dkvp.Value as Dictionary<string, object>;
                if (driverDict == null) continue;

                int pos;
                if (posByTag.TryGetValue(tag, out pos) && pos > cutoff) continue;

                object lapsObj;
                if (!driverDict.TryGetValue("laps", out lapsObj)) continue;
                var laps = lapsObj as List<object>;
                if (laps == null || laps.Count < 5) continue;

                var rawTimes = new List<int>();
                foreach (var lapObj in laps)
                {
                    var lap = lapObj as Dictionary<string, object>;
                    if (lap == null) continue;
                    object lnObj, msObj;
                    lap.TryGetValue("lapNumber", out lnObj);
                    lap.TryGetValue("lapTimeMs", out msObj);
                    int ln = lnObj != null ? (int)lnObj : 0;
                    int ms = msObj != null ? (int)msObj : 0;
                    if (ln <= 1 || ms <= 0) continue;
                    rawTimes.Add(ms);
                }
                if (rawTimes.Count < 5) continue;

                rawTimes.Sort();
                int median = rawTimes[rawTimes.Count / 2];
                int threshold = (int)(median * 1.15);
                var clean = rawTimes.Where(t => t <= threshold).ToList();
                if (clean.Count < 5) continue;

                double mean = clean.Average();
                double variance = clean.Sum(t => (t - mean) * (t - mean)) / clean.Count;
                int stdDev = (int)Math.Round(Math.Sqrt(variance));

                if (stdDev < bestStdDev)
                {
                    bestStdDev = stdDev;
                    best = new Dictionary<string, object>
                    {
                        { "tag", tag },
                        { "stdDevMs", stdDev },
                        { "stdDev", string.Format("{0:F3}", stdDev / 1000.0) },
                        { "cleanLaps", clean.Count },
                    };
                }
            }
            return best;
        }

        /// <summary>
        /// Uses linear interpolation between LGOT and CHQF timestamps to determine
        /// which lap numbers were under SC or VSC. Matches the standalone app approach.
        /// </summary>
        private static Dictionary<string, List<int>> ComputeLapsUnderSafetyCar(
            List<Dictionary<string, object>> events,
            List<Dictionary<string, object>> results)
        {
            var scLaps = new List<int>();
            var vscLaps = new List<int>();
            var output = new Dictionary<string, List<int>> { { "sc", scLaps }, { "vsc", vscLaps } };

            long lgotTs = 0, chqfTs = 0;
            long firstEventTs = 0, sendTs = 0;
            int totalLaps = 0;

            foreach (var ev in events)
            {
                object codeObj, tsObj;
                ev.TryGetValue("code", out codeObj);
                ev.TryGetValue("tsMs", out tsObj);
                string code = codeObj != null ? codeObj.ToString() : "";
                long ts = tsObj != null ? Convert.ToInt64(tsObj) : 0;
                if (code == "LGOT" && ts > 0) lgotTs = ts;
                if (code == "CHQF" && ts > 0) chqfTs = ts;
                if (code == "SEND" && ts > 0) sendTs = ts;
                if (ts > 0 && (firstEventTs == 0 || ts < firstEventTs)) firstEventTs = ts;
            }

            // Fallback: use first event as LGOT, SEND as CHQF when game doesn't emit them
            if (lgotTs == 0 && firstEventTs > 0) lgotTs = firstEventTs;
            if (chqfTs == 0 && sendTs > 0) chqfTs = sendTs;

            foreach (var r in results)
            {
                object nlObj;
                if (r.TryGetValue("numLaps", out nlObj) && nlObj != null)
                {
                    int nl = (int)nlObj;
                    if (nl > totalLaps) totalLaps = nl;
                }
            }

            if (lgotTs == 0 || chqfTs == 0 || totalLaps == 0 || chqfTs <= lgotTs)
                return output;

            double duration = chqfTs - lgotTs;

            // Collect SC/VSC periods as (startTs, endTs, type)
            // type: 1 = Full SC, 2 = VSC
            var periods = new List<long[]>();
            long currentStart = 0;
            int currentType = 0;

            foreach (var ev in events)
            {
                object codeObj;
                ev.TryGetValue("code", out codeObj);
                string code = codeObj != null ? codeObj.ToString() : "";

                if (code == "SCAR")
                {
                    object dataObj;
                    var data = ev.TryGetValue("data", out dataObj) ? dataObj as Dictionary<string, object> : null;
                    if (data == null) continue;

                    object scTypeObj, evTypeObj, tsObj;
                    data.TryGetValue("safetyCarType", out scTypeObj);
                    data.TryGetValue("eventType", out evTypeObj);
                    ev.TryGetValue("tsMs", out tsObj);
                    int scType = scTypeObj != null ? Convert.ToInt32(scTypeObj) : 0;
                    int evType = evTypeObj != null ? Convert.ToInt32(evTypeObj) : -1;
                    long ts = tsObj != null ? Convert.ToInt64(tsObj) : 0;

                    if (scType == 0 || scType == 3) continue; // formation lap or no SC

                    if (evType == 0 && ts > 0) // deployed
                    {
                        currentStart = ts;
                        currentType = scType;
                    }
                    else if ((evType == 2 || evType == 3) && currentStart > 0) // returned or resume
                    {
                        periods.Add(new long[] { currentStart, ts > 0 ? ts : currentStart + 60000, currentType });
                        currentStart = 0;
                    }
                }
                else if (code == "VSCN")
                {
                    object tsObj;
                    ev.TryGetValue("tsMs", out tsObj);
                    long ts = tsObj != null ? Convert.ToInt64(tsObj) : 0;
                    if (ts > 0) { currentStart = ts; currentType = 2; }
                }
                else if (code == "VSCE")
                {
                    object tsObj;
                    ev.TryGetValue("tsMs", out tsObj);
                    long ts = tsObj != null ? Convert.ToInt64(tsObj) : 0;
                    if (currentStart > 0 && currentType == 2)
                    {
                        periods.Add(new long[] { currentStart, ts > 0 ? ts : currentStart + 60000, 2 });
                        currentStart = 0;
                    }
                }
            }

            // If still open at CHQF, close it
            if (currentStart > 0)
                periods.Add(new long[] { currentStart, chqfTs, currentType });

            // Interpolate each period to lap numbers
            var scSet = new HashSet<int>();
            var vscSet = new HashSet<int>();

            foreach (var period in periods)
            {
                double startFrac = (period[0] - lgotTs) / duration * totalLaps;
                double endFrac = (period[1] - lgotTs) / duration * totalLaps;
                int startLap = Math.Max(1, (int)Math.Ceiling(startFrac));
                int endLap = Math.Min(totalLaps, (int)Math.Ceiling(endFrac));

                for (int lap = startLap; lap <= endLap; lap++)
                {
                    if (period[2] == 1) scSet.Add(lap);
                    else if (period[2] == 2) vscSet.Add(lap);
                }
            }

            scLaps.AddRange(scSet.OrderBy(x => x));
            vscLaps.AddRange(vscSet.OrderBy(x => x));
            return output;
        }

        private static void CheckWingRepair(List<object> repairs, int prev, int curr, int lap, string wing)
        {
            int drop = prev - curr;
            if (drop >= 10)
            {
                repairs.Add(new Dictionary<string, object>
                {
                    { "lap", lap },
                    { "wing", wing },
                    { "damageBefore", prev },
                    { "damageAfter", curr },
                    { "repaired", drop },
                });
            }
        }

        private static string ResolveTeamName(ParticipantEntry team)
        {
            if (team == null) return null;
            if (team.MyTeam) return "MyTeam";
            return Lookups.LookupOrDefault(Lookups.Teams, team.TeamId, "Team");
        }

        private static string MsToStr(int ms)
        {
            if (ms <= 0) return "";
            double total = ms / 1000.0;
            int m = (int)(total / 60.0);
            double s = total - 60.0 * m;
            return string.Format("{0}:{1:00.000}", m, s);
        }
    }
}
