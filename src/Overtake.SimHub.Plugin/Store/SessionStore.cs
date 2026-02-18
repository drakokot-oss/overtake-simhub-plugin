using System;
using System.Collections.Generic;
using System.Linq;
using Overtake.SimHub.Plugin.Finalizer;
using Overtake.SimHub.Plugin.Packets;
using Overtake.SimHub.Plugin.Parsers;

namespace Overtake.SimHub.Plugin.Store
{
    public class SessionStore
    {
        private const int MaxEventsPerSession = 8000;
        private const int MaxPitEventsPerDriver = 50;

        public bool Connected;
        public ulong? SessionUid;
        public long StartedAtMs;
        public long LastPacketMs;
        public Dictionary<int, int> PacketCounts = new Dictionary<int, int>();
        public List<string> Notes = new List<string>();
        public Dictionary<string, SessionRun> Sessions = new Dictionary<string, SessionRun>();

        // Cross-session name resolution: "raceNumber_teamId" -> real name
        private Dictionary<string, string> _bestKnownTags = new Dictionary<string, string>();

        // Diagnostics
        public int DiagShReceived;
        public int DiagShNoDriver;
        public int DiagShDedup;
        public int DiagShLapsParsed;
        public int DiagShLapsAccepted;
        public int DiagShLapsFiltered;
        public int DiagLdLapRecorded;
        public int DiagLdNoDriver;
        public int DiagLdNoPrevLap;
        public int DiagLdTimeZero;
        public int DiagLdAlreadyExists;
        public int DiagLdSanityFail;
        public int DiagLdEarlyRegister;
        public int DiagShEarlyRegister;
        public int DiagParticipantsReceived;
        public int DiagParticipantsNumActive;
        public int DiagPlayerCarIdx = -1;
        public int DiagPlayerRecoveredFromOverflow;

        private static readonly HashSet<string> IgnoreEvents =
            new HashSet<string> { "SPTP", "DRSE", "DRSD", "STLG", "BUTN" };

