namespace RosSharp.RosBridgeClient.MessageTypes.BuiltinInterfaces
{
    public class Time : Message
    {
        public const string RosMessageName = "builtin_interfaces/msg/Time";
        public int sec { get; set; }
        public uint nanosec { get; set; }
    }
}

namespace RosSharp.RosBridgeClient.MessageTypes.UniqueIdentifier
{
    public class UUID : Message
    {
        public const string RosMessageName = "unique_identifier_msgs/msg/UUID";
        public byte[] uuid { get; set; } = new byte[16];
    }
}

namespace RosSharp.RosBridgeClient.MessageTypes.Std
{
    using RosSharp.RosBridgeClient.MessageTypes.BuiltinInterfaces;

    public class String : Message
    {
        public const string RosMessageName = "std_msgs/msg/String";
        public string data { get; set; } = "";

        public String()
        {
        }

        public String(string data)
        {
            this.data = data;
        }
    }

    public class Header : Message
    {
        public const string RosMessageName = "std_msgs/msg/Header";
        public Time stamp { get; set; } = new();
        public string frame_id { get; set; } = "";
    }
}

namespace RosSharp.RosBridgeClient.MessageTypes.Action
{
    using RosSharp.RosBridgeClient.MessageTypes.BuiltinInterfaces;
    using RosSharp.RosBridgeClient.MessageTypes.UniqueIdentifier;

    public class GoalInfo : Message
    {
        public const string RosMessageName = "action_msgs/msg/GoalInfo";
        public UUID goal_id { get; set; } = new();
        public Time stamp { get; set; } = new();
    }

    public class GoalStatus : Message
    {
        public const string RosMessageName = "action_msgs/msg/GoalStatus";
        public const sbyte STATUS_UNKNOWN = 0;
        public const sbyte STATUS_ACCEPTED = 1;
        public const sbyte STATUS_EXECUTING = 2;
        public const sbyte STATUS_CANCELING = 3;
        public const sbyte STATUS_SUCCEEDED = 4;
        public const sbyte STATUS_CANCELED = 5;
        public const sbyte STATUS_ABORTED = 6;

        public GoalInfo goal_info { get; set; } = new();
        public sbyte status { get; set; }
    }

    public class CancelGoalRequest : Message
    {
        public const string RosMessageName = "action_msgs/srv/CancelGoal";
        public GoalInfo goal_info { get; set; } = new();
    }

    public class CancelGoalResponse : Message
    {
        public const string RosMessageName = "action_msgs/srv/CancelGoal";
        public const sbyte ERROR_NONE = 0;
        public const sbyte ERROR_REJECTED = 1;
        public const sbyte ERROR_UNKNOWN_GOAL_ID = 2;
        public const sbyte ERROR_GOAL_TERMINATED = 3;

        public sbyte return_code { get; set; }
        public GoalInfo[] goals_canceling { get; set; } = System.Array.Empty<GoalInfo>();
    }
}
