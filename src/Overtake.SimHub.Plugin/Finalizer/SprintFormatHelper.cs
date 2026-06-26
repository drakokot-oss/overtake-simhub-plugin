using System.Collections.Generic;
using System.Linq;
using Overtake.SimHub.Plugin.Store;

namespace Overtake.SimHub.Plugin.Finalizer
{
    /// <summary>
    /// Sprint Format weekend detection and session-type inference.
    ///
    /// v1.1.45 — F1 26 (and some F1 25 lobbies) report the Sprint Race with
    /// sessionType id=15 ("Race"), the same id as the Main Race. Relying only on
    /// Lookups.SessionType[id]=="Race" for auto-export therefore fired at the end
    /// of the Sprint Race, auto-cleaned the store, and lost Qualifying + Main Race
    /// (Austria_20260622_215918_77AEAC.otk regression).
    ///
    /// Strategy: when the capture already contains a Sprint Shootout (ids 10..14),
    /// treat a closing "Race" as terminal ONLY if a Qualifying session (ids 5..9)
    /// is already present in the store (Main Quali always precedes Main Race).
    /// The first wire-id=15 session in the capture is inferred as Race2 (Sprint Race)
    /// at export time even when the wire id was 15.
    /// </summary>
    public static class SprintFormatHelper
    {
        public static bool IsSprintShootoutType(int sessionTypeId)
        {
            return sessionTypeId >= 10 && sessionTypeId <= 14;
        }

        public static bool IsQualifyingType(int sessionTypeId)
        {
            return sessionTypeId >= 5 && sessionTypeId <= 9;
        }

        public static bool IsRaceWireType(int sessionTypeId)
        {
            string name;
            return Lookups.SessionType.TryGetValue(sessionTypeId, out name) && name == "Race";
        }

        public static bool HasSprintShootout(IEnumerable<SessionRun> sessions)
        {
            if (sessions == null) return false;
            foreach (var s in sessions)
            {
                if (s != null && s.SessionType.HasValue
                    && IsSprintShootoutType(s.SessionType.Value))
                    return true;
            }
            return false;
        }

        public static bool HasAnyQualifying(IEnumerable<SessionRun> sessions)
        {
            if (sessions == null) return false;
            foreach (var s in sessions)
            {
                if (s != null && s.SessionType.HasValue
                    && IsQualifyingType(s.SessionType.Value))
                    return true;
            }
            return false;
        }

        /// <summary>Main qualifying (Q1/Q2/Q3), not Sprint Shootout nor Short Quali.</summary>
        public static bool HasMainQualifying(IEnumerable<SessionRun> sessions)
        {
            if (sessions == null) return false;
            foreach (var s in sessions)
            {
                if (s != null && s.SessionType.HasValue)
                {
                    int id = s.SessionType.Value;
                    if (id >= 5 && id <= 7) return true;
                }
            }
            return false;
        }

        public static bool HasSprintRaceSessionId16(IEnumerable<SessionRun> sessions)
        {
            if (sessions == null) return false;
            foreach (var s in sessions)
            {
                if (s != null && s.SessionType.HasValue && s.SessionType.Value == 16)
                    return true;
            }
            return false;
        }

        public static int CountWireRaceSessions(IEnumerable<SessionRun> sessions)
        {
            int n = 0;
            if (sessions == null) return 0;
            foreach (var s in sessions)
            {
                if (s != null && s.SessionType.HasValue && s.SessionType.Value == 15)
                    n++;
            }
            return n;
        }

        private static IList<SessionRun> AsList(IEnumerable<SessionRun> sessions)
        {
            var list = sessions as IList<SessionRun>;
            return list ?? sessions.ToList();
        }

