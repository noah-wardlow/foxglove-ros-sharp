using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RosSharp.RosBridgeClient.Internal;
using RosSharp.RosBridgeClient.MessageTypes.Action;
using RosSharp.RosBridgeClient.MessageTypes.UniqueIdentifier;
using RosSharp.RosBridgeClient.Protocols;

namespace RosSharp.RosBridgeClient
{
    public sealed class RosSocket : IDisposable
    {
        private readonly FoxgloveProtocolClient protocolClient = new();
        private readonly Dictionary<int, FoxgloveChannel> channels = new();
        private readonly Dictionary<string, FoxgloveChannel> channelsByTopic = new(StringComparer.Ordinal);
        private readonly Dictionary<int, FoxgloveService> services = new();
        private readonly Dictionary<string, FoxgloveService> servicesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PublisherState> publishers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SubscriberState> subscribers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingSubscriber> pendingSubscribers = new(StringComparer.Ordinal);
        private readonly Dictionary<int, SubscriberState> subscriptionsByWireId = new();
        private readonly Dictionary<int, PendingServiceCall> pendingServiceCalls = new();
        private readonly Dictionary<string, PendingParameterRequest> pendingParameterRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ActionConsumerState> actionConsumers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CdrMessageCodec> codecCache = new(StringComparer.Ordinal);
        private readonly object gate = new();
        private int subscriptionCounter = 1;
        private int parameterCounter = 1;

        public enum SerializerEnum { Microsoft, Newtonsoft_JSON }

        public IProtocol protocol;

        public RosSocket(IProtocol transport, SerializerEnum serializer = SerializerEnum.Microsoft)
        {
            protocol = transport;
            Protocol = transport;
            SerializerType = serializer;
            HookProtocol();
            Connected = protocolClient.ConnectAsync(transport.Uri);
        }

        public RosSocket(string uri)
            : this(new WebSocketNetProtocol(uri))
        {
        }

        public IProtocol Protocol { get; }
        public SerializerEnum SerializerType { get; }
        public Task Connected { get; }
        public bool IsConnected => protocolClient.IsConnected;

        public event Action? ConnectedEvent;
        public event Action? Closed;
        public event Action<Exception>? Error;
        public event Action? ChannelsChanged;
        public event Action? AdvertisedServicesChanged;

        public string Advertise<T>(string topic) where T : Message
        {
            string id = topic;
            lock (gate)
            {
                if (publishers.ContainsKey(id))
                    Unadvertise(id);

                publishers[id] = new PublisherState(topic, RosTypeName.FromMessageType<T>());
            }
            return id;
        }

        public void Publish(string id, Message message)
        {
            PublisherState publisher;
            lock (gate)
                publisher = publishers[id];

            if (publisher.ClientChannelId == null)
                EnsureClientChannel(publisher);

            byte[] payload;
            if (publisher.Encoding == "cdr")
            {
                CdrMessageCodec codec = GetCodec(publisher.SchemaName, publisher.Schema ?? "");
                payload = codec.Serialize(message);
            }
            else
            {
                payload = JsonMessageSerializer.SerializeToUtf8(message);
            }

            protocolClient.PublishMessage(publisher.ClientChannelId!.Value, payload);
        }

        public void Unadvertise(string id)
        {
            PublisherState? publisher;
            lock (gate)
            {
                if (!publishers.TryGetValue(id, out publisher))
                    return;
                publishers.Remove(id);
            }

            if (publisher.ClientChannelId != null)
                protocolClient.UnadvertiseClientChannel(publisher.ClientChannelId.Value);
        }

        public string Subscribe<T>(
            string topic,
            SubscriptionHandler<T> subscriptionHandler,
            int throttle_rate = 0,
            int queue_length = 1,
            int fragment_size = int.MaxValue,
            string compression = "none",
            bool ensureThreadSafety = false) where T : Message
        {
            string id;
            lock (gate)
                id = $"{topic}:{subscriptionCounter++}";

            var state = new SubscriberState(
                id,
                topic,
                RosTypeName.FromMessageType<T>(),
                typeof(T),
                bytes => subscriptionHandler(Decode<T>(topic, bytes)),
                ensureThreadSafety);

            lock (gate)
            {
                subscribers[id] = state;
                if (channelsByTopic.TryGetValue(topic, out FoxgloveChannel? channel))
                    CreateSubscription(state, channel);
                else
                    pendingSubscribers[id] = new PendingSubscriber(state);
            }

            return id;
        }

