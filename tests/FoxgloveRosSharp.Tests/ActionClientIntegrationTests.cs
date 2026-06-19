using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RosSharp.RosBridgeClient;
using RosSharp.RosBridgeClient.Internal;
using RosSharp.RosBridgeClient.MessageTypes.Action;
using RosSharp.RosBridgeClient.MessageTypes.UniqueIdentifier;
using Xunit;

namespace FoxgloveRosSharp.Tests
{
    public sealed class ActionClientIntegrationTests
    {
        [Fact]
        public async Task SendActionGoalReceivesFeedbackAndResult()
        {
            int port = GetFreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            Task server = RunActionServer(listener);

            using var ros = new RosSocket($"ws://127.0.0.1:{port}/");
            await ros.Connected.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await ros.WaitForServiceAsync("/example_action/_action/send_goal", TimeSpan.FromSeconds(5)));

            var feedback = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var goal = new ExampleTaskActionGoal
            {
                action = "/example_action",
                feedback = true,
                args = new ExampleTaskGoal { task_name = "sample", parameters = "speed=1.0" }
            };

            string goalId = ros.SendActionGoalRequest<ExampleTaskActionGoal, ExampleTaskGoal, ExampleTaskActionFeedback, ExampleTaskActionResult>(
                goal,
                r => result.TrySetResult($"{r.status}:{r.values?.outcome}"),
                f => feedback.TrySetResult(f.values?.progress ?? ""));

