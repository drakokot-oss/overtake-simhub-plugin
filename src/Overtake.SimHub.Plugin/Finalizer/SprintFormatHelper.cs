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
        /// Should auto-export fire when a session with this wire id just ended?
        /// Called at SEND time with the current store state.
        /// </summary>
        public static bool IsTerminalRaceClosing(byte closingSessionTypeId, SessionStore store)
        {
            if (!IsRaceWireType(closingSessionTypeId)) return false;
            if (store == null || !HasSprintShootout(store.Sessions.Values)) return true;

            var all = store.Sessions.Values;
            // F1 26: defer while Main Quali is still pending (mislabeled Sprint on id=15).
            if (CountWireRaceSessions(all) < 2 && HasMainQualifying(all)
                && !HasSprintRaceSessionId16(all))
                return false;
            return HasAnyQualifying(all);
        }

        /// <summary>
        /// Was this stored session a terminal Main Race (for auto-rotate / HasClosed)?
        /// </summary>
        public static bool IsTerminalRaceSession(SessionRun sess, IEnumerable<SessionRun> allSessions)
        {
            if (sess == null || sess.FinalClassification == null || !sess.SessionType.HasValue)
                return false;
            if (!IsRaceWireType(sess.SessionType.Value)) return false;
            if (!HasSprintShootout(allSessions)) return true;

            int wireRaceCount = CountWireRaceSessions(allSessions);
            if (wireRaceCount >= 2)
            {
                if (!HasMainQualifying(allSessions)) return false;
                return !IsFirstWireRaceSession(sess, AsList(allSessions));
            }

            // Single wire-id=15 in a Sprint-format weekend.
            if (HasMainQualifying(allSessions) && !HasSprintRaceSessionId16(allSessions))
                return false; // F1 26 mislabel: Main Quali done, Main Race (2nd id=15) pending.
            if (!HasMainQualifying(allSessions) && HasAnyQualifying(allSessions))
                return true; // Baku-style: Short/OS Quali done, lone Race is Main.
            if (!HasMainQualifying(allSessions) && !HasAnyQualifying(allSessions))
                return false; // F1 26 mislabel: Sprint Race ended before Main Quali.
            if (HasSprintRaceSessionId16(allSessions))
                return true; // F1 25: Sprint on id=16, lone id=15 is Main Race.
            return false;
        }

        /// <summary>
        /// Export label id: remap wire id=15 to 16 (Race2) only when F1 26 sent TWO
        /// Race (id=15) sessions and this is the first one (Sprint Race).
        /// </summary>
        public static int GetExportSessionTypeId(SessionRun sess, IEnumerable<SessionRun> allSessions)
        {
            if (sess == null || !sess.SessionType.HasValue)
                return 0;
            int wireId = sess.SessionType.Value;
            if (wireId != 15) return wireId;
            if (!HasSprintShootout(allSessions)) return wireId;
            if (IsSprintRaceMislabeledAsWire15(sess, AsList(allSessions))) return 16;
            return 15;
        }
    }

}
