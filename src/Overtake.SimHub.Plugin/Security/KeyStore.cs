using System;

namespace Overtake.SimHub.Plugin.Security
{
    /// <summary>
    /// Obfuscated key storage. Keys are split via XOR and reassembled at runtime.
    /// After ConfuserEx string encryption, these become opaque in the binary.
    /// </summary>
    internal static class KeyStore
    {
        private static readonly byte[] _aesPartA = {
            202,234,66,218,231,105,143,115,130,40,220,134,133,1,223,11,
            57,25,143,152,205,172,139,222,15,163,149,188,96,50,113,67
        };
        private static readonly byte[] _aesMask = {
            112,149,248,201,134,232,229,178,21,121,191,75,13,158,83,152,
            242,197,98,222,207,81,27,156,137,130,61,254,27,55,169,71
        };

        private static readonly byte[] _hmacPartA = {
            150,118,1,19,37,70,224,184,248,146,204,164,97,161,154,168,
            127,23,215,35,11,66,210,153,204,210,141,135,93,121,115,80
        };
        private static readonly byte[] _hmacMask = {
            225,37,214,31,49,195,24,166,108,246,106,71,83,216,116,34,
            187,200,237,226,181,159,226,79,84,153,151,181,165,126,135,130
        };

        internal static byte[] GetAesKey()
        {
            var key = new byte[32];
            for (int i = 0; i < 32; i++)
                key[i] = (byte)(_aesPartA[i] ^ _aesMask[i]);
            return key;
        }

        internal static byte[] GetHmacKey()
        {
            var key = new byte[32];
            for (int i = 0; i < 32; i++)
                key[i] = (byte)(_hmacPartA[i] ^ _hmacMask[i]);
            return key;
        }
    }
}
