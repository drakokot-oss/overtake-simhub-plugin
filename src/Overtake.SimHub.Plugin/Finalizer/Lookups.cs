using System.Collections.Generic;

namespace Overtake.SimHub.Plugin.Finalizer
{
    public static class Lookups
    {
        public static readonly Dictionary<int, string> SessionType = new Dictionary<int, string>
        {
            { 0, "Unknown" }, { 1, "Practice1" }, { 2, "Practice2" }, { 3, "Practice3" },
            { 4, "ShortPractice" }, { 5, "Qualifying1" }, { 6, "Qualifying2" }, { 7, "Qualifying3" },
            { 8, "ShortQualifying" }, { 9, "OneShotQualifying" }, { 10, "Race" }, { 11, "Race2" },
            { 12, "TimeTrial" }, { 13, "Sprint" }, { 14, "SprintShootout" },
            { 15, "Race" }, { 19, "Race" }, { 24, "SprintShootout" },
            { 25, "Race" }, { 26, "Race" }, { 29, "Race" }, { 30, "Race" }, { 36, "Race" },
        };

        public static readonly Dictionary<int, string> Weather = new Dictionary<int, string>
        {
            { 0, "Clear" }, { 1, "LightCloud" }, { 2, "Overcast" },
            { 3, "LightRain" }, { 4, "HeavyRain" }, { 5, "Storm" },
        };

        public static readonly Dictionary<int, string> SafetyCarStatus = new Dictionary<int, string>
        {
            { 0, "NoSafetyCar" }, { 1, "FullSafetyCar" }, { 2, "VirtualSafetyCar" },
        };

        public static readonly Dictionary<string, string> EventNames = new Dictionary<string, string>
        {
            { "SSTA", "SessionStarted" }, { "SEND", "SessionEnded" }, { "LGOT", "LightsOut" },
            { "FTLP", "FastestLap" }, { "RTMT", "Retirement" }, { "SCAR", "SafetyCarDeployed" },
            { "VSCN", "VSCDeployed" }, { "VSCE", "VSCEnded" }, { "PENA", "PenaltyIssued" },
            { "OVTK", "Overtake" }, { "COLL", "Collision" }, { "DTSV", "DriveThroughServed" },
            { "SGSV", "StopGoServed" }, { "CHQF", "ChequeredFlag" }, { "RCWN", "RaceWinner" },
            { "RDFL", "RedFlag" }, { "FLBK", "Flashback" }, { "TMPT", "TeamMateInPits" },
            { "FINAL_CLASSIFICATION", "FinalClassification" },
        };

        public static readonly Dictionary<int, string> PenaltyType = new Dictionary<int, string>
        {
            { 0, "DriveThrough" }, { 1, "StopGo" }, { 2, "GridPenalty" }, { 3, "PenaltyReminder" },
            { 4, "TimePenalty" }, { 5, "Warning" }, { 6, "Disqualified" },
            { 7, "RemovedFromFormationLap" }, { 8, "ParkedTooLongTimer" }, { 9, "TyreRegulations" },
            { 10, "ThisLapInvalidated" }, { 11, "ThisAndNextLapInvalidated" },
            { 12, "ThisLapInvalidatedWithoutReason" }, { 13, "ThisAndNextLapInvalidatedWithoutReason" },
            { 14, "ThisAndPreviousLapInvalidated" }, { 15, "ThisAndPreviousLapInvalidatedWithoutReason" },
            { 16, "Retired" }, { 17, "BlackFlagTimer" },
        };

