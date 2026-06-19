using System;

namespace RosSharp.RosBridgeClient
{
    public sealed class ActionNotAdvertisedException : InvalidOperationException
    {
        public ActionNotAdvertisedException(string actionName, string[] missingEndpoints)
            : base(CreateMessage(actionName, missingEndpoints))
        {
            ActionName = actionName;
            MissingEndpoints = missingEndpoints;
        }

        public string ActionName { get; }
        public string[] MissingEndpoints { get; }

        private static string CreateMessage(string actionName, string[] missingEndpoints)
        {
            string missing = missingEndpoints.Length == 0
                ? "unknown action endpoints"
                : string.Join(", ", missingEndpoints);
            return $"Action {actionName} is not advertised by the Foxglove bridge. " +
                   $"Missing endpoint(s): {missing}. " +
                   "ROS 2 action endpoints are hidden topics/services; start foxglove_bridge with include_hidden:=true " +
                   "or expose an application-level service that forwards to the action.";
        }
    }
}
