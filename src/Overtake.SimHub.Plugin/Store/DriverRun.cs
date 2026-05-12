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

        // Grid position (from LapData packet 2)
        public byte GridPosition;

        // Car position in race (1=leader, 2=2nd...) — for FinalClassification row mapping
        public byte CarPosition;

        // Warnings tracking (from LapData packet 2)
        public int LastTotalWarnings;
        public int LastCornerCuttingWarnings;

        // Per-driver assists (from CarStatus packet 7)
        public byte TractionControl;
        public byte AntiLockBrakes;
        public bool AssistsCaptured;

        // Fuel snapshot (CarStatus packet 7) — only when FuelCapacity >= threshold
        // (multiplayer Restricted telemetry zeroes fuel for other cars).
        public byte FuelMixLast;
        public float FuelCapacityKg;
        public float FuelInTankFirst;
        public float FuelInTankLast;
        public float FuelRemainingLapsFirst;
        public float FuelRemainingLapsLast;
        public bool FuelFirstSampleSet;
        public bool FuelCaptured;

        // v1.1.34 — ERS / battery telemetry. All values stored as PERCENT
        // (0..100) of the regulation capacity (4 MJ). The conversion from Joules
        // to % is done at ingest time so this struct holds the human-facing unit.
        //
        // - Store* fields track the energy level in the battery itself.
        // - Deployed*PerLap / Harvested*PerLap are per-lap snapshots, closed at
        //   the LapData rollover (currentLapNum increment).
        // - StorePctSumWeighted / StorePctTimeMs implement a time-weighted mean
        //   for StorePctAvg: each sample contributes (value * dtMs) and we
        //   divide at finalize time. Samples received with NetworkPaused=1 are
        //   counted in ErsSamplesPaused but excluded from the average so a
        //   driver going idle during a long pause does not bias the result.
        // - DeployedLastSnapshot / HarvestedMguk/HarvestedMguhLastSnapshot keep
        //   the *latest seen* per-lap counter; we publish it to the per-lap
        //   array the first time we observe a lap rollover in LapData.
        public float ErsStorePctFirst;
        public float ErsStorePctLast;
        public float ErsStorePctMin;
        public float ErsStorePctMax;
        public double ErsStorePctSumWeighted;
        public long ErsStorePctTimeMs;
        public long ErsLastSampleMs;
        public byte ErsDeployModeLast;
        public bool ErsFirstSampleSet;
        public bool ErsCaptured;
        public int ErsSamplesCount;
        public int ErsSamplesPaused;
        public List<float> DeployedPctPerLap = new List<float>();
        public List<float> HarvestedMgukPctPerLap = new List<float>();
        public List<float> HarvestedMguhPctPerLap = new List<float>();
        public float DeployedPctLastSnapshot;
        public float HarvestedMgukPctLastSnapshot;
        public float HarvestedMguhPctLastSnapshot;
        public byte ErsCurrentLapNum;

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
            FuelMixLast = 0;
            FuelCapacityKg = 0f;
            FuelInTankFirst = 0f;
            FuelInTankLast = 0f;
            FuelRemainingLapsFirst = 0f;
            FuelRemainingLapsLast = 0f;
            FuelFirstSampleSet = false;
            FuelCaptured = false;
            ErsStorePctFirst = 0f;
            ErsStorePctLast = 0f;
            ErsStorePctMin = 0f;
            ErsStorePctMax = 0f;
            ErsStorePctSumWeighted = 0d;
            ErsStorePctTimeMs = 0L;
            ErsLastSampleMs = 0L;
            ErsDeployModeLast = 0;
            ErsFirstSampleSet = false;
            ErsCaptured = false;
            ErsSamplesCount = 0;
            ErsSamplesPaused = 0;
            DeployedPctPerLap.Clear();
            HarvestedMgukPctPerLap.Clear();
            HarvestedMguhPctPerLap.Clear();
            DeployedPctLastSnapshot = 0f;
            HarvestedMgukPctLastSnapshot = 0f;
            HarvestedMguhPctLastSnapshot = 0f;
            ErsCurrentLapNum = 0;
        }
    }
}

