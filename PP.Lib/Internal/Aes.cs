using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace PP.Lib.Internal
{
    public static class AesEnc
    {
        public static byte[] AES256IGEEncrypt(byte[] src, byte[] iv, byte[] key)
            => IGECrypter(src,
                x0: iv.Skip(16).ToArray(),
                y0: iv.Take(16).ToArray(),
                key,
                aes => aes.CreateEncryptor(key, null));

        public static byte[] AES256IGEDecrypt(byte[] src, byte[] iv, byte[] key)
            => IGECrypter(src,
                x0: iv.Take(16).ToArray(),
                y0: iv.Skip(16).ToArray(),
                key,
                aes => aes.CreateDecryptor(key, null));

        private static byte[] IGECrypter(byte[] src, byte[] x0, byte[] y0, byte[] key, Func<Aes, ICryptoTransform> transform)
        {
            using var aes = Aes.Create("AesManaged");
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            aes.BlockSize = 16 * 8;

            aes.Key = key;
            aes.KeySize = 32 * 8;

            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            using var aesDecrypt = transform(aes);
            int blocksCount = src.Length / 16;
            byte[] outData = new byte[src.Length];

            //yi=fk(xi^yi−1)^xi−1 - aes ige chain
            byte[] x = x0; //x0
            byte[] y = y0; //y0

            //compute y1
            for (int j = 0; j < 16; j++) //x1^y0
            {
                outData[j] = (byte)(src[j] ^ y[j]);
            }
            //fk(x1^y0)
            if (aesDecrypt.TransformBlock(outData, 0, 16, outData, 0) == 0)
                aesDecrypt.TransformBlock(outData, 0, 16, outData, 0);
            for (int j = 0; j < 16; j++)//fk(x1^y0)^x0
            {
                outData[j] = (byte)(outData[j] ^ x[j]);
            }

            x = src;
            y = outData;

            for (int i = 1; i < blocksCount; i++)//y2..n
            {
                int offset = i * 16;

                for (int j = 0; j < 16; j++) //xi^yi−1
                {
                    y[offset + j] = (byte)(x[offset + j] ^ y[offset - 16 + j]);
                }
                aesDecrypt.TransformBlock(outData, offset, 16, outData, offset);//fk(xi^yi−1)
                for (int j = 0; j < 16; j++)//fk(xi^yi−1)^xi−1
                {
                    y[offset + j] = (byte)(y[offset + j] ^ x[offset - 16 + j]);
                }
            }

            return outData;
#pragma warning restore CS8602 // Dereference of a possibly null reference.

        }
    }
}
