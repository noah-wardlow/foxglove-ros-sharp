using System;
using System.Reflection;

namespace RosSharp.RosBridgeClient.Internal
{
    internal static class RosTypeName
    {
        public static string FromMessageType<T>() where T : Message
        {
            return FromMessageType(typeof(T));
        }

        public static string FromMessageType(Type type)
        {
            FieldInfo? field = type.GetField(
                "RosMessageName",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if (field?.GetRawConstantValue() is string value && value.Length > 0)
                return Normalize(value, GuessKind(value, type.Name));

            throw new InvalidOperationException(
                $"{type.FullName} does not expose a public const string RosMessageName.");
        }

        public static string NormalizeMessage(string type) => Normalize(type, "msg");

        public static string NormalizeService(string type) => Normalize(type, "srv");

        private static string Normalize(string type, string kind)
        {
            string trimmed = type.Trim().TrimStart('/');
            if (trimmed.Contains("/msg/", StringComparison.Ordinal) ||
                trimmed.Contains("/srv/", StringComparison.Ordinal) ||
                trimmed.Contains("/action/", StringComparison.Ordinal))
                return trimmed;

            int slash = trimmed.IndexOf('/');
            return slash < 0 ? trimmed : $"{trimmed[..slash]}/{kind}/{trimmed[(slash + 1)..]}";
        }

        private static string GuessKind(string rosName, string className)
        {
            if (rosName.Contains("_Request", StringComparison.Ordinal) ||
                rosName.Contains("_Response", StringComparison.Ordinal) ||
                className.EndsWith("Request", StringComparison.Ordinal) ||
                className.EndsWith("Response", StringComparison.Ordinal))
                return "srv";

            return "msg";
        }
    }
}
