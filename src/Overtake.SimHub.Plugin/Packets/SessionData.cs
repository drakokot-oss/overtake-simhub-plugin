using System;

namespace Overtake.SimHub.Plugin.Packets
{
    public class WeatherForecastSample
    {
        public byte SessionType;
        public byte TimeOffsetMin;
        public byte Weather;
        public sbyte TrackTempC;
        public sbyte TrackTempChange;
        public sbyte AirTempC;
        public sbyte AirTempChange;
        public byte RainPercentage;
    }

    /// <summary>
    /// Packet ID 1: Session Data.
    /// Payload starts at offset 29 (after header). Total payload ~724 bytes.
    /// </summary>
    public class SessionData
    {
        public byte Weather;
        public sbyte TrackTempC;
        public sbyte AirTempC;
        public byte TotalLaps;
        public ushort TrackLength;
        public byte SessionType;
        public sbyte TrackId;
        public byte Formula;
        public ushort SessionTimeLeft;
        public ushort SessionDuration;
        public byte SafetyCarStatus;
        public byte NetworkGame;
        public byte GamePaused;
        public byte IsSpectating;
        public byte SpectatorCarIndex;
        public byte NumWeatherForecastSamples;
        public WeatherForecastSample[] WeatherForecast;
        public byte NumSafetyCarPeriods;
        public byte NumVirtualSafetyCarPeriods;
        public byte NumRedFlagPeriods;

        // Lobby settings (deep in Session packet payload)
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

        public static SessionData Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + 126)
                return null;

            int p = PacketHeader.Size;

            var result = new SessionData
            {
                Weather = data[p + 0],
                TrackTempC = (sbyte)data[p + 1],
                AirTempC = (sbyte)data[p + 2],
                TotalLaps = data[p + 3],
                TrackLength = BitConverter.ToUInt16(data, p + 4),
                SessionType = data[p + 6],
                TrackId = (sbyte)data[p + 7],
                Formula = data[p + 8],
                SessionTimeLeft = BitConverter.ToUInt16(data, p + 9),
                SessionDuration = BitConverter.ToUInt16(data, p + 11),
                SafetyCarStatus = data[p + 124],
                NetworkGame = data[p + 125],
                GamePaused = (data.Length > p + 14) ? data[p + 14] : (byte)0,
                IsSpectating = (data.Length > p + 15) ? data[p + 15] : (byte)0,
                SpectatorCarIndex = (data.Length > p + 16) ? data[p + 16] : (byte)255,
            };

            if (data.Length > p + 127)
            {
                result.NumWeatherForecastSamples = data[p + 126];
                int count = Math.Min(result.NumWeatherForecastSamples, (byte)64);
                result.WeatherForecast = new WeatherForecastSample[count];

                int fOff = p + 127;
                for (int i = 0; i < count; i++)
                {
                    int sOff = fOff + i * 8;
                    if (sOff + 8 > data.Length) break;
                    result.WeatherForecast[i] = new WeatherForecastSample
                    {
                        SessionType = data[sOff],
                        TimeOffsetMin = data[sOff + 1],
                        Weather = data[sOff + 2],
                        TrackTempC = (sbyte)data[sOff + 3],
                        TrackTempChange = (sbyte)data[sOff + 4],
                        AirTempC = (sbyte)data[sOff + 5],
                        AirTempChange = (sbyte)data[sOff + 6],
                        RainPercentage = data[sOff + 7],
                    };
                }
            }
            else
            {
                result.WeatherForecast = new WeatherForecastSample[0];
            }

            // SC / VSC / Red Flag counts deep in payload
            result.NumSafetyCarPeriods = (data.Length > p + 676) ? data[p + 676] : (byte)0;
            result.NumVirtualSafetyCarPeriods = (data.Length > p + 677) ? data[p + 677] : (byte)0;
            result.NumRedFlagPeriods = (data.Length > p + 678) ? data[p + 678] : (byte)0;

            // Lobby settings (offsets after weather forecast block)
            if (data.Length > p + 700)
            {
                result.ForecastAccuracy = data[p + 639];
                result.SteeringAssist = data[p + 656];
                result.BrakingAssist = data[p + 657];
                result.GearboxAssist = data[p + 658];
                result.PitAssist = data[p + 659];
                result.PitReleaseAssist = data[p + 660];
                result.ERSAssist = data[p + 661];
                result.DRSAssist = data[p + 662];
                result.DynamicRacingLine = data[p + 663];
                result.DynamicRacingLineType = data[p + 664];
                result.RuleSet = data[p + 666];
                result.RaceStarts = data[p + 684];
                result.RecoveryMode = data[p + 680];
                result.FlashbackLimit = data[p + 681];
                result.EqualCarPerformance = data[p + 679];
                result.SurfaceType = data[p + 682];
                result.LowFuelMode = data[p + 683];
                result.TyreTemperature = data[p + 685];
                result.PitLaneTyreSim = data[p + 686];
                result.CarDamage = data[p + 687];
                result.CarDamageRate = data[p + 688];
                result.Collisions = data[p + 689];
                result.CollisionsOffForFirstLapOnly = data[p + 690];
                result.CornerCuttingStringency = data[p + 693];
                result.ParcFermeRules = data[p + 694];
                result.FormationLap = data[p + 698];
                result.SafetyCarSetting = data[p + 696];
                result.RedFlagsSetting = data[p + 700];
            }

            return result;
        }
    }
}
