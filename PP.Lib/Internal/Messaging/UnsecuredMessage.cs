using PP.Lib.TL;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PP.Lib.Internal.Messaging
{
    public class UnsecuredMessage : IDisposable
    {
        private readonly static ArrayPool<byte> _pool = ArrayPool<byte>.Create();

        private readonly byte[] _array;

        private UnsecuredMessage(byte[] array)
        {
            _array = array;
        }

        public long AuthKeyId { get; private init; }
        public long MsgId { get; private init; }
        public int ContentLength { get; private init; }
        public Memory<byte> AsBytes => _array.AsMemory().Slice(0, 20 + ContentLength);
        public Memory<byte> Content => _array.AsMemory().Slice(20, ContentLength);

        public static UnsecuredMessage CreateFromBytes(Memory<byte> bytes)
        {
            using var input = new MemoryStream(bytes.ToArray());
            using var br = new BinaryReader(input);

            var authKeyId = br.ReadInt64();
            var msgId = br.ReadInt64() >> 32;
            Time.CorrectOffset(msgId);
            var objectSize = br.ReadInt32();
            var content = br.ReadBytes(objectSize);

            var memory = _pool.Rent(bytes.Length);
            bytes.CopyTo(memory);

            return new(memory)
            {
                AuthKeyId = authKeyId,
                MsgId = msgId,
                ContentLength = objectSize
            };
        }

        public static UnsecuredMessage CreateFromObject(TLObjectBase tLObject)
        {
            var id = Time.GetId();
            var objectSize = tLObject.Estimate();
            var memory = _pool.Rent(8 + 8 + 4 + objectSize);
            using (var ms = new MemoryStream(memory))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(0L);
                bw.Write(id);
                bw.Write(objectSize);
                tLObject.WriteToStream(bw);
            }

            return new(memory)
            {
                AuthKeyId = 0,
                MsgId = id,
                ContentLength = objectSize
            };
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendJoin("", _array.Take(8).Select(b => b.ToString("X2")));
            sb.AppendLine();
            sb.AppendJoin("", _array.Skip(8).Take(8).Select(b => b.ToString("X2")));
            sb.AppendLine();
            sb.AppendJoin("", _array.Skip(8 + 8).Take(4).Select(b => b.ToString("X2")));
            sb.AppendLine();

            int index = 0;
            foreach (var b in _array.Skip(8 + 8 + 4).Take(ContentLength))
            {
                if (index > 0)
                {
                    if (index % 4 == 0)
                        sb.Append(" ");
                    if (index % 16 == 0)
                        sb.AppendLine();
                }
                sb.Append(b.ToString("X2"));
                index++;
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            _pool.Return(_array);

            GC.SuppressFinalize(this);
        }
    }
}