        /// <summary>
        /// True when <paramref name="sess"/> is the first wire-id=15 session in the
        /// capture. Tie-break: lower LastPacketMs wins; equal ms uses list order.
        /// </summary>
        public static bool IsFirstWireRaceSession(SessionRun sess, IList<SessionRun> allSessions)
        {
            if (sess == null || !sess.SessionType.HasValue || sess.SessionType.Value != 15)
                return false;
            long thisMs = sess.LastPacketMs;
            int thisIdx = allSessions.IndexOf(sess);
            for (int i = 0; i < allSessions.Count; i++)
            {
                SessionRun s = allSessions[i];
                if (s == null || s == sess || !s.SessionType.HasValue) continue;
                if (s.SessionType.Value != 15) continue;
                if (s.LastPacketMs < thisMs) return false;
                if (s.LastPacketMs == thisMs && i < thisIdx) return false;
            }
            return true;
        }

        /// <summary>
        /// F1 26 mislabels Sprint Race as wire id=15. When TWO id=15 sessions exist,
        /// the chronologically first is the Sprint Race; a lone id=15 is always Main.
        /// </summary>
        public static bool IsSprintRaceMislabeledAsWire15(SessionRun sess, IList<SessionRun> allSessions)
        {
            if (sess == null || !sess.SessionType.HasValue || sess.SessionType.Value != 15)
                return false;
            if (CountWireRaceSessions(allSessions) < 2)
                return false;
            return IsFirstWireRaceSession(sess, allSessions);
        }

        /// <summary>
        /// Any race session: "Race" (15 + the online ids 19/25/26/29/30/36), "Race2"
        /// (16) or "Race3" (17). v1.1.47: the discriminator for terminal/export logic is
        /// now "race-like" instead of strictly "Race", because F1 26 Sprint Format
        /// weekends do NOT keep the F1 25 id convention. Observed (Austria/Abu Dhabi,
        /// offline F1 26): Sprint Race = wire id 15 ("Race"), Main Race = wire id 16
        /// ("Race2") -- the INVERSE of F1 25 (Sprint=16, Main=15). Online F1 26 sends
        /// BOTH as id=15. The official EA 2026 spec confirms 15=Race, 16=Race2, 17=Race3,
        /// but does not pin which the game assigns to Sprint vs Main in a weekend, so we
        /// rely on chronology instead of the raw id.
        /// </summary>
        public static bool IsRaceLikeType(int sessionTypeId)
        {
            string name;
            if (!Lookups.SessionType.TryGetValue(sessionTypeId, out name) || name == null)
                return false;
            return name.StartsWith("Race");
        }

        public static int CountRaceLikeSessions(IEnumerable<SessionRun> sessions)
        {
            int n = 0;
            if (sessions == null) return 0;
            foreach (var s in sessions)
                if (s != null && s.SessionType.HasValue && IsRaceLikeType(s.SessionType.Value))
                    n++;
            return n;
        }

        /// <summary>
        /// True when <paramref name="sess"/> is the chronologically last race-like session
        /// in the capture (highest LastPacketMs; tie-break: later list index wins). The
        /// Main Race of a Sprint Format weekend is always the last race.
        /// </summary>
        public static bool IsLastRaceSession(SessionRun sess, IList<SessionRun> allSessions)
        {
            if (sess == null || !sess.SessionType.HasValue || !IsRaceLikeType(sess.SessionType.Value))
                return false;
            long thisMs = sess.LastPacketMs;
            int thisIdx = allSessions.IndexOf(sess);
            for (int i = 0; i < allSessions.Count; i++)
            {
                SessionRun s = allSessions[i];
                if (s == null || s == sess || !s.SessionType.HasValue) continue;
                if (!IsRaceLikeType(s.SessionType.Value)) continue;
                if (s.LastPacketMs > thisMs) return false;
                if (s.LastPacketMs == thisMs && i > thisIdx) return false;
            }
            return true;
        }

        /// <summary>
        /// Main-qualifying session type for terminal detection: Q1/Q2/Q3 (5..7) or
        /// One-Shot Qualifying (9). Short Qualifying (id 8) is intentionally EXCLUDED:
        /// per field reports it is sometimes emitted as the Sprint-side qualifying before
        /// the Sprint Race, so treating it as "main quali" would mark the Sprint terminal.
        /// The Sprint qualifying proper is always a Sprint Shootout (ids 10..14).
        /// </summary>
        private static bool IsMainQualifyingType(int id)
        {
            return (id >= 5 && id <= 7) || id == 9;
        }

