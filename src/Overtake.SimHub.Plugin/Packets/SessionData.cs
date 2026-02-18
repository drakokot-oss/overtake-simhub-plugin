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
        public byte NumWeatherForecastSamples;
        public WeatherForecastSample[] WeatherForecast;
        public byte NumSafetyCarPeriods;
        public byte NumVirtualSafetyCarPeriods;
        public byte NumRedFlagPeriods;

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

            return result;
        }
    }
}
