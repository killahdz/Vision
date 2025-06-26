using IA.Vision.Rpc.Services;
using Grpc.Core;

internal class gRPCStreamHandler<T1, T2>
{
    public int ConsumerCount { get; internal set; }

    internal async Task<object> HandleStreamConsumerAsync(IServerStreamWriter<ImageStrip> responseStream, ServerCallContext context)
    {
        throw new NotImplementedException();
    }

    internal void Stop()
    {
        throw new NotImplementedException();
    }

    internal async Task WriteToConsumersAsync(ImageStrip strip)
    {
        throw new NotImplementedException();
    }
}
