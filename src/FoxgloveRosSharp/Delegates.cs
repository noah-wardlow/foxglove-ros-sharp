namespace RosSharp.RosBridgeClient
{
    public delegate void ServiceResponseHandler<T>(T message) where T : Message;

    public delegate void SubscriptionHandler<T>(T message) where T : Message;

    public delegate bool ServiceCallHandler<TIn, TOut>(TIn request, out TOut response)
        where TIn : Message
        where TOut : Message;

    public delegate void ActionResultResponseHandler<TActionResult>(TActionResult message)
        where TActionResult : Message;

    public delegate void ActionFeedbackResponseHandler<TActionFeedback>(TActionFeedback message)
        where TActionFeedback : Message;

    public delegate void ActionCancelResponseHandler<TActionResult>(TActionResult message)
        where TActionResult : Message;

    public delegate void SendActionGoalHandler<TActionGoal>(TActionGoal message)
        where TActionGoal : Message;

    public delegate void CancelActionGoalHandler(string frameId, string action);
}
