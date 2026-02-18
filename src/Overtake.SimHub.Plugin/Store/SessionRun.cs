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

        // Player
        public int PlayerCarIndex = -1;

        // Timing
        public long LastPacketMs;
        public long? SessionEndedAtMs;

        // SC counters (filtered from events)
        public int NumSafetyCarDeployments;
        public int NumVSCDeployments;
    }
}
