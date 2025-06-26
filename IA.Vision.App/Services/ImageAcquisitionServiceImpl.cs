using System.Collections.Concurrent;
using System.Threading.Channels;
using IA.Vision.App.Interfaces;
using IA.Vision.App.Models;
using IA.Vision.App.Utils;
using IA.Vision.Rpc.Services;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static IA.Vision.Rpc.Services.ImageAcquisition;

namespace IA.Vision.App.Services
{
    public class ImageAcquisitionServiceImpl : ImageAcquisitionBase, IHostedService, IDisposable
    {
        public readonly ServerOptions ServerOptions;
        private readonly ILogger<ImageAcquisitionServiceImpl> logger;
        private readonly VisionMonitorService monitorService;
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly FileWriteQueue fileWriteQueue;
        public readonly IReadOnlyList<ICameraService> CameraServices;
        public readonly ConcurrentDictionary<ICameraService, CameraStreamingService> CameraStreamingServices = new();
        private readonly ConcurrentDictionary<ICameraService, TaskCompletionSource<bool>> cameraReadiness = new();
        private readonly ConcurrentDictionary<ICameraService, SemaphoreSlim> cameraSemaphores = new();
        private readonly ConcurrentDictionary<ICameraService, bool> cameraRetrying = new();
        private readonly ConcurrentDictionary<ICameraService, Channel<ImageFrame>> frameProcessingChannels = new();
        private readonly ConcurrentDictionary<ICameraService, Task> frameProcessingTasks = new();

        private GrpcChannel encoderChannel;
        private ImageAcquisitionClient encoderStreamClient;
        private Task encoderStreamProcessingTask;
        private Task healthCheckTask;

        private TelemetryTracker telemetry;
        private CancellationToken cancellationToken;

        private long encoderValue;

        public long EncoderValue
        {
            get => Interlocked.Read(ref encoderValue);
            private set => Interlocked.Exchange(ref encoderValue, value);
        }

        public int FileWriteQueueSize => fileWriteQueue?.GetQueueCountNoLock() ?? 0;

        private int imageAcquisitionMode;

        public ImageAcquisitionMode ImageAcquisitionMode
        {
            get => (ImageAcquisitionMode)Interlocked.CompareExchange(ref imageAcquisitionMode, 0, 0);
            set => Interlocked.Exchange(ref imageAcquisitionMode, (int)value);
        }

        public int TotalFrameProcessingChannelSize => frameProcessingChannels.Sum(kvp => kvp.Value.Reader.Count);

        // Telemetry properties
        public long TotalRequestsProcessed => telemetry.TotalRequestsProcessed;

        public long TotalCaptureRequests => telemetry.TotalCaptureRequests;
        public long TotalFramesProcessed => telemetry.TotalFramesProcessed;
        public long FailedRequests => telemetry.FailedRequests;
        public long DroppedFrames => telemetry.DroppedFrames;
        public int ActiveRequests => telemetry.ActiveRequests;
        public long TotalProcessingTimeMs => telemetry.TotalProcessingTimeMs;
        public DateTime StartTime => telemetry.StartTime;
        public double RequestsReceivedPerSecond => telemetry.RequestsReceivedPerSecond;
        public double RequestsSentPerSecond => telemetry.RequestsSentPerSecond;

        public bool IsEncoderStreamConnected => telemetry.IsEncoderStreamConnected;

        public long GetRequestsAddedToStream(int cameraId) => telemetry.GetRequestsAddedToStream(cameraId);

        public long GetRequestsReadFromStream(int cameraId) => telemetry.GetRequestsReadFromStream(cameraId);

        public double GetRequestsAddedPerSecond(int cameraId) => telemetry.GetRequestsAddedPerSecond(cameraId);

        public double GetRequestsReadPerSecond(int cameraId) => telemetry.GetRequestsReadPerSecond(cameraId);

        public long GetErrorsFromStream(int cameraId) => telemetry.GetErrorsFromStream(cameraId);

        public ConcurrentDictionary<int, int> CameraErrorCounts => telemetry.CameraErrorCounts;
        private static SemaphoreSlim incomingRequestThrottle;

        static ImageAcquisitionServiceImpl()
        {
            incomingRequestThrottle = new SemaphoreSlim(370, 3700);
        }

        public ImageAcquisitionServiceImpl(
            IEnumerable<ICameraService> cameraServices,
            ILogger<ImageAcquisitionServiceImpl> logger,
            ILogger<CameraStreamingService> cameraStreamLogger,
            VisionMonitorService visionMonitorService,
            IOptions<ServerOptions> iServerOptions,
            IHostApplicationLifetime applicationLifetime)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.CameraServices = cameraServices?.ToList() ?? throw new ArgumentNullException(nameof(cameraServices));
            this.ServerOptions = iServerOptions.Value;
            this.monitorService = visionMonitorService;
            var fileOperationsLogger = VisionLoggerFactory.CreateFileOperationsLogger();

