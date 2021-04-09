using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace PP.Generator
{
    [Generator]
    public class TLEntityGenerator : ISourceGenerator
    {
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

        private static string GetTypeParameterTypeStr(string type)
        => type switch
        {
            "true" => "bool",
            "date" => "DateTime",
            "bytes" or "int128" or "int256" => "byte[]",
            _ when type.StartsWith("flags") => GetTypeParameterTypeStr(type.Split('?').Last()),
            _ when type.StartsWith("Vector") || type.StartsWith("vector") =>
                $"IReadOnlyList<{GetTypeParameterTypeStr(type.Replace("Vector<", "").Replace("vector<", "").Replace(">", ""))}>",
            "string" or "bool" or "int" or "long" or "double" => type,
            _ => "PP.Entity." + type
        };

        private static string WriteInitializerForParameter(string type, Dictionary<string, string[]> allTypes)
        => " = " + type switch
        {
            "true" => "true",
            "date" => "default(DateTime)",
            "bool" => "default(bool)",
            "int" => "default(int)",
            "long" => "default(long)",
            "double" => "default(double)",
            "string" => "string.Empty",
            "bytes" or "int128" or "int256" => "new byte[0]",
            _ when type.StartsWith("Vector") || type.StartsWith("vector") => $"new List<{GetTypeParameterTypeStr(type.Replace("Vector<", "").Replace("vector<", "").Replace(">", ""))}>()",
            _ => allTypes.ContainsKey(type) ? $"new PP.Entity.{(allTypes[type].FirstOrDefault(t => t.ToLower().Contains("empty")) ?? allTypes[type].First()).Split('#').First()}_ctr()" : "new ()"
        };

        private static string WriteTLTypeParams(TLTypeParams[] typeParams, Dictionary<string, string[]> allTypes)
        => string.Join("", typeParams
                .Where(p => p.name != "flags")
                .Select(p => $@"
            public {GetTypeParameterTypeStr(p.type)}{(p.type.StartsWith("flags") ? "?" : "")} {ReplaceReservedString(p.name)} {{ get; set; }}{(!p.type.StartsWith("flags") ? WriteInitializerForParameter(p.type, allTypes) + ";" : "")}
"));


        private static string WriteTLTypeCommonClass(string name, string fullTypeName, Dictionary<string, string[]> allTypes)
        => allTypes.ContainsKey(fullTypeName) ? @$"
        public abstract class {ReplaceReservedString(name)} : PP.Lib.TL.TLObjectBase
        {{ 
            public static {ReplaceReservedString(name)} Read(BinaryReader br)
                => ReadGeneric<{ReplaceReservedString(name)}>(br, new () {{
                    {string.Join("", allTypes[fullTypeName]
                        .Select(c => c.Split('#'))
                        .Select(c=> @$"
                    [{c[1]}] = (br) => {{
                        var entity = new {ReplaceReservedString(c[0])}_ctr();
                        entity.ReadFromStream(br);
                        return entity;
                    }},
"))}
                }});
        }}
" : @$"
        public abstract class {ReplaceReservedString(name)} : PP.Lib.TL.TLObjectBase
        {{ 
        }}
";

        private static string WriteTLType(TLType type, string name, string schema, Dictionary<string, string[]> allTypes)
            => $@"
        public class {ReplaceReservedString(name)}_ctr:{ReplaceReservedString(type.type.Split('.').Last())}
        {{
            protected override string Schema => ""{schema}"";

            {WriteTLTypeParams(type.@params, allTypes)}
        }}
";

        private static string WriteTLMethod(TLType type, string name, string schema, Dictionary<string, string[]> allTypes)
            => $@"
        public class {ReplaceReservedString(name)}_mth : PP.Lib.TL.TLObjectBase
        {{
            protected override string Schema => ""{schema}"";

            {WriteTLTypeParams(type.@params, allTypes)}
        }}
";

        private static void EnrichSchema(TLSchema schema, string[] serviceTypes)
        {
            var isMethod = false;
            foreach (var line in serviceTypes)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line == "--functions--")
                {
                    isMethod = true;
                    continue;
                }

                switch (MTProtoParser.ParseLine(line, isMethod))
                {
                    case TLConstructor ctr:
                        schema.constructors.Add(ctr);
                        break;
                    case TLMethod method:
                        schema.methods.Add(method);
                        break;
                };
            }
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var schemaFile = context.AdditionalFiles.FirstOrDefault(f => f.Path.Contains("tl_schema.json"));

            if (schemaFile == null)
                return;

            var text = schemaFile.GetText();
            if (text == null)
                return;

            var tlSchemas = context.AdditionalFiles.Where(f => f.Path.EndsWith(".tl")).SelectMany(f => f.GetText().Lines.Select(l => l.ToString())).ToList();

            //#if DEBUG
            //            if (!Debugger.IsAttached)
            //            {
            //                Debugger.Launch();
            //            }
            //#endif 
            var schema = JsonConvert.DeserializeObject<TLSchema>(text.ToString());

            EnrichSchema(schema,
                context.AdditionalFiles.FirstOrDefault(f => f.Path.Contains("ServiceTypes.tl"))?.GetText().Lines.Select(l => l.ToString()).ToArray() ?? new string[0]);

            var predicatesByType = new Dictionary<string, string[]>();
            schema.constructors.GroupBy(c => c.type).ToList().ForEach(c => predicatesByType.Add(c.Key, c.Select(ctr => $"{ctr.predicate}#{ctr.id}").ToArray()));

            var sb = new StringBuilder();

            //// Usings
            sb.Append(@"
#nullable enable
namespace PP.Entity {
    using System.IO;
    using System.Collections.Generic;
");
            foreach (var namespaceGroup in schema.constructors.GroupBy(c => c.predicate.Contains(".") ? c.predicate.Split('.').First() : ""))
            {
                if (namespaceGroup.Key != "")
                {
                    sb.Append(@$"
    namespace {namespaceGroup.Key} {{");
                }

                foreach (var ctr in namespaceGroup.Where(c => c.predicate != "vector"))
                {
                    var typeName = namespaceGroup.Key != "" ? ctr.predicate.Split('.').Last() : ctr.predicate;
                    var typeSchema = tlSchemas.FirstOrDefault(l => l.StartsWith($"{ctr.predicate}#"));

                    sb.Append(WriteTLType(ctr, typeName, typeSchema, predicatesByType));
                }

                foreach (var typeGroup in namespaceGroup.Where(c => c.predicate != "vector").GroupBy(c => c.type))
                {
                    sb.Append(WriteTLTypeCommonClass(typeGroup.Key.Split('.').Last(), typeGroup.Key, predicatesByType));
                }

                if (namespaceGroup.Key != "")
                {
                    sb.Append(@"
    }
");
                }
            }

            sb.Append(@"
}
");
            sb.Append(@"
#nullable enable
namespace PP.Entity.Methods {
    using PP.Lib.TL;
    using System.Collections.Generic;
");
            foreach (var namespaceGroup in schema.methods.GroupBy(c => c.method.Contains(".") ? c.method.Split('.').First() : ""))
            {
                if (namespaceGroup.Key != "")
                {
                    sb.Append(@$"
    namespace {namespaceGroup.Key} {{");
                }

                foreach (var method in namespaceGroup.Where(c => c.type != "X"))
                {
                    var typeName = namespaceGroup.Key != "" ? method.method.Split('.').Last() : method.method;
                    var typeSchema = tlSchemas.FirstOrDefault(l => l.StartsWith($"{method.method}#"));
                    sb.Append(WriteTLMethod(method, typeName, typeSchema, predicatesByType));
                }

                if (namespaceGroup.Key != "")
                {
                    sb.Append(@"
    }
");
                }
            }
            sb.Append(@"
}
");


            context.AddSource("schema", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        public void Initialize(GeneratorInitializationContext context)
        {
        }
    }
}