        private static bool HasMainQualifyingInStore(IEnumerable<SessionRun> all)
        {
            if (all == null) return false;
            foreach (var s in all)
                if (s != null && s.SessionType.HasValue && IsMainQualifyingType(s.SessionType.Value))
                    return true;
            return false;
        }

        /// <summary>A main qualifying ended chronologically at or before this race.</summary>
        private static bool OccursAfterMainQualifying(SessionRun sess, IEnumerable<SessionRun> all)
        {
            if (sess == null || all == null) return false;
            long raceMs = sess.LastPacketMs;
            foreach (var s in all)
            {
                if (s == null || s == sess || !s.SessionType.HasValue) continue;
                if (IsMainQualifyingType(s.SessionType.Value) && s.LastPacketMs <= raceMs)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Should auto-export fire when a session with this wire id just ended?
        /// Called at SEND time with the current store state -- the closing session is the
        /// latest one, so a closing race is the Main Race once the Sprint already ran
        /// (>= 2 races) or a main Qualifying is present.
        /// </summary>
        public static bool IsTerminalRaceClosing(byte closingSessionTypeId, SessionStore store)
        {
            if (!IsRaceLikeType(closingSessionTypeId)) return false;
            if (store == null) return IsRaceWireType(closingSessionTypeId);
            var all = store.Sessions.Values;
            if (!HasSprintShootout(all)) return IsRaceWireType(closingSessionTypeId);
            // Sprint Format weekend: the closing race is the Main (terminal) race once a
            // second race-like session exists (the Sprint already ran) or a main
            // Qualifying has appeared (lone-race Baku-style weekends).
            return CountRaceLikeSessions(all) >= 2 || HasMainQualifyingInStore(all);
        }

        /// <summary>
        /// Was this stored session a terminal Main Race (for auto-rotate / HasClosed)?
        /// The Main Race is the chronologically last race, and only after the Sprint
        /// (>= 2 races) or after the main Qualifying (lone-race Baku-style).
        /// </summary>
        public static bool IsTerminalRaceSession(SessionRun sess, IEnumerable<SessionRun> allSessions)
        {
            if (sess == null || sess.FinalClassification == null || !sess.SessionType.HasValue)
                return false;
            if (!IsRaceLikeType(sess.SessionType.Value)) return false;
            if (!HasSprintShootout(allSessions)) return IsRaceWireType(sess.SessionType.Value);
            if (!IsLastRaceSession(sess, AsList(allSessions))) return false;
            return CountRaceLikeSessions(allSessions) >= 2
                || OccursAfterMainQualifying(sess, allSessions);
        }

        /// <summary>
        /// Export label id. v1.1.47: in a Sprint Format weekend with two races, the
        /// chronologically LAST race is the Main Race -> "Race" (15) and the earlier one
        /// is the Sprint Race -> "Race2" (16), regardless of the raw wire ids. This
        /// normalizes F1 25 (Sprint=16/Main=15), F1 26 offline (Sprint=15/Main=16) and
        /// F1 26 online (both=15) to one consistent labeling the Race Hub can rely on.
        /// A lone race (no Sprint, e.g. Baku) keeps its wire id.
        /// </summary>
        public static int GetExportSessionTypeId(SessionRun sess, IEnumerable<SessionRun> allSessions)
        {
            if (sess == null || !sess.SessionType.HasValue)
                return 0;
            int wireId = sess.SessionType.Value;
            if (!IsRaceLikeType(wireId)) return wireId;
            if (!HasSprintShootout(allSessions)) return wireId;
            if (CountRaceLikeSessions(allSessions) < 2) return wireId;
            return IsLastRaceSession(sess, AsList(allSessions)) ? 15 : 16;
        }
    }

}
