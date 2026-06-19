using RosSharp.RosBridgeClient.MessageTypes.Action;
using RosSharp.RosBridgeClient.MessageTypes.Std;

namespace RosSharp.RosBridgeClient
{
    public abstract class Action<TActionGoal, TActionResult, TActionFeedback, TGoal, TResult, TFeedback> : Message
        where TActionGoal : ActionGoal<TGoal>
        where TActionResult : ActionResult<TResult>
        where TActionFeedback : ActionFeedback<TFeedback>
        where TGoal : Message
        where TResult : Message
        where TFeedback : Message
    {
        public TActionGoal? action_goal { get; set; }
        public TActionResult? action_result { get; set; }
        public TActionFeedback? action_feedback { get; set; }
    }

    public abstract class ActionGoal<TGoal> : Message where TGoal : Message
    {
        public Header header { get; set; } = new();
        public GoalInfo goalInfo { get; set; } = new();
        public TGoal? args { get; set; }
        public string id { get; set; } = "";
        public string action { get; set; } = "";
        public string action_type { get; set; } = "";
        public bool feedback { get; set; }
        public int fragment_size { get; set; }
        public string compression { get; set; } = "none";
    }

    public abstract class ActionResult<TResult> : Message where TResult : Message
    {
        public Header header { get; set; } = new();
        public string action { get; set; } = "";
        public TResult? values { get; set; }
        public sbyte status { get; set; }
        public GoalStatus goalStatus { get; set; } = new();
        public bool result { get; set; }
        public string id { get; set; } = "";
    }

    public abstract class ActionFeedback<TFeedback> : Message where TFeedback : Message
    {
        public Header header { get; set; } = new();
        public TFeedback? values { get; set; }
        public string id { get; set; } = "";
        public string action { get; set; } = "";
    }
}
