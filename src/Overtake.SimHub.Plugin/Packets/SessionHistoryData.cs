using System;
using System.Collections.Generic;

namespace Overtake.SimHub.Plugin.Packets
{
    public class LapHistoryEntry
    {
        public int LapNumber;
        public uint LapTimeMs;
        public int Sector1Ms;
        public int Sector2Ms;
        public int Sector3Ms;
        public byte ValidFlags;
    }

    public class TyreStintEntry
    {
        public int StintIndex;
        public byte EndLap;
        public byte TyreActual;
        public byte TyreVisual;
    }

    public class BestTimes
    {
        public byte BestLapTimeLapNum;
        public uint BestLapTimeMs;
        public byte BestSector1LapNum;
        public int BestSector1Ms;
        public byte BestSector2LapNum;
        public int BestSector2Ms;
        public byte BestSector3LapNum;
        public int BestSector3Ms;
    }

    /// <summary>
    /// Packet ID 11: Session History — per single car.
    /// Header: 7B (carIdx, numLaps, numTyreStints, bestLap/sector lap nums)
    /// Then: LapHistoryData[100] x 12 bytes each + TyreStintHistoryData[8] x 3 bytes each.
    /// LapHistoryData: I(4) H(2) B(1) H(2) B(1) H(2) B(1) B(1) = 14 bytes... wait
    /// Actually: uint32 lapTimeInMS, then 3 sectors each as (uint16 msPart, uint8 minutesPart), then uint8 validFlags
    /// = 4 + (2+1)*3 + 1 = 14 bytes
    /// </summary>
    public class SessionHistoryData
    {
        private const int LapHistSize = 14;
        private const int TyreStintSize = 3;
        private const int MaxLaps = 100;
        private const int MaxStints = 8;

        public byte CarIdx;
        public byte NumLaps;
        public LapHistoryEntry[] Laps;
        public TyreStintEntry[] TyreStints;
        public BestTimes Best;

        private static int SectorMs(ushort msPart, byte minutesPart)
        {
            return minutesPart * 60000 + msPart;
        }

        public static SessionHistoryData Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + 7)
                return null;

            int p = PacketHeader.Size;

            byte carIdx = data[p];
            byte numLaps = data[p + 1];
            byte numStints = data[p + 2];
            byte bestLapLapNum = data[p + 3];
            byte bestS1LapNum = data[p + 4];
            byte bestS2LapNum = data[p + 5];
            byte bestS3LapNum = data[p + 6];

            int off = p + 7;

            // Parse all 100 lap slots, keep only those with lapTimeMs > 0
            var laps = new List<LapHistoryEntry>();
            for (int i = 0; i < MaxLaps; i++)
            {
                if (off + LapHistSize > data.Length)
                    break;

                uint lapTimeMs = BitConverter.ToUInt32(data, off);
                ushort s1MsPart = BitConverter.ToUInt16(data, off + 4);
                byte s1MinPart = data[off + 6];
                ushort s2MsPart = BitConverter.ToUInt16(data, off + 7);
                byte s2MinPart = data[off + 9];
                ushort s3MsPart = BitConverter.ToUInt16(data, off + 10);
                byte s3MinPart = data[off + 12];
                byte validFlags = data[off + 13];

                off += LapHistSize;

                if (lapTimeMs <= 0) continue;

                laps.Add(new LapHistoryEntry
                {
                    LapNumber = i + 1,
                    LapTimeMs = lapTimeMs,
                    Sector1Ms = SectorMs(s1MsPart, s1MinPart),
                    Sector2Ms = SectorMs(s2MsPart, s2MinPart),
                    Sector3Ms = SectorMs(s3MsPart, s3MinPart),
                    ValidFlags = validFlags,
                });
            }

            // Tyre stints
            var stints = new List<TyreStintEntry>();
            for (int i = 0; i < MaxStints; i++)
            {
                if (off + TyreStintSize > data.Length)
                    break;

                byte endLap = data[off];
                byte tyreActual = data[off + 1];
                byte tyreVisual = data[off + 2];
                off += TyreStintSize;

                if (endLap == 0 && tyreActual == 0 && tyreVisual == 0)
                    continue;

                stints.Add(new TyreStintEntry
                {
                    StintIndex = i,
                    EndLap = endLap,
                    TyreActual = tyreActual,
                    TyreVisual = tyreVisual,
                });
            }

            // Trim laps to numLaps
            int trimCount = (numLaps > 0) ? Math.Min(numLaps, laps.Count) : laps.Count;
            if (trimCount < laps.Count)
                laps.RemoveRange(trimCount, laps.Count - trimCount);

            // Extract best times from raw data
            var best = new BestTimes
            {
                BestLapTimeLapNum = bestLapLapNum,
                BestSector1LapNum = bestS1LapNum,
                BestSector2LapNum = bestS2LapNum,
                BestSector3LapNum = bestS3LapNum,
            };

            if (bestLapLapNum > 0)
            {
                int bestIdx = bestLapLapNum - 1;
                int rawOff = p + 7 + bestIdx * LapHistSize;
                if (rawOff + LapHistSize <= data.Length)
                {
                    best.BestLapTimeMs = BitConverter.ToUInt32(data, rawOff);
                    best.BestSector1Ms = SectorMs(BitConverter.ToUInt16(data, rawOff + 4), data[rawOff + 6]);
                    best.BestSector2Ms = SectorMs(BitConverter.ToUInt16(data, rawOff + 7), data[rawOff + 9]);
                    best.BestSector3Ms = SectorMs(BitConverter.ToUInt16(data, rawOff + 10), data[rawOff + 12]);
                }
            }

            return new SessionHistoryData
            {
                CarIdx = carIdx,
                NumLaps = numLaps,
                Laps = laps.ToArray(),
                TyreStints = stints.ToArray(),
                Best = best,
            };
        }
    }
}
