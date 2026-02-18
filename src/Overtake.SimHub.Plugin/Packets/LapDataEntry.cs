using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Packet ID 2: Lap Data — one entry per car, 57 bytes each, 22 cars.
    /// Layout per car (packed, little-endian):
    ///   I(4) I(4) H(2) B(1) H(2) B(1) H(2) B(1) H(2) B(1)
    ///   f(4) f(4) f(4)
    ///   B(1)*15
    ///   H(2) H(2) B(1) f(4) B(1)
    /// </summary>
    public class LapDataEntry
    {
        public const int EntrySize = 57;
        public const int NumCars = 22;

        public int CarIdx;
        public uint LastLapTimeInMS;
        public uint CurrentLapTimeInMS;
        public int Sector1TimeInMS;
        public int Sector2TimeInMS;
        public byte CarPosition;
        public byte CurrentLapNum;
        public byte PitStatus;
        public byte NumPitStops;
        public byte CurrentLapInvalid;
        public byte Penalties;
        public byte TotalWarnings;
        public byte CornerCuttingWarnings;
        public byte UnservedDriveThrough;
        public byte UnservedStopGo;
        public byte GridPosition;
        public byte DriverStatus;
        public byte ResultStatus;
        public ushort PitLaneTimeInLaneInMS;
        public ushort PitStopTimerInMS;
        public byte PitStopShouldServePen;

        private static int SectorMs(ushort msPart, byte minutesPart)
        {
            return minutesPart * 60000 + msPart;
        }

        // Byte offsets within each 57-byte entry:
        //  0: lastLapTimeInMS (u32)
        //  4: currentLapTimeInMS (u32)
        //  8: sector1MsPart (u16), 10: sector1MinPart (u8)
        // 11: sector2MsPart (u16), 13: sector2MinPart (u8)
        // 14: deltaCarFrontMsPart (u16), 16: deltaCarFrontMinPart (u8)
        // 17: deltaLeaderMsPart (u16), 19: deltaLeaderMinPart (u8)
        // 20: lapDistance (f32), 24: totalDistance (f32), 28: safetyCarDelta (f32)
        // 32: carPosition, 33: currentLapNum, 34: pitStatus, 35: numPitStops
        // 36: sector, 37: currentLapInvalid, 38: penalties, 39: totalWarnings
        // 40: cornerCuttingWarnings, 41: unservedDriveThrough, 42: unservedStopGo
        // 43: gridPosition, 44: driverStatus, 45: resultStatus
        // 46: pitLaneTimerActive
        // 47: pitLaneTimeInLaneInMS (u16), 49: pitStopTimerInMS (u16)
        // 51: pitStopShouldServePen
        // 52: speedTrapFastestSpeed (f32), 56: speedTrapFastestLap (u8)

        public static LapDataEntry[] Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + EntrySize * NumCars)
                return null;

            var entries = new LapDataEntry[NumCars];
            int p = PacketHeader.Size;

            for (int i = 0; i < NumCars; i++)
            {
                int off = p + i * EntrySize;

                entries[i] = new LapDataEntry
                {
                    CarIdx = i,
                    LastLapTimeInMS = BitConverter.ToUInt32(data, off + 0),
                    CurrentLapTimeInMS = BitConverter.ToUInt32(data, off + 4),
                    Sector1TimeInMS = SectorMs(BitConverter.ToUInt16(data, off + 8), data[off + 10]),
                    Sector2TimeInMS = SectorMs(BitConverter.ToUInt16(data, off + 11), data[off + 13]),
                    CarPosition = data[off + 32],
                    CurrentLapNum = data[off + 33],
                    PitStatus = data[off + 34],
                    NumPitStops = data[off + 35],
                    CurrentLapInvalid = data[off + 37],
                    Penalties = data[off + 38],
                    TotalWarnings = data[off + 39],
                    CornerCuttingWarnings = data[off + 40],
                    UnservedDriveThrough = data[off + 41],
                    UnservedStopGo = data[off + 42],
                    GridPosition = data[off + 43],
                    DriverStatus = data[off + 44],
                    ResultStatus = data[off + 45],
                    PitLaneTimeInLaneInMS = BitConverter.ToUInt16(data, off + 47),
                    PitStopTimerInMS = BitConverter.ToUInt16(data, off + 49),
                    PitStopShouldServePen = data[off + 51],
                };
            }

            return entries;
        }
    }
}
