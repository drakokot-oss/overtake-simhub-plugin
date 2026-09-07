using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>Um conjunto de pneu, como o jogo entrega no packet 12.</summary>
    public class TyreSetEntry
    {
        public byte ActualCompound;       // +0  composto real (C1..C5, Inter, Wet)
        public byte VisualCompound;       // +1  S/M/H — casa com a cor da torre
        public byte Wear;                 // +2  desgaste %
        public bool Available;            // +3  conjunto ainda disponivel
        public byte RecommendedSession;   // +4  sessao recomendada pelo jogo
        public byte LifeSpan;             // +5  voltas RESTANTES neste conjunto
        public byte UsableLife;           // +6  maximo recomendado para o composto
        public short LapDeltaMs;          // +7  delta de volta vs o conjunto MONTADO, em ms
        public bool Fitted;               // +9  este e o que esta no carro
    }

    /// <summary>
    /// PacketTyreSetsData (id 12) — "Extended tyre set data".
    ///
    /// Layout CONFERIDO no apendice oficial em 07/09/2026 (anexos do post
    /// "EA SPORTS F1 25: 2026 Season Pack UDP SPECIFICATION"), e a boa noticia e que o pacote e
    /// IDENTICO em 2025 e 2026 — mesmo id, mesmo struct, mesma constante. Ao contrario de
    /// Participants / CarStatus / Motion, aqui NAO existe leitura format-aware.
    ///
    ///   header 29 | m_carIdx @29 | m_tyreSetData[20] @30 (stride 10) | m_fittedIdx @230
    ///   total 231 bytes
    ///
    /// ARMADILHA que o backlog documentava errado de memoria: no fio vem `m_lifeSpan` (+5) ANTES
    /// de `m_usableLife` (+6). As duas sao contagens de voltas plausiveis, entao trocar as duas
    /// nao quebra nada visivelmente — so mostra numero errado para sempre. Por isso este parser
    /// nomeia campo por campo em vez de despejar em array na ordem de leitura.
    ///
    /// `m_lapDeltaTime` e int16 em MILISSEGUNDOS e pode ser NEGATIVO (conjunto mais rapido que o
    /// montado): o "-3.395" da tela de referencia do SimHub e -3395 aqui. Guardado como int em
    /// ms; a divisao por 1000 e so na exibicao.
    ///
    /// O jogo envia este pacote POR CARRO, em rodizio — um carro por pacote. Quem consome tem
    /// que acumular por carIdx em vez de esperar a grade inteira num tick.
    /// </summary>
    public static class TyreSetsData
    {
        public const int PacketId = 12;

        /// <summary>13 slicks + 7 de chuva, per `cs_maxNumTyreSets` da spec.</summary>
        public const int MaxSets = 20;
        public const int SetStride = 10;

        private const int OffCarIdx = 0;                 // relativo ao fim do header
        private const int OffSets = 1;
        private const int MinSize = OffSets + MaxSets * SetStride + 1;   // 202 apos o header

        /// <summary>Indice do carro a que este pacote se refere, ou -1 se nao der para ler.</summary>
        public static int CarIdxOf(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + MinSize) return -1;
            return data[PacketHeader.Size + OffCarIdx];
        }

        /// <summary>
        /// Le os 20 conjuntos de UM carro. Devolve null quando o pacote nao tem tamanho para o
        /// bloco inteiro — parcial aqui seria pior que ausente, porque um conjunto lido pela
        /// metade viraria desgaste e delta plausiveis mas errados.
        /// </summary>
        public static TyreSetEntry[] Parse(byte[] data, out int carIdx, out int fittedIdx)
        {
            carIdx = -1;
            fittedIdx = -1;
            if (data == null || data.Length < PacketHeader.Size + MinSize) return null;

            int p = PacketHeader.Size;
            carIdx = data[p + OffCarIdx];

            var sets = new TyreSetEntry[MaxSets];
            for (int i = 0; i < MaxSets; i++)
            {
                int o = p + OffSets + i * SetStride;
                sets[i] = new TyreSetEntry
                {
                    ActualCompound = data[o + 0],
                    VisualCompound = data[o + 1],
                    Wear = data[o + 2],
                    Available = data[o + 3] != 0,
                    RecommendedSession = data[o + 4],
                    LifeSpan = data[o + 5],
                    UsableLife = data[o + 6],
                    LapDeltaMs = BitConverter.ToInt16(data, o + 7),
                    Fitted = data[o + 9] != 0,
                };
            }

            fittedIdx = data[p + OffSets + MaxSets * SetStride];
            return sets;
        }
    }
}
