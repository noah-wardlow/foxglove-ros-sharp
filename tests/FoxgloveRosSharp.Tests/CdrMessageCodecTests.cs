using RosSharp.RosBridgeClient;
using RosSharp.RosBridgeClient.Internal;
using RosSharp.RosBridgeClient.MessageTypes.UniqueIdentifier;
using Xunit;

namespace FoxgloveRosSharp.Tests
{
    public sealed class CdrFlattenedActionCodecTests
    {
        [Fact]
        public void SerializeObjectUsesNestedMessageFieldsForFlattenedActionGoalSchemas()
        {
            var request = new SendGoalRequest
            {
                goal_id = new UUID { uuid = new byte[16] },
                goal = new ExampleGoal
                {
                    task_name = "sample",
                    parameters = "speed=1.0"
                }
            };

            var codec = new CdrMessageCodec("example_msgs/action/ExampleTask_SendGoal_Request", FlattenedSendGoalSchema);
            byte[] data = codec.SerializeObject(request);

            FlatSendGoalRequest roundTrip = codec.Deserialize<FlatSendGoalRequest>(data);

            Assert.Equal("sample", roundTrip.task_name);
            Assert.Equal("speed=1.0", roundTrip.parameters);
        }

        [Fact]
        public void DeserializeObjectSetsNestedMessageFieldsForFlattenedActionResultSchemas()
        {
            var codec = new CdrMessageCodec("example_msgs/action/ExampleTask_GetResult_Response", FlattenedGetResultSchema);
            byte[] data = codec.Serialize(new FlatGetResultResponse
            {
                status = 4,
                outcome = "done",
                details = "ok"
            });

            GetResultResponse roundTrip = (GetResultResponse)codec.DeserializeObject(data, typeof(GetResultResponse));

            Assert.Equal(4, roundTrip.status);
            Assert.Equal("done", roundTrip.result.outcome);
            Assert.Equal("ok", roundTrip.result.details);
        }

        private const string FlattenedSendGoalSchema = """
unique_identifier_msgs/msg/UUID goal_id
string task_name
string parameters
================================================================================
MSG: unique_identifier_msgs/msg/UUID
uint8[16] uuid
""";

        private const string FlattenedGetResultSchema = """
int8 status
string outcome
string details
""";

        private sealed class SendGoalRequest : Message
        {
            public UUID goal_id { get; set; } = new();
            public ExampleGoal goal { get; set; } = new();
        }

        private sealed class FlatSendGoalRequest : Message
        {
            public UUID goal_id { get; set; } = new();
            public string task_name { get; set; } = "";
            public string parameters { get; set; } = "";
        }

        private sealed class ExampleGoal : Message
        {
            public string task_name { get; set; } = "";
            public string parameters { get; set; } = "";
        }

        private sealed class FlatGetResultResponse : Message
        {
            public sbyte status { get; set; }
            public string outcome { get; set; } = "";
            public string details { get; set; } = "";
        }

        private sealed class GetResultResponse : Message
        {
            public sbyte status { get; set; }
            public ExampleResult result { get; set; } = new();
        }

        private sealed class ExampleResult : Message
        {
            public string outcome { get; set; } = "";
            public string details { get; set; } = "";
        }
    }
}
