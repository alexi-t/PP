using PP.Lib.Internal.Messaging;
using PP.Lib.Internal.Transport;
using Serilog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using PP.Entity.Methods;
using System.Numerics;
using PP.Entity;

namespace PP.Lib.Internal.Auth
{
    public class AuthKeyGenerator
    {
        private enum AuthState
        {
            ReqPQ,
            ResPQ,
            ReqDH,
            ResDH,
            SetDH,
            DHGenCheck,
            End
        }

        private class AuthPayload
        {
            public byte[] NonceClient { get; set; } = new byte[16];
            public byte[] NonceServer { get; set; } = new byte[0];
            public byte[] NewNonce { get; set; } = new byte[32];
            public byte[]? PQ { get; set; }
            public byte[]? AuthKey { get; set; }

            public BigInteger DHPrime { get; set; }
            public BigInteger GA { get; set; }
            public BigInteger G { get; set; }
            public BigInteger B { get; set; }

            public long[]? PublicKeyTokens { get; set; }
            public long ServerSalt { get; set; }

            public byte[] CalculateTmpAesKey()
            {
                var hash1 = SHA1.HashData(NewNonce.Concat(NonceServer).ToArray());
                var hash2 = SHA1.HashData(NonceServer.Concat(NewNonce).ToArray());

                return hash1.Concat(hash2[0..12]).ToArray();
            }

            public byte[] CalculateTmpAesIV()
            {
                var hash1 = SHA1.HashData(NonceServer.Concat(NewNonce).ToArray());
                var hash2 = SHA1.HashData(NewNonce.Concat(NewNonce).ToArray());

                return hash1[^8..].Concat(hash2).Concat(NewNonce[0..4]).ToArray();
            }
        }

        private static readonly Dictionary<AuthState, AuthState> stateMap = new()
        {
            [AuthState.ReqPQ] = AuthState.ResPQ,
            [AuthState.ResPQ] = AuthState.ReqDH,
            [AuthState.ReqDH] = AuthState.ResDH,
            [AuthState.ResDH] = AuthState.SetDH,
            [AuthState.SetDH] = AuthState.DHGenCheck,
            [AuthState.DHGenCheck] = AuthState.End
        };

        private static AuthState next(AuthState state) => stateMap[state];

        private readonly ConnectionPool _connectionPool;
        private readonly ILogger _log = Log.ForContext<AuthKeyGenerator>();

        public AuthKeyGenerator(ConnectionPool connectionPool)
        {
            _connectionPool = connectionPool;
        }

        public async Task<(byte[], long)> GetKey()
        {
            _log.Verbose("Start GetKey");

            var state = AuthState.ReqPQ;
            var payload = new AuthPayload();
            while (state != AuthState.End)
            {
                (state, payload) = await ProcessState(state, payload);
            }

            if (payload.AuthKey != null)
                return (payload.AuthKey, payload.ServerSalt);
            else
                throw new ApplicationException();
        }

        private async Task<(AuthState, AuthPayload)> ProcessState(AuthState state, AuthPayload payload) =>
            state switch
            {
                AuthState.ReqPQ => (next(state), await ReqPQ(payload)),
                AuthState.ResPQ => (next(state), ResPQ(payload, await _connectionPool.ReadSingle())),
                AuthState.ReqDH => (next(state), await ReqDH(payload)),
                AuthState.ResDH => (next(state), ResDH(payload, await _connectionPool.ReadSingle())),
                AuthState.SetDH => (next(state), await SetDH(payload)),
                AuthState.DHGenCheck => (next(state), DHGenCheck(payload, await _connectionPool.ReadSingle())),
                _ => throw new NotImplementedException()
            };

        private AuthPayload DHGenCheck(AuthPayload payload, Memory<byte> memory)
        {
            var msg = UnsecuredMessage.CreateFromBytes(memory);
            using var ms = new MemoryStream(msg.Content.ToArray());
            using var br = new BinaryReader(ms);
            var dhAnswer = PP.Entity.Set_client_DH_params_answer.Read(br);
            _log.Verbose(msg.ToString());
            switch (dhAnswer)
            {
                case dh_gen_ok_ctr ok:
                    payload.ServerSalt = BitConverter.ToInt64(
                        payload.NewNonce[0..8]
                        .Zip(payload.NonceServer[0..8])
                        .Select(t => (byte)(t.First ^ t.Second))
                        .ToArray().AsSpan());
                    break;
                default:
                    break;
            }

            return payload;
        }

