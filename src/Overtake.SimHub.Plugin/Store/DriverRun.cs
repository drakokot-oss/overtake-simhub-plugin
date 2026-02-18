using System.Collections.Generic;

namespace Overtake.SimHub.Plugin.Store
{
    public class LapRecord
    {
        public int LapNumber;
        public int LapTimeMs;
        public string LapTime;
        public int Sector1Ms;
        public int Sector2Ms;
        public int Sector3Ms;
        public int ValidFlags;
        public long TsMs;
    }

    public class PitStopRecord
    {
        public int NumPitStops;
        public long TsMs;
        public int? LapNum;
    }

    public class PenaltySnapshot
    {
        public long TsMs;
        public int? PenaltyType;
        public int? InfringementType;
        public int? OtherVehicleIdx;
        public int? TimeSec;
        public int? LapNum;
        public int? PlacesGained;
        public string EventCode;
    }

    public class TyreWearSnapshot
    {
        public int LapNumber;
        public float RL;
        public float RR;
        public float FL;
        public float FR;
        public float Avg;
    }

    public class DamageSnapshot
    {
        public int LapNumber;
        public int WingFL;
        public int WingFR;
        public int WingRear;
        public int TyreDmgRL;
        public int TyreDmgRR;
        public int TyreDmgFL;
        public int TyreDmgFR;
    }

    public class TyreWearValues
    {
        public float RL;
        public float RR;
        public float FL;
        public float FR;
    }

    public class DamageValues
    {
        public int WingFrontLeft;
        public int WingFrontRight;
        public int WingRear;
        public int TyreDmgRL;
        public int TyreDmgRR;
        public int TyreDmgFL;
        public int TyreDmgFR;
    }

    /// <summary>
    /// Per-driver accumulated state within a session.
    /// Ported from Python DriverRun dataclass.
    /// </summary>
    public class DriverRun
    {
        public string Tag;
        public int CarIdx;

        // SessionHistory (packet 11) — authoritative source for laps/sectors
        public List<LapRecord> Laps = new List<LapRecord>();
        public List<Dictionary<string, object>> TyreStints = new List<Dictionary<string, object>>();
        public Dictionary<string, object> Best = new Dictionary<string, object>();

        // Pit stops (when numPitStops increments in LapData)
        public List<PitStopRecord> PitStops = new List<PitStopRecord>();
        public int? LastNumPitStops;

        // Lap tracking
        public int? LastCurrentLapNum;
        public int LastRecordedLapNumber;

        // SessionHistory dedup
        public int? LastHistoryHash;
        public long LastHistoryUpdateMs;

        // Penalties
        public List<PenaltySnapshot> PenaltySnapshots = new List<PenaltySnapshot>();

        // Tyre wear (latest from CarDamage packet 10)
        public TyreWearValues LatestTyreWear;
        public List<TyreWearSnapshot> TyreWearPerLap = new List<TyreWearSnapshot>();

        // Damage (latest from CarDamage packet 10)
        public DamageValues LatestDamage;
        public List<DamageSnapshot> DamagePerLap = new List<DamageSnapshot>();

        // Qualifying: last non-zero lastLapTimeInMS
        public int LastSeenLapTimeMs;

        // Warnings tracking (from LapData packet 2)
        public int LastTotalWarnings;
        public int LastCornerCuttingWarnings;

        public void Reset()
        {
            Laps.Clear();
            LastRecordedLapNumber = 0;
            LastCurrentLapNum = null;
            LastHistoryHash = null;
            LastHistoryUpdateMs = 0;
            PitStops.Clear();
            LastNumPitStops = null;
            LatestTyreWear = null;
            TyreWearPerLap.Clear();
            LatestDamage = null;
            DamagePerLap.Clear();
            LastSeenLapTimeMs = 0;
            LastTotalWarnings = 0;
            LastCornerCuttingWarnings = 0;
            PenaltySnapshots.Clear();
        }
    }
}