            this.fileWriteQueue = new FileWriteQueue(cancellationTokenSource, ServerOptions.FileWriteQueue.MaxConcurrentWrites);
            ImageAcquisitionMode = ServerOptions.ImageAcquisitionMode;

            if (cameraServices.Count() > ServerOptions.MaxCameraConnections)
            {
                throw new ArgumentException($"VisionServer supports up to {ServerOptions.MaxCameraConnections} cameras.");
            }

            telemetry = new TelemetryTracker(ServerOptions.ErrorQueueCapacity, cancellationToken);
            foreach (var camera in this.CameraServices)
            {
                cameraReadiness[camera] = new TaskCompletionSource<bool>();
                CameraStreamingServices[camera] = new CameraStreamingService(
                    ServerOptions.IP,
                    camera.Configuration.EndpointPort,
                    camera.Configuration.Id,
                    cameraStreamLogger,
                    applicationLifetime,
                    telemetry);
                cameraSemaphores[camera] = new SemaphoreSlim(1, 1);
                frameProcessingChannels[camera] = System.Threading.Channels.Channel.CreateBounded<ImageFrame>(new BoundedChannelOptions(ServerOptions.FrameProcessingChannel.Capacity)
                {
                    FullMode = Enum.Parse<BoundedChannelFullMode>(ServerOptions.FrameProcessingChannel.FullMode)
                });
                camera.OnStreamingStopped += () => Camera_OnStreamingStopped(camera);
            }

            if (ServerOptions.IncomingConcurrentRequestLimit > 0)
            {
                incomingRequestThrottle = new SemaphoreSlim(ServerOptions.IncomingConcurrentRequestLimit, ServerOptions.IncomingConcurrentRequestLimit);
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("StartAsync cancelled before execution");
                return;
            }

            try
            {
                this.cancellationToken = cancellationToken;

                // Validate configuration
                if (CameraServices == null || !CameraServices.Any())
                {
                    throw new InvalidOperationException("No camera services configured");
                }

                logger.LogInformation("Starting Vision Server...");

                // Start health monitoring first
                healthCheckTask = HealthCheckRunAsync(cancellationToken);

                // Initialize frame processing tasks (these can run in parallel as they await frames)
                foreach (var camera in CameraServices)
                {
                    frameProcessingTasks[camera] = StartFrameProcessingAsync(camera, cancellationToken);
                }
                // Start processing the encoder stream
                encoderStreamProcessingTask = ProcessEncoderStreamAsync(cancellationToken);

                // Staggered serial startup for camera connections
                foreach (var camera in CameraServices)
                {
                    logger.LogInformation("Initiating connection for camera {Id}", camera.Configuration.Id);
                    await ConnectCameraAsync(camera); // Run serially
                }

                logger.LogInformation("Vision Server started. All cameras initiated serially.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start Vision Server");
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Stopping Vision Server...");
            cancellationTokenSource.Cancel();

            await fileWriteQueue.WaitForCompletionAsync();

            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var combinedCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token).Token;

            try
            {
                await Task.WhenAll(
                 frameProcessingTasks.Values.Select(t => t.DisposeWithTimeoutAsync(TimeSpan.FromSeconds(5), combinedCt))
                     .Concat(new[] { healthCheckTask.DisposeWithTimeoutAsync(TimeSpan.FromSeconds(5), combinedCt) })
                     .Concat(new[] { encoderStreamProcessingTask.DisposeWithTimeoutAsync(TimeSpan.FromSeconds(5), combinedCt) })
                     .Concat(CameraStreamingServices.Values.Select(s => s.ShutdownAsync())));
            }
            catch (TaskCanceledException)
            {
                logger.LogWarning("Some tasks did not complete within the timeout and were canceled.");
            }

            await Task.WhenAll(CameraServices.Select(async camera =>
            {
                await camera.StopStreamingAsync();
                await camera.DisconnectAsync();
                frameProcessingChannels[camera].Writer.TryComplete();
                logger.LogInformation($"Camera {camera.Configuration.Name} stopped successfully.");
            }));

            await encoderChannel.ShutdownAsync();
            logger.LogInformation("Image Acquisition Service stopped successfully.");

            logger.LogInformation("Vision Server stopped successfully.");
        }

