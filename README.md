# Foxglove ROS Sharp

Foxglove ROS Sharp is a small ROS#-compatible C# runtime client for ROS 2
systems exposed through a Foxglove WebSocket bridge.

It keeps the familiar `RosSharp.RosBridgeClient` namespace and core runtime
shape while replacing rosbridge JSON transport with Foxglove WebSocket messages
and CDR payloads.

## Status

This project is early and intentionally focused on runtime communication:

- topic subscription and publishing
- service calls
- parameter get/set
- ROS 2 action clients when the bridge advertises standard action endpoints

It does not include Unity components, URDF tooling, editor extensions, or ROS
message generation.

## Install

Reference the project directly:

```bash
dotnet add package FoxgloveRosSharp
```

Or from source:

```bash
dotnet add reference path/to/foxglove-ros-sharp/src/FoxgloveRosSharp/FoxgloveRosSharp.csproj
```

## Connect

Start `foxglove_bridge` on the ROS 2 side:

```bash
ros2 launch foxglove_bridge foxglove_bridge_launch.xml port:=8765
```

Connect from C#:

```csharp
using RosSharp.RosBridgeClient;
using RosSharp.RosBridgeClient.MessageTypes.Std;
using RosSharp.RosBridgeClient.Protocols;

using var ros = new RosSocket(new WebSocketNetProtocol("ws://localhost:8765"));
await ros.Connected;

string subscription = ros.Subscribe<String>(
    "/chatter",
    msg => Console.WriteLine(msg.data));

string publisher = ros.Advertise<String>("/dotnet/chatter");
ros.Publish(publisher, new String("hello from C#"));
```

## Services

Use ROS# message classes for the request and response. Both classes should set
`RosMessageName` to the service type.

```csharp
var responseTask = new TaskCompletionSource<TriggerResponse>();

ros.CallService<TriggerRequest, TriggerResponse>(
    "/example_trigger",
    response => responseTask.TrySetResult(response),
    new TriggerRequest());

TriggerResponse response = await responseTask.Task;

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
```

## Actions

When Foxglove advertises the standard ROS 2 action services and feedback topic,
`RosSocket` maps ROS# action calls to:

- `<action>/_action/send_goal`
- `<action>/_action/get_result`
- `<action>/_action/cancel_goal`
- `<action>/_action/feedback`

```csharp
var goal = new ExampleTaskActionGoal
{
    action = "/example_action",
    feedback = true,
    args = new ExampleTaskGoal
    {
        task_name = "sample",
        parameters = "speed=1.0"
    }
};

await ros.WaitForServiceAsync(
    "/example_action/_action/send_goal",
    TimeSpan.FromSeconds(5));

string goalId = ros.SendActionGoalRequest<
    ExampleTaskActionGoal,
    ExampleTaskGoal,
    ExampleTaskActionFeedback,
    ExampleTaskActionResult>(
        goal,
        result => Console.WriteLine(result.values),
        feedback => Console.WriteLine(feedback.values));
```

Some Foxglove bridge deployments do not expose hidden ROS 2 action endpoints.
In that case, call an application-level service that forwards to the action on
the ROS side. This library does not hard-code any such service; use
`CallService<TRequest, TResponse>()` with your own generated service message
classes.

## Examples

The `examples/TopicServiceActionDemo` project demonstrates neutral topic,
service, and action usage. It skips service/action calls when the named endpoint
is not advertised.

```bash
dotnet run --project examples/TopicServiceActionDemo -- ws://localhost:8765
```

Optional arguments:

```text
TopicServiceActionDemo <websocket-url> <service-name> <action-name>
```

## Build

```bash
dotnet restore FoxgloveRosSharp.sln
dotnet build FoxgloveRosSharp.sln
dotnet test FoxgloveRosSharp.sln
dotnet pack src/FoxgloveRosSharp/FoxgloveRosSharp.csproj --configuration Release --output artifacts
```

## Compatibility Notes

Existing ROS# generated message classes can be reused when they expose
`public const string RosMessageName`.

Topic and service responses are decoded from Foxglove CDR payloads using the
schemas advertised by the bridge. Publishing uses CDR when a matching schema has
been advertised and falls back to JSON otherwise.

`foxglove_bridge` does not support client-side service advertisement through
the WebSocket protocol, so `AdvertiseService()` and `UnadvertiseService()` throw
`NotSupportedException`.

## License

MIT
