using System;
using System.Collections.Generic;
using System.Text;

namespace PP.Generator
{
    public record TLTypeParams
    {
        public string name { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
    }

    public record TLType
    {
        public string id { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public TLTypeParams[] @params { get; set; } = new TLTypeParams[0];
    }

    public record TLConstructor : TLType
    {
        public string predicate { get; set; } = string.Empty;
    }

    public record TLMethod : TLType
    {
        public string method { get; set; } = string.Empty;
    }

    public record TLSchema
    {
        public List<TLConstructor> constructors { get; set; } = new ();
        public List<TLMethod> methods { get; set; } = new ();
    }
}