        private async Task<AuthPayload> SetDH(AuthPayload payload)
        {
            if (payload.NonceServer == null)
                throw new ArgumentException("NonceServer are empty on SetDH stage");

            using var clientInnerMS = new MemoryStream();
            new client_DH_inner_data_ctr
            {
                nonce = payload.NonceClient,
                server_nonce = payload.NonceServer,
                g_b = BigInteger.ModPow(payload.G, payload.B, payload.DHPrime).ToByteArray(isUnsigned: true, isBigEndian: true),
                retry_id = 0
            }.WriteToStream(new BinaryWriter(clientInnerMS));

            var clientInner = clientInnerMS.ToArray();

            using var clientInnerMSEnc = new MemoryStream();
            using var clientInnerEncWriter = new BinaryWriter(clientInnerMSEnc);
            clientInnerEncWriter.Write(SHA1.HashData(clientInner));
            clientInnerEncWriter.Write(clientInner);
            var align = new byte[(16 - clientInnerMSEnc.Position % 16) % 16];
            var random = RandomNumberGenerator.Create();
            random.GetBytes(align);
            clientInnerEncWriter.Write(align);

            var msg = UnsecuredMessage.CreateFromObject(new set_client_DH_params_mth
            {
                nonce = payload.NonceClient,
                server_nonce = payload.NonceServer,
                encrypted_data = AesEnc.AES256IGEEncrypt(clientInnerMSEnc.ToArray(),
                    payload.CalculateTmpAesIV(),
                    payload.CalculateTmpAesKey())
            });

            await _connectionPool.Queue(msg.MsgId, msg.AsBytes);

            return payload;
        }

        private AuthPayload ResDH(AuthPayload payload, Memory<byte> response)
        {
            if (payload.PQ == null)
                throw new ArgumentException("PQ are empty on ResDH stage");
            if (payload.PublicKeyTokens == null)
                throw new ArgumentException("PublicKeyTokens are empty on ResDH stage");

            _log.Verbose("Start ResDH");

            var msg = UnsecuredMessage.CreateFromBytes(response);
            using var ms = new MemoryStream(msg.Content.ToArray());
            using var br = new BinaryReader(ms);
            var dhParams = PP.Entity.Server_DH_Params.Read(br);
            switch (dhParams)
            {
                case PP.Entity.server_DH_params_ok_ctr ok:
                    {
                        var aesVector = payload.CalculateTmpAesIV();
                        var aesKey = payload.CalculateTmpAesKey();

                        var answerWithHash = AesEnc.AES256IGEDecrypt(ok.encrypted_answer, aesVector, aesKey);
                        var answer = answerWithHash[20..];

                        using var encMs = new MemoryStream(answer);
                        using var innerReader = new BinaryReader(encMs);
                        var serverDHInner = Server_DH_inner_data.Read(innerReader);
                        if (serverDHInner is server_DH_inner_data_ctr innerDataCtr)
                        {
                            payload.DHPrime = new BigInteger(innerDataCtr.dh_prime, isBigEndian: true, isUnsigned: true);
                            payload.GA = new BigInteger(innerDataCtr.g_a, isBigEndian: true, isUnsigned: true);
                            payload.G = new BigInteger(innerDataCtr.g);

                            var random = RandomNumberGenerator.Create();
                            var bArray = new byte[256];
                            random.GetBytes(bArray);
                            payload.B = new BigInteger(bArray, isBigEndian: true, isUnsigned: true);

                            payload.AuthKey = BigInteger.ModPow(payload.GA, payload.B, payload.DHPrime).ToByteArray(isUnsigned: true, isBigEndian: true);
                        }
                        break;
                    }
                case PP.Entity.server_DH_params_fail_ctr fail:
                    {

                        break;
                    }
            };

            _log.Verbose(msg.ToString());

            return payload;
        }

