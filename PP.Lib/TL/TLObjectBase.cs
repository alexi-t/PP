using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PP.Lib.TL
{
    public abstract class TLObjectBase
    {
        protected abstract string Schema { get; }

        private static string ReplaceReservedString(string name)
        => name switch
        {
            "default" or
            "delete" or
            "static" or
            "public" or
            "null" or
            "true" or
            "false" or
            "long" or
            "out" or
            "params" or
            "private" => "@" + name,
            _ => name
        };

        private T? GetProp<T>(string name) => (T?)GetType().GetProperty(ReplaceReservedString(name))?.GetValue(this);
        private Type? GetPropType(string name) => GetType().GetProperty(ReplaceReservedString(name))?.PropertyType;
        private void SetProp<T>(string name, T value) => GetType().GetProperty(ReplaceReservedString(name))?.SetValue(this, value);

        protected static T ReadGeneric<T>(BinaryReader br, Dictionary<int, Func<BinaryReader, T>> ctrs)
        {
            var ctr = br.ReadInt32();
            br.BaseStream.Position -= 4;
            if (ctrs.ContainsKey(ctr))
                return ctrs[ctr](br);
            throw new InvalidOperationException($"No match for ctr {ctr:X2}");
        }

        public int Estimate()
        {
            var type = MTProtoParser.Parse(Schema);

            var size = 4;

            static int estimateString(string? s) => s != null ? s.Length + (s.Length >= 254 ? 4 + (4 - (4 + s.Length) % 4) % 4 : 1 + (4 - (1 + s.Length) % 4) % 4) : 0;
            static int estimateArray(Array? a) => a != null ? a.Length + (a.Length >= 254 ? 4 + (4 - (4 + a.Length) % 4) % 4 : 1 + (4 - (1 + a.Length) % 4) % 4) : 0;
            static int estimateVecor(IReadOnlyList<TLObjectBase>? v) => v != null ? 4 + v.Select(o => o.Estimate()).Sum() : 0;

            var hasFlags = type.@params.First()?.name == "flags";
            var flags = 0;
            foreach (var par in type.@params)
            {
                var parType = hasFlags ? par.type.Split('?').Last() : par.type;
                var parSize = parType switch
                {
                    "int" or "true" or "bool" or "date" or "#" => 4,
                    "int128" => 16,
                    "int256" => 32,
                    "long" or "double" => 8,
                    "string" => estimateString(GetProp<string>(par.name)),
                    "bytes" => estimateArray(GetProp<byte[]>(par.name)),
                    _ when parType.StartsWith("Vector") || parType.StartsWith("vector") => estimateVecor(GetProp<IReadOnlyList<TLObjectBase>>(par.name)),
                    _ => GetProp<TLObjectBase>(par.name)?.Estimate() ?? 0
                };
                size += parSize;
                if (hasFlags && par.type.StartsWith("flags"))
                {
                    var flagsIndex = int.Parse(par.type.Split('?').First().Replace("flags.", ""));
                    if (parSize > 0)
                        flags |= 1 << flagsIndex;
                }
            }

            return size;
        }

        protected void ReadFromStream(BinaryReader br)
        {
            var type = MTProtoParser.Parse(Schema);
            var id = br.ReadUInt32();
            if (id != type.id)
                throw new InvalidOperationException("Constructor mismatch");

            var hasFlags = type.@params.First()?.name == "flags";
            var flags = hasFlags ? br.ReadInt32() : 0;

            static int readInt(BinaryReader br) => br.ReadInt32();
            static long readLong(BinaryReader br) => br.ReadInt64();
            static double readDouble(BinaryReader br) => br.ReadDouble();
            static bool readBool(BinaryReader br) => br.ReadUInt32() == 0x997275b5;
            static byte[] readRawBytes(BinaryReader br, int count) => br.ReadBytes(count);
            static byte[] readBytes(BinaryReader br)
            {
                int count = br.ReadByte();
                int startOffset = 1;
                if (count >= 254)
                {
                    count = br.ReadByte() + (br.ReadByte() << 8) + (br.ReadByte() << 16);
                    startOffset = 4;
                }

                byte[] raw = new byte[count];
                br.Read(raw, 0, count);
                int offset = (count + startOffset) % 4;
                if (offset != 0)
                {
                    int offsetCount = 4 - offset;
                    var newTmp = new byte[offsetCount];
                    br.Read(newTmp, 0, offsetCount);
                }

                return raw;
            };
            static System.Collections.IList readVector(BinaryReader br, Type itemType, bool bare)
            {
                if (!bare)
                {
                    var id = br.ReadUInt32();
                    if (id != 0x1cb5c415)
                        throw new Exception("Error read vector");
                }
                var count = br.ReadInt32();
                var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType)) as System.Collections.IList;
                if (list == null)
                    throw new InvalidOperationException("Unbale to create generic list");
                for (int i = 0; i < count; i++)
                {
                    if (itemType == typeof(int))
                        list.Add(br.ReadInt32());
                    else if (itemType == typeof(long))
                        list.Add(br.ReadInt64());
                    else if (itemType == typeof(byte[]))
                        list.Add(readBytes(br));
                    else if (itemType == typeof(string))
                        list.Add(Encoding.UTF8.GetString(readBytes(br)));
                    else
                    {
                        var instance = Activator.CreateInstance(itemType) as TLObjectBase;
                        if (instance == null)
                            throw new ApplicationException($"Error create instance of type {itemType}");
                        instance.ReadFromStream(br);
                        list.Add(instance);
                    }
                }
                return list;
            }


            foreach (var par in type.@params.Skip(hasFlags ? 1 : 0))
            {
                var parType = hasFlags ? par.type.Split('?').Last() : par.type;

                var flagged = par.type.StartsWith("flags");

                var flagIndex = flagged ? int.Parse(par.type.Split('?').First().Replace("flags.", "")) : -1;
                if (flagged && ((flags & (1 << flagIndex)) == 0))
                    continue;

                switch (parType)
                {
                    case "true":
                        SetProp(par.name, true);
                        break;
                    case "int":
                        SetProp(par.name, readInt(br));
                        break;
                    case "long":
                        SetProp(par.name, readLong(br));
                        break;
                    case "double":
                        SetProp(par.name, readDouble(br));
                        break;
                    case "bool":
                        SetProp(par.name, readBool(br));
                        break;
                    case "date":
                        SetProp(par.name, DateTime.UnixEpoch.AddSeconds(readInt(br)));
                        break;
                    case "int128":
                    case "int256":
                        SetProp(par.name, readRawBytes(br, parType == "int128" ? 16 : 32));
                        break;
                    case "bytes":
                        SetProp(par.name, readBytes(br));
                        break;
                    case "string":
                        SetProp(par.name, Encoding.UTF8.GetString(readBytes(br)));
                        break;
                    case string when parType.StartsWith("Vector") || parType.StartsWith("vector"):
                        {
                            var bare = parType.StartsWith("vector");
                            var itemType = GetPropType(par.name)?.GetGenericArguments().First();
                            if (itemType != null)
                                SetProp(par.name, readVector(br, itemType, bare));
                            break;
                        }
                    default:
                        throw new Exception($"{par.name}:{par.type} not parsed");
                };
            }
        }

        public void WriteToStream(BinaryWriter bw)
        {
            var type = MTProtoParser.Parse(Schema);

            bw.Write(type.id);

            var hasFlags = type.@params.First()?.name == "flags";
            var flags = 0;

            static void writeInt(BinaryWriter bw, int value) => bw.Write(value);
            static void writeLong(BinaryWriter bw, long value) => bw.Write(value);
            static void writeDouble(BinaryWriter bw, double value) => bw.Write(value);
            static void writeBool(BinaryWriter bw, bool value) => bw.Write(value ? 0x997275b5 : 0xbc799737);
            static void writeRawBytes(BinaryWriter bw, byte[] value) => bw.Write(value);
            static void writeBytes(BinaryWriter bw, byte[] value)
            {
                int count = value.Length;
                int startOffset = 1;
                if (count >= 254)
                {
                    startOffset = 4;
                    bw.Write((byte)254);
                    bw.Write((byte)(count & 0xFF));
                    bw.Write((byte)((count >> 8) & 0xFF));
                    bw.Write((byte)((count >> 16) & 0xFF));
                }
                else
                    bw.Write((byte)count);

                byte[] raw = new byte[count];
                bw.Write(value);
                int offset = (count + startOffset) % 4;
                if (offset != 0)
                {
                    int offsetCount = 4 - offset;
                    var newTmp = new byte[offsetCount];
                    bw.Write(newTmp);
                }
            };
            static void writeVector(BinaryWriter bw, IReadOnlyList<TLObjectBase> vector)
            {
                bw.Write(0x1cb5c415);
                bw.Write(vector.Count);
                foreach (var o in vector)
                {
                    o.WriteToStream(bw);
                }
            }

            foreach (var par in type.@params.Skip(hasFlags ? 1 : 0))
            {
                var parType = hasFlags ? par.type.Split('?').Last() : par.type;

                var flagged = par.type.StartsWith("flags");

                var flagIndex = flagged ? int.Parse(par.type.Split('?').First().Replace("flags.", "")) : -1;

                switch (parType)
                {
                    case "true":
                        flags |= 1 << flagIndex;
                        break;
                    case "int":
                        writeInt(bw, GetProp<int>(par.name));
                        break;
                    case "long":
                        writeLong(bw, GetProp<long>(par.name));
                        break;
                    case "double":
                        writeDouble(bw, GetProp<double>(par.name));
                        break;
                    case "bool":
                        writeBool(bw, GetProp<bool>(par.name));
                        break;
                    case "date":
                        writeInt(bw, (int)GetProp<DateTime>(par.name).Subtract(DateTime.UnixEpoch).TotalSeconds);
                        break;
                    case "int128":
                    case "int256":
                        {
                            var value = GetProp<byte[]>(par.name);
                            if (value != null)
                            {
                                if (flagged)
                                    flags |= 1 << flagIndex;
                                writeRawBytes(bw, value);
                            }
                            break;
                        }
                    case "bytes":
                        {
                            var value = GetProp<byte[]>(par.name);
                            if (value != null)
                            {
                                if (flagged)
                                    flags |= 1 << flagIndex;
                                writeBytes(bw, value);
                            }
                            break;
                        }
                    case "string":
                        {
                            var value = GetProp<string>(par.name);
                            if (value != null)
                            {
                                if (flagged)
                                    flags |= 1 << flagIndex;
                                writeBytes(bw, Encoding.UTF8.GetBytes(value));
                            }
                            break;
                        }
                    case string when parType.StartsWith("Vector") || parType.StartsWith("vector"):
                        {
                            var value = GetProp<IReadOnlyList<TLObjectBase>>(par.name);
                            if (value != null)
                            {
                                if (flagged)
                                    flags |= 1 << flagIndex;
                                writeVector(bw, value);
                            }
                            break;
                        }
                };
            }
        }
    }
}