        public SessionStore()
        {
            StartedAtMs = NowMs();
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static string MsToStr(int ms)
        {
            if (ms <= 0) return "";
            double total = ms / 1000.0;
            int m = (int)(total / 60.0);
            double s = total - 60.0 * m;
            return string.Format("{0}:{1:00.000}", m, s);
        }

        private string GetSessionKey(PacketHeader header)
        {
            ulong sidVal = header.SessionUid;
            if (sidVal == 0 && SessionUid.HasValue)
                sidVal = SessionUid.Value;
            string sid = sidVal.ToString();
            if (!Sessions.ContainsKey(sid))
                Sessions[sid] = new SessionRun { SessionUID = sid };
            return sid;
        }

        private DriverRun EnsureDriver(string sid, int carIdx)
        {
            var sess = Sessions[sid];
            string tag;
            if (!sess.TagsByCarIdx.TryGetValue(carIdx, out tag) || tag == null)
                return null;
            DriverRun dr;
            if (!sess.Drivers.TryGetValue(tag, out dr))
            {
                dr = new DriverRun { Tag = tag, CarIdx = carIdx };
                sess.Drivers[tag] = dr;
            }
            return dr;
        }

        /// <summary>
        /// Register a placeholder driver when data arrives before the Participants packet.
        /// Returns the DriverRun if registration succeeded, null if the slot is empty.
        /// </summary>
        private DriverRun EarlyRegisterDriver(string sid, SessionRun sess, int carIdx)
        {
            if (carIdx < 0 || carIdx >= 22) return null;
            string placeholder = "Player_" + carIdx;
            sess.TagsByCarIdx[carIdx] = placeholder;
            return EnsureDriver(sid, carIdx);
        }

        /// <summary>
        /// Main entry point: ingests a parsed packet into the accumulated state.
        /// Called from OvertakePlugin.DataUpdate (SimHub main thread, ~60Hz).
        /// </summary>
        public void Ingest(ParsedPacket parsed)
        {
            if (parsed == null || parsed.Header == null) return;

            long nowMs = NowMs();
            LastPacketMs = nowMs;
            Connected = true;
            var header = parsed.Header;

            if (header.SessionUid != 0)
            {
                if (!SessionUid.HasValue)
                {
                    SessionUid = header.SessionUid;
                }
                else if (header.SessionUid != SessionUid.Value)
                {
                    if (Notes.Count < 500)
                        Notes.Add(string.Format("sessionUID changed: {0} -> {1}", SessionUid.Value, header.SessionUid));
                    SessionUid = header.SessionUid;
                }
            }

            int pid = header.PacketId;
            if (PacketCounts.ContainsKey(pid))
                PacketCounts[pid]++;
            else
                PacketCounts[pid] = 1;

            string sid = GetSessionKey(header);
            var sess = Sessions[sid];
            sess.LastPacketMs = nowMs;

            // Track player car index (255 = invalid/spectator)
            if (header.PlayerCarIndex < 255)
                sess.PlayerCarIndex = header.PlayerCarIndex;

            // 1) Session
            if (pid == 1 && parsed.Session != null)
                IngestSession(sess, parsed.Session, nowMs);

            // 4) Participants
            else if (pid == 4 && parsed.Participants != null)
                IngestParticipants(sess, parsed.Participants, header.PlayerCarIndex);

            // 3) Event
            else if (pid == 3 && parsed.Event != null)
                IngestEvent(sid, sess, parsed.Event, nowMs);

            // 11) SessionHistory
            else if (pid == 11 && parsed.SessionHistory != null)
                IngestSessionHistory(sid, sess, parsed.SessionHistory, nowMs);

            // 8) FinalClassification
            else if (pid == 8 && parsed.FinalClassification != null)
                IngestFinalClassification(sess, parsed.FinalClassification, nowMs);

            // 2) LapData
            else if (pid == 2 && parsed.LapData != null)
                IngestLapData(sid, sess, parsed.LapData, nowMs);

            // 10) CarDamage
            else if (pid == 10 && parsed.CarDamage != null)
                IngestCarDamage(sid, parsed.CarDamage);
        }

        private void IngestSession(SessionRun sess, SessionData s, long nowMs)
        {
            sess.SessionType = s.SessionType;
            sess.TrackId = s.TrackId;
            sess.Weather = s.Weather;
            sess.SafetyCarStatus = s.SafetyCarStatus;

            if (s.NumVirtualSafetyCarPeriods > sess.NumVSCDeployments)
                sess.NumVSCDeployments = s.NumVirtualSafetyCarPeriods;
            if (s.NumRedFlagPeriods > sess.NumRedFlagPeriods)
                sess.NumRedFlagPeriods = s.NumRedFlagPeriods;
            sess.NetworkGame = s.NetworkGame;

            sess.LatestTrackTempC = s.TrackTempC;
            sess.LatestAirTempC = s.AirTempC;

            if (s.Weather != sess.LastWeatherState)
            {
                sess.WeatherTimeline.Add(new WeatherTimelineEntry
                {
                    TsMs = nowMs,
                    Weather = s.Weather,
                    TrackTempC = s.TrackTempC,
                    AirTempC = s.AirTempC,
                });
                sess.LastWeatherState = s.Weather;
            }

            // Weather forecast (overwrite each tick — last snapshot wins)
            if (s.WeatherForecast != null && s.WeatherForecast.Length > 0)
            {
                var fc = new List<Dictionary<string, object>>();
                foreach (var f in s.WeatherForecast)
                {
                    fc.Add(new Dictionary<string, object>
                    {
                        { "timeOffsetMin", (int)f.TimeOffsetMin },
                        { "weather", (int)f.Weather },
                        { "trackTempC", (int)f.TrackTempC },
                        { "airTempC", (int)f.AirTempC },
                        { "rainPercentage", (int)f.RainPercentage },
                    });
                }
                sess.WeatherForecast = fc;
            }
        }

        private void IngestParticipants(SessionRun sess, ParticipantsData p, int playerCarIndex)
        {
            var tags = p.TagsByCarIdx;
            var entries = p.Entries;
            if (tags == null || tags.Count == 0) return;

            DiagParticipantsReceived++;
            DiagParticipantsNumActive = p.NumActiveCars;
            DiagPlayerCarIdx = playerCarIndex;

            // Include ALL valid overflow entries (cars beyond numActiveCars that have real data)
            if (p.OverflowEntries != null)
            {
                foreach (var ovKvp in p.OverflowEntries)
                {
                    int ovIdx = ovKvp.Key;
                    var ovEntry = ovKvp.Value;
                    if (tags.ContainsKey(ovIdx)) continue;
                    tags[ovIdx] = ovEntry.Name;
                    if (ovIdx == playerCarIndex) DiagPlayerRecoveredFromOverflow++;
                    if (entries != null)
                    {
                        if (ovIdx >= entries.Length)
                        {
                            var expanded = new ParticipantEntry[ovIdx + 1];
                            System.Array.Copy(entries, expanded, entries.Length);
                            entries = expanded;
                        }
                        entries[ovIdx] = ovEntry;
                    }
                }
            }

            // Cross-session name resolution
            foreach (var kvp in new Dictionary<int, string>(tags))
            {
                int carIdx = kvp.Key;
                string tag = kvp.Value;
                ParticipantEntry entry = (entries != null && carIdx < entries.Length) ? entries[carIdx] : null;
                int rn = (entry != null) ? entry.RaceNumber : 0;
                int tid = (entry != null) ? entry.TeamId : -1;
                string lookupKey = string.Format("{0}_{1}", rn, tid);

                bool isGeneric = tag.StartsWith("Driver_") || tag.StartsWith("Player_") || tag.StartsWith("Car");
                if (!isGeneric && !string.IsNullOrWhiteSpace(tag))
                {
                    _bestKnownTags[lookupKey] = tag;
                }
                else if (isGeneric && _bestKnownTags.ContainsKey(lookupKey))
                {
                    tags[carIdx] = _bestKnownTags[lookupKey];
                }
            }

            // Resolve player's gamer tag to real driver name via DriverId
            // Runs after cross-session resolution so generic names get resolved first
            if (playerCarIndex >= 0 && playerCarIndex < 255 && tags.ContainsKey(playerCarIndex))
            {
                var playerEntry = (entries != null && playerCarIndex < entries.Length) ? entries[playerCarIndex] : null;
                if (playerEntry != null && !playerEntry.AiControlled && playerEntry.DriverId != 255)
                {
                    string driverName;
                    if (Lookups.DriverById.TryGetValue(playerEntry.DriverId, out driverName))
                    {
                        string currentTag = tags[playerCarIndex];
                        bool alreadyKnown = false;
                        foreach (var kv in Lookups.DriverById)
                        {
                            if (string.Equals(kv.Value, currentTag, StringComparison.OrdinalIgnoreCase))
                            { alreadyKnown = true; break; }
                        }
                        if (!alreadyKnown)
                        {
                            tags[playerCarIndex] = driverName;
                        }
                    }
                }
            }

            // Rename existing drivers if tag changed
            foreach (var kvp in tags)
            {
                int carIdx = kvp.Key;
                string newTag = kvp.Value;
                string oldTag;
                if (sess.TagsByCarIdx.TryGetValue(carIdx, out oldTag) && oldTag != null && oldTag != newTag)
                {
                    DriverRun dr;
                    if (sess.Drivers.TryGetValue(oldTag, out dr))
                    {
                        sess.Drivers.Remove(oldTag);
                        dr.Tag = newTag;
                        DriverRun existing;
                        if (sess.Drivers.TryGetValue(newTag, out existing))
                        {
                            if (dr.Laps.Count > existing.Laps.Count)
                                sess.Drivers[newTag] = dr;
                        }
                        else
                        {
                            sess.Drivers[newTag] = dr;
                        }
                    }
                }
            }

            // Merge tags: add/update from current packet but keep previously known drivers
            foreach (var kvp in tags)
                sess.TagsByCarIdx[kvp.Key] = kvp.Value;

            // Merge team info: add/update but don't remove previously known teams
            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] != null)
                        sess.TeamByCarIdx[i] = entries[i];
                }
            }
        }

        private void IngestEvent(string sid, SessionRun sess, EventData evt, long nowMs)
        {
            string code = evt.Code;
            if (string.IsNullOrEmpty(code) || code.Length != 4) return;
            if (IgnoreEvents.Contains(code)) return;
            if (sess.Events.Count >= MaxEventsPerSession) return;

            var eventPayload = new Dictionary<string, object>
            {
                { "type", "EVENT" },
                { "code", code },
                { "tsMs", nowMs },
            };

            var data = new Dictionary<string, object>();
            if (code == "OVTK")
            {
                data["overtakerIdx"] = (int)evt.OvertakerIdx;
                data["overtakenIdx"] = (int)evt.OvertakenIdx;
            }
            else if (code == "PENA")
            {
                data["vehicleIdx"] = (int)evt.VehicleIdx;
                data["penaltyType"] = (int)evt.PenaltyType;
                data["infringementType"] = (int)evt.InfringementType;
                data["otherVehicleIdx"] = (int)evt.OtherVehicleIdx;
                data["timeSec"] = (int)evt.TimeSec;
                data["lapNum"] = (int)evt.LapNum;
                data["placesGained"] = (int)evt.PlacesGained;
            }
            else if (code == "COLL")
            {
                data["vehicle1Idx"] = (int)evt.Vehicle1Idx;
                data["vehicle2Idx"] = (int)evt.Vehicle2Idx;
            }
            else if (code == "RTMT")
            {
                data["vehicleIdx"] = (int)evt.RetiredVehicleIdx;
                data["reason"] = (int)evt.RetiredReason;
            }
            else if (code == "FTLP")
            {
                data["vehicleIdx"] = (int)evt.FastestLapVehicleIdx;
                data["lapTimeSec"] = evt.FastestLapTimeSec;
            }
            else if (code == "SCAR")
            {
                data["safetyCarType"] = (int)evt.SafetyCarType;
                data["eventType"] = (int)evt.SafetyCarEventType;
            }
            if (data.Count > 0) eventPayload["data"] = data;

            sess.Events.Add(eventPayload);

            if (code == "SEND")
            {
                sess.SessionEndedAtMs = nowMs;
            }
            else if (code == "SSTA")
            {
                bool isOnline = sess.NetworkGame == 1;
                bool hasData = sess.Drivers.Count > 0 || sess.Events.Count > 1;
                if (hasData && !isOnline)
                {
                    foreach (var dr in sess.Drivers.Values)
                        dr.Reset();
                    sess.Events.Clear();
                    sess.Events.Add(eventPayload);
                    sess.FinalClassification = null;
                    sess.SessionEndedAtMs = null;
                    sess.NumSafetyCarDeployments = 0;
                    sess.NumVSCDeployments = 0;
                }
            }
            else if (code == "SCAR")
            {
                if (evt.SafetyCarType == 1 && evt.SafetyCarEventType == 0)
                    sess.NumSafetyCarDeployments++;
            }
            else if (code == "PENA")
            {
                var d = EnsureDriver(sid, evt.VehicleIdx);
                if (d != null && d.PenaltySnapshots.Count < 100)
                {
                    d.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        PenaltyType = evt.PenaltyType,
                        InfringementType = evt.InfringementType,
                        OtherVehicleIdx = evt.OtherVehicleIdx,
                        TimeSec = evt.TimeSec,
                        LapNum = evt.LapNum,
                        PlacesGained = evt.PlacesGained,
                    });
                }
            }
            else if (code == "COLL")
            {
                int v1 = evt.Vehicle1Idx;
                int v2 = evt.Vehicle2Idx;
                var d1 = EnsureDriver(sid, v1);
                if (d1 != null && d1.PenaltySnapshots.Count < 100)
                {
                    d1.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        EventCode = "COLL",
                        OtherVehicleIdx = v2,
                    });
                }
                var d2 = EnsureDriver(sid, v2);
                if (d2 != null && d2.PenaltySnapshots.Count < 100)
                {
                    d2.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        EventCode = "COLL",
                        OtherVehicleIdx = v1,
                    });
                }
            }
        }

        private void IngestSessionHistory(string sid, SessionRun sess, SessionHistoryData sh, long nowMs)
        {
            DiagShReceived++;
            int carIdx = sh.CarIdx;
            var d = EnsureDriver(sid, carIdx);
            if (d == null)
            {
                if (sh.NumLaps > 0)
                {
                    d = EarlyRegisterDriver(sid, sess, carIdx);
                    if (d != null) DiagShEarlyRegister++;
                }
                if (d == null) { DiagShNoDriver++; return; }
            }

            var laps = sh.Laps;
            DiagShLapsParsed += (laps != null) ? laps.Length : 0;

            // Dedup hash: numLaps + first 60 lap times
            int hashInput = sh.NumLaps;
            if (laps != null)
            {
                int count = Math.Min(laps.Length, 60);
                for (int i = 0; i < count; i++)
                    hashInput = hashInput * 31 + (int)laps[i].LapTimeMs;
            }
            if (d.LastHistoryHash.HasValue && d.LastHistoryHash.Value == hashInput
                && (nowMs - d.LastHistoryUpdateMs) < 1200)
            {
                DiagShDedup++;
                return;
            }

            var newLaps = new List<LapRecord>();
            if (laps != null)
            {
                for (int i = 0; i < laps.Length; i++)
                {
                    int lapTimeMs = (int)laps[i].LapTimeMs;
                    int lapNumber = laps[i].LapNumber;
                    if (lapTimeMs <= 0 || lapNumber <= 0)
                    {
                        DiagShLapsFiltered++;
                        continue;
                    }
                    if (lapTimeMs < 5000 || lapTimeMs > 600000)
                    {
                        DiagShLapsFiltered++;
                        continue;
                    }
                    DiagShLapsAccepted++;
                    newLaps.Add(new LapRecord
                    {
                        LapNumber = lapNumber,
                        LapTimeMs = lapTimeMs,
                        LapTime = MsToStr(lapTimeMs),
                        Sector1Ms = laps[i].Sector1Ms,
                        Sector2Ms = laps[i].Sector2Ms,
                        Sector3Ms = laps[i].Sector3Ms,
                        ValidFlags = laps[i].ValidFlags,
                        TsMs = nowMs,
                    });
                }
            }

            if (newLaps.Count > 0)
            {
                d.Laps = newLaps;
                int maxLap = 0;
                for (int i = 0; i < newLaps.Count; i++)
                    if (newLaps[i].LapNumber > maxLap) maxLap = newLaps[i].LapNumber;
                d.LastRecordedLapNumber = maxLap;
                d.LastCurrentLapNum = maxLap + 1;
            }

            // Tyre stints from SessionHistory
            if (sh.TyreStints != null)
            {
                d.TyreStints.Clear();
                for (int i = 0; i < sh.TyreStints.Length; i++)
                {
                    var ts = sh.TyreStints[i];
                    d.TyreStints.Add(new Dictionary<string, object>
                    {
                        { "stintIndex", ts.StintIndex },
                        { "endLap", (int)ts.EndLap },
                        { "tyreActual", (int)ts.TyreActual },
                        { "tyreVisual", (int)ts.TyreVisual },
                    });
                }
            }

            // Best times
            if (sh.Best != null)
            {
                d.Best = new Dictionary<string, object>
                {
                    { "bestLapTimeLapNum", (int)sh.Best.BestLapTimeLapNum },
                    { "bestLapTimeMs", sh.Best.BestLapTimeMs > 0 ? (object)(int)sh.Best.BestLapTimeMs : null },
                    { "bestSector1LapNum", (int)sh.Best.BestSector1LapNum },
                    { "bestSector1Ms", sh.Best.BestSector1Ms > 0 ? (object)sh.Best.BestSector1Ms : null },
                    { "bestSector2LapNum", (int)sh.Best.BestSector2LapNum },
                    { "bestSector2Ms", sh.Best.BestSector2Ms > 0 ? (object)sh.Best.BestSector2Ms : null },
                    { "bestSector3LapNum", (int)sh.Best.BestSector3LapNum },
                    { "bestSector3Ms", sh.Best.BestSector3Ms > 0 ? (object)sh.Best.BestSector3Ms : null },
                };
            }

            d.LastHistoryHash = hashInput;
            d.LastHistoryUpdateMs = nowMs;
        }

        private void IngestFinalClassification(SessionRun sess, FinalClassificationData fc, long nowMs)
        {
            sess.FinalClassification = fc;
            sess.SessionEndedAtMs = nowMs;
            if (sess.Events.Count < MaxEventsPerSession)
            {
                sess.Events.Add(new Dictionary<string, object>
                {
                    { "type", "FINAL_CLASSIFICATION" },
                    { "code", "FINAL_CLASSIFICATION" },
                    { "tsMs", nowMs },
                });
            }
        }

        private void IngestLapData(string sid, SessionRun sess, LapDataEntry[] rows, long nowMs)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                int carIdx = row.CarIdx;
                var d = EnsureDriver(sid, carIdx);
                if (d == null)
                {
                    if (row.CarPosition > 0 || row.CurrentLapNum > 0)
                    {
                        d = EarlyRegisterDriver(sid, sess, carIdx);
                        if (d != null) DiagLdEarlyRegister++;
                    }
                    if (d == null) { DiagLdNoDriver++; continue; }
                }

                if (row.GridPosition > 0)
                    d.GridPosition = row.GridPosition;

                // Pit stops: track increments
                int numPit = row.NumPitStops;
                if (numPit >= 0 && numPit <= 10)
                {
                    if (!d.LastNumPitStops.HasValue)
                    {
                        d.LastNumPitStops = numPit;
                    }
                    else if (numPit > d.LastNumPitStops.Value)
                    {
                        if (d.PitStops.Count < MaxPitEventsPerDriver)
                        {
                            d.PitStops.Add(new PitStopRecord
                            {
                                NumPitStops = numPit,
                                TsMs = nowMs,
                                LapNum = row.CurrentLapNum > 0 ? (int?)row.CurrentLapNum : null,
                            });
                        }
                        d.LastNumPitStops = numPit;
                    }
                }

                int currentLap = row.CurrentLapNum;
                int lastLapTimeMs = (int)row.LastLapTimeInMS;
                int s1Ms = row.Sector1TimeInMS;
                int s2Ms = row.Sector2TimeInMS;

                if (lastLapTimeMs > 0 && lastLapTimeMs >= 5000 && lastLapTimeMs <= 600000)
                    d.LastSeenLapTimeMs = lastLapTimeMs;

                if (!d.LastCurrentLapNum.HasValue)
                {
                    DiagLdNoPrevLap++;
                }
                else if (currentLap > d.LastCurrentLapNum.Value)
                {
                    int completedLap = currentLap - 1;

                    // Tyre wear snapshot on lap completion
                    if (completedLap > 0 && d.LatestTyreWear != null)
                    {
                        bool twExists = false;
                        for (int j = 0; j < d.TyreWearPerLap.Count; j++)
                            if (d.TyreWearPerLap[j].LapNumber == completedLap) { twExists = true; break; }
                        if (!twExists)
                        {
                            var tw = d.LatestTyreWear;
                            d.TyreWearPerLap.Add(new TyreWearSnapshot
                            {
                                LapNumber = completedLap,
                                RL = tw.RL, RR = tw.RR, FL = tw.FL, FR = tw.FR,
                                Avg = (float)Math.Round((tw.RL + tw.RR + tw.FL + tw.FR) / 4.0, 1),
                            });
                        }
                    }

                    // Damage snapshot on lap completion
                    if (completedLap > 0 && d.LatestDamage != null)
                    {
                        bool dmgExists = false;
                        for (int j = 0; j < d.DamagePerLap.Count; j++)
                            if (d.DamagePerLap[j].LapNumber == completedLap) { dmgExists = true; break; }
                        if (!dmgExists)
                        {
                            var dmg = d.LatestDamage;
                            d.DamagePerLap.Add(new DamageSnapshot
                            {
                                LapNumber = completedLap,
                                WingFL = dmg.WingFrontLeft, WingFR = dmg.WingFrontRight, WingRear = dmg.WingRear,
                                TyreDmgRL = dmg.TyreDmgRL, TyreDmgRR = dmg.TyreDmgRR,
                                TyreDmgFL = dmg.TyreDmgFL, TyreDmgFR = dmg.TyreDmgFR,
                            });
                        }
                    }

                    // Record lap from LapData (fallback if SessionHistory didn't capture it)
                    if (lastLapTimeMs <= 0)
                    {
                        DiagLdTimeZero++;
                    }
                    else
                    {
                        int lapNumber = completedLap;
                        if (lapNumber <= d.LastRecordedLapNumber)
                        {
                            DiagLdAlreadyExists++;
                        }
                        else if (lastLapTimeMs < 5000 || lastLapTimeMs > 600000)
                        {
                            DiagLdSanityFail++;
                        }
                        else
                        {
                            bool exists = false;
                            for (int j = 0; j < d.Laps.Count; j++)
                                if (d.Laps[j].LapNumber == lapNumber) { exists = true; break; }
                            if (!exists)
                            {
                                int s3Ms = Math.Max(0, lastLapTimeMs - s1Ms - s2Ms);
                                d.Laps.Add(new LapRecord
                                {
                                    LapNumber = lapNumber,
                                    LapTimeMs = lastLapTimeMs,
                                    LapTime = MsToStr(lastLapTimeMs),
                                    Sector1Ms = s1Ms,
                                    Sector2Ms = s2Ms,
                                    Sector3Ms = s3Ms,
                                    ValidFlags = 0,
                                    TsMs = nowMs,
                                });
                                DiagLdLapRecorded++;
                                if (lapNumber > d.LastRecordedLapNumber)
                                    d.LastRecordedLapNumber = lapNumber;
                            }
                            else
                            {
                                DiagLdAlreadyExists++;
                            }
                        }
                    }
                }

                if (currentLap >= 0)
                    d.LastCurrentLapNum = currentLap;

                // Warnings tracking
                int totalWarn = row.TotalWarnings;
                int ccWarn = row.CornerCuttingWarnings;
                if (totalWarn > d.LastTotalWarnings || ccWarn > d.LastCornerCuttingWarnings)
                {
                    d.LastTotalWarnings = totalWarn;
                    d.LastCornerCuttingWarnings = ccWarn;
                }
            }
        }

        private void IngestCarDamage(string sid, CarDamageEntry[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                int carIdx = row.CarIdx;
                var d = EnsureDriver(sid, carIdx);
                if (d == null) continue;

                d.LatestTyreWear = new TyreWearValues
                {
                    RL = row.TyreWear.RL,
                    RR = row.TyreWear.RR,
                    FL = row.TyreWear.FL,
                    FR = row.TyreWear.FR,
                };
                d.LatestDamage = new DamageValues
                {
                    WingFrontLeft = row.Wing.FrontLeft,
                    WingFrontRight = row.Wing.FrontRight,
                    WingRear = row.Wing.Rear,
                    TyreDmgRL = (int)row.TyresDamage.RL,
                    TyreDmgRR = (int)row.TyresDamage.RR,
                    TyreDmgFL = (int)row.TyresDamage.FL,
                    TyreDmgFR = (int)row.TyresDamage.FR,
                };
            }
        }
    }
}