        public static readonly Dictionary<int, string> InfringementType = new Dictionary<int, string>
        {
            { 0, "BlockingBySlowDriving" }, { 1, "BlockingByWrongWayDriving" },
            { 2, "ReversingOffTheStartLine" }, { 3, "BigCollision" }, { 4, "SmallCollision" },
            { 5, "CollisionFailedToHandBackPosition" }, { 6, "CollisionFailedToHandBackPositionMultiple" },
            { 7, "CornerCuttingGainedTime" }, { 8, "CornerCuttingOvertakeSingle" },
            { 9, "CornerCuttingOvertakeMultiple" }, { 10, "CrossedPitExitLane" },
            { 11, "IgnoringBlueFlags" }, { 12, "IgnoringYellowFlags" }, { 13, "IgnoringDriveThrough" },
            { 14, "TooManyDriveThroughs" }, { 15, "DriveThroughReminderServeWithinNLaps" },
            { 16, "DriveThroughReminderServeThisLap" }, { 17, "PitLaneSpeeding" },
            { 18, "ParkedForTooLong" }, { 19, "IgnoringTyreRegulations" }, { 20, "TooManyPenalties" },
            { 21, "MultipleWarnings" }, { 22, "ApproachingDisqualification" },
            { 23, "TyreRegulationsSelectSingle" }, { 24, "TyreRegulationsSelectMultiple" },
            { 25, "LapInvalidatedCornerCutting" }, { 26, "LapInvalidatedRunningWide" },
            { 27, "CornerCuttingRanWideGainedTimeMinor" }, { 28, "CornerCuttingRanWideGainedTimeSignificant" },
            { 29, "CornerCuttingRanWideGainedTimeExtreme" }, { 30, "LapInvalidatedWallRiding" },
            { 31, "LapInvalidatedFlashbackUsed" }, { 32, "LapInvalidatedResetToTrack" },
            { 33, "BlockingThePitlane" }, { 34, "JumpStart" }, { 35, "SafetyCarToCarCollision" },
            { 36, "SafetyCarIllegalOvertake" }, { 37, "SafetyCarExceedingAllowedPace" },
            { 38, "VirtualSafetyCarExceedingAllowedPace" }, { 39, "FormationLapBelowAllowedSpeed" },
            { 40, "FormationLapParking" }, { 41, "RetiredMechanicalFailure" },
            { 42, "RetiredTerminallyDamaged" }, { 43, "SafetyCarFallingTooFarBack" },
            { 44, "BlackFlagTimer" }, { 45, "UnservedStopGoPenalty" }, { 46, "UnservedDriveThroughPenalty" },
            { 47, "EngineComponentChange" }, { 48, "GearboxChange" }, { 49, "ParcFermeChange" },
            { 50, "LeagueGridPenalty" }, { 51, "RetryPenalty" }, { 52, "IllegalTimeGain" },
            { 53, "MandatoryPitstop" }, { 54, "AttributeAssigned" },
        };

        public static readonly Dictionary<int, string> TyreVisual = new Dictionary<int, string>
        {
            { 16, "Soft" }, { 17, "Medium" }, { 18, "Hard" }, { 7, "Intermediate" }, { 8, "Wet" },
        };

        public static readonly Dictionary<int, string> TyreActual = new Dictionary<int, string>
        {
            { 16, "C5" }, { 17, "C4" }, { 18, "C3" }, { 19, "C2" }, { 20, "C1" }, { 21, "C0" }, { 22, "C6" },
            { 7, "Intermediate" }, { 8, "Wet" },
        };

        public static readonly Dictionary<int, string> Teams = new Dictionary<int, string>
        {
            { 0, "Mercedes-AMG Petronas" }, { 1, "Scuderia Ferrari HP" }, { 2, "Red Bull Racing" },
            { 3, "Williams Racing" }, { 4, "Aston Martin Aramco" }, { 5, "Alpine F1 Team" },
            { 6, "Visa Cash App Racing Bulls" }, { 7, "MoneyGram Haas F1 Team" },
            { 8, "McLaren Formula 1 Team" }, { 9, "Stake F1 Team Kick Sauber" },
            { 10, "AlphaTauri (2021)" }, { 42, "Art Grand Prix" }, { 51, "Trident" },
            { 85, "Mercedes (2020)" }, { 86, "Ferrari (2020)" }, { 87, "Red Bull (2020)" },
            { 88, "Williams (2020)" }, { 89, "Racing Point (2020)" }, { 90, "Renault (2020)" },
            { 91, "AlphaTauri (2020)" }, { 92, "Haas (2020)" }, { 93, "McLaren (2020)" },
            { 94, "Alfa Romeo (2020)" }, { 104, "F1 Generic" },
            { 106, "Art Grand Prix (2023)" }, { 107, "Campos Racing" }, { 108, "Carlin" },
            { 109, "Charouz Racing" }, { 110, "DAMS" }, { 111, "Hitech" }, { 112, "MP Motorsport" },
            { 113, "Prema" }, { 114, "Trident (2023)" }, { 115, "Van Amersfoort" },
            { 116, "Virtuosi" }, { 117, "Invicta" }, { 118, "PHM Racing" }, { 119, "VAR" },
            { 120, "Rodin" }, { 121, "AIX" }, { 122, "Campos (2024)" }, { 123, "Hitech (2024)" },
            { 124, "Prema (2024)" }, { 125, "MP (2024)" }, { 126, "Trident (2024)" },
            { 127, "DAMS (2024)" }, { 128, "Invicta (2024)" }, { 129, "Rodin (2024)" },
            { 130, "AIX (2024)" }, { 131, "ART (2024)" },
        };