            Assert.False(string.IsNullOrWhiteSpace(goalId));
            Assert.Equal("running", await feedback.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal($"{GoalStatus.STATUS_SUCCEEDED}:done", await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task SendActionGoalThrowsHelpfulErrorWhenActionEndpointsAreHidden()
        {
            int port = GetFreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            Task server = RunBridgeWithoutActionEndpoints(listener);

            using var ros = new RosSocket($"ws://127.0.0.1:{port}/");
            await ros.Connected.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await ros.WaitForActionAsync("/example_action", TimeSpan.FromMilliseconds(100), requireFeedback: true));

            var goal = new ExampleTaskActionGoal
            {
                action = "/example_action",
                feedback = true,
                args = new ExampleTaskGoal { task_name = "sample", parameters = "speed=1.0" }
            };

            ActionNotAdvertisedException exception = Assert.Throws<ActionNotAdvertisedException>(() =>
                ros.SendActionGoalRequest<ExampleTaskActionGoal, ExampleTaskGoal, ExampleTaskActionFeedback, ExampleTaskActionResult>(
                    goal,
                    _ => { },
                    _ => { }));

            Assert.Equal("/example_action", exception.ActionName);
            Assert.Contains("/example_action/_action/send_goal", exception.MissingEndpoints);
            Assert.Contains("include_hidden:=true", exception.Message);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static async Task RunActionServer(HttpListener listener)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync("foxglove.sdk.v1");
            WebSocket socket = wsContext.WebSocket;

            await SendText(socket, JsonSerializer.Serialize(new
            {
                op = "advertiseServices",
                services = new object[]
                {
                    new
                    {
                        id = 10,
                        name = "/example_action/_action/send_goal",
                        type = "example_msgs/action/ExampleTask_SendGoal",
                        request = new { schema = SendGoalSchema },
                        response = new { schema = SendGoalResponseSchema }
                    },
                    new
                    {
                        id = 11,
                        name = "/example_action/_action/get_result",
                        type = "example_msgs/action/ExampleTask_GetResult",
                        request = new { schema = GetResultRequestSchema },
                        response = new { schema = GetResultResponseSchema }
                    }
                }
            }));
            await SendText(socket, JsonSerializer.Serialize(new
            {
                op = "advertise",
                channels = new[]
                {
                    new
                    {
                        id = 20,
                        topic = "/example_action/_action/feedback",
                        encoding = "cdr",
                        schemaName = "example_msgs/action/ExampleTask_FeedbackMessage",
                        schema = FeedbackSchema
                    }
                }
            }));

            int feedbackSubscriptionId = -1;
            UUID? goalId = null;
            bool sentFeedback = false;

            while (true)
            {
                WebSocketMessage message = await Receive(socket);
                if (message.Type == WebSocketMessageType.Text)
                {
                    using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(message.Payload));
                    string op = doc.RootElement.GetProperty("op").GetString() ?? "";
                    if (op == "subscribe")
                    {
                        feedbackSubscriptionId = doc.RootElement.GetProperty("subscriptions")[0].GetProperty("id").GetInt32();
                    }
                }
                else
                {
                    byte opcode = message.Payload[0];
                    if (opcode != 0x02)
                        continue;

                    int serviceId = BinaryPrimitives.ReadInt32LittleEndian(message.Payload.AsSpan(1, 4));
                    int callId = BinaryPrimitives.ReadInt32LittleEndian(message.Payload.AsSpan(5, 4));
                    int encodingLength = BinaryPrimitives.ReadInt32LittleEndian(message.Payload.AsSpan(9, 4));
                    byte[] requestData = message.Payload[(13 + encodingLength)..];

                    if (serviceId == 10)
                    {
                        var request = new CdrMessageCodec("example_msgs/action/ExampleTask_SendGoal_Request", SendGoalSchema)
                            .Deserialize<SendGoalRequest>(requestData);
                        goalId = request.goal_id;

                        var response = new CdrMessageCodec("example_msgs/action/ExampleTask_SendGoal_Response", SendGoalResponseSchema)
                            .Serialize(new SendGoalResponse { accepted = true });
                        await SendServiceResponse(socket, serviceId, callId, response);

                        if (feedbackSubscriptionId > 0 && !sentFeedback)
                        {
                            sentFeedback = true;
                            var feedback = new FeedbackMessage
                            {
                                goal_id = goalId,
                                feedback = new ExampleTaskFeedback { progress = "running" }
                            };
                            byte[] cdr = new CdrMessageCodec("example_msgs/action/ExampleTask_FeedbackMessage", FeedbackSchema).Serialize(feedback);
                            await SendTopicMessage(socket, feedbackSubscriptionId, cdr);
                        }
                    }
                    else if (serviceId == 11)
                    {
                        var response = new CdrMessageCodec("example_msgs/action/ExampleTask_GetResult_Response", GetResultResponseSchema)
                            .Serialize(new GetResultResponse
                            {
                                status = GoalStatus.STATUS_SUCCEEDED,
                                result = new ExampleTaskResult { outcome = "done" }
                            });
                        await SendServiceResponse(socket, serviceId, callId, response);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                        return;
                    }
                }
            }
        }

        private static async Task RunBridgeWithoutActionEndpoints(HttpListener listener)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync("foxglove.sdk.v1");
            WebSocket socket = wsContext.WebSocket;

            await SendText(socket, JsonSerializer.Serialize(new
            {
                op = "advertiseServices",
                services = Array.Empty<object>()
            }));
            await SendText(socket, JsonSerializer.Serialize(new
            {
                op = "advertise",
                channels = Array.Empty<object>()
            }));

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        private const string SendGoalSchema = """
unique_identifier_msgs/msg/UUID goal_id
ExampleTask_Goal goal
================================================================================
MSG: unique_identifier_msgs/msg/UUID
uint8[16] uuid
================================================================================
MSG: example_msgs/action/ExampleTask_Goal
string task_name
string parameters
""";

        private const string SendGoalResponseSchema = """
bool accepted
builtin_interfaces/msg/Time stamp
================================================================================
MSG: builtin_interfaces/msg/Time
int32 sec
uint32 nanosec
""";

        private const string GetResultRequestSchema = """
unique_identifier_msgs/msg/UUID goal_id
================================================================================
MSG: unique_identifier_msgs/msg/UUID
uint8[16] uuid
""";

        private const string GetResultResponseSchema = """
int8 status
ExampleTask_Result result
================================================================================
MSG: example_msgs/action/ExampleTask_Result
string outcome
""";

