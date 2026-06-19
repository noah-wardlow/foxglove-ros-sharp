using System;
using System.Threading.Tasks;
using RosSharp.RosBridgeClient;
using RosSharp.RosBridgeClient.MessageTypes.Action;
using RosString = RosSharp.RosBridgeClient.MessageTypes.Std.String;

string url = args.Length > 0 ? args[0] : "ws://localhost:8765";
string serviceName = args.Length > 1 ? args[1] : "/example_trigger";
string actionName = args.Length > 2 ? args[2] : "/example_action";

using var ros = new RosSocket(url);
ros.Error += ex => Console.Error.WriteLine($"Foxglove error: {ex.Message}");
await ros.Connected;

Console.WriteLine($"Connected to {url}");

ros.Subscribe<RosString>("/chatter", message => Console.WriteLine($"/chatter: {message.data}"));

string publisherId = ros.Advertise<RosString>("/foxglove_ros_sharp/chatter");
ros.Publish(publisherId, new RosString("hello from FoxgloveRosSharp"));

if (await ros.WaitForServiceAsync(serviceName, TimeSpan.FromSeconds(2)))
{
    var responseTask = new TaskCompletionSource<TriggerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
    ros.CallService<TriggerRequest, TriggerResponse>(
        serviceName,
        response => responseTask.TrySetResult(response),
        new TriggerRequest());

    TriggerResponse response = await responseTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"{serviceName}: success={response.success}, message={response.message}");
}
else
{
    Console.WriteLine($"Service {serviceName} was not advertised; skipping service call.");
}

if (await ros.WaitForActionAsync(actionName, TimeSpan.FromSeconds(2), requireFeedback: true))
{
    var resultTask = new TaskCompletionSource<ExampleTaskActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var feedbackTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    string goalId = ros.SendActionGoalRequest<
        ExampleTaskActionGoal,
        ExampleTaskGoal,
        ExampleTaskActionFeedback,
        ExampleTaskActionResult>(
            new ExampleTaskActionGoal
            {
                action = actionName,
                feedback = true,
                args = new ExampleTaskGoal { task_name = "sample", parameters = "" }
            },
            result => resultTask.TrySetResult(result),
            feedback => feedbackTask.TrySetResult(feedback.values?.progress ?? ""));

    Console.WriteLine($"Sent action goal {goalId}");
    ExampleTaskActionResult result = await resultTask.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine($"{actionName}: status={result.status}, outcome={result.values?.outcome}");
}
else
{
    string[] missing = ros.GetMissingActionEndpoints(actionName, requireFeedback: true);
    Console.WriteLine($"Action {actionName} was not advertised; skipping action goal.");
    Console.WriteLine($"Missing endpoint(s): {string.Join(", ", missing)}");
}

public sealed class TriggerRequest : Message
{
    public const string RosMessageName = "std_srvs/srv/Trigger";
}

public sealed class TriggerResponse : Message
{
    public const string RosMessageName = "std_srvs/srv/Trigger";

    public bool success { get; set; }
    public string message { get; set; } = "";
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
