using System;

namespace RosSharp.RosBridgeClient.Protocols
{
    public interface IProtocol : IDisposable
    {
        string Uri { get; }
    }
}