        public void Unsubscribe(string id)
        {
            SubscriberState? state;
            lock (gate)
            {
                pendingSubscribers.Remove(id);
                if (!subscribers.TryGetValue(id, out state))
                    return;
                subscribers.Remove(id);
                if (state.SubscriptionId != null)
                    subscriptionsByWireId.Remove(state.SubscriptionId.Value);
            }

            if (state.SubscriptionId != null)
                protocolClient.Unsubscribe(state.SubscriptionId.Value);
        }

        public string CallService<TIn, TOut>(
            string service,
            ServiceResponseHandler<TOut> serviceResponseHandler,
            TIn serviceArguments)
            where TIn : Message
            where TOut : Message
        {
            FoxgloveService svc;
            lock (gate)
            {
                if (!servicesByName.TryGetValue(service, out svc!))
                    throw new InvalidOperationException($"Service {service} is not advertised by foxglove_bridge.");
            }

            string serviceType = RosTypeName.NormalizeService(svc.Type.Length > 0 ? svc.Type : RosTypeName.FromMessageType<TIn>());
            string requestSchema = svc.Request?.Schema ?? svc.RequestSchema
                ?? throw new InvalidOperationException($"Service {service} did not advertise a request schema.");
            string responseSchema = svc.Response?.Schema ?? svc.ResponseSchema ?? "";
            string requestType = serviceType + "_Request";
            string responseType = serviceType + "_Response";

            byte[] requestData = GetCodec(requestType, requestSchema).Serialize(serviceArguments);
            int callId = protocolClient.CallService(svc.Id, "cdr", requestData);
            lock (gate)
            {
                pendingServiceCalls[callId] = new PendingServiceCall(
                    service,
                    data => serviceResponseHandler(DecodeServiceResponse<TOut>(responseType, responseSchema, data)),
                    ex => Error?.Invoke(ex));
            }

            return $"{service}:{callId}";
        }

        public string AdvertiseService<TIn, TOut>(
            string service,
            ServiceCallHandler<TIn, TOut> serviceCallHandler)
            where TIn : Message
            where TOut : Message
        {
            throw new NotSupportedException(
                "foxglove_bridge does not support client-side service advertisement over the WebSocket protocol.");
        }

        public void UnadvertiseService(string id)
        {
            throw new NotSupportedException(
                "foxglove_bridge does not support client-side service advertisement over the WebSocket protocol.");
        }

        public string SendActionGoalRequest<TActionGoal, TGoal, TActionFeedback, TActionResult>(
            TActionGoal actionGoal,
            ActionResultResponseHandler<TActionResult> actionResultResponseHandler,
            ActionFeedbackResponseHandler<TActionFeedback> actionFeedbackResponseHandler)
            where TActionGoal : ActionGoal<TGoal>
            where TGoal : Message
            where TActionResult : Message
            where TActionFeedback : Message
        {
            string actionName = RequireActionName(actionGoal.action);
            string actionType = string.IsNullOrWhiteSpace(actionGoal.action_type)
                ? InferActionType(typeof(TActionGoal))
                : RosTypeName.NormalizeMessage(actionGoal.action_type);

            string id = string.IsNullOrWhiteSpace(actionGoal.id) ? NewGoalId() : actionGoal.id;
            actionGoal.id = id;
            actionGoal.action = actionName;
            actionGoal.action_type = actionType;
            SetGoalInfo(actionGoal.goalInfo, id);

            EnsureActionAdvertised(actionName, actionGoal.feedback);

            if (actionGoal.feedback && actionFeedbackResponseHandler != null)
            {
                string feedbackTopic = ActionEndpoint(actionName, "feedback");
                string feedbackSchemaName = actionType + "_FeedbackMessage";
                SubscribeActionFeedback<TActionFeedback>(
                    id,
                    actionName,
                    feedbackTopic,
                    feedbackSchemaName,
                    actionFeedbackResponseHandler);
            }

            var request = new SendGoalRequest<TGoal>
            {
                goal_id = ToUuid(id),
                goal = actionGoal.args ?? throw new InvalidOperationException("Action goal args must be set before sending.")
            };

            CallServiceObject<SendGoalResponse>(
                ActionEndpoint(actionName, "send_goal"),
                request,
                response =>
                {
                    if (!response.accepted)
                    {
                        actionResultResponseHandler(CreateActionResult<TActionResult>(
                            actionName,
                            id,
                            GoalStatus.STATUS_UNKNOWN,
                            false,
                            null));
                        return;
                    }

                    var getResultRequest = new GetResultRequest { goal_id = ToUuid(id) };
                    Type resultValueType = GetActionPayloadType(typeof(TActionResult), "values");

                    CallServiceObject(
                        ActionEndpoint(actionName, "get_result"),
                        getResultRequest,
                        typeof(GetResultResponse<>).MakeGenericType(resultValueType),
                        raw =>
                        {
                            sbyte status = Convert.ToSByte(ReflectionAccess.GetMemberValue(raw, "status") ?? 0);
                            object? values = ReflectionAccess.GetMemberValue(raw, "result");
                            actionResultResponseHandler(CreateActionResult<TActionResult>(
                                actionName,
                                id,
                                status,
                                true,
                                values));
                            UnsubscribeActionFeedback(id);
                        });
                });

            return id;
        }

