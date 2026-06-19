using System.Collections.Generic;

namespace RosSharp.RosBridgeClient.Internal
{
    internal sealed class FoxgloveChannel
    {
        public int Id { get; set; }
        public string Topic { get; set; } = "";
        public string Encoding { get; set; } = "";
        public string SchemaName { get; set; } = "";
        public string Schema { get; set; } = "";
        public string? SchemaEncoding { get; set; }
    }

    internal sealed class FoxgloveService
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? RequestSchema { get; set; }
        public string? ResponseSchema { get; set; }
        public FoxgloveServiceSchemaSide? Request { get; set; }
        public FoxgloveServiceSchemaSide? Response { get; set; }
    }

    internal sealed class FoxgloveServiceSchemaSide
    {
        public string? Encoding { get; set; }
        public string? SchemaName { get; set; }
        public string? SchemaEncoding { get; set; }
        public string Schema { get; set; } = "";
    }

    internal sealed class ParameterValue
    {
        public string Name { get; set; } = "";
        public object? Value { get; set; }
        public string? Type { get; set; }
    }

    internal sealed class ServiceCallResponse
    {
        public int ServiceId { get; set; }
        public int CallId { get; set; }
        public string Encoding { get; set; } = "";
        public byte[] Data { get; set; } = System.Array.Empty<byte>();
    }

    internal sealed class ServiceCallFailure
    {
        public int ServiceId { get; set; }
        public int CallId { get; set; }
        public string Message { get; set; } = "service call failed";
    }

    internal sealed class ServerInfo
    {
        public string Name { get; set; } = "";
        public IReadOnlyList<string> Capabilities { get; set; } = System.Array.Empty<string>();
        public IReadOnlyList<string> SupportedEncodings { get; set; } = System.Array.Empty<string>();
    }
}
