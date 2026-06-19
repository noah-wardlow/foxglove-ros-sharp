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
using Xunit;

namespace FoxgloveRosSharp.Tests
{
    public sealed class FoxgloveProtocolIntegrationTests
    {
        [Fact]
        public async Task ReceivesAdvertisedCdrTopicOverWebSocket()
        {
            int port = GetFreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            Task server = RunSingleTopicServer(listener);

            using var ros = new RosSocket($"ws://127.0.0.1:{port}/");
            await ros.Connected.WaitAsync(TimeSpan.FromSeconds(5));

            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ros.Subscribe<CdrMessageCodecTests.StringMessage>(
                "/chatter",
                message => received.TrySetResult(message.data));

            Assert.Equal("from server", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task PublishesClientChannelWithGeneratedCdrSchema()
        {
            int port = GetFreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            Task server = RunPublishCaptureServer(listener);

            using var ros = new RosSocket($"ws://127.0.0.1:{port}/");
            await ros.Connected.WaitAsync(TimeSpan.FromSeconds(5));

            string publisherId = ros.Advertise<CdrMessageCodecTests.StringMessage>("/client_chatter");
            ros.Publish(publisherId, new CdrMessageCodecTests.StringMessage { data = "from client" });

            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static async Task RunSingleTopicServer(HttpListener listener)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync("foxglove.sdk.v1");
            WebSocket socket = wsContext.WebSocket;

            await SendText(socket, """
{"op":"advertise","channels":[{"id":1,"topic":"/chatter","encoding":"cdr","schemaName":"std_msgs/msg/String","schema":"string data"}]}
""");

            string subscribe = await ReceiveText(socket);
            using JsonDocument doc = JsonDocument.Parse(subscribe);
            int subscriptionId = doc.RootElement
                .GetProperty("subscriptions")[0]
                .GetProperty("id")
                .GetInt32();

            var codec = new CdrMessageCodec("std_msgs/msg/String", "string data");
            byte[] cdr = codec.Serialize(new CdrMessageCodecTests.StringMessage { data = "from server" });
            byte[] frame = new byte[1 + 4 + 8 + cdr.Length];
            frame[0] = 0x01;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, 4), subscriptionId);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(5, 8), 0);
            cdr.CopyTo(frame.AsSpan(13));

            await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        private static async Task RunPublishCaptureServer(HttpListener listener)
        {
            HttpListenerContext context = await listener.GetContextAsync();
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync("foxglove.sdk.v1");
            WebSocket socket = wsContext.WebSocket;

            string advertise = await ReceiveText(socket);
            using (JsonDocument doc = JsonDocument.Parse(advertise))
            {
                JsonElement channel = doc.RootElement.GetProperty("channels")[0];
                Assert.Equal("advertise", doc.RootElement.GetProperty("op").GetString());
                Assert.Equal("/client_chatter", channel.GetProperty("topic").GetString());
                Assert.Equal("cdr", channel.GetProperty("encoding").GetString());
                Assert.Equal("std_msgs/msg/String", channel.GetProperty("schemaName").GetString());
                Assert.Equal("ros2msg", channel.GetProperty("schemaEncoding").GetString());
                Assert.Equal("string data\n", channel.GetProperty("schema").GetString());
            }

            WebSocketMessage message = await Receive(socket);
            Assert.Equal(WebSocketMessageType.Binary, message.Type);
            Assert.Equal(0x01, message.Payload[0]);
            int channelId = BinaryPrimitives.ReadInt32LittleEndian(message.Payload.AsSpan(1, 4));
            Assert.Equal(1, channelId);
            byte[] data = message.Payload[5..];

            var codec = new CdrMessageCodec("std_msgs/msg/String", "string data");
            CdrMessageCodecTests.StringMessage decoded = codec.Deserialize<CdrMessageCodecTests.StringMessage>(data);
            Assert.Equal("from client", decoded.data);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        private static async Task SendText(WebSocket socket, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task<string> ReceiveText(WebSocket socket)
        {
            WebSocketMessage message = await Receive(socket);
            Assert.Equal(WebSocketMessageType.Text, message.Type);
            return Encoding.UTF8.GetString(message.Payload);
        }

        private static async Task<WebSocketMessage> Receive(WebSocket socket)
        {
            byte[] buffer = new byte[4096];
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

        private readonly struct WebSocketMessage
        {
            public WebSocketMessage(WebSocketMessageType type, byte[] payload)
            {
                Type = type;
                Payload = payload;
            }

            public WebSocketMessageType Type { get; }
            public byte[] Payload { get; }
        }
    }
}
