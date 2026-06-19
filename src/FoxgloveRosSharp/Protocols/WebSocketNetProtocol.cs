namespace RosSharp.RosBridgeClient.Protocols
{
    public sealed class WebSocketNetProtocol : IProtocol
    {
        public WebSocketNetProtocol(string uri)
        {
            Uri = uri;
        }

        public string Uri { get; }

        public void Dispose() { }
    }
}
