using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PP.Lib.TL
{
    public record TLTypeParams
    {
        public string name { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
    }

    public record TLTypeSchema
    {
        public string name { get; set; } = string.Empty;
        public uint id { get; set; }
        public string type { get; set; } = string.Empty;
        public TLTypeParams[] @params { get; set; } = new TLTypeParams[0];
    }

    public static class MTProtoParser
    {
        private static readonly Dictionary<string, TLTypeSchema> _cache = new();

        public static TLTypeSchema Parse(string line)
        {
            if (_cache.ContainsKey(line))
                return _cache[line];

            var parts = line.TrimEnd(';').Split(' ').Where(p => p != "=").ToList();
            var type = parts.Last();
            var id = uint.Parse(parts.First().Split('#').Last(), System.Globalization.NumberStyles.HexNumber);
            var name = parts.First().Split('#').First();
            var pars = parts.Skip(1).TakeWhile(p => p != type).Select(s => new TLTypeParams
            {
                name = s.Split(':').First(),
                type = s.Split(':').Last()
            }).ToArray();

            var schema = new TLTypeSchema
            {
                id = id,
                name = name,
                @params = pars,
                type = type
            };
            _cache.Add(line, schema);

            return schema;
        }
    }
}