        public string CancelActionGoalRequest<TActionResult>(
            string frameId,
            string action,
            ActionCancelResponseHandler<TActionResult> actionCancelResponseHandler)
            where TActionResult : Message
        {
            string actionName = RequireActionName(action);
            string id = string.IsNullOrWhiteSpace(frameId) ? NewGoalId(zero: true) : frameId;

            EnsureActionCancelAdvertised(actionName);

            var request = new CancelGoalRequest
            {
                goal_info = new GoalInfo { goal_id = ToUuid(id) }
            };

            CallServiceObject<CancelGoalResponse>(
                ActionEndpoint(actionName, "cancel_goal"),
                request,
                response =>
                {
                    bool accepted = response.return_code == CancelGoalResponse.ERROR_NONE;
                    actionCancelResponseHandler(CreateActionResult<TActionResult>(
                        actionName,
                        id,
                        GoalStatus.STATUS_CANCELING,
                        accepted,
                        null));
                    UnsubscribeActionFeedback(id);
                });

            return id;
        }

        public Task<object?> GetParamAsync(string name)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            string wireName = ToFoxgloveParamName(name);
            string requestId;
            lock (gate)
            {
                requestId = $"param_get_{parameterCounter++}";
                pendingParameterRequests[requestId] = new PendingParameterRequest(
                    values => tcs.TrySetResult(values.FirstOrDefault(p => ToFoxgloveParamName(p.Name) == wireName)?.Value),
                    ex => tcs.TrySetException(ex));
            }

            protocolClient.GetParameters(new[] { wireName }, requestId);
            return tcs.Task;
        }

        public Task SetParamAsync(string name, object? value)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string wireName = ToFoxgloveParamName(name);
            string requestId;
            lock (gate)
            {
                requestId = $"param_set_{parameterCounter++}";
                pendingParameterRequests[requestId] = new PendingParameterRequest(
                    _ => tcs.TrySetResult(true),
                    ex => tcs.TrySetException(ex));
            }

