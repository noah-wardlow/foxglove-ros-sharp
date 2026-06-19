using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RosSharp.RosBridgeClient.Internal
{
    internal sealed class FoxgloveProtocolClient : IDisposable
    {
        private const byte OpMessageData = 0x01;
        private const byte OpServiceCallResponse = 0x03;
        private const byte OpClientMessageData = 0x01;
        private const byte OpClientServiceCallRequest = 0x02;

        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly object gate = new();
        private ClientWebSocket? socket;
        private CancellationTokenSource? receiveCts;
        private int nextSubscriptionId = 1;
        private int nextCallId = 1;
        private int nextClientChannelId = 1;

        public event Action<ServerInfo>? ServerInfo;
        public event Action<IReadOnlyList<FoxgloveChannel>>? Advertise;
        public event Action<IReadOnlyList<int>>? Unadvertise;
        public event Action<IReadOnlyList<FoxgloveService>>? AdvertiseServices;
        public event Action<IReadOnlyList<int>>? UnadvertiseServices;
        public event Action<int, ulong, byte[]>? Message;
        public event Action<ServiceCallResponse>? ServiceResponse;
        public event Action<ServiceCallFailure>? ServiceCallFailure;
        public event Action<string, IReadOnlyList<ParameterValue>>? ParameterValues;
        public event Action? Open;
        public event Action? Close;
        public event Action<Exception>? Error;

        public bool IsConnected => socket?.State == WebSocketState.Open;

        public async Task ConnectAsync(string url, CancellationToken cancellationToken = default)
        {
            CloseSocket();

            string wsUrl = url.StartsWith("http://", StringComparison.Ordinal)
                ? "ws://" + url[7..]
                : url.StartsWith("https://", StringComparison.Ordinal)
                    ? "wss://" + url[8..]
                    : url;

            var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("foxglove.sdk.v1");
            ws.Options.AddSubProtocol("foxglove.websocket.v1");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (gate)
            {
                socket = ws;
                receiveCts = cts;
            }

            await ws.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(false);
            Open?.Invoke();
            _ = Task.Run(() => ReceiveLoopAsync(ws, cts.Token));
        }

        public void CloseSocket()
        {
            ClientWebSocket? ws;
            CancellationTokenSource? cts;
            lock (gate)
            {
                ws = socket;
                cts = receiveCts;
                socket = null;
                receiveCts = null;
            }

            cts?.Cancel();
            cts?.Dispose();

            if (ws != null)
            {
                try
                {
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "close", CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                }
                catch
                {
                    ws.Abort();
                }
                finally
                {
                    ws.Dispose();
                }
            }

            Close?.Invoke();
        }

        public int Subscribe(int channelId)
        {
            int id = nextSubscriptionId++;
            _ = SendJsonAsync(new
            {
                op = "subscribe",
                subscriptions = new[] { new { id, channelId } }
            });
            return id;
        }

        public void Unsubscribe(int subscriptionId)
        {
            _ = SendJsonAsync(new
            {
                op = "unsubscribe",
                subscriptionIds = new[] { subscriptionId }
            });
        }

        public int AdvertiseClientChannel(
            string topic,
            string encoding,
            string schemaName,
            string? schema = null,
            string? schemaEncoding = null)
        {
            int id = nextClientChannelId++;
            _ = SendJsonAsync(new
            {
                op = "advertise",
                channels = new[] { new { id, topic, encoding, schemaName, schema, schemaEncoding } }
            });
            return id;
        }

        public void UnadvertiseClientChannel(int channelId)
        {
            _ = SendJsonAsync(new
            {
                op = "unadvertise",
                channelIds = new[] { channelId }
            });
        }

        public void PublishMessage(int channelId, byte[] data)
        {
            byte[] message = new byte[1 + 4 + data.Length];
            message[0] = OpClientMessageData;
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1, 4), channelId);
            data.CopyTo(message.AsSpan(5));
            _ = SendBinaryAsync(message);
        }

        public int CallService(int serviceId, string encoding, byte[] requestData)
        {
            int callId = nextCallId++;
            byte[] encodingBytes = Encoding.UTF8.GetBytes(encoding);
            byte[] message = new byte[1 + 4 + 4 + 4 + encodingBytes.Length + requestData.Length];
            int offset = 0;
            message[offset++] = OpClientServiceCallRequest;
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(offset, 4), serviceId);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(offset, 4), callId);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(offset, 4), encodingBytes.Length);
            offset += 4;
            encodingBytes.CopyTo(message.AsSpan(offset));
            offset += encodingBytes.Length;
            requestData.CopyTo(message.AsSpan(offset));
            _ = SendBinaryAsync(message);
            return callId;
        }

        public void GetParameters(IEnumerable<string> names, string? requestId = null)
        {
            _ = SendJsonAsync(new
            {
                op = "getParameters",
                parameterNames = names.ToArray(),
                id = requestId
            });
        }

        public void SetParameters(IEnumerable<ParameterValue> parameters, string? requestId = null)
        {
            _ = SendJsonAsync(new
            {
                op = "setParameters",
                parameters = parameters.Select(p => new { name = p.Name, value = p.Value, type = p.Type }).ToArray(),
                id = requestId
            });
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[64 * 1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            CloseSocket();
                            return;
                        }
                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    byte[] payload = stream.ToArray();
                    if (result.MessageType == WebSocketMessageType.Text)
                        HandleTextMessage(Encoding.UTF8.GetString(payload));
                    else if (result.MessageType == WebSocketMessageType.Binary)
                        HandleBinaryMessage(payload);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                CloseSocket();
            }
        }

        private void HandleTextMessage(string text)
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("op", out JsonElement opElement))
                return;

            string? op = opElement.GetString();
            switch (op)
            {
                case "serverInfo":
                    ServerInfo?.Invoke(new ServerInfo
                    {
                        Name = GetString(root, "name"),
                        Capabilities = GetStringArray(root, "capabilities"),
                        SupportedEncodings = GetStringArray(root, "supportedEncodings")
                    });
                    break;
                case "advertise":
                    Advertise?.Invoke(ReadChannels(root.GetProperty("channels")));
                    break;
                case "unadvertise":
                    Unadvertise?.Invoke(GetIntArray(root, "channelIds"));
                    break;
                case "advertiseServices":
                    AdvertiseServices?.Invoke(ReadServices(root.GetProperty("services")));
                    break;
                case "unadvertiseServices":
                    UnadvertiseServices?.Invoke(GetIntArray(root, "serviceIds"));
                    break;
                case "parameterValues":
                    ParameterValues?.Invoke(GetString(root, "id"), ReadParameters(root.GetProperty("parameters")));
                    break;
                case "serviceCallFailure":
                    ServiceCallFailure?.Invoke(new ServiceCallFailure
                    {
                        ServiceId = root.GetProperty("serviceId").GetInt32(),
                        CallId = root.GetProperty("callId").GetInt32(),
                        Message = root.TryGetProperty("message", out JsonElement msg) ? msg.GetString() ?? "service call failed" : "service call failed"
                    });
                    break;
            }
        }

        private void HandleBinaryMessage(byte[] payload)
        {
            if (payload.Length < 1)
                return;

            switch (payload[0])
            {
                case OpMessageData:
                    if (payload.Length < 13) return;
                    Message?.Invoke(
                        BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1, 4)),
                        BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(5, 8)),
                        payload[13..]);
                    break;
                case OpServiceCallResponse:
                    if (payload.Length < 13) return;
                    int serviceId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1, 4));
                    int callId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5, 4));
                    int encodingLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(9, 4));
                    if (payload.Length < 13 + encodingLength) return;
                    string encoding = Encoding.UTF8.GetString(payload, 13, encodingLength);
                    byte[] data = payload[(13 + encodingLength)..];
                    ServiceResponse?.Invoke(new ServiceCallResponse
                    {
                        ServiceId = serviceId,
                        CallId = callId,
                        Encoding = encoding,
                        Data = data
                    });
                    break;
            }
        }

        private async Task SendJsonAsync(object value)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            await SendAsync(data, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private Task SendBinaryAsync(byte[] data)
        {
            return SendAsync(data, WebSocketMessageType.Binary);
        }

        private async Task SendAsync(byte[] data, WebSocketMessageType messageType)
        {
            ClientWebSocket? ws = socket;
            if (ws?.State != WebSocketState.Open)
                return;

            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(new ArraySegment<byte>(data), messageType, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private static IReadOnlyList<FoxgloveChannel> ReadChannels(JsonElement channels)
        {
            var result = new List<FoxgloveChannel>();
            foreach (JsonElement ch in channels.EnumerateArray())
            {
                result.Add(new FoxgloveChannel
                {
                    Id = ch.GetProperty("id").GetInt32(),
                    Topic = GetString(ch, "topic"),
                    Encoding = GetString(ch, "encoding"),
                    SchemaName = GetString(ch, "schemaName"),
                    Schema = GetString(ch, "schema"),
                    SchemaEncoding = ch.TryGetProperty("schemaEncoding", out JsonElement enc) ? enc.GetString() : null
                });
            }
            return result;
        }

        private static IReadOnlyList<FoxgloveService> ReadServices(JsonElement services)
        {
            var result = new List<FoxgloveService>();
            foreach (JsonElement svc in services.EnumerateArray())
            {
                result.Add(new FoxgloveService
                {
                    Id = svc.GetProperty("id").GetInt32(),
                    Name = GetString(svc, "name"),
                    Type = GetString(svc, "type"),
                    RequestSchema = svc.TryGetProperty("requestSchema", out JsonElement req) ? req.GetString() : null,
                    ResponseSchema = svc.TryGetProperty("responseSchema", out JsonElement resp) ? resp.GetString() : null,
                    Request = svc.TryGetProperty("request", out JsonElement request) ? ReadSchemaSide(request) : null,
                    Response = svc.TryGetProperty("response", out JsonElement response) ? ReadSchemaSide(response) : null
                });
            }
            return result;
        }

        private static FoxgloveServiceSchemaSide ReadSchemaSide(JsonElement element)
        {
            return new FoxgloveServiceSchemaSide
            {
                Encoding = element.TryGetProperty("encoding", out JsonElement encoding) ? encoding.GetString() : null,
                SchemaName = element.TryGetProperty("schemaName", out JsonElement schemaName) ? schemaName.GetString() : null,
                SchemaEncoding = element.TryGetProperty("schemaEncoding", out JsonElement schemaEncoding) ? schemaEncoding.GetString() : null,
                Schema = GetString(element, "schema")
            };
        }

        private static IReadOnlyList<ParameterValue> ReadParameters(JsonElement parameters)
        {
            var result = new List<ParameterValue>();
            foreach (JsonElement parameter in parameters.EnumerateArray())
            {
                object? value = parameter.TryGetProperty("value", out JsonElement v)
                    ? JsonSerializer.Deserialize<object>(v.GetRawText())
                    : null;
                result.Add(new ParameterValue
                {
                    Name = GetString(parameter, "name"),
                    Value = value,
                    Type = parameter.TryGetProperty("type", out JsonElement type) ? type.GetString() : null
                });
            }
            return result;
        }

        private static string GetString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement value) ? value.GetString() ?? "" : "";
        }

        private static IReadOnlyList<string> GetStringArray(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement values)
                ? values.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
                : Array.Empty<string>();
        }

        private static IReadOnlyList<int> GetIntArray(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement values)
                ? values.EnumerateArray().Select(v => v.GetInt32()).ToArray()
                : Array.Empty<int>();
        }

        public void Dispose()
        {
            CloseSocket();
            sendLock.Dispose();
        }
    }
}