        private const string FeedbackSchema = """
unique_identifier_msgs/msg/UUID goal_id
ExampleTask_Feedback feedback
================================================================================
MSG: unique_identifier_msgs/msg/UUID
uint8[16] uuid
================================================================================
MSG: example_msgs/action/ExampleTask_Feedback
string progress
""";

        private sealed class SendGoalRequest : Message
        {
            public UUID goal_id { get; set; } = new();
            public ExampleTaskGoal goal { get; set; } = new();
        }

        private sealed class SendGoalResponse : Message
        {
            public bool accepted { get; set; }
            public RosSharp.RosBridgeClient.MessageTypes.BuiltinInterfaces.Time stamp { get; set; } = new();
        }

        private sealed class GetResultResponse : Message
        {
            public sbyte status { get; set; }
            public ExampleTaskResult result { get; set; } = new();
        }

        private sealed class FeedbackMessage : Message
        {
            public UUID goal_id { get; set; } = new();
            public ExampleTaskFeedback feedback { get; set; } = new();
        }

        public sealed class ExampleTaskActionGoal : ActionGoal<ExampleTaskGoal>
        {
            public const string RosMessageName = "example_msgs/action/ExampleTaskActionGoal";

            public ExampleTaskActionGoal()
            {
                args = new ExampleTaskGoal();
            }
        }

        public sealed class ExampleTaskActionResult : ActionResult<ExampleTaskResult>
        {
            public const string RosMessageName = "example_msgs/action/ExampleTaskActionResult";

            public ExampleTaskActionResult()
            {
                values = new ExampleTaskResult();
            }
        }

        public sealed class ExampleTaskActionFeedback : ActionFeedback<ExampleTaskFeedback>
        {
            public const string RosMessageName = "example_msgs/action/ExampleTaskActionFeedback";

            public ExampleTaskActionFeedback()
            {
                values = new ExampleTaskFeedback();
            }
        }

        public sealed class ExampleTaskGoal : Message
        {
            public const string RosMessageName = "example_msgs/action/ExampleTask_Goal";
            public string task_name { get; set; } = "";
            public string parameters { get; set; } = "";
        }

        public sealed class ExampleTaskResult : Message
        {
            public const string RosMessageName = "example_msgs/action/ExampleTask_Result";
            public string outcome { get; set; } = "";
        }

        public sealed class ExampleTaskFeedback : Message
        {
            public const string RosMessageName = "example_msgs/action/ExampleTask_Feedback";
            public string progress { get; set; } = "";
        }

        private static async Task SendServiceResponse(WebSocket socket, int serviceId, int callId, byte[] cdr)
        {
            byte[] encoding = Encoding.UTF8.GetBytes("cdr");
            byte[] frame = new byte[1 + 4 + 4 + 4 + encoding.Length + cdr.Length];
            int offset = 0;
            frame[offset++] = 0x03;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, 4), serviceId);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, 4), callId);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, 4), encoding.Length);
            offset += 4;
            encoding.CopyTo(frame.AsSpan(offset));
            offset += encoding.Length;
            cdr.CopyTo(frame.AsSpan(offset));
            await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private static async Task SendTopicMessage(WebSocket socket, int subscriptionId, byte[] cdr)
        {
            byte[] frame = new byte[1 + 4 + 8 + cdr.Length];
            frame[0] = 0x01;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, 4), subscriptionId);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(5, 8), 0);
            cdr.CopyTo(frame.AsSpan(13));
            await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private static async Task SendText(WebSocket socket, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task<WebSocketMessage> Receive(WebSocket socket)
        {
            byte[] buffer = new byte[8192];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return new WebSocketMessage(result.MessageType, stream.ToArray());
        }

        private static int GetFreePort()
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            return ((IPEndPoint)tcp.LocalEndpoint).Port;
        }

        private readonly record struct WebSocketMessage(WebSocketMessageType Type, byte[] Payload);
    }
}
