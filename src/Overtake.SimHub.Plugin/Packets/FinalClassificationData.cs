using System;

namespace Overtake.SimHub.Plugin.Packets
{
    public class FinalClassificationEntry
    {
        public int CarIdx;
        public byte Position;
        public byte NumLaps;
        public byte GridPosition;
        public byte Points;
        public byte NumPitStops;
        public byte ResultStatus;
        public byte ResultReason;
        public uint BestLapTimeMs;
        public double TotalRaceTimeSec;
        public byte PenaltiesTimeSec;
        public byte NumPenalties;
        public byte NumTyreStints;
    }

    /// <summary>
    /// Packet ID 8: Final Classification.
    /// Payload: 1 byte numCars, then 46 bytes per car.
    /// Per-car layout: 7B + I(4) + d(8) + 3B + 8B*3 = 46 bytes.
    /// </summary>
    public class FinalClassificationData
    {
        private const int RowSize = 46;

        public byte NumCars;
        public FinalClassificationEntry[] Classification;

        public static FinalClassificationData Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + 1)
                return null;

            int p = PacketHeader.Size;
            byte numCars = data[p];
            int off = p + 1;

            int count = Math.Min(numCars, (byte)22);
            var rows = new FinalClassificationEntry[count];

            for (int i = 0; i < count; i++)
            {
                if (off + RowSize > data.Length)
                    break;

                // 7 x uint8
                byte position = data[off + 0];
                byte numLaps = data[off + 1];
                byte gridPos = data[off + 2];
                byte points = data[off + 3];
                byte numPitStops = data[off + 4];
                byte resultStatus = data[off + 5];
                byte resultReason = data[off + 6];

                // uint32 bestLapTimeInMS
                uint bestLapTime = BitConverter.ToUInt32(data, off + 7);

                // double totalRaceTime
                double totalRaceTime = BitConverter.ToDouble(data, off + 11);

                // 3 x uint8
                byte penaltiesTime = data[off + 19];
                byte numPenalties = data[off + 20];
                byte numTyreStints = data[off + 21];

                // remaining 24 bytes: tyreStintsActual[8] + tyreStintsVisual[8] + tyreStintsEndLaps[8]

                rows[i] = new FinalClassificationEntry
                {
                    CarIdx = i,
                    Position = position,
                    NumLaps = numLaps,
                    GridPosition = gridPos,
                    Points = points,
                    NumPitStops = numPitStops,
                    ResultStatus = resultStatus,
                    ResultReason = resultReason,
                    BestLapTimeMs = bestLapTime,
                    TotalRaceTimeSec = totalRaceTime,
                    PenaltiesTimeSec = penaltiesTime,
                    NumPenalties = numPenalties,
                    NumTyreStints = numTyreStints,
                };

                off += RowSize;
            }

            return new FinalClassificationData
            {
                NumCars = numCars,
                Classification = rows,
            };
        }
    }
}