        private async Task<AuthPayload> ReqDH(AuthPayload payload)
        {
            if (payload.PQ == null)
                throw new ArgumentException("PQ are empty on ReqDH stage");
            if (payload.NonceServer == null)
                throw new ArgumentException("NonceServer are empty on ReqDH stage");
            if (payload.PublicKeyTokens == null)
                throw new ArgumentException("PublicKeyTokens are empty on ReqDH stage");

            var pq = new BigInteger(payload.PQ, isBigEndian: true);

            BigInteger rhoPollard(BigInteger n)
            {
                BigInteger x = 2;
                BigInteger y = 2;
                BigInteger d = 1;

                Func<BigInteger, BigInteger> gX = _x => BigInteger.Remainder(BigInteger.Pow(_x, 2) + 1, n);

                while (d == 1)
                {
                    x = gX(x);
                    y = gX(gX(y));
                    d = BigInteger.GreatestCommonDivisor(BigInteger.Abs(x - y), n);
                }

                if (d == n)
                    throw new ArgumentException("RHO failure");
                else
                    return d;
            }

            var primeFactor1 = rhoPollard(pq);
            var primeFactor2 = pq / primeFactor1;

            BigInteger p, q;

            if (primeFactor1 < primeFactor2)
            {
                p = primeFactor1;
                q = primeFactor2;
            }
            else
            {
                p = primeFactor2;
                q = primeFactor1;
            }

            var random = RandomNumberGenerator.Create();
            random.GetBytes(payload.NewNonce);

            var pqInner = new PP.Entity.p_q_inner_data_ctr
            {
                pq = payload.PQ,
                p = p.ToByteArray(isBigEndian: true),
                q = q.ToByteArray(isBigEndian: true),
                nonce = payload.NonceClient,
                server_nonce = payload.NonceServer,
                new_nonce = payload.NewNonce
            };

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            pqInner.WriteToStream(bw);

            var pqInnerBytes = ms.ToArray();
            var pqInnerBytesHash = SHA1.HashData(pqInnerBytes);
            var pqInnerAlign = new byte[255 - (pqInnerBytes.Length + pqInnerBytesHash.Length) % 255];
            random.GetBytes(pqInnerAlign);

            var innerData = new byte[pqInnerBytesHash.Length + pqInnerBytes.Length + pqInnerAlign.Length];
            pqInnerBytesHash.CopyTo(innerData, 0);
            pqInnerBytes.CopyTo(innerData, pqInnerBytesHash.Length);
            pqInnerAlign.CopyTo(innerData, pqInnerBytesHash.Length + pqInnerBytes.Length);

            var key = PublicKeys.Keys.FirstOrDefault(k => payload.PublicKeyTokens.Contains(k.FingerPrint));
            if (key == null)
                throw new ArgumentException("Matched key not found");

            var encrypted = key.Encrypt(innerData);

            var reqDH = new PP.Entity.Methods.req_DH_params_mth
            {
                p = p.ToByteArray(isBigEndian: true),
                q = q.ToByteArray(isBigEndian: true),
                nonce = payload.NonceClient,
                server_nonce = payload.NonceServer,
                public_key_fingerprint = key.FingerPrint,
                encrypted_data = encrypted
            };

            var message = UnsecuredMessage.CreateFromObject(reqDH);

            _log.Verbose(message.ToString());

            await _connectionPool.Queue(message.MsgId, message.AsBytes);

            return payload;
        }

        private async Task<AuthPayload> ReqPQ(AuthPayload payload)
        {
            _log.Verbose("Start ReqPQ");

            var random = RandomNumberGenerator.Create();
            random.GetBytes(payload.NonceClient);

            var message = UnsecuredMessage.CreateFromObject(new req_pq_multi_mth
            {
                nonce = payload.NonceClient
            });

            _log.Verbose(message.ToString());

            await _connectionPool.Queue(message.MsgId, message.AsBytes);

            return payload;
        }

        private AuthPayload ResPQ(AuthPayload payload, Memory<byte> response)
        {
            _log.Verbose("Start ResPQ");
            _log.Verbose(string.Join("", response.ToArray().Select(b => b.ToString("X2"))));

            var msg = UnsecuredMessage.CreateFromBytes(response);

            using var ms = new MemoryStream(msg.Content.ToArray());
            using var br = new BinaryReader(ms);
            var resPQ = PP.Entity.ResPQ.Read(br);
            if (resPQ is PP.Entity.resPQ_ctr resPQCtr)
            {
                _log.Verbose(msg.ToString());

                payload.NonceServer = resPQCtr.server_nonce;
                payload.PublicKeyTokens = resPQCtr.server_public_key_fingerprints.ToArray();
                payload.PQ = resPQCtr.pq;
            }

            return payload;
        }
    }
}
