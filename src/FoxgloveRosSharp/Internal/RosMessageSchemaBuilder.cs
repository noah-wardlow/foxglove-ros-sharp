using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RosSharp.RosBridgeClient.Internal
{
    internal static class RosMessageSchemaBuilder
    {
        private static readonly Dictionary<Type, string> Builtins = new()
        {
            [typeof(bool)] = "bool",
            [typeof(byte)] = "uint8",
            [typeof(sbyte)] = "int8",
            [typeof(short)] = "int16",
            [typeof(ushort)] = "uint16",
            [typeof(int)] = "int32",
            [typeof(uint)] = "uint32",
            [typeof(long)] = "int64",
            [typeof(ulong)] = "uint64",
            [typeof(float)] = "float32",
            [typeof(double)] = "float64",
            [typeof(string)] = "string",
        };

        public static string Build(Type rootType)
        {
            var visited = new HashSet<Type>();
            var dependencies = new List<Type>();
            string root = BuildDefinition(rootType, visited, dependencies);
            var schema = new StringBuilder(root);

            for (int i = 0; i < dependencies.Count; i++)
            {
                Type dependency = dependencies[i];
                schema.AppendLine();
                schema.AppendLine("================================================================================");
                schema.Append("MSG: ");
                schema.AppendLine(RosTypeName.FromMessageType(dependency));
                schema.Append(BuildDefinition(dependency, visited, dependencies));
            }

            return schema.ToString();
        }

        private static string BuildDefinition(Type type, HashSet<Type> visited, List<Type> dependencies)
        {
            visited.Add(type);
            var schema = new StringBuilder();
            foreach (MemberInfo member in GetSerializableMembers(type))
            {
                Type memberType = GetMemberType(member);
                string suffix = "";
                Type elementType = memberType;
                if (memberType.IsArray)
                {
                    suffix = "[]";
                    elementType = memberType.GetElementType() ?? typeof(object);
                }

                string rosType = ToRosType(elementType);
                if (typeof(Message).IsAssignableFrom(elementType) && elementType != typeof(Message) && visited.Add(elementType))
                    dependencies.Add(elementType);

                schema.Append(rosType);
                schema.Append(suffix);
                schema.Append(' ');
                schema.AppendLine(member.Name);
            }

            return schema.ToString();
        }

        private static IEnumerable<MemberInfo> GetSerializableMembers(Type type)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length == 0 && property.CanRead)
                    yield return property;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                yield return field;
        }

        private static Type GetMemberType(MemberInfo member)
        {
            return member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => throw new InvalidOperationException($"Unsupported member type {member.MemberType}.")
            };
        }

        private static string ToRosType(Type type)
        {
            Type nonNullable = Nullable.GetUnderlyingType(type) ?? type;
            if (Builtins.TryGetValue(nonNullable, out string? builtin))
                return builtin;

            if (typeof(Message).IsAssignableFrom(nonNullable))
                return RosTypeName.FromMessageType(nonNullable);

            throw new InvalidOperationException($"Cannot infer ROS schema field type for {type.FullName}.");
        }
    }
}