            protocolClient.SetParameters(new[] { new ParameterValue { Name = wireName, Value = value } }, requestId);
            return tcs.Task;
        }

        public void GetTopicsForType(string messageType, Action<string[]> onSuccess)
        {
            string canonical = RosTypeName.NormalizeMessage(messageType);
            string[] topics;
            lock (gate)
            {
                topics = channels.Values
                    .Where(ch => RosTypeName.NormalizeMessage(ch.SchemaName) == canonical)
                    .Select(ch => ch.Topic)
                    .ToArray();
            }
            onSuccess(topics);
        }

        public bool IsTopicAdvertised(string topic)
        {
            lock (gate)
                return channelsByTopic.ContainsKey(topic);
        }

        public bool IsServiceAdvertised(string serviceName)
        {
            lock (gate)
                return servicesByName.ContainsKey(serviceName);
        }

        public bool IsActionAdvertised(string actionName, bool requireFeedback = false, bool requireCancel = false)
        {
            return GetMissingActionEndpoints(RequireActionName(actionName), requireFeedback, requireCancel).Length == 0;
        }

        public string[] GetMissingActionEndpoints(string actionName, bool requireFeedback = false, bool requireCancel = false)
        {
            actionName = RequireActionName(actionName);
            var missing = new List<string>();
            lock (gate)
            {
                foreach (string leaf in new[] { "send_goal", "get_result" })
                {
                    string service = ActionEndpoint(actionName, leaf);
                    if (!servicesByName.ContainsKey(service))
                        missing.Add(service);
                }

                if (requireCancel)
                {
                    string cancel = ActionEndpoint(actionName, "cancel_goal");
                    if (!servicesByName.ContainsKey(cancel))
                        missing.Add(cancel);
                }

                if (requireFeedback)
                {
                    string feedback = ActionEndpoint(actionName, "feedback");
                    if (!channelsByTopic.ContainsKey(feedback))
                        missing.Add(feedback);
                }
            }

            return missing.ToArray();
        }

        public async Task<bool> WaitForServiceAsync(
            string serviceName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (servicesByName.ContainsKey(serviceName))
                    return true;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            void Check()
            {
                lock (gate)
                {
                    if (servicesByName.ContainsKey(serviceName))
                        tcs.TrySetResult(true);
                }
            }

            AdvertisedServicesChanged += Check;
            using CancellationTokenRegistration registration = linkedCts.Token.Register(() => tcs.TrySetResult(false));
            try
            {
                Check();
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                AdvertisedServicesChanged -= Check;
            }
        }

        public async Task<bool> WaitForActionAsync(
            string actionName,
            TimeSpan timeout,
            bool requireFeedback = false,
            bool requireCancel = false,
            CancellationToken cancellationToken = default)
        {
            if (IsActionAdvertised(actionName, requireFeedback, requireCancel))
                return true;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            void Check()
            {
                if (IsActionAdvertised(actionName, requireFeedback, requireCancel))
                    tcs.TrySetResult(true);
            }

            AdvertisedServicesChanged += Check;
            ChannelsChanged += Check;
            using CancellationTokenRegistration registration = linkedCts.Token.Register(() => tcs.TrySetResult(false));
            try
            {
                Check();
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                AdvertisedServicesChanged -= Check;
                ChannelsChanged -= Check;
            }
        }

        public void Close(int millisecondsWait = 0)
        {
            if (millisecondsWait > 0)
                Thread.Sleep(millisecondsWait);
            protocolClient.CloseSocket();
            Protocol.Dispose();
        }

        public void Dispose()
        {
            Close();
            protocolClient.Dispose();
        }

        private void HookProtocol()
        {
            protocolClient.Open += () => ConnectedEvent?.Invoke();
            protocolClient.Close += HandleClose;
            protocolClient.Error += ex => Error?.Invoke(ex);
            protocolClient.Advertise += HandleAdvertise;
            protocolClient.Unadvertise += HandleUnadvertise;
            protocolClient.AdvertiseServices += HandleAdvertiseServices;
            protocolClient.UnadvertiseServices += HandleUnadvertiseServices;
            protocolClient.Message += HandleMessage;
            protocolClient.ServiceResponse += HandleServiceResponse;
            protocolClient.ServiceCallFailure += HandleServiceCallFailure;
            protocolClient.ParameterValues += HandleParameterValues;
        }

        private void HandleClose()
        {
            List<PendingServiceCall> serviceCalls;
            List<PendingParameterRequest> parameterRequests;
            lock (gate)
            {
                channels.Clear();
                channelsByTopic.Clear();
                services.Clear();
                servicesByName.Clear();
                subscriptionsByWireId.Clear();
                pendingSubscribers.Clear();
                foreach (SubscriberState subscriber in subscribers.Values)
                    subscriber.SubscriptionId = null;
                foreach (PublisherState publisher in publishers.Values)
                    publisher.ClearClientChannel();
                codecCache.Clear();
                serviceCalls = pendingServiceCalls.Values.ToList();
                parameterRequests = pendingParameterRequests.Values.ToList();
                pendingServiceCalls.Clear();
                pendingParameterRequests.Clear();
            }

            var error = new InvalidOperationException("WebSocket closed before response was received.");
            foreach (PendingServiceCall pending in serviceCalls)
                pending.Fail(error);
            foreach (PendingParameterRequest pending in parameterRequests)
                pending.Fail(error);

            ChannelsChanged?.Invoke();
            Closed?.Invoke();
        }

        private void HandleAdvertise(IReadOnlyList<FoxgloveChannel> advertised)
        {
            lock (gate)
            {
                foreach (FoxgloveChannel channel in advertised)
                {
                    channels[channel.Id] = channel;
                    channelsByTopic[channel.Topic] = channel;
                }

                foreach (PendingSubscriber pending in pendingSubscribers.Values.ToArray())
                {
                    if (channelsByTopic.TryGetValue(pending.State.Topic, out FoxgloveChannel? channel))
                    {
                        CreateSubscription(pending.State, channel);
                        pendingSubscribers.Remove(pending.State.Id);
                    }
                }
            }
            ChannelsChanged?.Invoke();
        }

        private void HandleUnadvertise(IReadOnlyList<int> channelIds)
        {
            lock (gate)
            {
                foreach (int channelId in channelIds)
                {
                    if (channels.TryGetValue(channelId, out FoxgloveChannel? channel))
                        channelsByTopic.Remove(channel.Topic);
                    channels.Remove(channelId);
                }
            }
            ChannelsChanged?.Invoke();
        }

        private void HandleAdvertiseServices(IReadOnlyList<FoxgloveService> advertised)
        {
            lock (gate)
            {
                foreach (FoxgloveService service in advertised)
                {
                    services[service.Id] = service;
                    servicesByName[service.Name] = service;
                }
            }
            AdvertisedServicesChanged?.Invoke();
        }

        private void HandleUnadvertiseServices(IReadOnlyList<int> serviceIds)
        {
            lock (gate)
            {
                foreach (int serviceId in serviceIds)
                {
                    if (services.TryGetValue(serviceId, out FoxgloveService? service))
                        servicesByName.Remove(service.Name);
                    services.Remove(serviceId);
                }
            }
            AdvertisedServicesChanged?.Invoke();
        }

        private void HandleMessage(int subscriptionId, ulong timestamp, byte[] data)
        {
            SubscriberState? subscriber;
            lock (gate)
                subscriptionsByWireId.TryGetValue(subscriptionId, out subscriber);
            subscriber?.Receive(data);
        }

        private void HandleServiceResponse(ServiceCallResponse response)
        {
            PendingServiceCall? pending;
            lock (gate)
            {
                if (!pendingServiceCalls.TryGetValue(response.CallId, out pending))
                    return;
                pendingServiceCalls.Remove(response.CallId);
            }
            pending.Complete(response.Data);
        }

        private void HandleServiceCallFailure(ServiceCallFailure failure)
        {
            PendingServiceCall? pending;
            lock (gate)
            {
                if (!pendingServiceCalls.TryGetValue(failure.CallId, out pending))
                    return;
                pendingServiceCalls.Remove(failure.CallId);
            }
            pending.Fail(new InvalidOperationException(failure.Message));
        }

        private void HandleParameterValues(string requestId, IReadOnlyList<ParameterValue> values)
        {
            PendingParameterRequest? pending;
            lock (gate)
            {
                if (!pendingParameterRequests.TryGetValue(requestId, out pending))
                    return;
                pendingParameterRequests.Remove(requestId);
            }
            pending.Complete(values);
        }

        private void CreateSubscription(SubscriberState state, FoxgloveChannel channel)
        {
            int subscriptionId = protocolClient.Subscribe(channel.Id);
            state.SubscriptionId = subscriptionId;
            state.SchemaName = channel.SchemaName;
            state.Schema = channel.Schema;
            subscriptionsByWireId[subscriptionId] = state;
        }

        private void SubscribeActionFeedback<TActionFeedback>(
            string goalId,
            string actionName,
            string topic,
            string schemaName,
            ActionFeedbackResponseHandler<TActionFeedback> callback)
            where TActionFeedback : Message
        {
            Type feedbackValueType = GetActionPayloadType(typeof(TActionFeedback), "values");
            Type envelopeType = typeof(FeedbackMessage<>).MakeGenericType(feedbackValueType);

            var state = new SubscriberState(
                $"action-feedback:{goalId}",
                topic,
                schemaName,
                envelopeType,
                bytes =>
                {
                    FoxgloveChannel channel;
                    lock (gate)
                        channel = channelsByTopic[topic];

                    object envelope = GetCodec(channel.SchemaName, channel.Schema).DeserializeObject(bytes, envelopeType);
                    var goalUuid = (UUID?)ReflectionAccess.GetMemberValue(envelope, "goal_id");
                    if (goalUuid != null && goalUuid.uuid.Length == 16 && ToGoalId(goalUuid) != goalId)
                        return;

                    object? values = ReflectionAccess.GetMemberValue(envelope, "feedback");
                    TActionFeedback feedback = CreateActionFeedback<TActionFeedback>(actionName, goalId, values);
                    callback(feedback);
                },
                ensureThreadSafety: true);

            lock (gate)
            {
                subscribers[state.Id] = state;
                actionConsumers[goalId] = new ActionConsumerState(goalId, actionName, state.Id);
                if (channelsByTopic.TryGetValue(topic, out FoxgloveChannel? channel))
                    CreateSubscription(state, channel);
                else
                    pendingSubscribers[state.Id] = new PendingSubscriber(state);
            }
        }

        private void UnsubscribeActionFeedback(string goalId)
        {
            string? subscriptionId;
            lock (gate)
            {
                if (!actionConsumers.TryGetValue(goalId, out ActionConsumerState? state))
                    return;
                subscriptionId = state.FeedbackSubscriptionId;
                actionConsumers.Remove(goalId);
            }

            if (subscriptionId != null)
                Unsubscribe(subscriptionId);
        }

        private T Decode<T>(string topic, byte[] bytes) where T : Message
        {
            FoxgloveChannel channel;
            lock (gate)
                channel = channelsByTopic[topic];

            if (string.Equals(channel.Encoding, "json", StringComparison.OrdinalIgnoreCase))
                return JsonMessageSerializer.Deserialize<T>(bytes);

            return GetCodec(channel.SchemaName, channel.Schema).Deserialize<T>(bytes);
        }

        private T DecodeServiceResponse<T>(string responseType, string responseSchema, byte[] bytes)
            where T : Message
        {
            return responseSchema.Length == 0
                ? (T)(Activator.CreateInstance(typeof(T))
                    ?? throw new InvalidOperationException($"Unable to create {typeof(T).FullName}."))
                : GetCodec(responseType, responseSchema).Deserialize<T>(bytes);
        }

        private void CallServiceObject<TResponse>(
            string serviceName,
            object request,
            Action<TResponse> responseHandler)
            where TResponse : Message
        {
            CallServiceObject(serviceName, request, typeof(TResponse), raw => responseHandler((TResponse)raw));
        }

        private void CallServiceObject(
            string serviceName,
            object request,
            Type responseType,
            Action<object> responseHandler)
        {
            FoxgloveService svc;
            lock (gate)
            {
                if (!servicesByName.TryGetValue(serviceName, out svc!))
                    throw new InvalidOperationException($"Service {serviceName} is not advertised by foxglove_bridge.");
            }

            string serviceType = RosTypeName.NormalizeService(svc.Type);
            string requestSchema = svc.Request?.Schema ?? svc.RequestSchema
                ?? throw new InvalidOperationException($"Service {serviceName} did not advertise a request schema.");
            string responseSchema = svc.Response?.Schema ?? svc.ResponseSchema ?? "";
            string requestType = serviceType + "_Request";
            string responseSchemaType = serviceType + "_Response";
            byte[] requestData = GetCodec(requestType, requestSchema).SerializeObject(request);
            int callId = protocolClient.CallService(svc.Id, "cdr", requestData);

            lock (gate)
            {
                pendingServiceCalls[callId] = new PendingServiceCall(
                    serviceName,
                    data =>
                    {
                        object response = responseSchema.Length == 0
                            ? Activator.CreateInstance(responseType)
                                ?? throw new InvalidOperationException($"Unable to create {responseType.FullName}.")
                            : GetCodec(responseSchemaType, responseSchema).DeserializeObject(data, responseType);
                        responseHandler(response);
                    },
                    ex => Error?.Invoke(ex));
            }
        }

        private void EnsureActionAdvertised(string actionName, bool requireFeedback)
        {
            string[] missingEndpoints = GetMissingActionEndpoints(actionName, requireFeedback);
            if (missingEndpoints.Length > 0)
                throw new ActionNotAdvertisedException(actionName, missingEndpoints);
        }

        private void EnsureActionCancelAdvertised(string actionName)
        {
            string cancelEndpoint = ActionEndpoint(actionName, "cancel_goal");
            lock (gate)
            {
                if (servicesByName.ContainsKey(cancelEndpoint))
                    return;
            }

            throw new ActionNotAdvertisedException(actionName, new[] { cancelEndpoint });
        }

        private void EnsureClientChannel(PublisherState publisher)
        {
            FoxgloveChannel? matchingChannel;
            lock (gate)
            {
                matchingChannel = channels.Values.FirstOrDefault(
                    ch => RosTypeName.NormalizeMessage(ch.SchemaName) == publisher.SchemaName);
            }

            publisher.Encoding = matchingChannel != null ? "cdr" : "json";
            publisher.Schema = matchingChannel?.Schema;
            int channelId = protocolClient.AdvertiseClientChannel(
                publisher.Topic,
                publisher.Encoding,
                publisher.SchemaName);
            publisher.ClientChannelId = channelId;
        }

        private CdrMessageCodec GetCodec(string schemaName, string schema)
        {
            string key = schemaName + "\n" + schema;
            lock (gate)
            {
                if (!codecCache.TryGetValue(key, out CdrMessageCodec? codec))
                {
                    codec = new CdrMessageCodec(schemaName, schema);
                    codecCache[key] = codec;
                }
                return codec;
            }
        }

        private static string ToFoxgloveParamName(string name) => name.Replace(':', '.');

        private static string RequireActionName(string action)
        {
            if (string.IsNullOrWhiteSpace(action))
                throw new InvalidOperationException("Action goal must set the ROS action name.");
            return action;
        }

        private static string ActionEndpoint(string actionName, string leaf)
        {
            return actionName.TrimEnd('/') + "/_action/" + leaf;
        }

        private static string InferActionType(Type actionGoalType)
        {
            string rosName = RosTypeName.FromMessageType(actionGoalType);
            const string suffix = "ActionGoal";
            return rosName.EndsWith(suffix, StringComparison.Ordinal)
                ? rosName[..^suffix.Length]
                : rosName;
        }

        private static string NewGoalId(bool zero = false)
        {
            if (zero)
                return new Guid(new byte[16]).ToString("D");

            byte[] bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            return new Guid(bytes).ToString("D");
        }

        private static UUID ToUuid(string id)
        {
            return new UUID { uuid = Guid.TryParse(id, out Guid guid) ? guid.ToByteArray() : new Guid(id).ToByteArray() };
        }

        private static string ToGoalId(UUID uuid)
        {
            return uuid.uuid.Length == 16 ? new Guid(uuid.uuid).ToString("D") : "";
        }

        private static void SetGoalInfo(GoalInfo goalInfo, string id)
        {
            goalInfo.goal_id = ToUuid(id);
        }

        private static Type GetActionPayloadType(Type actionWrapperType, string propertyName)
        {
            Type? type = ReflectionAccess.GetMemberType(actionWrapperType, propertyName);
            if (type == null || !typeof(Message).IsAssignableFrom(type))
                throw new InvalidOperationException($"{actionWrapperType.FullName}.{propertyName} must be a ROS# Message type.");
            return type;
        }

        private static TActionResult CreateActionResult<TActionResult>(
            string action,
            string id,
            sbyte status,
            bool result,
            object? values)
            where TActionResult : Message
        {
            TActionResult actionResult = (TActionResult)(Activator.CreateInstance(typeof(TActionResult))
                ?? throw new InvalidOperationException($"Unable to create {typeof(TActionResult).FullName}."));
            ReflectionAccess.SetMemberValue(actionResult, "action", action);
            ReflectionAccess.SetMemberValue(actionResult, "id", id);
            ReflectionAccess.SetMemberValue(actionResult, "status", status);
            ReflectionAccess.SetMemberValue(actionResult, "result", result);
            if (values != null)
                ReflectionAccess.SetMemberValue(actionResult, "values", values);

            if (ReflectionAccess.GetMemberValue(actionResult, "goalStatus") is GoalStatus goalStatus)
            {
                goalStatus.goal_info = new GoalInfo { goal_id = ToUuid(id) };
                goalStatus.status = status;
            }

            return actionResult;
        }

        private static TActionFeedback CreateActionFeedback<TActionFeedback>(
            string action,
            string id,
            object? values)
            where TActionFeedback : Message
        {
            TActionFeedback feedback = (TActionFeedback)(Activator.CreateInstance(typeof(TActionFeedback))
                ?? throw new InvalidOperationException($"Unable to create {typeof(TActionFeedback).FullName}."));
            ReflectionAccess.SetMemberValue(feedback, "action", action);
            ReflectionAccess.SetMemberValue(feedback, "id", id);
            if (values != null)
                ReflectionAccess.SetMemberValue(feedback, "values", values);
            return feedback;
        }

        private sealed class PublisherState
        {
            public PublisherState(string topic, string schemaName)
            {
                Topic = topic;
                SchemaName = RosTypeName.NormalizeMessage(schemaName);
            }

            public string Topic { get; }
            public string SchemaName { get; }
            public string Encoding { get; set; } = "json";
            public string? Schema { get; set; }
            public int? ClientChannelId { get; set; }

            public void ClearClientChannel()
            {
                ClientChannelId = null;
                Schema = null;
                Encoding = "json";
            }
        }

        private sealed class SubscriberState
        {
            private readonly Action<byte[]> receive;
            private readonly object receiveLock = new();
            private readonly bool ensureThreadSafety;

            public SubscriberState(
                string id,
                string topic,
                string schemaName,
                Type messageType,
                Action<byte[]> receive,
                bool ensureThreadSafety)
            {
                Id = id;
                Topic = topic;
                SchemaName = schemaName;
                MessageType = messageType;
                this.receive = receive;
                this.ensureThreadSafety = ensureThreadSafety;
            }

            public string Id { get; }
            public string Topic { get; }
            public string SchemaName { get; set; }
            public string? Schema { get; set; }
            public Type MessageType { get; }
            public int? SubscriptionId { get; set; }

            public void Receive(byte[] data)
            {
                if (!ensureThreadSafety)
                {
                    receive(data);
                    return;
                }

                lock (receiveLock)
                    receive(data);
            }
        }

        private sealed class PendingSubscriber
        {
            public PendingSubscriber(SubscriberState state)
            {
                State = state;
            }

            public SubscriberState State { get; }
        }

        private sealed class PendingServiceCall
        {
            private readonly Action<byte[]> complete;
            private readonly Action<Exception> fail;

            public PendingServiceCall(
                string service,
                Action<byte[]> complete,
                Action<Exception> fail)
            {
                Service = service;
                this.complete = complete;
                this.fail = fail;
            }

            public string Service { get; }

            public void Complete(byte[] data) => complete(data);

            public void Fail(Exception ex) => fail(new InvalidOperationException($"Service call to {Service} failed.", ex));
        }

        private sealed class PendingParameterRequest
        {
            private readonly Action<IReadOnlyList<ParameterValue>> complete;
            private readonly Action<Exception> fail;

            public PendingParameterRequest(Action<IReadOnlyList<ParameterValue>> complete, Action<Exception> fail)
            {
                this.complete = complete;
                this.fail = fail;
            }

            public void Complete(IReadOnlyList<ParameterValue> values) => complete(values);
            public void Fail(Exception ex) => fail(ex);
        }

        private sealed class ActionConsumerState
        {
            public ActionConsumerState(string id, string action, string feedbackSubscriptionId)
            {
                Id = id;
                Action = action;
                FeedbackSubscriptionId = feedbackSubscriptionId;
            }

            public string Id { get; }
            public string Action { get; }
            public string FeedbackSubscriptionId { get; }
        }

        private sealed class SendGoalRequest<TGoal> : Message where TGoal : Message
        {
            public UUID goal_id { get; set; } = new();
            public TGoal? goal { get; set; }
        }

        private sealed class SendGoalResponse : Message
        {
            public bool accepted { get; set; }
            public MessageTypes.BuiltinInterfaces.Time stamp { get; set; } = new();
        }

        private sealed class GetResultRequest : Message
        {
            public UUID goal_id { get; set; } = new();
        }

        private sealed class GetResultResponse<TResult> : Message where TResult : Message
        {
            public sbyte status { get; set; }
            public TResult? result { get; set; }
        }

        private sealed class FeedbackMessage<TFeedback> : Message where TFeedback : Message
        {
            public UUID goal_id { get; set; } = new();
            public TFeedback? feedback { get; set; }
        }
    }
}
