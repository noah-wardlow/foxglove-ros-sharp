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
- ROS 2 action clients when the bridge advertises hidden action endpoints

It does not include Unity components, URDF tooling, editor extensions, or ROS
message generation.

## ROS# Drop-In Surface

The runtime API intentionally uses the ROS# namespace and method names used by
existing `RosSharp.RosBridgeClient` code:

- `new RosSocket(...)`
- `Advertise<T>()`, `Publish()`, `Unadvertise()`
- `Subscribe<T>()`, `Unsubscribe()`
- `CallService<TRequest, TResponse>()`
- `SendActionGoalRequest<...>()`, `CancelActionGoalRequest<...>()`
- `Close(int millisecondsWait = 0)` and `Dispose()`

Generated ROS# message classes can be reused when they expose
`public const string RosMessageName` and public fields or properties matching
the ROS message field names. This package swaps the transport under that API:
it talks to `foxglove_bridge` over the Foxglove WebSocket protocol and decodes
the bridge-advertised CDR schemas.

## Install

### From NuGet

```bash
dotnet add package FoxgloveRosSharp
```

### From Source

Use this when testing local changes before publishing a package:

```bash
git clone https://github.com/noah-wardlow/foxglove-ros-sharp.git
dotnet add reference foxglove-ros-sharp/src/FoxgloveRosSharp/FoxgloveRosSharp.csproj
```

### From a Local Package

Use this when testing the packaged output before publishing it:

Build a `.nupkg`:

```bash
dotnet pack src/FoxgloveRosSharp/FoxgloveRosSharp.csproj --configuration Release --output artifacts
```

Then install it into another .NET project from that local folder:

```bash
dotnet add package FoxgloveRosSharp --source /absolute/path/to/foxglove-ros-sharp/artifacts
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

ROS 2 action clients are available only when the Foxglove bridge advertises the
hidden ROS 2 action services and feedback topic. `RosSocket` maps ROS# action
calls to:

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

// Or wait for the endpoints needed to send a goal and receive the result:
await ros.WaitForActionAsync(
    "/example_action",
    TimeSpan.FromSeconds(5),
    requireFeedback: true);

string goalId = ros.SendActionGoalRequest<
    ExampleTaskActionGoal,
    ExampleTaskGoal,
    ExampleTaskActionFeedback,
    ExampleTaskActionResult>(
        goal,
        result => Console.WriteLine(result.values),
        feedback => Console.WriteLine(feedback.values));
```

Foxglove Bridge sets `include_hidden` to `false` by default. Since ROS 2 action
endpoints are hidden services/topics, default bridge launches usually do not
advertise actions. Launch the bridge with hidden entities enabled if you want
direct action support:

```bash
ros2 launch foxglove_bridge foxglove_bridge_launch.xml include_hidden:=true
```

You can check action availability before sending a goal:

```csharp
if (!ros.IsActionAdvertised("/example_action", requireFeedback: true))
{
    string[] missing = ros.GetMissingActionEndpoints("/example_action", requireFeedback: true);
    Console.WriteLine(string.Join(", ", missing));
}
```

If direct action endpoints are not available, `SendActionGoalRequest()` and
`CancelActionGoalRequest()` throw `ActionNotAdvertisedException` with the
missing endpoint list. In that case, call an application-level service that
forwards to the action on the ROS side. This library does not hard-code any such
service; use `CallService<TRequest, TResponse>()` with your own generated
service message classes.

Foxglove may advertise ROS 2 action service schemas with the goal/result fields
flattened at the request or response root. The client handles both flattened
Foxglove action schemas and nested ROS# action wrapper classes.

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
schemas advertised by the bridge. Publishing uses Foxglove client channels: it
sends CDR when a matching schema has been advertised, otherwise it sends a
JSON-encoded message payload.

The CDR codec handles ROS 2 encapsulation-header-relative alignment, including
8-byte aligned fields such as `float64[]` in `sensor_msgs/msg/JointState`.

`foxglove_bridge` does not support client-side service advertisement through
the WebSocket protocol, so `AdvertiseService()` and `UnadvertiseService()` throw
`NotSupportedException`.

## Live Verification

The client has been live-tested against a Foxglove bridge with
`include_hidden:=true` for:

- subscribing to `/joint_states` as `sensor_msgs/msg/JointState`
- sending `/do_objective` as a ROS 2 action
- receiving a successful action result

## License

MIT
