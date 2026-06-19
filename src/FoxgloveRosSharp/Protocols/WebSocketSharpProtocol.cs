namespace RosSharp.RosBridgeClient.Protocols
{
    public sealed class WebSocketSharpProtocol : IProtocol
    {
        public WebSocketSharpProtocol(string uri)
        {
            Uri = uri;
        }

        public string Uri { get; }

        public void Dispose() { }
    }
}
