using System;
using System.Collections.Generic;

namespace RosSharp.RosBridgeClient.Internal
{
    internal sealed class RosMessageDefinition
    {
        public RosMessageDefinition(string name, IReadOnlyList<RosField> fields)
        {
            Name = name;
            Fields = fields;
        }

        public string Name { get; }
        public IReadOnlyList<RosField> Fields { get; }
    }

    internal sealed class RosField
    {
        public RosField(string name, string type, bool isArray, int? fixedLength)
        {
            Name = name;
            Type = type;
            IsArray = isArray;
            FixedLength = fixedLength;
        }

        public string Name { get; }
        public string Type { get; }
        public bool IsArray { get; }
        public int? FixedLength { get; }
    }

    internal static class RosMessageDefinitionParser
    {
        private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
        {
            "bool", "byte", "char",
            "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64",
            "float32", "float64", "string", "wstring"
        };

        public static IReadOnlyDictionary<string, RosMessageDefinition> Parse(string rootType, string schema)
        {
            var definitions = new Dictionary<string, List<RosField>>(StringComparer.Ordinal);
            string currentType = RosTypeName.NormalizeMessage(rootType);
            definitions[currentType] = new List<RosField>();

            foreach (string rawLine in schema.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0 || line.StartsWith("===", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("MSG:", StringComparison.Ordinal))
                {
                    currentType = RosTypeName.NormalizeMessage(line[4..].Trim());
                    if (!definitions.ContainsKey(currentType))
                        definitions[currentType] = new List<RosField>();
                    continue;
                }

                if (line.Contains('=', StringComparison.Ordinal))
                    continue;

                string[] pieces = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length < 2)
                    continue;

                ParseType(pieces[0], out string fieldType, out bool isArray, out int? fixedLength);
                definitions[currentType].Add(new RosField(
                    pieces[1],
                    ResolveType(currentType, fieldType),
                    isArray,
                    fixedLength));
            }

            var result = new Dictionary<string, RosMessageDefinition>(StringComparer.Ordinal);
            foreach ((string name, List<RosField> fields) in definitions)
                result[name] = new RosMessageDefinition(name, fields);
            return result;
        }

        private static string StripComment(string line)
        {
            int index = line.IndexOf('#');
            return index >= 0 ? line[..index] : line;
        }

        private static void ParseType(string rawType, out string type, out bool isArray, out int? fixedLength)
        {
            isArray = false;
            fixedLength = null;
            type = rawType;

            int bracket = rawType.IndexOf('[');
            if (bracket < 0)
            {
                type = StripBounds(rawType);
                return;
            }

            isArray = true;
            type = StripBounds(rawType[..bracket]);
            int close = rawType.IndexOf(']', bracket);
            if (close > bracket + 1 && int.TryParse(rawType[(bracket + 1)..close], out int length))
                fixedLength = length;
        }

        private static string StripBounds(string type)
        {
            int bound = type.IndexOf("<=", StringComparison.Ordinal);
            return bound >= 0 ? type[..bound] : type;
        }

        private static string ResolveType(string currentType, string fieldType)
        {
            if (Builtins.Contains(fieldType))
                return fieldType;

            if (fieldType.Contains('/'))
                return RosTypeName.NormalizeMessage(fieldType);

            string[] parts = currentType.Split('/');
            if (parts.Length < 3)
                return fieldType;

            string kind = parts[1] == "action" ? "action" : "msg";
            return $"{parts[0]}/{kind}/{fieldType}";
        }
    }
}
