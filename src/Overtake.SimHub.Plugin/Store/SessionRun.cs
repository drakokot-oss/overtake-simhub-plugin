using System.Collections.Generic;
using Overtake.SimHub.Plugin.Packets;

namespace Overtake.SimHub.Plugin.Store
{
    public class WeatherTimelineEntry
    {
        public long TsMs;
        public int Weather;
        public int? TrackTempC;
        public int? AirTempC;
    }

    /// <summary>
    /// Per-session accumulated state.
    /// Ported from Python SessionRun dataclass.
    /// </summary>
    public class SessionRun
    {
        public string SessionUID;
        public Dictionary<int, string> TagsByCarIdx = new Dictionary<int, string>();
        public Dictionary<int, ParticipantEntry> TeamByCarIdx = new Dictionary<int, ParticipantEntry>();
        public Dictionary<string, DriverRun> Drivers = new Dictionary<string, DriverRun>();

        /// <summary>Per-carIdx tag reliability level (0=GENERIC, 1=UNRELIABLE, 2=RELIABLE).</summary>
        public Dictionary<int, int> TagReliability = new Dictionary<int, int>();

        /// <summary>
        /// carIdx values that were EVER seen as human (aiControlled=false, platform!=255).
        /// Once marked human, stays human even after disconnect/AI takeover.
        /// </summary>
        public Dictionary<int, bool> HumanCarIdxs = new Dictionary<int, bool>();
        public List<Dictionary<string, object>> Events = new List<Dictionary<string, object>>();
        public FinalClassificationData FinalClassification;

        // Session packet (ID 1) data
        public int? SessionType;
        public int? TrackId;
        public int? Weather;
        public int? SafetyCarStatus;
        public int NetworkGame;
        public int NumRedFlagPeriods;

        // Weather timeline + forecast
        public List<WeatherTimelineEntry> WeatherTimeline = new List<WeatherTimelineEntry>();
        public List<Dictionary<string, object>> WeatherForecast = new List<Dictionary<string, object>>();
        public int? LastWeatherState;
        public int? LatestTrackTempC;
        public int? LatestAirTempC;

        // Player / Spectator
        public int PlayerCarIndex = -1;
        public int IsSpectating;
        public int GamePaused;
        public int SpectatorCarIndex = 255;

        // Timing
        public long LastPacketMs;
        public long? SessionEndedAtMs;

        // SC counters (filtered from events)
        public int NumSafetyCarDeployments;
        public int NumVSCDeployments;

        // Peak numActiveCars seen across all Participants packets (spectator mode fluctuates)
        public int MaxNumActiveCars;

        /// <summary>Peak NumActiveCars from Participants (packet 4) only — never bumped by FC.
        /// Used to drop overflow FC rows in online quali (e.g. Driver_19 when lobby has 19 drivers).</summary>
        public int ParticipantsPeakNumActive;

        // Lobby settings (captured once from first Session packet with data)
        public bool LobbySettingsCaptured;
        public byte ForecastAccuracy;
        public byte SteeringAssist;
        public byte BrakingAssist;
        public byte GearboxAssist;
        public byte PitAssist;
        public byte PitReleaseAssist;
        public byte ERSAssist;
        public byte DRSAssist;
        public byte DynamicRacingLine;
        public byte DynamicRacingLineType;
        public byte RuleSet;
        public byte RaceStarts;
        public byte RecoveryMode;
        public byte FlashbackLimit;
        public byte EqualCarPerformance;
        public byte SurfaceType;
        public byte LowFuelMode;
        public byte TyreTemperature;
        public byte PitLaneTyreSim;
        public byte CarDamage;
        public byte CarDamageRate;
        public byte Collisions;
        public byte CollisionsOffForFirstLapOnly;
        public byte CornerCuttingStringency;
        public byte ParcFermeRules;
        public byte FormationLap;
        public byte SafetyCarSetting;
        public byte RedFlagsSetting;
    }
}
