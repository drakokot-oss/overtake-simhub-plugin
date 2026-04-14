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
        private static readonly Regex DriverPlaceholderRe = new Regex(@"^Driver_\d+$");
        private static readonly Regex EarlyRegRe = new Regex(@"^Car_\d+$");
        private static readonly Regex PlayerGenericRe = new Regex(@"^(Player[_ #]\d+|Player)$");

        /// <summary>
        /// Determines if an entry is a phantom that should be excluded.
        /// Post-dedup: placeholder tag matching is no longer needed because
        /// DeduplicateDrivers already merged duplicate Car_X/Driver_X entries
        /// into their canonical counterparts. Only filter AI with 0 laps.
        /// </summary>
        /// <summary>Slot index from "Driver_17" style tags; -1 if not a Driver_N placeholder.</summary>
        private static int ParseDriverPlaceholderCarIndex(string tag)
        {
            if (string.IsNullOrEmpty(tag) || !DriverPlaceholderRe.IsMatch(tag))
                return -1;
            const string prefix = "Driver_";
            if (!tag.StartsWith(prefix, StringComparison.Ordinal))
                return -1;
            int idx;
            return int.TryParse(tag.Substring(prefix.Length), out idx) ? idx : -1;
        }

        private static bool IsPhantomEntry(string tag, DriverRun dr, SessionRun sess)
        {
            if (string.IsNullOrEmpty(tag))
                return true;

            // A driver who completed at least one lap is NEVER phantom.
            if (dr.Laps.Count > 0)
                return false;

            ParticipantEntry teamInfo;
            if (!sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo)
                || teamInfo == null
                || teamInfo.TeamId == 255)
            {
                int fromTag = ParseDriverPlaceholderCarIndex(tag);
                if (fromTag >= 0)
                    sess.TeamByCarIdx.TryGetValue(fromTag, out teamInfo);
            }

            bool hasValidTeam = teamInfo != null && teamInfo.TeamId != 255;

            // AI-controlled + 0 laps = grid filler, regardless of tag name.
            // In showOnlineNames=OFF lobbies, AI fillers get real F1 driver names
            // (VERSTAPPEN, LECLERC, etc.) instead of generic tags. Requiring
            // IsGenericTag would miss these. The laps>0 barrier above already
            // protects any AI car that completed laps (e.g. disconnected human
            // whose car was taken over by AI).
            if (teamInfo != null && teamInfo.AiControlled)
                return true;

            // Generic tag with 0 laps AND no valid team data = empty slot, exclude.
            if (IsGenericTag(tag) && !hasValidTeam)
                return true;

            return false;
        }

        private static bool IsGenericTag(string tag)
        {
            return EarlyRegRe.IsMatch(tag) || GhostCarRe.IsMatch(tag)
                || DriverPlaceholderRe.IsMatch(tag) || PlayerGenericRe.IsMatch(tag);
        }

        /// <summary>
        /// Game-assigned official surnames for empty grid slots when showOnlineNames=OFF.
        /// Fallback when TeamByCarIdx lacks aiControlled (legacy captures).
        /// </summary>
        private static readonly HashSet<string> AiGridFillerSurnameTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VERSTAPPEN", "HAMILTON", "LECLERC", "NORRIS", "SAINZ", "RUSSELL",
            "PIASTRI", "ALONSO", "OCON", "GASLY", "TSUNODA", "STROLL",
            "ALBON", "HULKENBERG", "RICCIARDO", "BOTTAS", "ZHOU", "MAGNUSSEN",
            "SARGEANT", "LAWSON", "BEARMAN", "ANTONELLI", "HADJAR", "DOOHAN",
            "BORTOLETO", "COLAPINTO", "DRUGOVICH", "VRIES",
        };

        private static bool TagMatchesOfficialAiSeatRoster(string tag)
        {
            if (string.IsNullOrEmpty(tag) || IsGenericTag(tag))
                return false;
            string u = tag.ToUpperInvariant();
            if (u.IndexOf("DE VRIES", StringComparison.Ordinal) >= 0)
                return true;
            foreach (string chunk in Regex.Split(u, @"[^\w]+"))
            {
                if (chunk.Length >= 2 && AiGridFillerSurnameTokens.Contains(chunk))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Skip FC rows for AI grid fillers with no laps — matches Python _should_skip_fc_ai_grid_filler_row.
        /// Online enhancement: also catches phantom slots (generic tag, 0 laps, 0 best-lap, not
        /// confirmed human, and absent from lobby/bestKnown name maps).
        /// </summary>
        private static bool ShouldSkipFcAiGridFillerRow(
            SessionRun sess, string tag, FinalClassificationEntry row, DriverRun preDr,
            SessionStore store)
        {
            int fcLaps = row.NumLaps;
            int telemLaps = preDr != null ? preDr.Laps.Count : 0;
            if (Math.Max(fcLaps, telemLaps) > 0)
                return false;

            ParticipantEntry slotInfo;
            sess.TeamByCarIdx.TryGetValue(row.CarIdx, out slotInfo);
            bool? effAi = slotInfo != null ? (bool?)slotInfo.AiControlled : null;

            if (effAi == true)
                return true;

            bool online = sess.NetworkGame == 1;

            if (online)
            {
                bool confirmedHuman;
                if (!sess.HumanCarIdxs.TryGetValue(row.CarIdx, out confirmedHuman))
                    confirmedHuman = false;
                if (confirmedHuman)
                    return false;

                if (IsGenericTag(tag) && row.BestLapTimeMs == 0)
                {
                    string lk = slotInfo != null
                        ? string.Format("{0}_{1}", slotInfo.RaceNumber, slotInfo.TeamId)
                        : null;

                    bool inLobby = false, inBkt = false;
                    if (lk != null)
                    {
                        var lobbyMap = store.DebugLobbyMap;
                        var bkt = store.DebugBestKnownTags;
                        string lobbyName;
                        if (lobbyMap.TryGetValue(lk, out lobbyName) && !string.IsNullOrEmpty(lobbyName) && !IsGenericTag(lobbyName))
                            inLobby = true;
                        string bktName;
                        if (bkt.TryGetValue(lk, out bktName) && !string.IsNullOrEmpty(bktName) && !IsGenericTag(bktName))
                            inBkt = true;
                    }

                    if (!inLobby && !inBkt)
                        return true;
                }

                return false;
            }

            // Offline path
            if (effAi == false)
                return false;
            if (preDr == null && TagMatchesOfficialAiSeatRoster(tag))
                return true;
            return false;
        }

        /// <summary>
        /// F1 UDP often uses ResultStatus Retired (7) for drivers classified +1 lap behind the leader
        /// (game shows points and "+1 Lap"); raw "status" stays Retired for parity with the packet.
        /// League front-ends can treat classifiedLapped like a normal classified finish.
        /// </summary>
        private static void ApplyRaceClassifiedLappedFlags(IList<Dictionary<string, object>> resultsOut, bool isRace)
        {
            if (!isRace || resultsOut == null || resultsOut.Count == 0) return;
            int maxLaps = 0;
            foreach (var res in resultsOut)
            {
                object nlObj;
                int nl = 0;
                if (res.TryGetValue("numLaps", out nlObj) && nlObj != null)
                    int.TryParse(nlObj.ToString(), out nl);
                if (nl > maxLaps) maxLaps = nl;
            }
            if (maxLaps < 2) return;

            foreach (var res in resultsOut)
            {
                string st = res.ContainsKey("status") && res["status"] != null ? res["status"].ToString() : "";
                object nlObj;
                int nl = 0;
                if (res.TryGetValue("numLaps", out nlObj) && nlObj != null)
                    int.TryParse(nlObj.ToString(), out nl);
                bool lapped = string.Equals(st, "Retired", StringComparison.Ordinal) && nl == maxLaps - 1;
                res["classifiedLapped"] = lapped;
                res["classificationLeaderLaps"] = maxLaps;
            }
        }

        private static int DriverRunLapCount(DriverRun dr)
        {
            if (dr == null || dr.Laps == null) return 0;
            return dr.Laps.Count;
        }

        /// <summary>
        /// Effective lap count: max of Laps.Count, highest LapNumber in the list, and
        /// LastRecordedLapNumber. Gaps in the Laps list (e.g. pit-stop lap lost in
        /// spectator telemetry) must not reduce the count used for ranking.
        /// </summary>
        private static int EffectiveLapCount(DriverRun dr)
        {
            if (dr == null || dr.Laps == null) return 0;
            int n = dr.Laps.Count;
            foreach (var lap in dr.Laps)
                if (lap.LapNumber > n) n = lap.LapNumber;
            if (dr.LastRecordedLapNumber > n)
                n = dr.LastRecordedLapNumber;
            return n;
        }

        /// <summary>
        /// True if key is Driver_K and K equals carIdx (slot index when showOnlineNames is off).
        /// </summary>
        private static bool DriverTagMatchesCarIdx(string key, int carIdx)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var m = DriverPlaceholderRe.Match(key);
            if (!m.Success) return false;
            int k;
            return int.TryParse(key.Substring("Driver_".Length), out k) && k == carIdx;
        }

        /// <summary>
        /// FC may use Car{N} while lap data stayed under Driver_N (duplicate-tag path creates an empty CarN stub).
        /// SessionStore sometimes leaves DriverRun.CarIdx at 0; Driver_K in the key still identifies the slot.
        /// </summary>
        private static string ReconcileFcTagWithExistingDriver(SessionRun sess, int carIdx, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return tag;

            string driverIdxTag = string.Format("Driver_{0}", carIdx);
            DriverRun drSlot;
            sess.Drivers.TryGetValue(driverIdxTag, out drSlot);

            // CarN bucket vs Driver_N: prefer the one with telemetry (fixes Car13 empty + Driver_13 full).
            if (GhostCarRe.IsMatch(tag))
            {
                DriverRun drCarTag;
                sess.Drivers.TryGetValue(tag, out drCarTag);
                int lapsCar = DriverRunLapCount(drCarTag);
                int lapsSlot = DriverRunLapCount(drSlot);
                if (drSlot != null && (lapsSlot > lapsCar || (lapsSlot == lapsCar && lapsCar == 0)))
                {
                    if (!string.Equals(tag, driverIdxTag, StringComparison.Ordinal) && drCarTag != null && sess.Drivers.ContainsKey(tag))
                        sess.Drivers.Remove(tag);
                    return driverIdxTag;
                }
            }

            if (sess.Drivers.ContainsKey(tag))
                return tag;

            if (drSlot != null)
                return driverIdxTag;

            string bestTag = null;
            int bestLaps = -1;
            foreach (var kvp in sess.Drivers)
            {
                if (kvp.Value == null) continue;
                bool idxMatch = kvp.Value.CarIdx == carIdx || DriverTagMatchesCarIdx(kvp.Key, carIdx);
                if (!idxMatch) continue;
                int n = DriverRunLapCount(kvp.Value);
                if (n > bestLaps)
                {
                    bestLaps = n;
                    bestTag = kvp.Key;
                }
            }
            if (bestTag != null && bestLaps > 0)
                return bestTag;

            foreach (var kvp in sess.Drivers)
            {
                if (kvp.Value == null) continue;
                bool idxMatch = kvp.Value.CarIdx == carIdx || DriverTagMatchesCarIdx(kvp.Key, carIdx);
                if (!idxMatch) continue;
                if (!GhostCarRe.IsMatch(kvp.Key))
                    return kvp.Key;
            }
            foreach (var kvp in sess.Drivers)
            {
                if (kvp.Value != null && (kvp.Value.CarIdx == carIdx || DriverTagMatchesCarIdx(kvp.Key, carIdx)))
                    return kvp.Key;
            }
            return tag;
        }

        /// <summary>
        /// Remove phantom drivers before FC processing (Python parity). Cleans TagsByCarIdx entries for removed tags.
        /// </summary>
        private static void RemovePhantomDrivers(SessionRun sess)
        {
            var phantomTags = new List<string>();
            foreach (var kvp in sess.Drivers)
            {
                if (IsPhantomEntry(kvp.Key, kvp.Value, sess))
                    phantomTags.Add(kvp.Key);
            }
            foreach (string t in phantomTags)
            {
                sess.Drivers.Remove(t);
                var staleIdx = new List<int>();
                foreach (var kv in sess.TagsByCarIdx)
                {
                    if (kv.Value == t)
                        staleIdx.Add(kv.Key);
                }
                foreach (int idx in staleIdx)
                    sess.TagsByCarIdx.Remove(idx);
            }
        }

        /// <summary>
        /// Remove ghost duplicates: generic-tagged, 0-lap drivers whose raceNumber_teamId
        /// is already claimed by another driver with a real name. This catches the case
        /// where a carIdx slot is reassigned (e.g. driver disconnects and reconnects on a
        /// different slot), leaving a phantom entry at the old index.
        /// Only applies to online sessions.
        /// </summary>
        private static void RemovePhantomDuplicateSeats(SessionRun sess)
        {
            if (sess.NetworkGame != 1) return;

            var seatOwner = new Dictionary<string, string>();
            foreach (var kvp in sess.Drivers)
            {
                string tag = kvp.Key;
                DriverRun dr = kvp.Value;
                if (IsGenericTag(tag)) continue;

                ParticipantEntry ti;
                sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out ti);
                if (ti == null) continue;

                string seatKey = string.Format("{0}_{1}", ti.RaceNumber, ti.TeamId);
                seatOwner[seatKey] = tag;
            }

            var phantoms = new List<string>();
            foreach (var kvp in sess.Drivers)
            {
                string tag = kvp.Key;
                DriverRun dr = kvp.Value;
                if (!IsGenericTag(tag)) continue;
                if (dr.Laps.Count > 0) continue;

                ParticipantEntry ti;
                sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out ti);
                if (ti == null) continue;

                string seatKey = string.Format("{0}_{1}", ti.RaceNumber, ti.TeamId);
                string owner;
                if (seatOwner.TryGetValue(seatKey, out owner) && owner != tag)
                    phantoms.Add(tag);
            }

            foreach (string t in phantoms)
            {
                sess.Drivers.Remove(t);
                var staleIdx = new List<int>();
                foreach (var kv in sess.TagsByCarIdx)
                {
                    if (kv.Value == t)
                        staleIdx.Add(kv.Key);
                }
                foreach (int idx in staleIdx)
                    sess.TagsByCarIdx.Remove(idx);
            }
        }

        /// <summary>
        /// Returns tag priority: 1 = real gamertag, 2 = Player #XX, 3 = placeholder (Car_X, Driver_X, CarN).
        /// Lower is better.
        /// </summary>
        private static int TagPriority(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 99;
            if (DriverPlaceholderRe.IsMatch(tag) || EarlyRegRe.IsMatch(tag) || GhostCarRe.IsMatch(tag))
                return 3;
            if (tag.StartsWith("Player #") || tag.StartsWith("Player_"))
                return 2;
            return 1;
        }

        /// <summary>
        /// Retroactively resolves driver tags using bestKnownTags from later sessions
        /// in the same lobby. Handles two cases:
        ///   1. Generic tags (Car_X, Driver_X) — always resolved.
        ///   2. AI seat names (HAMILTON, LECLERC, etc.) — resolved only when bestKnownTags
        ///      holds a different, non-generic, non-AI-roster name for the same raceNumber_teamId.
        ///      This fixes the scenario where a human joins mid-qualifying with showOnlineNames=OFF,
        ///      inherits an F1 AI surname, then reveals their real gamertag in the race session.
        /// </summary>
        private static void RetroResolveNames(SessionRun sess, SessionStore store)
        {
            var bkt = store.DebugBestKnownTags;
            if (bkt == null || bkt.Count == 0) return;

            var renames = new List<KeyValuePair<string, string>>();
            foreach (var kvp in sess.Drivers)
            {
                string tag = kvp.Key;
                bool generic = IsGenericTag(tag);
                bool aiRoster = !generic && TagMatchesOfficialAiSeatRoster(tag);
                if (!generic && !aiRoster) continue;

                DriverRun dr = kvp.Value;

                ParticipantEntry teamInfo;
                sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo);
                if (teamInfo == null) continue;

                string lookupKey = string.Format("{0}_{1}", teamInfo.RaceNumber, teamInfo.TeamId);
                string resolvedName;
                if (!bkt.TryGetValue(lookupKey, out resolvedName) || string.IsNullOrEmpty(resolvedName)
                    || IsGenericTag(resolvedName) || resolvedName == tag
                    || sess.Drivers.ContainsKey(resolvedName))
                    continue;

                if (aiRoster && TagMatchesOfficialAiSeatRoster(resolvedName))
                    continue;

                renames.Add(new KeyValuePair<string, string>(tag, resolvedName));
            }

            foreach (var r in renames)
            {
                DriverRun dr = sess.Drivers[r.Key];
                sess.Drivers.Remove(r.Key);
                sess.Drivers[r.Value] = dr;

                foreach (var kv in sess.TagsByCarIdx.ToList())
                {
                    if (kv.Value == r.Key)
                        sess.TagsByCarIdx[kv.Key] = r.Value;
                }
            }
        }

        /// <summary>
        /// Merge duplicate tags that refer to the same physical car. Key is teamId + raceNumber + carIdx
        /// so spectator duplicate names for one slot still merge, but two MyTeam humans on squad 41 do not
        /// (different race numbers and/or carIdx).
        /// </summary>
        private static int DeduplicateDrivers(SessionRun sess)
        {
            var physGroups = new Dictionary<string, List<string>>();

            foreach (var dkvp in sess.Drivers)
            {
                string tag = dkvp.Key;
                DriverRun dr = dkvp.Value;

                ParticipantEntry teamInfo;
                sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo);
                if (teamInfo == null) continue;

                string physKey = teamInfo.TeamId + "_" + teamInfo.RaceNumber + "_" + dr.CarIdx;
                List<string> group;
                if (!physGroups.TryGetValue(physKey, out group))
                {
                    group = new List<string>();
                    physGroups[physKey] = group;
                }
                group.Add(tag);
            }

            var tagsToRemove = new List<string>();

            foreach (var gkvp in physGroups)
            {
                var group = gkvp.Value;
                if (group.Count <= 1) continue;

                // Pick canonical: lowest priority number wins; tiebreak = more laps
                string canonicalTag = group[0];
                DriverRun canonicalDr = sess.Drivers[canonicalTag];
                int canonicalPri = TagPriority(canonicalTag);

                for (int i = 1; i < group.Count; i++)
                {
                    string tag = group[i];
                    DriverRun dr = sess.Drivers[tag];
                    int pri = TagPriority(tag);

                    bool replace = false;
                    if (pri < canonicalPri)
                        replace = true;
                    else if (pri == canonicalPri && dr.Laps.Count > canonicalDr.Laps.Count)
                        replace = true;

                    if (replace)
                    {
                        canonicalTag = tag;
                        canonicalDr = dr;
                        canonicalPri = pri;
                    }
                }

                // Merge non-canonical entries into canonical, then remove them
                foreach (var tag in group)
                {
                    if (tag == canonicalTag) continue;
                    var dupDr = sess.Drivers[tag];

                    // Laps: keep set with more entries (data is byte-identical for duplicates)
                    if (dupDr.Laps.Count > canonicalDr.Laps.Count)
                    {
                        canonicalDr.Laps = dupDr.Laps;
                        canonicalDr.LastRecordedLapNumber = dupDr.LastRecordedLapNumber;
                    }

                    // TyreStints, TyreWearPerLap, DamagePerLap: keep from entry with more data
                    if (dupDr.TyreStints.Count > canonicalDr.TyreStints.Count)
                        canonicalDr.TyreStints = dupDr.TyreStints;
                    if (dupDr.TyreWearPerLap.Count > canonicalDr.TyreWearPerLap.Count)
                        canonicalDr.TyreWearPerLap = dupDr.TyreWearPerLap;
                    if (dupDr.DamagePerLap.Count > canonicalDr.DamagePerLap.Count)
                        canonicalDr.DamagePerLap = dupDr.DamagePerLap;

                    // PenaltySnapshots: union without duplicates (by tsMs + penaltyType)
                    foreach (var ps in dupDr.PenaltySnapshots)
                    {
                        bool exists = false;
                        foreach (var eps in canonicalDr.PenaltySnapshots)
                        {
                            if (eps.TsMs == ps.TsMs && eps.PenaltyType == ps.PenaltyType
                                && eps.EventCode == ps.EventCode)
                            { exists = true; break; }
                        }
                        if (!exists) canonicalDr.PenaltySnapshots.Add(ps);
                    }

                    // PitStops: union without duplicates (by tsMs)
                    foreach (var pit in dupDr.PitStops)
                    {
                        bool exists = false;
                        foreach (var epit in canonicalDr.PitStops)
                        {
                            if (epit.TsMs == pit.TsMs) { exists = true; break; }
                        }
                        if (!exists) canonicalDr.PitStops.Add(pit);
                    }

                    // Best: keep the one with lower bestLapTimeMs
                    if (dupDr.Best.ContainsKey("bestLapTimeMs") && dupDr.Best["bestLapTimeMs"] != null)
                    {
                        int dupBest;
                        if (int.TryParse(dupDr.Best["bestLapTimeMs"].ToString(), out dupBest) && dupBest > 0)
                        {
                            bool canonicalHasBest = canonicalDr.Best.ContainsKey("bestLapTimeMs")
                                && canonicalDr.Best["bestLapTimeMs"] != null;
                            int canBest = 0;
                            if (canonicalHasBest)
                                int.TryParse(canonicalDr.Best["bestLapTimeMs"].ToString(), out canBest);
                            if (canBest <= 0 || dupBest < canBest)
                                canonicalDr.Best = dupDr.Best;
                        }
                    }

                    // Keep better LastSeenLapTimeMs
                    if (dupDr.LastSeenLapTimeMs > 0 &&
                        (canonicalDr.LastSeenLapTimeMs <= 0 || dupDr.LastSeenLapTimeMs < canonicalDr.LastSeenLapTimeMs))
                        canonicalDr.LastSeenLapTimeMs = dupDr.LastSeenLapTimeMs;

                    tagsToRemove.Add(tag);
                }
            }

            // Remove deduplicated entries
            foreach (var tag in tagsToRemove)
            {
                sess.Drivers.Remove(tag);
                var idxToRemove = new List<int>();
                foreach (var kvp in sess.TagsByCarIdx)
                {
                    if (kvp.Value == tag) idxToRemove.Add(kvp.Key);
                }
                foreach (var idx in idxToRemove)
                    sess.TagsByCarIdx.Remove(idx);
            }

            return tagsToRemove.Count;
        }

        public static Dictionary<string, object> Finalize(SessionStore store)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            store.LastExportedNameKeyConflicts = store.SnapshotNameKeyConflicts();
            store.ApplyFullMyTeamLobbyMergeIfNeeded();

            var capture = new Dictionary<string, object>
            {
                { "sessionUID", store.SessionUid.HasValue ? store.SessionUid.Value.ToString() : "unknown" },
                { "startedAtMs", store.StartedAtMs },
                { "endedAtMs", nowMs },
                { "source", new Dictionary<string, object>() },
            };

            // Participants will be rebuilt from session outputs so they match actual drivers shown
            var allParticipants = new List<string>();

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

                sessionsOut.Add(FinalizeSession(sid, sess, store, sessionsOut, globalTeamByTag));
            }

            // Rebuild participants from session outputs (drivers + results) so count matches actual data
            var participantsSet = new HashSet<string>();
            foreach (var sessObj in sessionsOut)
            {
                var sessDict = sessObj as Dictionary<string, object>;
                if (sessDict == null) continue;
                var drivers = sessDict.ContainsKey("drivers") ? sessDict["drivers"] as Dictionary<string, object> : null;
                if (drivers != null)
                {
                    foreach (var k in drivers.Keys)
                        if (!string.IsNullOrEmpty(k)) participantsSet.Add(k);
                }
                var results = sessDict.ContainsKey("results") ? sessDict["results"] as List<object> : null;
                if (results != null)
                {
                    foreach (var r in results)
                    {
                        var rd = r as Dictionary<string, object>;
                        if (rd != null && rd.ContainsKey("tag"))
                        {
                            var tag = rd["tag"] as string;
                            if (!string.IsNullOrEmpty(tag)) participantsSet.Add(tag);
                        }
                    }
                }
            }
            allParticipants = participantsSet.OrderBy(s => s).ToList();

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

            // Integrity: drivers without team, spectator mode
            var driversWithoutTeam = new List<string>();
            bool anySpectator = store.Sessions.Values.Any(s => s.IsSpectating != 0);
            foreach (var sessObj in sessionsOut)
            {
                var sessDict = sessObj as Dictionary<string, object>;
                if (sessDict == null) continue;
                var drivers = sessDict.ContainsKey("drivers") ? sessDict["drivers"] as Dictionary<string, object> : null;
                if (drivers == null) continue;
                foreach (var dkvp in drivers)
                {
                    var d = dkvp.Value as Dictionary<string, object>;
                    if (d == null) continue;
                    object tidObj;
                    if (!d.TryGetValue("teamId", out tidObj) || tidObj == null)
                    {
                        driversWithoutTeam.Add(dkvp.Key);
                    }
                    else
                    {
                        int tid;
                        if (int.TryParse(tidObj.ToString(), out tid) && tid == 255)
                            driversWithoutTeam.Add(dkvp.Key);
                    }
                }
            }

            result["_debug"] = new Dictionary<string, object>
            {
                { "packetIdCounts", pktCounts },
                { "notes", store.Notes },
                { "integrity", new Dictionary<string, object>
                    {
                        { "driversWithoutTeam", driversWithoutTeam },
                        { "isSpectating", anySpectator },
                    }
                },
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
                        { "finalClassification", new Dictionary<string, object>
                            {
                                { "totalProcessed", store.DiagFcTotal },
                                { "newRegistered", store.DiagFcRegistered },
                            }
                        },
                        { "lobbyInfo", new Dictionary<string, object>
                            {
                                { "received", store.DiagLobbyInfoReceived },
                                { "numPlayers", store.DiagLobbyInfoPlayers },
                                { "tagsMapped", store.LobbyNumPlayers },
                                { "lobbyResolved", store.DiagLobbyResolved },
                                { "lobbyFailed", store.DiagLobbyFailed },
                                { "lobbyNameMap", store.DebugLobbyMap },
                                { "bestKnownTags", store.DebugBestKnownTags },
                                { "lobbyByTeamOnly", store.DebugLobbyByTeamOnly },
                                { "fullMyTeamGrid", store.CaptureFullMyTeam },
                                { "nameKeyConflicts", store.LastExportedNameKeyConflicts ?? new List<Dictionary<string, object>>() },
                            }
                        },
                    }
                },
            };

            return result;
        }

        private static Dictionary<string, object> FinalizeSession(
            string sid, SessionRun sess, SessionStore store, List<object> previousSessions,
            Dictionary<string, ParticipantEntry> globalTeamByTag = null)
        {
            // Deduplicate drivers by physical car identity before building any results
            int dedupMerged = DeduplicateDrivers(sess);

            // Retroactive name resolution: replace generic tags with real names from bestKnownTags
            RetroResolveNames(sess, store);

            // Drop AI+0-lap phantoms before FC (Python parity); strip stale TagsByCarIdx
            RemovePhantomDrivers(sess);

            // Remove ghost duplicate seats: generic 0-lap entries whose raceNumber_teamId
            // is already owned by a real-named driver (carIdx slot reassignment artifact).
            RemovePhantomDuplicateSeats(sess);

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

            // Best lap by tag — use minimum of scanned laps and SessionHistory best.
            // SessionHistory (Packet 11) best is authoritative per-driver and immune
            // to carIdx-reuse contamination that can pollute the Laps list.
            var bestByTag = new Dictionary<string, int>();
            foreach (var dkvp in sess.Drivers)
            {
                string tag = dkvp.Key;
                DriverRun dr = dkvp.Value;
                int bestMs = 0;
                foreach (var lap in dr.Laps)
                    if (lap.LapTimeMs > 0 && (bestMs == 0 || lap.LapTimeMs < bestMs))
                        bestMs = lap.LapTimeMs;
                if (dr.Best.ContainsKey("bestLapTimeMs") && dr.Best["bestLapTimeMs"] != null)
                {
                    int shBest;
                    if (int.TryParse(dr.Best["bestLapTimeMs"].ToString(), out shBest) && shBest > 0)
                        if (bestMs == 0 || shBest < bestMs)
                            bestMs = shBest;
                }
                if (bestMs == 0 && dr.LastSeenLapTimeMs > 0)
                    bestMs = dr.LastSeenLapTimeMs;
                if (bestMs > 0)
                    bestByTag[tag] = bestMs;
            }

            string stName = sess.SessionType.HasValue ? Lookups.LookupOrDefault(Lookups.SessionType, sess.SessionType.Value, "S") : "";
            bool isQuali = stName.IndexOf("Qualifying", StringComparison.OrdinalIgnoreCase) >= 0
                        || stName.IndexOf("Shootout", StringComparison.OrdinalIgnoreCase) >= 0;

            // Build results from FinalClassification
            var resultsOut = new List<Dictionary<string, object>>();
            int fcRowsClassified = 0, droppedSkipAi = 0, droppedGhost = 0, droppedDupTag = 0, emittedFromFc = 0;
            if (sess.FinalClassification != null && sess.FinalClassification.Classification != null)
            {
                var seenResultTags = new HashSet<string>();
                bool onlineAuthoritative = sess.NetworkGame == 1;
                foreach (var row in sess.FinalClassification.Classification)
                {
                    if (row == null || row.Position <= 0) continue;
                    fcRowsClassified++;
                    string tag;
                    if (!sess.TagsByCarIdx.TryGetValue(row.CarIdx, out tag))
                    {
                        // Online: default to Driver_{idx} so FC rows join the same bucket as lap ingestion
                        // (Car{N} only when duplicate disambiguation explicitly creates it).
                        tag = onlineAuthoritative
                            ? string.Format("Driver_{0}", row.CarIdx)
                            : string.Format("Car{0}", row.CarIdx);
                    }

                    // Try to recover unknown drivers from bestKnownTags
                    if (!sess.Drivers.ContainsKey(tag) && (IsGenericTag(tag) || tag.StartsWith("Car")))
                    {
                        ParticipantEntry pe;
                        sess.TeamByCarIdx.TryGetValue(row.CarIdx, out pe);
                        if (pe != null)
                        {
                            string lk = string.Format("{0}_{1}", pe.RaceNumber, pe.TeamId);
                            var bkt = store.DebugBestKnownTags;
                            string resolved;
                            if (bkt.TryGetValue(lk, out resolved) && !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved))
                                tag = resolved;
                        }
                    }

                    tag = ReconcileFcTagWithExistingDriver(sess, row.CarIdx, tag);

                    // Online quali: FC lists 22 slots; overflow carIdx (>= Participants peak) + empty generic = ghost.
                    int peakNa = sess.ParticipantsPeakNumActive;
                    if (isQuali && onlineAuthoritative && peakNa > 0 && row.NumLaps == 0
                        && row.CarIdx >= peakNa && IsGenericTag(tag) && row.BestLapTimeMs == 0)
                    {
                        droppedGhost++;
                        continue;
                    }

                    DriverRun preDrFc;
                    sess.Drivers.TryGetValue(tag, out preDrFc);
                    if (ShouldSkipFcAiGridFillerRow(sess, tag, row, preDrFc, store))
                    {
                        droppedSkipAi++;
                        continue;
                    }

                    if (!seenResultTags.Add(tag))
                    {
                        if (!onlineAuthoritative)
                        {
                            droppedDupTag++;
                            continue;
                        }
                        // Never use Driver_{raceNumber} here: many online/MyTeam slots share the same
                        // default race number (#2, etc.). The first duplicate would steal Driver_11 from
                        // carIdx 11's telemetry bucket — seen as Car11/Car0 stubs with 0 laps (Monza-style).
                        string altTag = string.Format("Driver_{0}", row.CarIdx);
                        if (seenResultTags.Contains(altTag))
                            altTag = string.Format("Car{0}", row.CarIdx);
                        tag = altTag;
                        tag = ReconcileFcTagWithExistingDriver(sess, row.CarIdx, tag);
                        if (!seenResultTags.Add(tag))
                        {
                            droppedDupTag++;
                            continue;
                        }
                    }

                    // A classified driver who completed laps is NEVER filtered.
                    if (row.NumLaps > 0)
                        goto fc_accept;

                    // Ghost filter: offline — unregistered Car{N} + 0 laps = empty grid slot.
                    // Online — FC row is authoritative; keep stub even with no telemetry.
                    if (GhostCarRe.IsMatch(tag) && !sess.Drivers.ContainsKey(tag))
                    {
                        if (!onlineAuthoritative)
                        {
                            droppedGhost++;
                            continue;
                        }
                    }

                    // 0 laps — empty generic slots (offline); online keeps FC-backed rows.
                    if (!onlineAuthoritative)
                    {
                        ParticipantEntry slotInfo;
                        sess.TeamByCarIdx.TryGetValue(row.CarIdx, out slotInfo);
                        if (IsGenericTag(tag) && (slotInfo == null || slotInfo.TeamId == 255))
                        {
                            droppedGhost++;
                            continue;
                        }
                    }
                    fc_accept:
                    emittedFromFc++;

                    // FC is the authoritative source for who participated.
                    // Always create DriverRun for valid FC entries.
                    if (!sess.Drivers.ContainsKey(tag))
                    {
                        sess.Drivers[tag] = new DriverRun { CarIdx = row.CarIdx };
                    }
                    if (!idxToTag.ContainsKey(row.CarIdx))
                        idxToTag[row.CarIdx] = tag;

                    int bestMs;
                    bestByTag.TryGetValue(tag, out bestMs);
                    if (bestMs == 0 && row.BestLapTimeMs > 0)
                        bestMs = (int)row.BestLapTimeMs;

                    int? totalMs = row.TotalRaceTimeSec > 0 ? (int?)(int)(row.TotalRaceTimeSec * 1000) : null;

                    string statusStr;
                    Lookups.ResultStatus.TryGetValue(row.ResultStatus, out statusStr);
                    if (statusStr == null) statusStr = "FinishedOrUnknown";

                    // Qualifying quirk: game marks drivers as Retired at session transition
                    if (statusStr == "Retired" && row.BestLapTimeMs > 0 && isQuali)
                        statusStr = "Finished";

                    var drData = sess.Drivers.ContainsKey(tag) ? sess.Drivers[tag] : null;
                    ParticipantEntry teamInfo;
                    sess.TeamByCarIdx.TryGetValue(row.CarIdx, out teamInfo);
                    if (teamInfo == null && globalTeamByTag != null)
                        globalTeamByTag.TryGetValue(tag, out teamInfo);

                    string teamName = ResolveTeamName(teamInfo);
                    int? raceNumberOut = teamInfo != null ? (int?)teamInfo.RaceNumber : null;

                    resultsOut.Add(new Dictionary<string, object>
                    {
                        { "position", (int)row.Position },
                        { "tag", tag },
                        { "carIdx", row.CarIdx },
                        { "raceNumber", raceNumberOut },
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

            // Fallback: drivers in sess.Drivers that have real data (team info, laps)
            // but were not captured by any FC packet. This happens in spectator mode
            // when the FC numCars undercounts the actual classified field.
            if (resultsOut.Count > 0)
            {
                var resultTags = new HashSet<string>();
                foreach (var res in resultsOut)
                    resultTags.Add(res["tag"] as string ?? "");

                int maxPos = 0;
                foreach (var res in resultsOut)
                {
                    int pos = (int)res["position"];
                    if (pos > maxPos) maxPos = pos;
                }

                foreach (var dkvp in sess.Drivers)
                {
                    string tag = dkvp.Key;
                    if (resultTags.Contains(tag)) continue;
                    DriverRun dr = dkvp.Value;
                    if (IsPhantomEntry(tag, dr, sess)) continue;

                    ParticipantEntry teamInfo;
                    sess.TeamByCarIdx.TryGetValue(dr.CarIdx, out teamInfo);
                    if (teamInfo == null && globalTeamByTag != null)
                        globalTeamByTag.TryGetValue(tag, out teamInfo);
                    string teamName = ResolveTeamName(teamInfo);

                    int bestMs;
                    bestByTag.TryGetValue(tag, out bestMs);

                    maxPos++;
                    int? rnFall = teamInfo != null ? (int?)teamInfo.RaceNumber : null;
                    resultsOut.Add(new Dictionary<string, object>
                    {
                        { "position", maxPos },
                        { "tag", tag },
                        { "carIdx", dr.CarIdx },
                        { "raceNumber", rnFall },
                        { "teamId", teamInfo != null ? (object)(int)teamInfo.TeamId : null },
                        { "teamName", teamName },
                        { "grid", dr.GridPosition > 0 ? (object)dr.GridPosition : null },
                        { "numLaps", EffectiveLapCount(dr) },
                        { "bestLapTimeMs", bestMs > 0 ? (object)bestMs : null },
                        { "bestLapTime", bestMs > 0 ? MsToStr(bestMs) : "" },
                        { "totalTimeMs", null },
                        { "totalTime", "" },
                        { "penaltiesTimeSec", 0 },
                        { "pitStops", dr.PitStops.Count },
                        { "status", "DidNotFinish" },
                        { "numPenalties", 0 },
                    });
                }
            }

            // Fallback: reconstruct race results from lap data
            bool isRace = stName.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                       || stName.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;

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

            ApplyRaceClassifiedLappedFlags(resultsOut, isRace);

            // Aggregate penalty data from PenaltySnapshots into results.
            // The FC packet in online multiplayer sends 0 for all penalty fields;
            // compute real values from captured penalty events.
            foreach (var res in resultsOut)
            {
                string rTag = res["tag"] as string;
                DriverRun penDr;
                if (string.IsNullOrEmpty(rTag) || !sess.Drivers.TryGetValue(rTag, out penDr))
                    continue;

                int driverNumLaps = 0;
                object nlObj;
                if (res.TryGetValue("numLaps", out nlObj) && nlObj != null)
                    int.TryParse(nlObj.ToString(), out driverNumLaps);

                int nDt = 0, nSg = 0, nTimePen = 0, nWarn = 0, penTimeTotal = 0;
                bool phantomFiltered = false;
                foreach (var ps in penDr.PenaltySnapshots)
                {
                    if (ps.EventCode == "COLL" || ps.EventCode == "DTSV" || ps.EventCode == "SGSV")
                        continue;
                    if (!ps.PenaltyType.HasValue) continue;

                    // F1 25 UDP bug: duplicate UnservedStopGo/UnservedDriveThrough events
                    // fired on last lap or after race end. Filter out post-race duplicates.
                    int inf = ps.InfringementType.HasValue ? ps.InfringementType.Value : -1;
                    if ((inf == 45 || inf == 46) && driverNumLaps > 0
                        && ps.LapNum.HasValue && ps.LapNum.Value > driverNumLaps)
                    {
                        phantomFiltered = true;
                        continue;
                    }

                    int pt = ps.PenaltyType.Value;
                    if (pt == 0) nDt++;
                    else if (pt == 1) nSg++;
                    else if (pt == 4)
                    {
                        nTimePen++;
                        if (ps.TimeSec.HasValue && ps.TimeSec.Value != 255)
                            penTimeTotal += ps.TimeSec.Value;
                    }
                    else if (pt == 5) nWarn++;
                }

                int nActionable = nDt + nSg + nTimePen;
                object existingObj;

                int existingPen = 0;
                if (res.TryGetValue("numPenalties", out existingObj) && existingObj != null)
                    int.TryParse(existingObj.ToString(), out existingPen);
                if (nActionable > existingPen)
                    res["numPenalties"] = nActionable;

                if (!res.ContainsKey("numWarnings") || res["numWarnings"] == null || nWarn > 0)
                    res["numWarnings"] = nWarn;

                if (!res.ContainsKey("numDriveThroughPens") || res["numDriveThroughPens"] == null || nDt > 0)
                    res["numDriveThroughPens"] = nDt;

                if (!res.ContainsKey("numStopGoPens") || res["numStopGoPens"] == null || nSg > 0)
                    res["numStopGoPens"] = nSg;

                int existingPenTime = 0;
                if (res.TryGetValue("penaltiesTimeSec", out existingObj) && existingObj != null)
                    int.TryParse(existingObj.ToString(), out existingPenTime);
                if (phantomFiltered || penTimeTotal > existingPenTime)
                    res["penaltiesTimeSec"] = penTimeTotal;
            }

            // Build drivers payload
            var driversOut = new Dictionary<string, object>();
            // When we have FC-derived results (race), use those as source of truth — includes all classified drivers
            // (DNF, DSQ, etc.); avoids excluding real pilots with Car_X tags.
            if (resultsOut.Count > 0)
            {
                foreach (var res in resultsOut)
                {
                    string tag = res["tag"] as string;
                    if (string.IsNullOrEmpty(tag)) continue;
                    DriverRun dr;
                    if (!sess.Drivers.TryGetValue(tag, out dr)) continue;
                    driversOut[tag] = FinalizeDriver(tag, dr, sess, idxToTag, globalTeamByTag);
                }
            }
            else
            {
                foreach (var dkvp in sess.Drivers)
                {
                    string tag = dkvp.Key;
                    DriverRun dr = dkvp.Value;
                    if (IsPhantomEntry(tag, dr, sess)) continue;
                    driversOut[tag] = FinalizeDriver(tag, dr, sess, idxToTag, globalTeamByTag);
                }
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

            var lobbySettings = BuildLobbySettings(sess);

            bool isTerminalRace = stName.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                || stName.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;
            bool fcMissing = isTerminalRace && fcRowsClassified == 0 && sess.Drivers.Count > 0;

            int exportDiagEmitted = resultsOut.Count;
            var exportDiag = new Dictionary<string, object>
            {
                { "fcRowsPositionGt0", fcRowsClassified },
                { "fcRowsEmittedFromLoop", emittedFromFc },
                { "resultsCountAfterRenumber", exportDiagEmitted },
                { "fcDroppedSkipAi", droppedSkipAi },
                { "fcDroppedGhostOrGenericSlot", droppedGhost },
                { "fcDroppedDuplicateTag", droppedDupTag },
                { "driversMergedByDedup", dedupMerged },
                { "fcAuthoritativeOnline", sess.NetworkGame == 1 },
                { "fcMissingForRace", fcMissing },
                { "resultSource", fcMissing ? "fallback_telemetry" : (emittedFromFc > 0 ? "final_classification" : "none") },
            };

            return new Dictionary<string, object>
            {
                { "sessionUID", sid },
                { "sessionType", Lookups.Label(Lookups.SessionType, sess.SessionType, "SessionType") },
                { "track", Lookups.Label(Lookups.Tracks, sess.TrackId.HasValue ? (int?)(int)sess.TrackId.Value : null, "Track") },
                { "weather", Lookups.Label(Lookups.Weather, sess.Weather.HasValue ? (int?)(int)sess.Weather.Value : null, "Weather") },
                { "trackTempC", sess.LatestTrackTempC },
                { "airTempC", sess.LatestAirTempC },
                { "forecastAccuracy", Lookups.LookupOrDefault(Lookups.ForecastAccuracyMap, sess.ForecastAccuracy, "ForecastAccuracy") },
                { "weatherTimeline", weatherTimelineOut },
                { "weatherForecast", FinalizeWeatherForecast(sess) },
                { "lastPacketMs", sess.LastPacketMs },
                { "sessionEndedAtMs", sess.SessionEndedAtMs },
                { "safetyCar", safetyOut },
                { "networkGame", sess.NetworkGame == 1 },
                { "participantsPeakNumActive", sess.ParticipantsPeakNumActive },
                { "isSpectating", sess.IsSpectating != 0 },
                { "lobbySettings", lobbySettings },
                { "awards", awards },
                { "results", resultsOut },
                { "drivers", driversOut },
                { "events", eventsOut },
                { "exportDiagnostics", exportDiag },
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

            // Tyre stints (endLap=255 = current/last stint per F1 spec; include it)
            var tyreOut = new List<object>();
            foreach (var ts in dr.TyreStints)
            {
                object endLapObj, taObj, tvObj;
                int rawEndLap = ts.TryGetValue("endLap", out endLapObj) && endLapObj != null ? Convert.ToInt32(endLapObj) : 0;
                ts.TryGetValue("tyreActual", out taObj);
                ts.TryGetValue("tyreVisual", out tvObj);
                int taId = taObj != null ? Convert.ToInt32(taObj) : -1;
                int tvId = tvObj != null ? Convert.ToInt32(tvObj) : -1;
                // Skip only truly empty: no tyre data
                if (taId <= 0 && tvId <= 0)
                    continue;

                // F1 25 endLap is 0-indexed; convert to 1-indexed for output.
                // endLap=255 is sentinel "until end of race" — keep as-is.
                int endLap = (rawEndLap >= 0 && rawEndLap < 255) ? rawEndLap + 1 : rawEndLap;

                tyreOut.Add(new Dictionary<string, object>
                {
                    { "endLap", endLap },
                    { "tyreActualId", taId >= 0 ? (object)taId : null },
                    { "tyreActual", taId >= 0 ? Lookups.LookupOrDefault(Lookups.TyreActual, taId, "Tyre") : "Unknown" },
                    { "tyreVisualId", tvId >= 0 ? (object)tvId : null },
                    { "tyreVisual", tvId >= 0 ? Lookups.LookupOrDefault(Lookups.TyreVisual, tvId, "Tyre") : "Unknown" },
                });
            }

            // Penalties vs collisions vs served markers
            var penTl = new List<object>();
            var collTl = new List<object>();
            bool hasDtsvOrSgsv = false;
            foreach (var ps in dr.PenaltySnapshots)
            {
                if (ps.EventCode == "DTSV" || ps.EventCode == "SGSV")
                { hasDtsvOrSgsv = true; break; }
            }
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
                if (ps.EventCode == "DTSV" || ps.EventCode == "SGSV")
                    continue;

                var entry = new Dictionary<string, object> { { "tsMs", ps.TsMs } };
                if (ps.PenaltyType.HasValue)
                {
                    entry["penaltyType"] = ps.PenaltyType.Value;
                    entry["penaltyTypeName"] = Lookups.LookupOrDefault(Lookups.PenaltyType, ps.PenaltyType.Value, "PenaltyType");
                    int pt = ps.PenaltyType.Value;
                    entry["category"] = pt == 5 ? "warning" : pt == 6 ? "disqualification"
                        : pt == 16 ? "retired" : (pt == 0 || pt == 1 || pt == 4) ? "penalty" : "other";

                    // Determine status: unserved conversion, reminder, served (via DTSV/SGSV), or issued
                    int inf = ps.InfringementType.HasValue ? ps.InfringementType.Value : -1;
                    if (inf == 45 || inf == 46)
                        entry["status"] = "unserved";
                    else if (pt == 3)
                        entry["status"] = "reminder";
                    else if (hasDtsvOrSgsv && (pt == 0 || pt == 1))
                        entry["status"] = "served";
                    else
                        entry["status"] = "issued";
                }
                else
                {
                    entry["status"] = "issued";
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

            // Last-lap wear fix: session often ends before we get the final lap transition
            // in spectator mode. Target the last COMPLETED lap (from laps data), not the
            // in-progress lap (lastCurrentLapNum), which would be one ahead.
            int maxLapFromLaps = sortedLaps.Count > 0 ? sortedLaps[sortedLaps.Count - 1].LapNumber : 0;
            int targetLap = maxLapFromLaps > 0 ? maxLapFromLaps : ((dr.LastCurrentLapNum ?? 0) > 0 ? (dr.LastCurrentLapNum.Value - 1) : 0);
            if (targetLap > 0)
            {
                bool hasTargetLap = false;
                foreach (var tw in twSorted)
                    if (tw.LapNumber == targetLap) { hasTargetLap = true; break; }
                if (!hasTargetLap && dr.LatestTyreWear != null)
                {
                    var tw = dr.LatestTyreWear;
                    float avg = (float)Math.Round((tw.RL + tw.RR + tw.FL + tw.FR) / 4.0f, 1);
                    twOut.Add(new Dictionary<string, object>
                    {
                        { "lapNumber", targetLap },
                        { "rl", tw.RL }, { "rr", tw.RR }, { "fl", tw.FL }, { "fr", tw.FR }, { "avg", avg },
                    });
                }
                else if (!hasTargetLap && twSorted.Count > 0)
                {
                    var last = twSorted[twSorted.Count - 1];
                    twOut.Add(new Dictionary<string, object>
                    {
                        { "lapNumber", targetLap },
                        { "rl", last.RL }, { "rr", last.RR }, { "fl", last.FL }, { "fr", last.FR }, { "avg", last.Avg },
                    });
                }
            }

            // Assign tyre compound to each wear entry by cross-referencing stints.
            // endLap in tyreOut is now 1-indexed (converted above); endLap=255 = "until end".
            if (tyreOut.Count > 0)
            {
                foreach (var twEntry in twOut)
                {
                    var twDict = twEntry as Dictionary<string, object>;
                    if (twDict == null) continue;
                    int lapNum = Convert.ToInt32(twDict["lapNumber"]);

                    Dictionary<string, object> matchingStint = null;
                    foreach (var stintObj in tyreOut)
                    {
                        var stint = stintObj as Dictionary<string, object>;
                        if (stint == null) continue;
                        int endLap = Convert.ToInt32(stint["endLap"]);
                        if (endLap == 255 || lapNum <= endLap)
                        {
                            matchingStint = stint;
                            break;
                        }
                    }

                    if (matchingStint != null)
                    {
                        twDict["tyreCompoundId"] = matchingStint.ContainsKey("tyreActualId") ? matchingStint["tyreActualId"] : null;
                        twDict["tyreCompound"] = matchingStint.ContainsKey("tyreActual") ? matchingStint["tyreActual"] : null;
                        twDict["tyreVisualId"] = matchingStint.ContainsKey("tyreVisualId") ? matchingStint["tyreVisualId"] : null;
                        twDict["tyreVisual"] = matchingStint.ContainsKey("tyreVisual") ? matchingStint["tyreVisual"] : null;
                    }
                }
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

            object driverAssistsOut = null;
            if (dr.AssistsCaptured)
            {
                // Match Python league_finalizer _build_driver_assists: string labels, not {id,name}
                driverAssistsOut = new Dictionary<string, object>
                {
                    { "tractionControl", Lookups.LookupOrDefault(Lookups.TractionControlMap, dr.TractionControl, "TC") },
                    { "antiLockBrakes", dr.AntiLockBrakes != 0 },
                };
            }

            object fuelTelemetryOut = null;
            if (dr.FuelCaptured)
            {
                fuelTelemetryOut = new Dictionary<string, object>
                {
                    { "fuelMix", Lookups.LookupOrDefault(Lookups.FuelMixMap, dr.FuelMixLast, "FuelMix") },
                    { "fuelCapacityKg", Math.Round(dr.FuelCapacityKg, 3) },
                    { "fuelInTankKgFirst", Math.Round(dr.FuelInTankFirst, 3) },
                    { "fuelInTankKgLast", Math.Round(dr.FuelInTankLast, 3) },
                    { "fuelRemainingLapsFirst", Math.Round(dr.FuelRemainingLapsFirst, 3) },
                    { "fuelRemainingLapsLast", Math.Round(dr.FuelRemainingLapsLast, 3) },
                };
            }

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
                { "driverAssists", driverAssistsOut },
                { "fuelTelemetry", fuelTelemetryOut },
            };
        }

        private static Dictionary<string, object> BuildLobbySettings(SessionRun sess)
        {
            if (!sess.LobbySettingsCaptured) return null;

            return new Dictionary<string, object>
            {
                { "safetyCarAndFlags", new Dictionary<string, object>
                    {
                        { "safetyCar", Lookups.LookupOrDefault(Lookups.SafetyCarSettingMap, sess.SafetyCarSetting, "SC") },
                        { "redFlags", Lookups.LookupOrDefault(Lookups.RedFlagsSettingMap, sess.RedFlagsSetting, "RF") },
                    }
                },
                { "rulesAndSimulation", new Dictionary<string, object>
                    {
                        { "ruleSet", Lookups.LookupOrDefault(Lookups.RuleSetMap, sess.RuleSet, "RuleSet") },
                        { "collisions", Lookups.LookupOrDefault(Lookups.CollisionsMap, sess.Collisions, "Coll") },
                        { "collisionsOffForFirstLapOnly", sess.CollisionsOffForFirstLapOnly != 0 },
                        { "cornerCuttingStringency", Lookups.LookupOrDefault(Lookups.CornerCuttingMap, sess.CornerCuttingStringency, "CC") },
                        { "parcFermeRules", sess.ParcFermeRules != 0 },
                        { "formationLap", sess.FormationLap != 0 },
                        { "equalCarPerformance", sess.EqualCarPerformance != 0 },
                    }
                },
                { "damageAndRealism", new Dictionary<string, object>
                    {
                        { "carDamage", Lookups.LookupOrDefault(Lookups.CarDamageMap, sess.CarDamage, "Dmg") },
                        { "carDamageRate", Lookups.LookupOrDefault(Lookups.CarDamageRateMap, sess.CarDamageRate, "DmgRate") },
                        { "surfaceType", Lookups.LookupOrDefault(Lookups.SurfaceTypeSettingMap, sess.SurfaceType, "Surf") },
                        { "lowFuelMode", Lookups.LookupOrDefault(Lookups.LowFuelModeMap, sess.LowFuelMode, "Fuel") },
                        { "tyreTemperature", Lookups.LookupOrDefault(Lookups.TyreTemperatureMap, sess.TyreTemperature, "TyreTemp") },
                        { "pitLaneTyreSim", sess.PitLaneTyreSim == 0 },
                    }
                },
                { "assists", new Dictionary<string, object>
                    {
                        { "steeringAssist", sess.SteeringAssist != 0 },
                        { "brakingAssist", Lookups.LookupOrDefault(Lookups.BrakingAssistMap, sess.BrakingAssist, "Brake") },
                        { "gearboxAssist", Lookups.LookupOrDefault(Lookups.GearboxAssistMap, sess.GearboxAssist, "Gear") },
                        { "pitAssist", sess.PitAssist != 0 },
                        { "pitReleaseAssist", sess.PitReleaseAssist != 0 },
                        { "ersAssist", sess.ERSAssist != 0 },
                        { "drsAssist", sess.DRSAssist != 0 },
                        { "dynamicRacingLine", Lookups.LookupOrDefault(Lookups.DynamicRacingLineMap, sess.DynamicRacingLine, "Line") },
                        { "dynamicRacingLineType", Lookups.LookupOrDefault(Lookups.DynamicRacingLineTypeMap, sess.DynamicRacingLineType, "LineType") },
                        { "raceStarts", sess.RaceStarts == 0 ? "Manual" : "Assisted" },
                        { "recoveryMode", Lookups.LookupOrDefault(Lookups.RecoveryModeMap, sess.RecoveryMode, "Rec") },
                        { "flashbackLimit", Lookups.LookupOrDefault(Lookups.FlashbackLimitMap, sess.FlashbackLimit, "FB") },
                    }
                },
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
            {
                int eff = EffectiveLapCount(dr);
                if (eff > maxLaps) maxLaps = eff;
            }

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

                int nLaps = EffectiveLapCount(dr);
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
                object clObj;
                bool classifiedLapped = r.TryGetValue("classifiedLapped", out clObj) && clObj is bool && (bool)clObj;
                if (status != "Finished" && status != "FinishedOrUnknown" && !classifiedLapped) continue;
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
                object clObj;
                bool classifiedLapped = r.TryGetValue("classifiedLapped", out clObj) && clObj is bool && (bool)clObj;
                string s = statusObj != null ? statusObj.ToString() : "";
                if ((s == "Finished" || s == "FinishedOrUnknown" || classifiedLapped) && posObj != null)
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