        private async Task ProcessEncoderStreamAsync(CancellationToken cancellationToken)
        {
            var streamHostAddress = $"http://{ServerOptions.EncoderStreamSettings.EncoderStreamAddress}:{ServerOptions.EncoderStreamSettings.EncoderStreamPort}";
            logger.LogInformation($"Connecting to EncoderStream at {streamHostAddress}");
            encoderChannel = GrpcChannel.ForAddress(streamHostAddress);
            encoderStreamClient = new ImageAcquisitionClient(encoderChannel);

            var retryDelay = TimeSpan.FromMilliseconds(ServerOptions.EncoderStreamSettings.InitialRetryDelayMs);
            var maxRetryDelay = TimeSpan.FromMilliseconds(ServerOptions.EncoderStreamSettings.MaxRetryDelayMs);
            int attempt = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    logger.LogInformation("Connecting to VisionEncoderStream (attempt {Attempt})...", attempt + 1);
                    var call = encoderStreamClient.RetrieveImageStream(new RetrieveImageStreamRequest(), cancellationToken: cancellationToken);
                    attempt++;

                    bool firstMessageReceived = false; // Track if we've set the connected state
                    await foreach (var request in call.ResponseStream.ReadAllAsync(cancellationToken))
                    {
                        if (!firstMessageReceived)
                        {
                            telemetry.SetEncoderStreamConnected(true); // Record connection on first message
                            logger.LogInformation("Encoder stream connection confirmed.");
                            firstMessageReceived = true;
                        }

                        telemetry.IncrementRequestsProcessed();
                        TrackEncoderValue(request.EncoderValue);

                        if (!request.IsValid)
                        {
                            logger.LogDebug("Received invalid request: Encoder={Encoder}", request.EncoderValue);
                            continue;
                        }

                        telemetry.IncrementCaptureRequests();
                        await incomingRequestThrottle.WaitAsync(cancellationToken);
                        try
                        {
                            telemetry.IncrementActiveRequests();
                            var requestTimeStamp = DateTimeOffset.UtcNow;
                            await Task.WhenAll(CameraServices
                                .Where(c => c.IsConnected && c.IsStreaming)
                                .Select(camera => CaptureAndProcessFrameAsync(camera, request, requestTimeStamp)));
                        }
                        finally
                        {
                            incomingRequestThrottle.Release();
                            telemetry.DecrementActiveRequests();
                        }
                    }

                    logger.LogInformation("VisionEncoderStream ended cleanly.");
                    break;
                }
                catch (RpcException ex)
                {
                    logger.LogError($"Failed to connect to VisionEncoderStream (attempt {attempt}). Retrying in {retryDelay.TotalMilliseconds}ms...");
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(retryDelay, cancellationToken);
                        retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Encoder stream processing canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error in VisionEncoderStream processing (attempt {Attempt}). Retrying in {DelayMs}ms...", attempt, retryDelay.TotalMilliseconds);
                    await Task.Delay(retryDelay, cancellationToken);
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
                }
            }

            logger.LogInformation("Encoder stream processing task completed.");
        }

        public override async Task<GenericResultResponse> SetMode(SetModeRequest request, ServerCallContext context)
        {
            try
            {
                if (ImageAcquisitionMode == request.Mode)
                    return new GenericResultResponse { Success = true, Message = $"Image acquisition mode is already set to {request.Mode}." };

                ImageAcquisitionMode = request.Mode;
                logger.LogInformation("Image acquisition mode set to: {Mode}", ImageAcquisitionMode);
                return new GenericResultResponse { Success = true, Message = $"Image acquisition mode changed to {ImageAcquisitionMode}." };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error setting image acquisition mode.");
                return new GenericResultResponse { Success = false, Message = "An error occurred while setting the image acquisition mode." };
            }
        }

        private async Task ConnectCameraAsync(ICameraService camera)
        {
            Thread.CurrentThread.Name = $"CameraConnectThread_{camera.Configuration.Id}";
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RetryUntilConnectedAsync(camera);
                    logger.LogInformation("Camera {Id} connected and streaming.", camera.Configuration.Id);
                    cameraReadiness[camera].TrySetResult(true);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in camera {Id} connection loop.", camera.Configuration.Id);
                    telemetry.AddError(camera.Configuration.Id, $"Initial connection failed: {ex.Message}. Retrying...", logger);
                    await Task.Delay(TimeSpan.FromMilliseconds(ServerOptions.RetrySettings.InitialRetryDelayMs), cancellationToken);
                }
            }
        }

        private async Task CaptureAndProcessFrameAsync(ICameraService camera, RetrieveImageRequest request, DateTimeOffset requestTimestamp)
        {
            if (!request.IsValid) return;

            ImageFrame frame;
            await cameraSemaphores[camera].WaitAsync();
            try
            {
                frame = camera.GetFrameByEncoderValue(request.EncoderValue) ?? new();
            }
            finally
            {
                cameraSemaphores[camera].Release();
            }

            if (frame?.Data.Length > 0)
            {
                bool syncLogginActive = false; //dumb switch
                if (syncLogginActive)
                {
                    // Offload Synch logging to a separate task
                    _ = Task.Run(() =>
                {
                    // Calculate sync metrics
                    long deltaEncoder = request.EncoderValue - frame.EncoderValue;
                    double deltaTimeMs = (requestTimestamp - frame.Timestamp).TotalMilliseconds;

                    // Define out-of-sync conditions (adjust thresholds as needed)
                    bool isOutOfSync = deltaEncoder != 0 || Math.Abs(deltaTimeMs) > 50; // Example: > 50ms delay

                    // Log with a prefix for out-of-sync cases
                    logger.LogInformation(
                        "{SyncTag}Camera[{CameraId}] Frame captured: " +
                        "RequestEncoder={RequestEncoder}, RequestTimestamp={RequestTimestamp}, " +
                        "ServedEncoder={ServedEncoder}, ServedTimestamp={ServedTimestamp}, " +
                        "DeltaEncoder={DeltaEncoder}, DeltaTimeMs={DeltaTimeMs}, DataSize={DataSize}b",
                        isOutOfSync ? "[SYNC-ISSUE] " : "",  // Prefix for out-of-sync cases
                        camera.Configuration.Id,
                        request.EncoderValue, requestTimestamp,
                        frame.EncoderValue, frame.Timestamp,
                        deltaEncoder, deltaTimeMs,
                        frame.Data.Length
                    );
                });
                }
                if (!frameProcessingChannels[camera].Writer.TryWrite(frame))
                {
                    telemetry.IncrementDroppedFrames();
                    logger.LogWarning($"Frame dropped due to full queue for camera {camera.Configuration.Id}. Encoder: {request.EncoderValue} Timestamp: {requestTimestamp}");
                }
            }
            else
            {
                logger.LogWarning($"No Frame Data. Camera[{camera.Configuration.Id}] E: {request.EncoderValue}");
            }
        }

        private async Task StartFrameProcessingAsync(ICameraService camera, CancellationToken cancellationToken)
        {
            var loggingScope = $" ♣ Frame Processing Channel [Camera {camera.Configuration.Id}]";
            Thread.CurrentThread.Name = $"FrameProcessingThread_{camera.Configuration.Id}";
            logger.LogInformation($"{loggingScope} Awaiting work.");

            try
            {
                await foreach (var frame in frameProcessingChannels[camera].Reader.ReadAllAsync(cancellationToken))
                {
                    logger.LogInformation($"{loggingScope} received: {frame.Data.Length}b E:{frame.EncoderValue} | Frames: {TotalFramesProcessed}");
                    await ProcessFrameAsync(frame);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation($"{loggingScope} cancelled.");
            }
            finally
            {
                logger.LogInformation($"{loggingScope} stopped.");
            }
        }

        private async Task ProcessFrameAsync(ImageFrame frame)
        {
            var cameraService = CameraServices.FirstOrDefault(c => c.Configuration.Id == frame.CameraId);
            if (cameraService == null || !CameraStreamingServices.TryGetValue(cameraService, out var cameraStream)) return;

            try
            {
                var imageStrip = new ImageStrip
                {
                    CameraID = (int)frame.CameraId,
                    Image = Google.Protobuf.ByteString.CopyFrom(frame.Data),
                    EncoderValue = (int)frame.RequestEncoderValue
                };

                if (!ServerOptions.ImageProcessingClientSkip)
                {
                    if (cameraStream != null)
                    {
                        cameraStream.PostImageStrip(imageStrip);
                        logger.LogDebug("Posted image strip to stream for Camera ID {CameraId}", frame.CameraId);
                    }
                }

                if (ImageAcquisitionMode == ImageAcquisitionMode.Debug)
                {
                    var basePath = ServerOptions.DebugImageSettings.BasePath;
                    var folderPath = ServerOptions.DebugImageSettings.UseCameraIdSubfolder
                        ? Path.Combine(basePath, frame.CameraId.ToString())
                        : basePath;
                    folderPath = ServerOptions.DebugImageSettings.UseDateSubfolder
                        ? Path.Combine(folderPath, frame.Timestamp.ToString("yyyy-MM-dd"))
                        : folderPath;
                    var filePath = Path.Combine(folderPath, $"Frame_{frame.Timestamp:HH-mm-ss-ffffff}-{frame.Timestamp.Ticks}-E-{frame.EncoderValue}.raw");
                    fileWriteQueue.EnqueueWrite(frame.Data, filePath, frame.Width, frame.Height);
                }

                var processingTime = DateTime.UtcNow - frame.Timestamp;
                telemetry.AddProcessingTime(processingTime.Milliseconds);
                telemetry.IncrementFramesProcessed();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing frame for Camera ID {CameraId}.", frame.CameraId);
                telemetry.AddError(cameraService.Configuration.Id, $"Frame processing failed: {ex.Message}", logger);
            }
        }

        private void Camera_OnStreamingStopped(ICameraService camera)
        {
            logger.LogWarning("Camera_OnStreamingStopped Event Fired for {camera}", camera.Configuration.CameraAddress);
            _ = Task.Run(async () =>
            {
                if (cameraRetrying.TryGetValue(camera, out var isRetrying) && isRetrying) return;

                await cameraSemaphores[camera].WaitAsync(cancellationToken);
                try
                {
                    Thread.CurrentThread.Name = $"CameraRetryThread_{camera.Configuration.Id}";
                    cameraRetrying[camera] = true;

                    try
                    {
                        await camera.StopStreamingAsync();
                    }
                    catch (Exception ex)
                    {
                        telemetry.AddError(camera.Configuration.Id, $"Cleanup failed before retry: {ex.Message}", logger);
                    }

                    await RetryUntilConnectedAsync(camera);
                }
                finally
                {
                    cameraRetrying[camera] = false;
                    cameraSemaphores[camera].Release();
                }
            }, cancellationToken);
        }

        private void TrackEncoderValue(long encoderValue)
        {
            if (EncoderValue == encoderValue) return;
            EncoderValue = encoderValue;
            logger.LogInformation($" ? Encoder updated: {encoderValue}");
            foreach (var camera in CameraServices)
            {
                camera.EncoderValue = encoderValue;
            }
        }

        private async Task RetryUntilConnectedAsync(ICameraService cameraService)
        {
            var retryDelay = TimeSpan.FromMilliseconds(ServerOptions.RetrySettings.InitialRetryDelayMs);
            var maxRetryDelay = TimeSpan.FromMilliseconds(ServerOptions.RetrySettings.MaxRetryDelayMs);

            while (!cameraService.IsConnected || !cameraService.IsStreaming)
            {
                try
                {
                    if (!cameraService.IsConnected && await cameraService.ConnectAsync())
                    {
                        logger.LogDebug("Connected to camera: {IP}", cameraService.Configuration.CameraAddress);
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                    else if (!cameraService.IsConnected)
                    {
                        await cameraService.DisconnectAsync();
                        telemetry.AddError(cameraService.Configuration.Id, cameraService.LastError ?? "Unknown connection error", logger);
                    }

                    if (cameraService.IsConnected && !cameraService.IsStreaming && await cameraService.StartStreamingAsync())
                    {
                        logger.LogInformation("Camera is streaming: {IP}", cameraService.Configuration.CameraAddress);
                        cameraReadiness[cameraService].TrySetResult(true);
                        return;
                    }
                    else if (cameraService.IsConnected)
                    {
                        telemetry.AddError(cameraService.Configuration.Id, "Failed to start streaming", logger);
                    }
                }
                catch (Exception ex)
                {
                    telemetry.AddError(cameraService.Configuration.Id, $"Unexpected Error: {ex.Message}", logger);
                }

                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
            }
        }

        public void AddError(ICameraService camera, string error) => telemetry.AddError(camera.Configuration.Id, error, logger);

        private async Task HealthCheckRunAsync(CancellationToken cancellationToken)
        {
            Thread.CurrentThread.Name = "HealthCheckThread";
            if (!ServerOptions.CameraHealth.EnableLogging) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                PrintStatus();
                await Task.Delay(TimeSpan.FromMilliseconds(ServerOptions.CameraHealth.PollIntervalMs), cancellationToken);
            }
        }

        public void PrintStatus() => monitorService?.PrintServerHealth(cancellationToken, this, ServerOptions.CameraHealth.RefreshConsoleOnUpdate);

        public void Dispose()
        {
            cancellationTokenSource.Dispose();
            incomingRequestThrottle.Dispose();
            foreach (var semaphore in cameraSemaphores.Values) semaphore.Dispose();
            foreach (var channel in frameProcessingChannels.Values) channel.Writer.TryComplete();
            monitorService?.Dispose();
            encoderChannel.Dispose();
        }
    }
}
