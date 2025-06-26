using System.Diagnostics;
using IA.Vision.App.Interfaces;
using IA.Vision.App.Models;
using IA.Vision.App.Utils;
using GenICam;
using GigeVision.Core.Enums;
using GigeVision.Core.Interfaces;
using GigeVision.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IA.Vision.App.Services
{
    public partial class GigECameraService : BackgroundService, ICameraService
    {
        // Constants and readonly fields
        private readonly int syncRetries = 3;

        private readonly int frameWaitTimeoutMs = 3000;
        private readonly Stopwatch stopwatch = new();
        private readonly ILogger<GigECameraService> logger;
        private readonly int streamingTimeoutMs = 10000; //overriden by config

        // Private fields
        private Gvcp? gvcp;

        private ICamera? camera;
        private volatile bool isStreaming;
        private volatile bool isConnected;

        // private volatile bool frameReceived = false;
        private DateTimeOffset? lastFrameReceivedTime;

        private DateTimeOffset streamStartTime;
        private CancellationTokenSource? streamingTimeoutCancellationTokenSource;
        private Task? streamingTimeoutTask;
        private readonly object eventLock = new object();

        // Properties
        public CameraConfiguration Configuration { get; }

        public long EncoderValue { get; set; }
        public List<ICategory>? Categories { get; set; }
        public RingBuffer FrameBuffer { get; set; }
        public long TotalFramesProcessed = 0;
        public bool IsConnected => isConnected;

        public bool IsStreaming => isStreaming
            && lastFrameReceivedTime.HasValue
            && (DateTimeOffset.UtcNow - lastFrameReceivedTime.Value) <= TimeSpan.FromMilliseconds(streamingTimeoutMs * 2); // Check time since last frame

        public ServerOptions ServerOptions { get; }
        public StreamingState StreamingState { get; private set; } = StreamingState.NotStarted;
        public double Fps => IsStreaming ? FrameBuffer.Fps : 0;
        public long FrameCount => FrameBuffer.FrameCount;
        public string LastError { get; private set; }

        // Events
        public event Action? OnStreamingStopped;

        public GigECameraService(IOptions<CameraConfiguration> config, ILogger<GigECameraService> logger, IOptions<ServerOptions> iServerOptions)
        {
            this.logger = logger;
            this.Configuration = config.Value;
            this.ServerOptions = iServerOptions.Value;
            FrameBuffer = new RingBuffer((int)ServerOptions.BufferSize);
            streamingTimeoutMs = ServerOptions.StreamMonitor.PollIntervalMs;
        }

        // Camera connection logic
        public async Task<bool> ConnectAsync()
        {
            try
            {
                LogWithCameraIp(LogLevel.Information, "Attempting to connect to camera.");
                LogWithCameraIp(LogLevel.Information, Configuration.GetConfigurationSummary());

                if (!await DiagnoseNetworkSupportAsync())
                {
                    LogWithCameraIp(LogLevel.Error, $"Failed to ping jumboframe to Camera[{Configuration.Id}]:{Configuration.ReceiverAddress}. Check your device connection and network MTU limit settings.");
                    return false;
                }

                gvcp = new Gvcp(Configuration.CameraAddress);
                await gvcp.ReadXmlFileAsync();

                if (!gvcp.IsXmlFileLoaded)
                {
                    LogWithCameraIp(LogLevel.Error, "Camera XML configuration not loaded.");
                    return false;
                }
                LogWithCameraIp(LogLevel.Debug, "[ConnectAsync] XML Configuration Loaded");
                UnhookFrameReadyEvent();
                camera = new Camera(gvcp)
                {
                    IP = Configuration.CameraAddress,
                    PortRx = (int)Configuration.ReceiverPort,
                    RxIP = Configuration.ReceiverAddress,
                    Width = Configuration.Width,
                    Height = Configuration.Height,
                    PixelFormat = Configuration.PixelFormat,
                    FrameReady = HandleFrameReady!
                };

                if (camera == null ||
                    camera.IP != Configuration.CameraAddress ||
                    camera.PortRx != Configuration.ReceiverPort ||
                    camera.RxIP != Configuration.ReceiverAddress ||
                    camera.Width != Configuration.Width ||
                    camera.PixelFormat != Configuration.PixelFormat ||
                    camera.FrameReady == null)
                {
                    LogWithCameraIp(LogLevel.Error, "Failed to initialize camera or camera is not ready.");
                    return false;
                }

                var isSynched = await camera.SyncParameters(syncRetries);
                if (!isSynched)
                {
                    LogWithCameraIp(LogLevel.Error, "Failed to synchronize camera parameters.");
                    return false;
                }
                LogWithCameraIp(LogLevel.Debug, "synchronized camera parameters.");

                var cameraStatus = await gvcp.CheckCameraStatusAsync(Configuration.CameraAddress);
                if (cameraStatus == CameraStatus.UnAvailable)
                {
                    LogWithCameraIp(LogLevel.Error, $"Camera status is {cameraStatus}. Not ready.");
                    return false;
                }
                LogWithCameraIp(LogLevel.Debug, "Camera Status Ok: " + cameraStatus);

                await SetImageDimensionsAsync(Configuration.Width, Configuration.Height);
                LogWithCameraIp(LogLevel.Debug, $"Image Dimensions Set: width = {Configuration.Width}, height = {Configuration.Height}");

                LogWithCameraIp(LogLevel.Information, $"Camera initialized and connected successfully to IP: {Configuration.CameraAddress}");
                LogWithCameraIp(LogLevel.Information, camera.GetGigECameraStatus());

                isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                LogWithCameraIp(LogLevel.Error, $"Error connecting to camera: {ex.Message}");
                isConnected = false;
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            streamingTimeoutCancellationTokenSource?.Cancel();
            streamingTimeoutCancellationTokenSource?.Dispose();
            streamingTimeoutCancellationTokenSource = null;
            streamingTimeoutTask = null;
            gvcp = null;

            isStreaming = false;
            isConnected = false;
            LogWithCameraIp(LogLevel.Information, "Camera disconnected.");
        }

        public async Task<bool> StartStreamingAsync()
        {
            if (IsStreaming) return true;
            if (camera == null) return false;

            LogWithCameraIp(LogLevel.Debug, "Starting streaming.");
            StreamingState = StreamingState.Starting;

            try
            {
                stopwatch.Start();

                isStreaming = await camera.StartStreamAsync(Configuration.ReceiverAddress, (int)Configuration.ReceiverPort).ConfigureAwait(false);
                await Task.Delay(gvcp.ControlSocket.Client.ReceiveTimeout);
                if (!isStreaming)
                {
                    StreamingState = StreamingState.Failed;
                    return false;
                }

                lastFrameReceivedTime = null; // Reset to ensure we wait for a new frame

                // Wait for the first frame
                var waitStart = DateTimeOffset.UtcNow;
                LogWithCameraIp(LogLevel.Debug, "Waiting for initial frame...");
                while (DateTimeOffset.UtcNow - waitStart < TimeSpan.FromMilliseconds(frameWaitTimeoutMs))
                {
                    if (lastFrameReceivedTime.HasValue && lastFrameReceivedTime.Value >= streamStartTime)
                    {
                        LogWithCameraIp(LogLevel.Information, $"First frame received in {(DateTimeOffset.UtcNow - waitStart).Seconds} seconds.");
                        StreamingState = StreamingState.Active;
                        streamStartTime = lastFrameReceivedTime.Value;
                        isStreaming = true;
                        InitializeStreamingTimeoutMonitor();
                        return true; // Frame received, streaming confirmed
                    }
                    await Task.Delay(frameWaitTimeoutMs / 3); // Brief delay to avoid tight loop
                }

                // If we get here, no frame was received within 3 seconds
                LogWithCameraIp(LogLevel.Warning, $"No frame received within {frameWaitTimeoutMs} seconds, marking streaming as failed.");
                StreamingState = StreamingState.Failed;
                isStreaming = false;
                return false;
            }
            catch (Exception ex)
            {
                StreamingState = StreamingState.Failed;
                LogWithCameraIp(LogLevel.Error, $"Error starting streaming: {ex}");
                return false;
            }
        }

        public async Task StopStreamingAsync()
        {
            if (!isStreaming) return;
            LogWithCameraIp(LogLevel.Information, "Stopping streaming.");

            if (camera != null && gvcp != null)
            {
                UnhookFrameReadyEvent();
                //disable the heartbeat thread as to not interfere with the control socket shutting down
                await gvcp.LeaveControl().ConfigureAwait(false);
                //wait for the heartbeat to pass
                await Task.Delay(TimeSpan.FromSeconds(2));
                //now stop the stream
                await camera.StopStream().ConfigureAwait(false);
            }

            stopwatch.Stop();

            isStreaming = false;
            StreamingState = StreamingState.NotStarted;
            streamingTimeoutCancellationTokenSource?.Cancel();

            if (streamingTimeoutTask != null)
                await streamingTimeoutTask;

            OnStreamingStopped?.Invoke();
            LogWithCameraIp(LogLevel.Information, "Streaming stopped.");
        }

        private async Task<bool> SetImageDimensionsAsync(uint width, uint height)
        {
            if (camera == null)
            {
                LogWithCameraIp(LogLevel.Error, "Camera is null, cannot set resolution.");
                return false;
            }

            try
            {
                await camera.SetResolutionAsync(width, height);

                if (camera.Width != width || camera.Height != height)
                {
                    throw new ApplicationException($"Camera resolution is not set correctly. Actual: {camera.Width}x{camera.Height}, Expected: {width}x{height}.");
                }

                LogWithCameraIp(LogLevel.Debug, $"Resolution set to: Width = {width}, Height = {height}");
            }
            catch (Exception ex)
            {
                LogWithCameraIp(LogLevel.Error, $"Error setting image resolution. {ex.Message}");
                throw;
            }
            return true;
        }

        public async Task<bool> DiagnoseNetworkSupportAsync()
        {
            if (!Configuration.EnforceJumboFrames) return true;

            var ipAddress = Configuration.ReceiverAddress; //frames are streamed on the receiver address

            var canPingJumbo = await new NetworkUtils(logger).CanPingJumboFrameAsync(ipAddress);

            if (!canPingJumbo)
            {
                LogWithCameraIp(LogLevel.Information, "Unable to ping jumbo frame without fragmentation. Adjust your network MTU settings.");
                return false;
            }

            LogWithCameraIp(LogLevel.Information, "Network supports jumbo frames.");
            return true;
        }

        // FrameReady event handler to record metrics without intensive calculations
        public void HandleFrameReady(object? sender, byte[] frame)
        {
            if (!isStreaming || frame == null) return;

            // Store the frame in the buffer for later processing
            OnFrameReady(frame);
        }

        private void OnFrameReady(byte[] frame)
        {
            if (!isStreaming || frame == null) return;

            // Start the stopwatch to measure how long the frame processing takes
            stopwatch.Restart();

            // Create an ImageFrame object using the raw byte data for the frame
            var imageFrame = new ImageFrame
            {
                Data = frame,
                Width = (int)camera.Width,
                Height = (int)camera.Height,
                CameraId = Configuration.Id,
                Timestamp = DateTimeOffset.UtcNow,
                EncoderValue = this.EncoderValue,
                IsRequestValid = true
            };

            TotalFramesProcessed++;
            lastFrameReceivedTime = imageFrame.Timestamp;

            //Buffer Operation
            EnqueueFrame(imageFrame);
        }

        #region Buffer Locking operations

        private void EnqueueFrame(ImageFrame imageFrame) => FrameBuffer.Enqueue(imageFrame);

        public ImageFrame? GetFrameByEncoderValue(long encoderValue) => FrameBuffer.GetFrameByEncoderValue(encoderValue);

        #endregion Buffer Locking operations

        private void InitializeStreamingTimeoutMonitor()
        {
            var loggingScope = $" ♥ Stream Monitor - thread:{Thread.CurrentThread.ManagedThreadId} ";
            LogWithCameraIp(LogLevel.Information, $"{loggingScope} Listening for frames...");

            streamingTimeoutCancellationTokenSource?.Cancel();
            streamingTimeoutCancellationTokenSource = new CancellationTokenSource();
            var firstFrameReceived = false;

            // Start a new timeout task that will check if frames are received every {streamingTimeoutSecs} seconds
            streamingTimeoutTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name = $"StreamTimeoutMonitor_Camera{Configuration.Id}";
                while (!streamingTimeoutCancellationTokenSource.Token.IsCancellationRequested)
                {
                    // Wait for {streamingTimeoutSecs} seconds (this will check if a frame was received)
                    await Task.Delay(streamingTimeoutMs);
                    if (!isStreaming) continue; // Exit if streaming is no longer active

                    DateTimeOffset startWindow = lastFrameReceivedTime ?? streamStartTime;
                    var timeSinceLastFrame = DateTimeOffset.UtcNow - startWindow;
                    if (timeSinceLastFrame > TimeSpan.FromMilliseconds(streamingTimeoutMs))
                    {
                        // Timeout occurred (no frame received in the last {streamingTimeoutSecs} seconds)
                        logger.LogWarning($"{loggingScope} Timeout: No frame received in the last {streamingTimeoutMs} ms.");
                        isStreaming = false;
                        StreamingState = StreamingState.Failed;
                        OnStreamingStopped?.Invoke();
                        // Task completed - stream polling will reestablish on reconnection
                        return;
                    }

                    if (startWindow == streamStartTime)
                    {
                        //have not received a frame yet
                        LogWithCameraIp(LogLevel.Information, $"{loggingScope} Awaiting first frame from camera {Configuration.CameraAddress}.");
                    }
                    else
                    {
                        //streaming
                        isStreaming = true;

                        if (ServerOptions.StreamMonitor.EnableLogging)
                        {
                            LogWithCameraIp(LogLevel.Information, $"{loggingScope} Last frame received: {lastFrameReceivedTime.Value:HH:mm:ss.ffffff}, FPS: {Fps:F2}");
                        }

                        if (!firstFrameReceived)
                        {
                            firstFrameReceived = true;
                            LogWithCameraIp(LogLevel.Information, this.GetCameraServiceDetails());
                        }
                    }
                }
            });

            LogWithCameraIp(LogLevel.Debug, "Streaming is now active.");
        }

        private void UnhookFrameReadyEvent()
        {
            lock (eventLock)
            {
                if (camera != null && camera.FrameReady != null)
                {
                    camera.FrameReady -= HandleFrameReady;
                    LogWithCameraIp(LogLevel.Debug, "FrameReady event handler successfully unhooked.");
                }
            }
        }

        private void LogWithCameraIp(LogLevel level, string message, params object?[] args)
        {
            var formattedMessage = $"[{Configuration.CameraAddress}] {message}";
            logger.Log(level, formattedMessage, args);
            if (level == LogLevel.Error)
            {
                LastError = string.Format(formattedMessage, args);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //Managed by hosting service
        }
    }
}
