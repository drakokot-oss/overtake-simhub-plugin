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

        private const int TAG_RELIABLE = 2;
        private const int TAG_UNRELIABLE = 1;
        private const int TAG_GENERIC = 0;

        public bool Connected;
        public ulong? SessionUid;
        public long StartedAtMs;
        public long LastPacketMs;
        public Dictionary<int, int> PacketCounts = new Dictionary<int, int>();
        public List<string> Notes = new List<string>();
        public Dictionary<string, SessionRun> Sessions = new Dictionary<string, SessionRun>();

        // Cross-session name resolution: "raceNumber_teamId" -> real name
        private Dictionary<string, string> _bestKnownTags = new Dictionary<string, string>();
        private Dictionary<string, int> _bestKnownTagReliability = new Dictionary<string, int>();

        // LobbyInfo name lookup: "raceNumber_teamId" -> name (including generic names).
        // LobbyInfo slot index does NOT correspond to in-session carIdx, so we
        // must NOT use it for carIdx-based seeding. Instead we match via (teamId, raceNumber).
        private Dictionary<string, string> _lobbyNameByTeamRn = new Dictionary<string, string>();
        // Fallback: teamId-only → name. Only used when (raceNumber,teamId) key fails.
        // Stores only NON-generic names. If multiple players share a teamId, value = null (ambiguous).
        private Dictionary<int, string> _lobbyNameByTeamOnly = new Dictionary<int, string>();
        // Track distinct lookup_keys per teamId to detect shared teams.
        private Dictionary<int, HashSet<string>> _lobbyTeamKeys = new Dictionary<int, HashSet<string>>();
        public int LobbyNumPlayers;

        public Dictionary<string, string> DebugLobbyMap { get { return new Dictionary<string, string>(_lobbyNameByTeamRn); } }
        public Dictionary<string, string> DebugBestKnownTags { get { return new Dictionary<string, string>(_bestKnownTags); } }
        public Dictionary<string, string> DebugLobbyByTeamOnly
        {
            get
            {
                var d = new Dictionary<string, string>();
                foreach (var kvp in _lobbyNameByTeamOnly)
                    d[kvp.Key.ToString()] = kvp.Value ?? "(ambiguous)";
                return d;
            }
        }

        // Diagnostics
        public int DiagLobbyResolved;
        public int DiagLobbyFailed;
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
        public int DiagFcTotal;
        public int DiagFcRegistered;
        public int DiagLobbyInfoReceived;
        public int DiagLobbyInfoPlayers;

        private static readonly HashSet<string> IgnoreEvents =
            new HashSet<string> { "SPTP", "DRSE", "DRSD", "STLG", "BUTN" };

        // Track the last-known trackId to detect lobby changes.
        // When trackId changes, we clear cross-session name caches because
        // raceNumber_teamId keys can be reused by different players in different lobbies.
        private int? _lastTrackId;

        public SessionStore()
        {
            StartedAtMs = NowMs();
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Detect lobby change: when trackId changes, clear all cross-session name caches.
        /// raceNumber_teamId keys are reused by different players in different lobbies,
        /// so stale entries would cause wrong names.
        /// </summary>
        private void CheckLobbyChange(int newTrackId)
        {
            if (_lastTrackId.HasValue && _lastTrackId.Value != newTrackId)
            {
                if (Notes.Count < 500)
                    Notes.Add(string.Format("LOBBY CHANGE detected: trackId {0}->{1}. Clearing name caches.",
                        _lastTrackId.Value, newTrackId));
                _bestKnownTags.Clear();
                _bestKnownTagReliability.Clear();
                _lobbyNameByTeamRn.Clear();
                _lobbyNameByTeamOnly.Clear();
                _lobbyTeamKeys.Clear();
                DiagLobbyResolved = 0;
                DiagLobbyFailed = 0;
            }
            _lastTrackId = newTrackId;
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
            {
                var newSess = new SessionRun { SessionUID = sid };
                // Cross-session carry-over from the most recent previous session.
                // Only within the SAME lobby (same trackId). Across different lobbies,
                // carIdx slots are used by different players, so carrying names would
                // assign the wrong gamertag to the wrong car.
                if (Sessions.Count > 0)
                {
                    SessionRun prevSess = null;
                    foreach (var kvp in Sessions)
                        prevSess = kvp.Value;
                    if (prevSess != null)
                    {
                        bool sameLobby = _lastTrackId.HasValue && prevSess.TrackId.HasValue
                            && prevSess.TrackId.Value == _lastTrackId.Value;
                        foreach (var kvp in prevSess.TagsByCarIdx)
                        {
                            if (sameLobby)
                            {
                                newSess.TagsByCarIdx[kvp.Key] = kvp.Value;
                            }
                            else
                            {
                                // Different lobby: only carry generic placeholders.
                                // Non-generic names belong to different players now.
                                if (IsGenericTag(kvp.Value))
                                    newSess.TagsByCarIdx[kvp.Key] = kvp.Value;
                            }
                        }
                        foreach (var kvp in prevSess.TeamByCarIdx)
                        {
                            if (sameLobby)
                                newSess.TeamByCarIdx[kvp.Key] = kvp.Value;
                            // Don't carry team data across lobbies either:
                            // different player may be at same carIdx with different team.
                        }
                        if (prevSess.MaxNumActiveCars > newSess.MaxNumActiveCars)
                            newSess.MaxNumActiveCars = prevSess.MaxNumActiveCars;
                        if (sameLobby)
                        {
                            foreach (var kvp in prevSess.TagReliability)
                                newSess.TagReliability[kvp.Key] = kvp.Value;
                            foreach (var kvp in prevSess.HumanCarIdxs)
                                newSess.HumanCarIdxs[kvp.Key] = kvp.Value;
                        }
                    }
                }
                Sessions[sid] = newSess;
            }
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
        /// Uses "Car_X" prefix so it's distinct from the game's "Player" dedup naming.
        /// Returns the DriverRun if registration succeeded, null if the slot is empty.
        /// </summary>
        private DriverRun EarlyRegisterDriver(string sid, SessionRun sess, int carIdx)
        {
            if (carIdx < 0 || carIdx >= 22) return null;
            string placeholder = "Car_" + carIdx;
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
                IngestParticipants(sid, sess, parsed.Participants, header.PlayerCarIndex);

            // 9) LobbyInfo
            else if (pid == 9 && parsed.LobbyInfo != null)
                IngestLobbyInfo(sid, sess, parsed.LobbyInfo);

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

            // 7) CarStatus (per-car assists)
            else if (pid == 7 && parsed.CarStatus != null)
                IngestCarStatus(sid, parsed.CarStatus);

            // 10) CarDamage
            else if (pid == 10 && parsed.CarDamage != null)
                IngestCarDamage(sid, parsed.CarDamage);
        }

        private void IngestSession(SessionRun sess, SessionData s, long nowMs)
        {
            sess.SessionType = s.SessionType;
            sess.TrackId = s.TrackId;

            // Detect lobby changes (track change = different lobby)
            if (s.TrackId >= 0)
                CheckLobbyChange(s.TrackId);
            sess.Weather = s.Weather;
            sess.SafetyCarStatus = s.SafetyCarStatus;

            if (s.NumVirtualSafetyCarPeriods > sess.NumVSCDeployments)
                sess.NumVSCDeployments = s.NumVirtualSafetyCarPeriods;
            if (s.NumRedFlagPeriods > sess.NumRedFlagPeriods)
                sess.NumRedFlagPeriods = s.NumRedFlagPeriods;
            sess.NetworkGame = s.NetworkGame;

            sess.LatestTrackTempC = s.TrackTempC;
            sess.LatestAirTempC = s.AirTempC;
            sess.IsSpectating = s.IsSpectating;
            sess.GamePaused = s.GamePaused;
            sess.SpectatorCarIndex = s.SpectatorCarIndex;

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

            // Lobby settings — keep updating until we see non-zero data.
            // Early Session packets may arrive before the game populates lobby fields.
            bool hasData = s.CarDamage != 0 || s.Collisions != 0 || s.RuleSet != 0
                || s.SafetyCarSetting != 0 || s.RedFlagsSetting != 0
                || s.SteeringAssist != 0 || s.GearboxAssist != 0
                || s.FormationLap != 0 || s.EqualCarPerformance != 0;

            if (!sess.LobbySettingsCaptured || hasData)
            {
                sess.ForecastAccuracy = s.ForecastAccuracy;
                sess.SteeringAssist = s.SteeringAssist;
                sess.BrakingAssist = s.BrakingAssist;
                sess.GearboxAssist = s.GearboxAssist;
                sess.PitAssist = s.PitAssist;
                sess.PitReleaseAssist = s.PitReleaseAssist;
                sess.ERSAssist = s.ERSAssist;
                sess.DRSAssist = s.DRSAssist;
                sess.DynamicRacingLine = s.DynamicRacingLine;
                sess.DynamicRacingLineType = s.DynamicRacingLineType;
                sess.RuleSet = s.RuleSet;
                sess.RaceStarts = s.RaceStarts;
                sess.RecoveryMode = s.RecoveryMode;
                sess.FlashbackLimit = s.FlashbackLimit;
                sess.EqualCarPerformance = s.EqualCarPerformance;
                sess.SurfaceType = s.SurfaceType;
                sess.LowFuelMode = s.LowFuelMode;
                sess.TyreTemperature = s.TyreTemperature;
                sess.PitLaneTyreSim = s.PitLaneTyreSim;
                sess.CarDamage = s.CarDamage;
                sess.CarDamageRate = s.CarDamageRate;
                sess.Collisions = s.Collisions;
                sess.CollisionsOffForFirstLapOnly = s.CollisionsOffForFirstLapOnly;
                sess.CornerCuttingStringency = s.CornerCuttingStringency;
                sess.ParcFermeRules = s.ParcFermeRules;
                sess.FormationLap = s.FormationLap;
                sess.SafetyCarSetting = s.SafetyCarSetting;
                sess.RedFlagsSetting = s.RedFlagsSetting;
                if (hasData) sess.LobbySettingsCaptured = true;
            }
        }

        private const float CarStatusFuelCapacityMinKg = 5f;

        private void IngestCarStatus(string sid, CarStatusEntry[] entries)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] == null) continue;
                var d = EnsureDriver(sid, i);
                if (d == null) continue;

                byte newTc = entries[i].TractionControl;
                byte newAbs = entries[i].AntiLockBrakes;

                if (!d.AssistsCaptured)
                {
                    d.TractionControl = newTc;
                    d.AntiLockBrakes = newAbs;
                    d.AssistsCaptured = true;
                }
                else
                {
                    // Prefer lowest (most restrictive) value: post-race packets
                    // send defaults (TC=2/Full, ABS=1/On) that must not overwrite
                    // the correct mid-race values.
                    if (newTc < d.TractionControl) d.TractionControl = newTc;
                    if (newAbs < d.AntiLockBrakes) d.AntiLockBrakes = newAbs;
                }

                float cap = entries[i].FuelCapacity;
                if (cap >= CarStatusFuelCapacityMinKg)
                {
                    if (!d.FuelFirstSampleSet)
                    {
                        d.FuelInTankFirst = entries[i].FuelInTank;
                        d.FuelRemainingLapsFirst = entries[i].FuelRemainingLaps;
                        d.FuelFirstSampleSet = true;
                    }
                    d.FuelInTankLast = entries[i].FuelInTank;
                    d.FuelRemainingLapsLast = entries[i].FuelRemainingLaps;
                    d.FuelCapacityKg = cap;
                    d.FuelMixLast = entries[i].FuelMix;
                    d.FuelCaptured = true;
                }
            }
        }

        private void IngestLobbyInfo(string sid, SessionRun sess, LobbyInfoData lobby)
        {
            if (lobby == null || lobby.Entries == null) return;
            DiagLobbyInfoReceived++;
            DiagLobbyInfoPlayers = lobby.NumPlayers;
            LobbyNumPlayers = lobby.NumPlayers;

            for (int i = 0; i < lobby.Entries.Length; i++)
            {
                var entry = lobby.Entries[i];
                if (entry == null) continue;
                if (entry.TeamId == 255) continue;

                string name = entry.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                bool nameIsGeneric = IsGenericTag(name);
                string lookupKey = string.Format("{0}_{1}", entry.CarNumber, entry.TeamId);

                int nameReliability;
                if (entry.ShowOnlineNames != 0 && !entry.AiControlled)
                    nameReliability = TAG_RELIABLE;
                else
                    nameReliability = TAG_UNRELIABLE;

                string existing;
                if (nameIsGeneric && _lobbyNameByTeamRn.TryGetValue(lookupKey, out existing)
                    && !IsGenericTag(existing))
                {
                    // keep existing non-generic name
                }
                else
                {
                    _lobbyNameByTeamRn[lookupKey] = name;
                }

                int teamIdVal = entry.TeamId;
                HashSet<string> teamKeys;
                if (!_lobbyTeamKeys.TryGetValue(teamIdVal, out teamKeys))
                {
                    teamKeys = new HashSet<string>();
                    _lobbyTeamKeys[teamIdVal] = teamKeys;
                }
                teamKeys.Add(lookupKey);
                if (teamKeys.Count > 1)
                    _lobbyNameByTeamOnly[teamIdVal] = null;

                if (!nameIsGeneric)
                {
                    int existingBktRel;
                    if (!_bestKnownTagReliability.TryGetValue(lookupKey, out existingBktRel))
                        existingBktRel = TAG_GENERIC;
                    if (nameReliability >= existingBktRel)
                    {
                        _bestKnownTags[lookupKey] = name;
                        _bestKnownTagReliability[lookupKey] = nameReliability;
                    }

                    if (nameReliability >= TAG_RELIABLE && teamKeys.Count <= 1)
                    {
                        string existingTeamName;
                        if (!_lobbyNameByTeamOnly.TryGetValue(teamIdVal, out existingTeamName))
                        {
                            _lobbyNameByTeamOnly[teamIdVal] = name;
                        }
                        else if (existingTeamName != null && existingTeamName != name)
                        {
                            _lobbyNameByTeamOnly[teamIdVal] = null;
                        }
                    }
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex _genericTagRe =
            new System.Text.RegularExpressions.Regex(
                @"^(Driver_\d+|Player_\d+|Player #\d+|Player|Car_\d+|Car\d+)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool IsGenericTag(string tag)
        {
            return !string.IsNullOrEmpty(tag) && _genericTagRe.IsMatch(tag);
        }

        private void IngestParticipants(string sid, SessionRun sess, ParticipantsData p, int playerCarIndex)
        {
            var entries = p.Entries;
            if (entries == null) return;

            DiagParticipantsReceived++;
            DiagParticipantsNumActive = p.NumActiveCars;
            DiagPlayerCarIdx = playerCarIndex;

            if (p.NumActiveCars > sess.MaxNumActiveCars)
                sess.MaxNumActiveCars = p.NumActiveCars;

            // Track human carIdx: once seen as human, stays human forever in this session.
            // Only check the active range (i < NumActiveCars) to prevent overflow
            // entries (AI grid fillers) from being falsely marked as human when the
            // game sends early packets with incorrect AiControlled=false flags.
            int humanScanLimit = Math.Min(p.NumActiveCars, entries.Length);
            for (int i = 0; i < humanScanLimit; i++)
            {
                if (entries[i] == null) continue;
                if (!entries[i].AiControlled && entries[i].Platform != 255)
                    sess.HumanCarIdxs[i] = true;
            }

            // Compute per-entry reliability for this packet
            var entryReliability = new Dictionary<int, int>();
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] == null) continue;
                bool showOn = entries[i].ShowOnlineNames != 0;
                bool ai = entries[i].AiControlled;
                byte plat = entries[i].Platform;
                bool wasHuman;
                if (!sess.HumanCarIdxs.TryGetValue(i, out wasHuman)) wasHuman = false;

                if (showOn && !ai && plat != 255)
                    entryReliability[i] = TAG_RELIABLE;
                else if (wasHuman && !showOn)
                    entryReliability[i] = TAG_GENERIC;
                else if (ai && wasHuman)
                    entryReliability[i] = TAG_GENERIC;
                else if (ai && !wasHuman)
                    entryReliability[i] = TAG_UNRELIABLE;
                else
                    entryReliability[i] = TAG_UNRELIABLE;
            }

            // In offline mode (networkGame=0), showOnlineNames is irrelevant.
            if (sess.NetworkGame == 0)
            {
                for (int i = 0; i < p.NumActiveCars && i < entries.Length; i++)
                {
                    if (entries[i] != null
                        && entries[i].ShowOnlineNames == 0
                        && !entries[i].AiControlled
                        && !string.IsNullOrWhiteSpace(entries[i].Name)
                        && !IsGenericTag(entries[i].Name))
                    {
                        p.TagsByCarIdx[i] = entries[i].Name;
                        entryReliability[i] = TAG_RELIABLE;
                    }
                }
            }

            // ── Step 1: Store team data for ALL 22 entries ──
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] == null) continue;
                if (entries[i].TeamId == 255 && entries[i].RaceNumber == 0) continue;

                ParticipantEntry existingTeam;
                if (sess.TeamByCarIdx.TryGetValue(i, out existingTeam) && existingTeam != null && existingTeam.TeamId != 255)
                {
                    if (entries[i].TeamId == 255)
                        continue;
                }
                sess.TeamByCarIdx[i] = entries[i];
            }

            // ── Step 2: Force Driver_X for unreliable names on known-human carIdx ──
            var tags = p.TagsByCarIdx;
            if (tags == null) tags = new Dictionary<int, string>();

            foreach (var kvp in new Dictionary<int, string>(tags))
            {
                int rel;
                if (!entryReliability.TryGetValue(kvp.Key, out rel)) rel = TAG_UNRELIABLE;
                if (rel == TAG_GENERIC && !IsGenericTag(kvp.Value))
                    tags[kvp.Key] = "Driver_" + kvp.Key;
            }

            // ── Step 3: Resolve generic names via lobby lookup ──
            foreach (var kvp in new Dictionary<int, string>(tags))
            {
                int carIdx = kvp.Key;
                string tag = kvp.Value;
                ParticipantEntry entry = (carIdx < entries.Length) ? entries[carIdx] : null;
                int rn = (entry != null) ? entry.RaceNumber : 0;
                int tid = (entry != null) ? entry.TeamId : -1;
                string lookupKey = string.Format("{0}_{1}", rn, tid);

                int rel;
                if (!entryReliability.TryGetValue(carIdx, out rel)) rel = TAG_UNRELIABLE;

                if (!IsGenericTag(tag) && !string.IsNullOrWhiteSpace(tag))
                {
                    // Only store in bestKnownTags if source is reliable (not AI seat name)
                    if (rel >= TAG_RELIABLE)
                    {
                        _bestKnownTags[lookupKey] = tag;
                        _bestKnownTagReliability[lookupKey] = rel;
                    }
                }
                else if (IsGenericTag(tag))
                {
                    string resolved = ResolveLobbyName(rn, tid);
                    if (!string.IsNullOrEmpty(resolved))
                        tags[carIdx] = resolved;
                }
            }

            // Resolve player's gamer tag to real driver name via DriverId
            if (playerCarIndex >= 0 && playerCarIndex < 255 && tags.ContainsKey(playerCarIndex))
            {
                var playerEntry = (playerCarIndex < entries.Length) ? entries[playerCarIndex] : null;
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
                            tags[playerCarIndex] = driverName;
                    }
                }
            }

            // ── Step 4: Authoritative tag assignment with reliability-aware downgrade ──
            var existingTagToIdx = new Dictionary<string, int>();
            foreach (var kvp in sess.TagsByCarIdx)
                existingTagToIdx[kvp.Value] = kvp.Key;

            var safeTags = new Dictionary<int, string>();
            var safeReliability = new Dictionary<int, int>();
            foreach (var kvp in tags)
            {
                int carIdx = kvp.Key;
                string newTag = kvp.Value;
                if (string.IsNullOrWhiteSpace(newTag)) continue;

                int newRel;
                if (!entryReliability.TryGetValue(carIdx, out newRel)) newRel = TAG_UNRELIABLE;

                int existingIdx;
                if (existingTagToIdx.TryGetValue(newTag, out existingIdx) && existingIdx != carIdx)
                {
                    string oldIdxTagInPacket;
                    bool oldIdxInPacket = tags.TryGetValue(existingIdx, out oldIdxTagInPacket);
                    bool allowMove;
                    if (!oldIdxInPacket)
                        allowMove = true;
                    else if (oldIdxTagInPacket != newTag)
                        allowMove = true;
                    else
                        allowMove = false;

                    if (allowMove)
                    {
                        string oldTagAtOldIdx;
                        if (sess.TagsByCarIdx.TryGetValue(existingIdx, out oldTagAtOldIdx)
                            && oldTagAtOldIdx == newTag)
                        {
                            sess.TagsByCarIdx.Remove(existingIdx);
                            DriverRun dr;
                            if (sess.Drivers.TryGetValue(newTag, out dr))
                                dr.CarIdx = carIdx;
                        }
                        existingTagToIdx.Remove(newTag);
                    }
                    else
                    {
                        continue;
                    }
                }

                // Reliability-aware "never downgrade" logic
                if (IsGenericTag(newTag))
                {
                    string currentTag;
                    if (sess.TagsByCarIdx.TryGetValue(carIdx, out currentTag)
                        && !string.IsNullOrEmpty(currentTag) && !IsGenericTag(currentTag))
                    {
                        int currentRel;
                        if (!sess.TagReliability.TryGetValue(carIdx, out currentRel))
                            currentRel = TAG_GENERIC;
                        if (currentRel >= TAG_RELIABLE)
                            continue;
                        // Allow downgrade of UNRELIABLE AI name to Driver_X
                        // only when this carIdx is confirmed human with privacy on
                        bool wasHuman;
                        if (!sess.HumanCarIdxs.TryGetValue(carIdx, out wasHuman)) wasHuman = false;
                        var ent = (carIdx < entries.Length) ? entries[carIdx] : null;
                        bool showOn = (ent != null) ? ent.ShowOnlineNames != 0 : false;
                        if (wasHuman && !showOn)
                        { /* allow Driver_X to replace AI name */ }
                        else
                            continue;
                    }
                }

                bool dupInBatch = false;
                foreach (var other in safeTags)
                {
                    if (other.Value == newTag && other.Key != carIdx)
                    { dupInBatch = true; break; }
                }
                if (dupInBatch) continue;

                safeTags[carIdx] = newTag;
                safeReliability[carIdx] = newRel;
            }

            // Rename existing drivers if tag changed
            foreach (var kvp in safeTags)
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

            foreach (var kvp in safeTags)
            {
                sess.TagsByCarIdx[kvp.Key] = kvp.Value;
                int rel;
                if (safeReliability.TryGetValue(kvp.Key, out rel))
                    sess.TagReliability[kvp.Key] = rel;
            }

            if (sess.TagsByCarIdx.Count > sess.MaxNumActiveCars)
                sess.MaxNumActiveCars = sess.TagsByCarIdx.Count;

            foreach (var kvp in safeTags)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                EnsureDriver(sid, kvp.Key);
            }

            // ── Step 5: Resolve names for ALL entries that have team data but no/generic tag ──
            ResolveNamesFromLobby(sid, sess);
        }

        /// <summary>
        /// For any carIdx that has team data in TeamByCarIdx but no tag (or a
        /// generic/placeholder tag), attempt to resolve the real name via
        /// _bestKnownTags or _lobbyNameByTeamRn.
        /// </summary>
        private string ResolveLobbyName(int raceNumber, int teamId)
        {
            string lookupKey = string.Format("{0}_{1}", raceNumber, teamId);
            string resolved = null;

            if (_bestKnownTags.ContainsKey(lookupKey))
                resolved = _bestKnownTags[lookupKey];
            else if (_lobbyNameByTeamRn.ContainsKey(lookupKey))
                resolved = _lobbyNameByTeamRn[lookupKey];

            // Fallback: teamId-only when primary key misses
            if ((string.IsNullOrEmpty(resolved) || IsGenericTag(resolved)) && teamId >= 0 && teamId < 255)
            {
                string teamOnly;
                if (_lobbyNameByTeamOnly.TryGetValue(teamId, out teamOnly) && teamOnly != null)
                    resolved = teamOnly;
            }

            if (IsGenericTag(resolved))
                return null;
            return resolved;
        }

        private void ResolveNamesFromLobby(string sid, SessionRun sess)
        {
            var tagToIdx = new Dictionary<string, int>();
            foreach (var kvp in sess.TagsByCarIdx)
                tagToIdx[kvp.Value] = kvp.Key;

            foreach (var kvp in new Dictionary<int, ParticipantEntry>(sess.TeamByCarIdx))
            {
                int carIdx = kvp.Key;
                var team = kvp.Value;
                if (team == null || team.TeamId == 255) continue;

                string existingTag;
                sess.TagsByCarIdx.TryGetValue(carIdx, out existingTag);

                if (!string.IsNullOrEmpty(existingTag) && !IsGenericTag(existingTag))
                    continue;

                string resolved = ResolveLobbyName(team.RaceNumber, team.TeamId);
                if (string.IsNullOrEmpty(resolved))
                {
                    DiagLobbyFailed++;
                    // Still register with placeholder if no tag at all — ensures
                    // LapData/SessionHistory capture starts immediately.
                    if (string.IsNullOrEmpty(existingTag))
                    {
                        string placeholder = "Driver_" + carIdx;
                        if (!tagToIdx.ContainsKey(placeholder))
                        {
                            sess.TagsByCarIdx[carIdx] = placeholder;
                            tagToIdx[placeholder] = carIdx;
                            EnsureDriver(sid, carIdx);
                        }
                    }
                    continue;
                }

                int existingIdx;
                if (tagToIdx.TryGetValue(resolved, out existingIdx) && existingIdx != carIdx)
                    continue;

                DiagLobbyResolved++;

                if (!string.IsNullOrEmpty(existingTag) && existingTag != resolved)
                {
                    DriverRun dr;
                    if (sess.Drivers.TryGetValue(existingTag, out dr))
                    {
                        sess.Drivers.Remove(existingTag);
                        dr.Tag = resolved;
                        if (!sess.Drivers.ContainsKey(resolved))
                            sess.Drivers[resolved] = dr;
                    }
                    tagToIdx.Remove(existingTag);
                }

                sess.TagsByCarIdx[carIdx] = resolved;
                tagToIdx[resolved] = carIdx;
                EnsureDriver(sid, carIdx);
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
            else if (code == "DTSV" || code == "SGSV")
            {
                data["vehicleIdx"] = (int)evt.ServedVehicleIdx;
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
            else if (code == "DTSV" || code == "SGSV")
            {
                var d = EnsureDriver(sid, evt.ServedVehicleIdx);
                if (d != null && d.PenaltySnapshots.Count < 100)
                {
                    d.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        EventCode = code,
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
                // Merge: keep existing laps not in new SH, update/add from new SH.
                // In spectator mode, the game may send partial SH (only the latest
                // lap) for non-spectated cars. A full replace would erase all
                // previously accumulated laps. Merging preserves them.
                var merged = new Dictionary<int, LapRecord>();
                foreach (var lap in d.Laps)
                    merged[lap.LapNumber] = lap;
                foreach (var lap in newLaps)
                    merged[lap.LapNumber] = lap;
                d.Laps = merged.Values.OrderBy(l => l.LapNumber).ToList();
                int maxLap = 0;
                for (int i = 0; i < d.Laps.Count; i++)
                    if (d.Laps[i].LapNumber > maxLap) maxLap = d.Laps[i].LapNumber;
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
            // Accumulate FC: merge entries across repeated broadcasts.
            // The game may send FC packets with varying numCars in spectator mode.
            // Keep entries with Position > 0 from ANY packet so no classified car is lost.
            if (sess.FinalClassification == null)
            {
                sess.FinalClassification = fc;
            }
            else
            {
                var existing = sess.FinalClassification;
                for (int i = 0; i < fc.Classification.Length && i < existing.Classification.Length; i++)
                {
                    var newRow = fc.Classification[i];
                    if (newRow == null) continue;
                    var oldRow = existing.Classification[i];
                    if (newRow.Position > 0 && (oldRow == null || oldRow.Position <= 0))
                        existing.Classification[i] = newRow;
                    else if (newRow.Position > 0)
                        existing.Classification[i] = newRow;
                }
                if (fc.NumCars > existing.NumCars)
                    existing.NumCars = fc.NumCars;
            }
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

            if (fc.Classification == null) return;
            string sid = null;
            foreach (var kvp in Sessions)
            {
                if (kvp.Value == sess) { sid = kvp.Key; break; }
            }
            if (sid == null) return;

            // Count actual classified entries (Position > 0) across the full 22-slot array.
            // In spectator mode fc.NumCars may undercount; the real count is more reliable.
            int classifiedCount = 0;
            for (int i = 0; i < fc.Classification.Length; i++)
            {
                if (fc.Classification[i] != null && fc.Classification[i].Position > 0)
                    classifiedCount++;
            }
            int effectiveNumCars = Math.Max(fc.NumCars, classifiedCount);
            if (effectiveNumCars > sess.MaxNumActiveCars)
                sess.MaxNumActiveCars = effectiveNumCars;

            // FC rows are indexed by car index (fixed array[22]). row.CarIdx = array
            // index = true carIdx, set by the parser. Only process entries with Position > 0.
            for (int fcRow = 0; fcRow < fc.Classification.Length; fcRow++)
            {
                var row = fc.Classification[fcRow];
                if (row == null || row.Position <= 0) continue;

                int carIdx = row.CarIdx;

                // Cross-session tag recovery (prefer non-generic tags)
                if (!sess.TagsByCarIdx.ContainsKey(carIdx))
                {
                    string bestCross = null;
                    ParticipantEntry bestCrossTeam = null;
                    foreach (var otherKvp in Sessions)
                    {
                        if (otherKvp.Value == sess) continue;
                        string crossTag;
                        if (otherKvp.Value.TagsByCarIdx.TryGetValue(carIdx, out crossTag)
                            && !string.IsNullOrEmpty(crossTag))
                        {
                            if (!IsGenericTag(crossTag))
                            {
                                bestCross = crossTag;
                                ParticipantEntry ct;
                                if (otherKvp.Value.TeamByCarIdx.TryGetValue(carIdx, out ct))
                                    bestCrossTeam = ct;
                                break;
                            }
                            if (bestCross == null)
                            {
                                bestCross = crossTag;
                                ParticipantEntry ct;
                                if (otherKvp.Value.TeamByCarIdx.TryGetValue(carIdx, out ct))
                                    bestCrossTeam = ct;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(bestCross))
                    {
                        sess.TagsByCarIdx[carIdx] = bestCross;
                        if (bestCrossTeam != null && !sess.TeamByCarIdx.ContainsKey(carIdx))
                            sess.TeamByCarIdx[carIdx] = bestCrossTeam;
                    }
                }

                // FC validates that this car is real. Try lobby resolution before falling
                // back to a placeholder so drivers aren't silently dropped.
                if (!sess.TagsByCarIdx.ContainsKey(carIdx))
                {
                    ParticipantEntry team;
                    if (sess.TeamByCarIdx.TryGetValue(carIdx, out team) && team != null && team.TeamId != 255)
                    {
                        string resolved = ResolveLobbyName(team.RaceNumber, team.TeamId);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            bool isDup = false;
                            foreach (var existKvp in sess.TagsByCarIdx)
                            {
                                if (existKvp.Key != carIdx && existKvp.Value == resolved)
                                { isDup = true; break; }
                            }
                            if (!isDup)
                                sess.TagsByCarIdx[carIdx] = resolved;
                        }
                    }
                }
                if (!sess.TagsByCarIdx.ContainsKey(carIdx))
                {
                    sess.TagsByCarIdx[carIdx] = "Car_" + carIdx;
                    DiagFcRegistered++;
                }

                DiagFcTotal++;
                var d = EnsureDriver(sid, carIdx);
                if (d == null) continue;

                // Tyre stints: FinalClassification is authoritative — use it when we have valid stint data
                if (row.NumTyreStints > 0 && row.TyreStintsActual != null && row.TyreStintsVisual != null && row.TyreStintsEndLaps != null)
                {
                    var stints = new List<Dictionary<string, object>>();
                    int numStints = Math.Min(row.NumTyreStints, (byte)8);
                    for (int i = 0; i < numStints; i++)
                    {
                        byte endLap = row.TyreStintsEndLaps[i];
                        byte tyreActual = row.TyreStintsActual[i];
                        byte tyreVisual = row.TyreStintsVisual[i];

                        if (endLap == 0 && tyreActual == 0 && tyreVisual == 0) continue;

                        stints.Add(new Dictionary<string, object>
                        {
                            { "stintIndex", i },
                            { "endLap", (int)endLap },
                            { "tyreActual", (int)tyreActual },
                            { "tyreVisual", (int)tyreVisual },
                        });
                    }

                    // FC is official; replace SessionHistory data when we have any valid stints
                    if (stints.Count > 0)
                        d.TyreStints = stints;
                }
            }

            // Register ALL cars from TeamByCarIdx that still lack tags.
            for (int ci = 0; ci < 22; ci++)
            {
                if (sess.TagsByCarIdx.ContainsKey(ci)) continue;
                ParticipantEntry team;
                if (!sess.TeamByCarIdx.TryGetValue(ci, out team)) continue;
                if (team == null || team.TeamId == 255) continue;

                string resolved = ResolveLobbyName(team.RaceNumber, team.TeamId);
                if (!string.IsNullOrEmpty(resolved))
                {
                    bool isDup = false;
                    foreach (var kv in sess.TagsByCarIdx)
                    {
                        if (kv.Key != ci && kv.Value == resolved)
                        { isDup = true; break; }
                    }
                    if (!isDup)
                    {
                        sess.TagsByCarIdx[ci] = resolved;
                        EnsureDriver(sid, ci);
                        continue;
                    }
                }
                sess.TagsByCarIdx[ci] = "Driver_" + ci;
                EnsureDriver(sid, ci);
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
                    // In spectator mode, active cars may have valid ResultStatus/DriverStatus
                    // even when CarPosition and CurrentLapNum are zeroed (outside camera view).
                    if (row.CarPosition > 0 || row.CurrentLapNum > 0
                        || row.ResultStatus > 1 || row.DriverStatus > 0)
                    {
                        d = EarlyRegisterDriver(sid, sess, carIdx);
                        if (d != null) DiagLdEarlyRegister++;
                    }
                    if (d == null) { DiagLdNoDriver++; continue; }
                }

                if (row.GridPosition > 0)
                    d.GridPosition = row.GridPosition;
                if (row.CarPosition > 0)
                    d.CarPosition = row.CarPosition;

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
