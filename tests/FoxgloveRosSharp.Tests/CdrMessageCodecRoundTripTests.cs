using RosSharp.RosBridgeClient;
using RosSharp.RosBridgeClient.Internal;
using Xunit;

namespace FoxgloveRosSharp.Tests
{
    public sealed class CdrMessageCodecRoundTripTests
    {
        [Fact]
        public void RoundTripsGeneratedStringMessageShape()
        {
            var codec = new CdrMessageCodec("std_msgs/msg/String", "string data");

            byte[] bytes = codec.Serialize(new CdrMessageCodecTests.StringMessage { data = "hello" });
            CdrMessageCodecTests.StringMessage decoded = codec.Deserialize<CdrMessageCodecTests.StringMessage>(bytes);

            Assert.Equal("hello", decoded.data);
        }

        [Fact]
        public void RoundTripsNestedMessageAndVariableArray()
        {
            const string schema = """
std_msgs/msg/Header header
string[] name
float64[] position
================================================================================
MSG: std_msgs/msg/Header
builtin_interfaces/msg/Time stamp
string frame_id
================================================================================
MSG: builtin_interfaces/msg/Time
int32 sec
uint32 nanosec
""";
            var codec = new CdrMessageCodec("sensor_msgs/msg/JointState", schema);

            var source = new JointState
            {
                header = new Header
                {
                    stamp = new Time { sec = 12, nanosec = 34 },
                    frame_id = "base"
                },
                name = new[] { "joint_1", "joint_2" },
                position = new[] { 1.5, 2.5 }
            };

            JointState decoded = codec.Deserialize<JointState>(codec.Serialize(source));

            Assert.Equal("base", decoded.header.frame_id);
            Assert.Equal(12, decoded.header.stamp.sec);
            Assert.Equal(new[] { "joint_1", "joint_2" }, decoded.name);
            Assert.Equal(new[] { 1.5, 2.5 }, decoded.position);
        }

        public sealed class JointState : Message
        {
            public const string RosMessageName = "sensor_msgs/msg/JointState";
            public Header header { get; set; } = new();
            public string[] name { get; set; } = System.Array.Empty<string>();
            public double[] position { get; set; } = System.Array.Empty<double>();
        }

        public sealed class Header : Message
        {
            public const string RosMessageName = "std_msgs/msg/Header";
            public Time stamp { get; set; } = new();
            public string frame_id { get; set; } = "";
        }

        public sealed class Time : Message
        {
            public const string RosMessageName = "builtin_interfaces/msg/Time";
            public int sec { get; set; }
            public uint nanosec { get; set; }
        }
    }
}
