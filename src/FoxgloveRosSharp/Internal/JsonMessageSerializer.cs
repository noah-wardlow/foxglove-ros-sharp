using System.Text.Json;

namespace RosSharp.RosBridgeClient.Internal
{
    internal static class JsonMessageSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            IncludeFields = true,
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = false
        };

        public static byte[] SerializeToUtf8(object value)
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Options);
        }

        public static T Deserialize<T>(byte[] utf8)
        {
            return JsonSerializer.Deserialize<T>(utf8, Options)
                ?? throw new JsonException($"Unable to deserialize {typeof(T).FullName}.");
        }
    }
}
