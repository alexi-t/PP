using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PP.Generator
{
    public static class MTProtoParser
    {
        public static TLType ParseLine(string line, bool isMethod)
        {
            var parts = line.TrimEnd(';').Split(' ').Where(p => p != "=").ToList();
            var type = parts.Last();
            var id = int.Parse(parts.First().Split('#').Last(), System.Globalization.NumberStyles.HexNumber).ToString();
            var name = parts.First().Split('#').First();
            var pars = parts.Skip(1).TakeWhile(p => p != type).Select(s => new TLTypeParams
            {
                name = s.Split(':').First(),
                type = s.Split(':').Last()
            }).ToArray();

            return isMethod ? new TLMethod
            {
                id = id,
                method = name,
                @params = pars,
                type = type
            } : new TLConstructor
            {
                id = id,
                predicate = name,
                @params = pars,
                type = type
            };
        }
    }
}
