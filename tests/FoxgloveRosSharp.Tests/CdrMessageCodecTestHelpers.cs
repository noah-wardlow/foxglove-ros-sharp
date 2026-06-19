using RosSharp.RosBridgeClient;

namespace FoxgloveRosSharp.Tests
{
    public static class CdrMessageCodecTests
    {
        public sealed class StringMessage : Message
        {
            public const string RosMessageName = "std_msgs/msg/String";

            public string data { get; set; } = "";
        }
    }
}
