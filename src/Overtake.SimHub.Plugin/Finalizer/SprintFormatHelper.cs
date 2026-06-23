using System.Collections.Generic;
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
    /// The first "Race" before any Qualifying is inferred as Race2 (Sprint Race)
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

        public static bool HasQualifyingBeforeMs(IEnumerable<SessionRun> sessions, long beforeMs)
        {
            if (sessions == null) return false;
            foreach (var s in sessions)
            {
                if (s != null && s.SessionType.HasValue
                    && IsQualifyingType(s.SessionType.Value)
                    && s.LastPacketMs < beforeMs)
                    return true;
            }
            return false;
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
            foreach (var s in store.Sessions.Values)
            {
                if (s != null && s.SessionType.HasValue
                    && IsQualifyingType(s.SessionType.Value))
                    return true;
            }
            return false;
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
            return HasQualifyingBeforeMs(allSessions, sess.LastPacketMs);
        }

        /// <summary>
        /// Export label id: remap wire id=15 to 16 (Race2) for the Sprint Race when
        /// it arrived before any Qualifying session in this capture.
        /// </summary>
        public static int GetExportSessionTypeId(SessionRun sess, IEnumerable<SessionRun> allSessions)
        {
            if (sess == null || !sess.SessionType.HasValue)
                return 0;
            int wireId = sess.SessionType.Value;
            if (wireId != 15) return wireId;
            if (!HasSprintShootout(allSessions)) return wireId;
            if (HasQualifyingBeforeMs(allSessions, sess.LastPacketMs)) return 15;
            return 16;
        }
    }

}
