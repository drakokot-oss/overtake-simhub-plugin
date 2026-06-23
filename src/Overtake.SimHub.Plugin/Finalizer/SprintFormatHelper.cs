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

            // Sprint-format weekend: defer until Main Quali has been seen.
            return HasAnyQualifying(store.Sessions.Values);
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
            // F1 25 Sprint Format: Sprint Race is wire id=16 — only one id=15 exists.
            if (CountWireRaceSessions(allSessions) < 2) return true;
            if (!HasAnyQualifying(allSessions)) return false;
            return !IsFirstWireRaceSession(sess, AsList(allSessions));
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
