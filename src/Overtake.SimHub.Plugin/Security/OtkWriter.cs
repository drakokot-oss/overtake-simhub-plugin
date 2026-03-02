using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Overtake.SimHub.Plugin.Security
{
    /// <summary>
    /// Writes Overtake Telemetry (.otk) encrypted files.
    /// Format: OTK1 header + AES-256-CBC ciphertext + HMAC-SHA256 (Encrypt-then-MAC).
    ///
    /// Layout:
    ///   [4 bytes]  Magic "OTK1"
    ///   [2 bytes]  Version (uint16 LE, currently 1)
    ///   [16 bytes] AES-CBC IV (random)
    ///   [4 bytes]  Ciphertext length (uint32 LE)
    ///   [N bytes]  Ciphertext (AES-256-CBC, PKCS7 padding)
    ///   [32 bytes] HMAC-SHA256 over (magic + version + IV + ciphertextLen + ciphertext)
    /// </summary>
    internal static class OtkWriter
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OTK1");
        private const ushort FormatVersion = 1;

        /// <summary>
        /// Encrypts and signs the JSON string, writing the .otk binary to the given path.
        /// Returns the JSON string unchanged so callers can verify data integrity.
        /// </summary>
        internal static string WriteOtk(string json, string path)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(json);
            byte[] aesKey = KeyStore.GetAesKey();
            byte[] hmacKey = KeyStore.GetHmacKey();

            byte[] iv;
            byte[] ciphertext;

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.GenerateIV();
                iv = aes.IV;

                using (var encryptor = aes.CreateEncryptor())
                {
                    ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }
            }

            byte[] versionBytes = BitConverter.GetBytes(FormatVersion);
            byte[] ciphertextLenBytes = BitConverter.GetBytes((uint)ciphertext.Length);

            // Build the signed payload: magic + version + IV + ciphertextLen + ciphertext
            byte[] signedPayload;
            using (var ms = new MemoryStream())
            {
                ms.Write(Magic, 0, Magic.Length);
                ms.Write(versionBytes, 0, versionBytes.Length);
                ms.Write(iv, 0, iv.Length);
                ms.Write(ciphertextLenBytes, 0, ciphertextLenBytes.Length);
                ms.Write(ciphertext, 0, ciphertext.Length);
                signedPayload = ms.ToArray();
            }

            byte[] hmac;
            using (var hmacAlg = new HMACSHA256(hmacKey))
            {
                hmac = hmacAlg.ComputeHash(signedPayload);
            }

            // Write final file: signedPayload + HMAC
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                fs.Write(signedPayload, 0, signedPayload.Length);
                fs.Write(hmac, 0, hmac.Length);
            }

            // Clear sensitive material from memory
            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(hmacKey, 0, hmacKey.Length);
            Array.Clear(plaintext, 0, plaintext.Length);

            return json;
        }

        /// <summary>
        /// Reads and decrypts an .otk file. Used for local verification/testing only.
        /// In production, decryption happens server-side.
        /// </summary>
        internal static string ReadOtk(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 4 + 2 + 16 + 4 + 32)
                throw new InvalidDataException("File too small to be a valid .otk");

            // Verify magic
            if (data[0] != (byte)'O' || data[1] != (byte)'T' || data[2] != (byte)'K' || data[3] != (byte)'1')
                throw new InvalidDataException("Invalid .otk magic number");

            int offset = 4;
            ushort version = BitConverter.ToUInt16(data, offset); offset += 2;
            if (version != FormatVersion)
                throw new InvalidDataException("Unsupported .otk version: " + version);

            byte[] iv = new byte[16];
            Array.Copy(data, offset, iv, 0, 16); offset += 16;

            uint ciphertextLen = BitConverter.ToUInt32(data, offset); offset += 4;
            if (ciphertextLen > data.Length - offset - 32)
                throw new InvalidDataException("Invalid ciphertext length");

            byte[] ciphertext = new byte[ciphertextLen];
            Array.Copy(data, offset, ciphertext, 0, (int)ciphertextLen);
            offset += (int)ciphertextLen;

            byte[] storedHmac = new byte[32];
            Array.Copy(data, offset, storedHmac, 0, 32);

            // Verify HMAC (Encrypt-then-MAC: HMAC covers everything before the HMAC)
            byte[] hmacKey = KeyStore.GetHmacKey();
            byte[] computedHmac;
            using (var hmacAlg = new HMACSHA256(hmacKey))
            {
                computedHmac = hmacAlg.ComputeHash(data, 0, offset);
            }
            Array.Clear(hmacKey, 0, hmacKey.Length);

            if (!ConstantTimeEquals(storedHmac, computedHmac))
                throw new CryptographicException("HMAC verification failed — file may be tampered");

            // Decrypt
            byte[] aesKey = KeyStore.GetAesKey();
            byte[] plaintext;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;

                using (var decryptor = aes.CreateDecryptor())
                {
                    plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                }
            }
            Array.Clear(aesKey, 0, aesKey.Length);

            return Encoding.UTF8.GetString(plaintext);
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
