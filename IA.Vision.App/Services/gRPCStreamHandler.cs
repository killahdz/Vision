using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using IA.Vision.Rpc.Services;

namespace IA.Vision.App.Services
{
    /// <summary>
    /// Manages long-running client-to-server gRPC streams for ImageStrip messages.
    /// </summary>
    public class gRPCStreamHandler<TRequest, ImageStrip>
        where TRequest : class
        where ImageStrip : class
    {
        private readonly ConcurrentDictionary<string, StreamConsumer<ImageStrip>> consumers
            = new ConcurrentDictionary<string, StreamConsumer<ImageStrip>>();

        private readonly CancellationTokenSource stopToken = new CancellationTokenSource();

        /// <summary>
        /// Gets the number of active stream consumers.
        /// </summary>
        public int ConsumerCount => consumers.Count;

        /// <summary>
        /// Stops all active streams and cancels their tokens.
        /// </summary>
        public void Stop()
        {
            stopToken.Cancel();
            foreach (var consumer in consumers.Values)
            {
                consumer.StopWaitingOnConsumer();
            }
        }

        /// <summary>
        /// Writes an ImageStrip response to all active consumers.
        /// </summary>
        /// <param name="strip">The ImageStrip message to write.</param>
        public async Task WriteToConsumersAsync(ImageStrip strip)
        {
            foreach (var consumer in consumers)
            {
                try
                {
                    await consumer.Value.Writer.WriteAsync(strip);
                }
                catch (Exception ex)
                {
                    // Log error if consumer fails (e.g., client disconnected)
                    // Note: Logger injection could be added if needed
                    consumer.Value.StopWaitingOnConsumer();
                    consumers.TryRemove(consumer.Key, out _);
                }
            }
        }

        /// <summary>
        /// Handles the registration of a new stream consumer.
        /// </summary>
        /// <param name="responseStream">The stream writer for sending ImageStrip messages.</param>
        /// <param name="context">The gRPC server call context.</param>
        /// <returns>A task that completes when the stream is canceled or stopped.</returns>
        public async Task HandleStreamConsumerAsync(IServerStreamWriter<ImageStrip> responseStream, ServerCallContext context)
        {
            var peer = context.Peer;
            var streamConsumer = new StreamConsumer<ImageStrip>(responseStream);
            if (consumers.TryAdd(peer, streamConsumer))
            {
                context.CancellationToken.Register(() =>
                {
                    if (consumers.TryRemove(peer, out var goodbye))
                    {
                        goodbye.StopWaitingOnConsumer();
                    }
                });

                // Wait until cancellation is triggered (client, consumer, or global stop)
                try
                {
                    await Task.Delay(-1, CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken,
                        streamConsumer.CancelToken,
                        stopToken.Token).Token);
                }
                catch (TaskCanceledException)
                {
                    // Expected when stream is canceled
                }
            }
        }

        private class StreamConsumer<T>
        {
            private readonly CancellationTokenSource ctSource = new CancellationTokenSource();

            public StreamConsumer(IServerStreamWriter<T> responseStream)
            {
                Writer = responseStream;
            }

            public IServerStreamWriter<T> Writer { get; }
            public CancellationToken CancelToken => ctSource.Token;

            public void StopWaitingOnConsumer() => ctSource.Cancel();
        }
    }
}
