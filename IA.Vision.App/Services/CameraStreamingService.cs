using System.Collections.Concurrent;
using IA.Vision.App.Services;
using IA.Vision.Rpc.Services;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class CameraStreamingService : ImageProcessing.ImageProcessingBase
{
    private readonly ILogger<CameraStreamingService> logger;
    private readonly ILogger messageLogger;
    private readonly gRPCStreamHandler<ImageStripStreamRequest, ImageStrip> imageStripStreamHandler = new();
    private readonly BlockingCollection<ImageStrip> outgoingImageStrips = new();
    private readonly Server server;
    public readonly int CameraId; // Changed to int for telemetry consistency
    private readonly TelemetryTracker telemetry;
    public readonly string StreamAddress;

    public int ConnectedClients => imageStripStreamHandler.ConsumerCount;
    public long RequestsAddedToStream => telemetry.GetRequestsAddedToStream(CameraId);
    public long RequestsReadFromStream => telemetry.GetRequestsReadFromStream(CameraId);

    public long ErrorsFromStream => telemetry.GetErrorsFromStream(CameraId);

    public CameraStreamingService(
        string ip,
        int port,
        int cameraId,
        ILogger<CameraStreamingService> logger,
        IHostApplicationLifetime applicationLifetime,
        TelemetryTracker telemetry)
    {
        this.logger = logger;
        this.CameraId = cameraId;
        this.messageLogger = VisionLoggerFactory.CreateMessageOutputLogger(cameraId.ToString());
        this.telemetry = telemetry;
        this.StreamAddress = $"{ip}:{port}";
        // Initialize telemetry with initial connected clients (0)
        telemetry.SetConnectedClients(CameraId, 0);

        server = new Server();
        server.Ports.Add(ip, port, ServerCredentials.Insecure);
        server.Services.Add(ImageProcessing.BindService(this));
        server.Start();

        Task.Run(() => ProcessImageStripStreamAsync(applicationLifetime?.ApplicationStopping ?? CancellationToken.None));

        applicationLifetime?.ApplicationStopping.Register(() =>
        {
            outgoingImageStrips.CompleteAdding();
            imageStripStreamHandler.Stop();
            telemetry.SetConnectedClients(CameraId, 0); // Reset to 0 on shutdown
        });
    }

    public override Task ImageStripStream(ImageStripStreamRequest request, IServerStreamWriter<ImageStrip> responseStream, ServerCallContext context)
    {
        logger.LogInformation("New image strip stream request for camera {CameraId} from {Peer}", CameraId, context.Peer);

        // Update connected clients when a new client connects
        int newClientCount = imageStripStreamHandler.ConsumerCount + 1; // Anticipate the addition
        telemetry.SetConnectedClients(CameraId, newClientCount);

        return imageStripStreamHandler.HandleStreamConsumerAsync(responseStream, context).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                logger.LogError(t.Exception, "Image strip stream failed for camera {CameraId}, peer {Peer}", CameraId, context.Peer);
                telemetry.IncrementErrorsFromStream(CameraId);
            }
            // Update connected clients on disconnect or failure
            telemetry.SetConnectedClients(CameraId, imageStripStreamHandler.ConsumerCount);
        });
    }

    public void PostImageStrip(ImageStrip imageStrip)
    {
        outgoingImageStrips.Add(imageStrip);
        telemetry.IncrementRequestsAddedToStream(CameraId); // Track requests added to stream
        messageLogger.LogInformation("Posted image strip: CameraID={CameraId}, EncoderValue={EncoderValue}, Size={Size}bytes",
            imageStrip.CameraID, imageStrip.EncoderValue, imageStrip.Image.Length);
    }

    private async Task ProcessImageStripStreamAsync(CancellationToken cancellationToken)
    {
        foreach (var strip in outgoingImageStrips.GetConsumingEnumerable(cancellationToken))
        {
            try
            {
                await imageStripStreamHandler.WriteToConsumersAsync(strip);
                telemetry.IncrementRequestsReadFromStream(CameraId); // Track requests read from stream
                messageLogger.LogInformation("Streamed image strip: CameraID={CameraId}, EncoderValue={EncoderValue}, Consumers={ConsumerCount}",
                    strip.CameraID, strip.EncoderValue, imageStripStreamHandler.ConsumerCount);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error streaming image strip for camera {CameraId}, encoder {Encoder}", CameraId, strip.EncoderValue);
            }
        }
    }

    public async Task ShutdownAsync()
    {
        if (outgoingImageStrips.Count > 0)
        {
            outgoingImageStrips.CompleteAdding();
        }
        await server.KillAsync();
        await server.ShutdownTask;
    }
}