        public static readonly Dictionary<int, string> Platforms = new Dictionary<int, string>
        {
            { 1, "Steam" }, { 3, "PlayStation" }, { 4, "Xbox" }, { 6, "Origin" }, { 255, "Unknown" },
        };

        /// <summary>F1 25 DriverId -> surname (used to resolve player car when name is gamer tag).</summary>
        public static readonly Dictionary<int, string> DriverById = new Dictionary<int, string>
        {
            { 0, "SAINZ" }, { 2, "HAMILTON" }, { 6, "NORRIS" }, { 7, "RICCIARDO" },
            { 9, "LECLERC" }, { 10, "STROLL" }, { 11, "OCON" }, { 14, "ALBON" },
            { 15, "GASLY" }, { 17, "VERSTAPPEN" }, { 19, "HULKENBERG" }, { 20, "TSUNODA" },
            { 46, "PIASTRI" }, { 49, "RUSSELL" }, { 54, "BEARMAN" }, { 55, "LAWSON" },
            { 58, "ALONSO" }, { 61, "BORTOLETO" }, { 62, "HADJAR" }, { 63, "ANTONELLI" },
            { 64, "COLAPINTO" }, { 72, "DOOHAN" },
        };

        public static readonly Dictionary<int, string> Tracks = new Dictionary<int, string>
        {
            { 0, "Melbourne" }, { 1, "PaulRicard" }, { 2, "Shanghai" }, { 3, "Sakhir" },
            { 4, "Catalunya" }, { 5, "Monaco" }, { 6, "Montreal" }, { 7, "Silverstone" },
            { 8, "Hockenheim" }, { 9, "Hungaroring" }, { 10, "Spa" }, { 11, "Monza" },
            { 12, "Singapore" }, { 13, "Suzuka" }, { 14, "AbuDhabi" }, { 15, "Texas" },
            { 16, "Brazil" }, { 17, "Austria" }, { 18, "Sochi" }, { 19, "Mexico" },
            { 20, "Baku" }, { 21, "SakhirShort" }, { 22, "SilverstoneShort" },
            { 23, "TexasShort" }, { 24, "SuzukaShort" }, { 25, "Hanoi" },
            { 26, "Zandvoort" }, { 27, "Imola" }, { 28, "Portimao" }, { 29, "Jeddah" },
            { 30, "Miami" }, { 31, "LasVegas" }, { 32, "Losail" }, { 33, "Lusail" },
            { 39, "Silverstone Reverse" }, { 40, "Austria Reverse" }, { 41, "Zandvoort Reverse" },
        };

        public static readonly Dictionary<int, string> ResultStatus = new Dictionary<int, string>
        {
            { 0, "Unknown" }, { 1, "Invalid" }, { 2, "Inactive" }, { 3, "Finished" },
            { 4, "DidNotFinish" }, { 5, "Disqualified" }, { 6, "NotClassified" }, { 7, "Retired" },
        };

        public static Dictionary<string, object> Label(Dictionary<int, string> map, int? key, string prefix)
        {
            if (!key.HasValue)
                return new Dictionary<string, object> { { "id", null }, { "name", prefix + "(None)" } };
            string name;
            if (!map.TryGetValue(key.Value, out name))
                name = string.Format("{0}({1})", prefix, key.Value);
            return new Dictionary<string, object> { { "id", key.Value }, { "name", name } };
        }

        public static string LookupOrDefault(Dictionary<int, string> map, int key, string prefix)
        {
            string name;
            if (map.TryGetValue(key, out name)) return name;
            return string.Format("{0}({1})", prefix, key);
        }
    }
}
