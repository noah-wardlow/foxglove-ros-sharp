using RosSharp.RosBridgeClient;
using RosSharp.RosBridgeClient.Internal;
using Xunit;

namespace FoxgloveRosSharp.Tests
{
    public sealed class RosTypeNameTests
    {
        [Fact]
        public void NormalizesRos1MessageNames()
        {
            Assert.Equal("std_msgs/msg/String", RosTypeName.FromMessageType<Ros1String>());
        }

        [Fact]
        public void KeepsRos2ServiceNames()
        {
            Assert.Equal("std_srvs/srv/Trigger", RosTypeName.FromMessageType<TriggerRequest>());
        }

        private sealed class Ros1String : Message
        {
            public const string RosMessageName = "std_msgs/String";
        }

        private sealed class TriggerRequest : Message
        {
            public const string RosMessageName = "std_srvs/srv/Trigger";
        }
    }
}
