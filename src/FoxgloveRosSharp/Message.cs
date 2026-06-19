namespace RosSharp.RosBridgeClient
{
    public abstract class Message { }

    public abstract class Service
    {
        public abstract Message Request { get; set; }
        public abstract Message Response { get; set; }
    }
}
