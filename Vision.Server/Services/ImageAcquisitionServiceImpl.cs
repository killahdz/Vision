// --------------------------------------------------------------------------------------
// Copyright 2025 Daniel Kereama
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// Author      : Daniel Kereama
// Created     : 2025-06-27
// --------------------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
    /// <summary>
    /// Implements the image acquisition service, managing camera connections, frame processing, and encoder stream communication.
    /// </summary>
    public class ImageAcquisitionServiceImpl : ImageAcquisitionBase, IHostedService, IDisposable
    {
        private readonly ServerOptions _serverOptions;
        public ServerOptions ServerOptions => _serverOptions;
        private readonly ILogger<ImageAcquisitionServiceImpl> _logger;
        private readonly VisionMonitorService _monitorService;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly FileWriteQueue _fileWriteQueue;
        private readonly IReadOnlyList<ICameraService> _cameraServices;
        public IReadOnlyList<ICameraService> CameraServices => _cameraServices;
        private readonly ConcurrentDictionary<ICameraService, CameraStreamingService> _cameraStreamingServices = new();
        public ConcurrentDictionary<ICameraService, CameraStreamingService> CameraStreamingServices => _cameraStreamingServices;
        private readonly ConcurrentDictionary<ICameraService, TaskCompletionSource<bool>> _cameraReadiness = new();
        private readonly ConcurrentDictionary<ICameraService, SemaphoreSlim> _cameraSemaphores = new();
        private readonly ConcurrentDictionary<ICameraService, bool> _cameraRetrying = new();
        private readonly ConcurrentDictionary<ICameraService, Channel<ImageFrame>> _frameProcessingChannels = new();
        private readonly ConcurrentDictionary<ICameraService, Task> _frameProcessingTasks = new();
        private GrpcChannel _encoderChannel;
        private ImageAcquisitionClient _encoderStreamClient;
        private Task _encoderStreamProcessingTask;
        private Task _healthCheckTask;
        private TelemetryTracker _telemetry;
        private CancellationToken _cancellationToken;
        private long _encoderValue;
        private int _imageAcquisitionMode;
        private static SemaphoreSlim _incomingRequestThrottle;
        private int _disposed;

        /// <summary>
        /// Gets the current encoder value in a thread-safe manner.
        /// </summary>
        public long EncoderValue
        {
            get => Interlocked.Read(ref _encoderValue);
            private set => Interlocked.Exchange(ref _encoderValue, value);
        }

        /// <summary>
        /// Gets the current size of the file write queue.
        /// </summary>
        public int FileWriteQueueSize => _fileWriteQueue?.GetQueueCountNoLock() ?? 0;

        /// <summary>
        /// Gets or sets the image acquisition mode in a thread-safe manner.
        /// </summary>
        public ImageAcquisitionMode ImageAcquisitionMode
        {
            get => (ImageAcquisitionMode)Interlocked.CompareExchange(ref _imageAcquisitionMode, 0, 0);
            set => Interlocked.Exchange(ref _imageAcquisitionMode, (int)value);
        }

        /// <summary>
        /// Gets the total number of frames in all frame processing channels.
        /// </summary>
        public int TotalFrameProcessingChannelSize => _frameProcessingChannels.Sum(kvp => kvp.Value.Reader.Count);

        // Telemetry properties
        public long TotalRequestsProcessed => _telemetry.TotalRequestsProcessed;
        public long TotalCaptureRequests => _telemetry.TotalCaptureRequests;
        public long TotalFramesProcessed => _telemetry.TotalFramesProcessed;
        public long FailedRequests => _telemetry.FailedRequests;
        public long DroppedFrames => _telemetry.DroppedFrames;
        public int ActiveRequests => _telemetry.ActiveRequests;
        public long TotalProcessingTimeMs => _telemetry.TotalProcessingTimeMs;
        public DateTime StartTime => _telemetry.StartTime;
        public double RequestsReceivedPerSecond => _telemetry.RequestsReceivedPerSecond;
        public double RequestsSentPerSecond => _telemetry.RequestsSentPerSecond;
        public bool IsEncoderStreamConnected => _telemetry.IsEncoderStreamConnected;

        /// <summary>
        /// Gets the number of requests added to the stream for a specific camera.
        /// </summary>
        public long GetRequestsAddedToStream(int cameraId) => _telemetry.GetRequestsAddedToStream(cameraId);

        /// <summary>
        /// Gets the number of requests read from the stream for a specific camera.
        /// </summary>
        public long GetRequestsReadFromStream(int cameraId) => _telemetry.GetRequestsReadFromStream(cameraId);

        /// <summary>
        /// Gets the rate of requests added per second for a specific camera.
        /// </summary>
        public double GetRequestsAddedPerSecond(int cameraId) => _telemetry.GetRequestsAddedPerSecond(cameraId);

        /// <summary>
        /// Gets the rate of requests read per second for a specific camera.
        /// </summary>
        public double GetRequestsReadPerSecond(int cameraId) => _telemetry.GetRequestsReadPerSecond(cameraId);

        /// <summary>
        /// Gets the number of errors from the stream for a specific camera.
        /// </summary>
        public long GetErrorsFromStream(int cameraId) => _telemetry.GetErrorsFromStream(cameraId);

        public ConcurrentDictionary<int, int> CameraErrorCounts => _telemetry.CameraErrorCounts;

        static ImageAcquisitionServiceImpl()
        {
            _incomingRequestThrottle = new SemaphoreSlim(370, 3700);
        }

        /// <summary>
        /// Initializes a new instance of the image acquisition service.
        /// </summary>
        /// <param name="cameraServices">The collection of camera services.</param>
        /// <param name="logger">The logger for the service.</param>
        /// <param name="cameraStreamLogger">The logger for camera streaming.</param>
        /// <param name="visionMonitorService">The vision monitor service.</param>
        /// <param name="serverOptions">The server configuration options.</param>
        /// <param name="applicationLifetime">The application lifetime for shutdown handling.</param>
        public ImageAcquisitionServiceImpl(
            IEnumerable<ICameraService> cameraServices,
            ILogger<ImageAcquisitionServiceImpl> logger,
            ILogger<CameraStreamingService> cameraStreamLogger,
            VisionMonitorService visionMonitorService,
            IOptions<ServerOptions> serverOptions,
            IHostApplicationLifetime applicationLifetime)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cameraServices = cameraServices?.ToList() ?? throw new ArgumentNullException(nameof(cameraServices));
            _serverOptions = serverOptions?.Value ?? throw new ArgumentNullException(nameof(serverOptions));
            _monitorService = visionMonitorService ?? throw new ArgumentNullException(nameof(visionMonitorService));

            var fileOperationsLogger = VisionLoggerFactory.CreateFileOperationsLogger();
            _fileWriteQueue = new FileWriteQueue(_cancellationTokenSource, _serverOptions.FileWriteQueue.MaxConcurrentWrites);
            ImageAcquisitionMode = _serverOptions.ImageAcquisitionMode;

            if (_cameraServices.Count > _serverOptions.MaxCameraConnections)
            {
                throw new ArgumentException($"VisionServer supports up to {_serverOptions.MaxCameraConnections} cameras.");
            }

            _telemetry = new TelemetryTracker(_serverOptions.ErrorQueueCapacity, _cancellationToken, _logger);
            foreach (var camera in _cameraServices)
            {
                _cameraReadiness[camera] = new TaskCompletionSource<bool>();
                _cameraStreamingServices[camera] = new CameraStreamingService(
                    _serverOptions.IP,
                    camera.Configuration.EndpointPort,
                    camera.Configuration.Id,
                    cameraStreamLogger,
                    applicationLifetime,
                    _telemetry);
                _cameraSemaphores[camera] = new SemaphoreSlim(1, 1);
                _frameProcessingChannels[camera] = System.Threading.Channels.Channel.CreateBounded<ImageFrame>(new BoundedChannelOptions(_serverOptions.FrameProcessingChannel.Capacity)
                {
                    FullMode = Enum.Parse<BoundedChannelFullMode>(_serverOptions.FrameProcessingChannel.FullMode)
                });
                camera.OnStreamingStopped += () => Camera_OnStreamingStopped(camera);
            }

            if (_serverOptions.IncomingConcurrentRequestLimit > 0)
            {
                _incomingRequestThrottle.Dispose();
                _incomingRequestThrottle = new SemaphoreSlim(_serverOptions.IncomingConcurrentRequestLimit, _serverOptions.IncomingConcurrentRequestLimit);
            }
        }

        /// <summary>
        /// Starts the image acquisition service, initializing camera connections and processing tasks.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>A task representing the asynchronous start operation.</returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("StartAsync cancelled before execution.");
                return;
            }

            try
            {
                _cancellationToken = cancellationToken;

                if (_cameraServices == null || !_cameraServices.Any())
                {
                    throw new InvalidOperationException("No camera services configured.");
                }

                _logger.LogInformation("Starting Vision Server...");

                _healthCheckTask = HealthCheckRunAsync(cancellationToken);

                foreach (var camera in _cameraServices)
                {
                    _frameProcessingTasks[camera] = StartFrameProcessingAsync(camera, cancellationToken);
                }

                _encoderStreamProcessingTask = ProcessEncoderStreamAsync(cancellationToken);

                foreach (var camera in _cameraServices)
                {
                    _logger.LogInformation("Initiating connection for camera {Id}.", camera.Configuration.Id);
                    await ConnectCameraAsync(camera);
                }

                _logger.LogInformation("Vision Server started. All cameras initiated.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Vision Server.");
                throw;
            }
        }

        /// <summary>
        /// Stops the image acquisition service, shutting down tasks and connections.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>A task representing the asynchronous stop operation.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Vision Server...");
            _cancellationTokenSource.Cancel();

            await _fileWriteQueue.WaitForCompletionAsync();

            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var combinedCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token).Token;

            try
            {
                await Task.WhenAll(
                    _frameProcessingTasks.Values.Select(t => t.DisposeWithTimeoutAsync(TimeSpan.FromSeconds(5), combinedCt, _logger))
                        .Concat(new[] { _healthCheckTask.DisposeWithTimeoutAsync(TimeSpan.FromSeconds(5), combinedCt, _logger) })
                        .Concat(new[] { _encoderStreamProcessingTask.DisposeWithTimeoutAsync(TimeSpan.FromSeconds(5), combinedCt, _logger) })
                        .Concat(_cameraStreamingServices.Values.Select(s => s.ShutdownAsync())));
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Some tasks did not complete within the timeout and were canceled.");
            }

            await Task.WhenAll(_cameraServices.Select(async camera =>
            {
                await camera.StopStreamingAsync();
                await camera.DisconnectAsync();
                _frameProcessingChannels[camera].Writer.TryComplete();
                _logger.LogInformation("Camera {Name} stopped successfully.", camera.Configuration.Name);
            }));

            if (_encoderChannel != null)
            {
                await _encoderChannel.ShutdownAsync();
            }
            _logger.LogInformation("Image Acquisition Service stopped successfully.");
        }

        /// <summary>
        /// Processes the encoder stream, handling incoming image requests.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>A task representing the asynchronous processing operation.</returns>
        private async Task ProcessEncoderStreamAsync(CancellationToken cancellationToken)
        {
            var streamHostAddress = $"http://{_serverOptions.EncoderStreamSettings.EncoderStreamAddress}:{_serverOptions.EncoderStreamSettings.EncoderStreamPort}";
            _logger.LogInformation("Connecting to EncoderStream at {Address}.", streamHostAddress);
            _encoderChannel = GrpcChannel.ForAddress(streamHostAddress);
            _encoderStreamClient = new ImageAcquisitionClient(_encoderChannel);

            var retryDelay = TimeSpan.FromMilliseconds(_serverOptions.EncoderStreamSettings.InitialRetryDelayMs);
            var maxRetryDelay = TimeSpan.FromMilliseconds(_serverOptions.EncoderStreamSettings.MaxRetryDelayMs);
            int attempt = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Connecting to VisionEncoderStream (attempt {Attempt})...", attempt + 1);
                    var call = _encoderStreamClient.RetrieveImageStream(new RetrieveImageStreamRequest(), cancellationToken: cancellationToken);
                    attempt++;

                    bool firstMessageReceived = false;
                    await foreach (var request in call.ResponseStream.ReadAllAsync(cancellationToken))
                    {
                        if (!firstMessageReceived)
                        {
                            _telemetry.SetEncoderStreamConnected(true);
                            _logger.LogInformation("Encoder stream connection confirmed.");
                            firstMessageReceived = true;
                        }

                        _telemetry.IncrementRequestsProcessed();
                        TrackEncoderValue(request.EncoderValue);

                        if (!request.IsValid)
                        {
                            _logger.LogDebug("Received invalid request: Encoder={Encoder}.", request.EncoderValue);
                            continue;
                        }

                        _telemetry.IncrementCaptureRequests();
                        await _incomingRequestThrottle.WaitAsync(cancellationToken);
                        try
                        {
                            _telemetry.IncrementActiveRequests();
                            var requestTimeStamp = DateTimeOffset.UtcNow;
                            await Task.WhenAll(_cameraServices
                                .Where(c => c.IsConnected && c.IsStreaming)
                                .Select(camera => CaptureAndProcessFrameAsync(camera, request, requestTimeStamp)));
                        }
                        finally
                        {
                            _incomingRequestThrottle.Release();
                            _telemetry.DecrementActiveRequests();
                        }
                    }

                    _logger.LogInformation("VisionEncoderStream ended cleanly.");
                    break;
                }
                catch (RpcException ex)
                {
                    _logger.LogError(ex, "Failed to connect to VisionEncoderStream (attempt {Attempt}). Retrying in {DelayMs}ms...", attempt, retryDelay.TotalMilliseconds);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(retryDelay, cancellationToken);
                        retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Encoder stream processing canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in VisionEncoderStream processing (attempt {Attempt}). Retrying in {DelayMs}ms...", attempt, retryDelay.TotalMilliseconds);
                    await Task.Delay(retryDelay, cancellationToken);
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
                }
            }

            _logger.LogInformation("Encoder stream processing task completed.");
        }

        /// <summary>
        /// Sets the image acquisition mode via gRPC request.
        /// </summary>
        /// <param name="request">The mode change request.</param>
        /// <param name="context">The server call context.</param>
        /// <returns>A response indicating the success or failure of the mode change.</returns>
        public override async Task<GenericResultResponse> SetMode(SetModeRequest request, ServerCallContext context)
        {
            try
            {
                if (ImageAcquisitionMode == request.Mode)
                {
                    return new GenericResultResponse { Success = true, Message = $"Image acquisition mode is already set to {request.Mode}." };
                }

                ImageAcquisitionMode = request.Mode;
                _logger.LogInformation("Image acquisition mode set to: {Mode}.", ImageAcquisitionMode);
                return new GenericResultResponse { Success = true, Message = $"Image acquisition mode changed to {ImageAcquisitionMode}." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting image acquisition mode.");
                return new GenericResultResponse { Success = false, Message = "An error occurred while setting the image acquisition mode." };
            }
        }

        /// <summary>
        /// Connects a camera service with retry logic until successful.
        /// </summary>
        /// <param name="camera">The camera service to connect.</param>
        /// <returns>A task representing the asynchronous connection operation.</returns>
        private async Task ConnectCameraAsync(ICameraService camera)
        {
            Thread.CurrentThread.Name = $"CameraConnectThread_{camera.Configuration.Id}";
            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RetryUntilConnectedAsync(camera);
                    _logger.LogInformation("Camera {Id} connected and streaming.", camera.Configuration.Id);
                    _cameraReadiness[camera].TrySetResult(true);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in camera {Id} connection loop.", camera.Configuration.Id);
                    _telemetry.AddError(camera.Configuration.Id, $"Initial connection failed: {ex.Message}. Retrying...", _logger);
                    await Task.Delay(TimeSpan.FromMilliseconds(_serverOptions.RetrySettings.InitialRetryDelayMs), _cancellationToken);
                }
            }
        }

        /// <summary>
        /// Captures and processes a frame from a camera based on an encoder request.
        /// </summary>
        /// <param name="camera">The camera service to capture from.</param>
        /// <param name="request">The image request containing encoder value.</param>
        /// <param name="requestTimestamp">The timestamp of the request.</param>
        /// <returns>A task representing the asynchronous capture and processing operation.</returns>
        private async Task CaptureAndProcessFrameAsync(ICameraService camera, RetrieveImageRequest request, DateTimeOffset requestTimestamp)
        {
            if (!request.IsValid) return;

            ImageFrame frame;
            await _cameraSemaphores[camera].WaitAsync();
            try
            {
                frame = camera.GetFrameByEncoderValue(request.EncoderValue) ?? new();
            }
            finally
            {
                _cameraSemaphores[camera].Release();
            }

            if (frame?.Data.Length > 0)
            {
                bool syncLoggingActive = false;
                if (syncLoggingActive)
                {
                    _ = Task.Run(() =>
                    {
                        long deltaEncoder = request.EncoderValue - frame.EncoderValue;
                        double deltaTimeMs = (requestTimestamp - frame.Timestamp).TotalMilliseconds;
                        bool isOutOfSync = deltaEncoder != 0 || Math.Abs(deltaTimeMs) > 50;

                        _logger.LogInformation(
                            "{SyncTag}Camera[{CameraId}] Frame captured: RequestEncoder={RequestEncoder}, RequestTimestamp={RequestTimestamp}, ServedEncoder={ServedEncoder}, ServedTimestamp={ServedTimestamp}, DeltaEncoder={DeltaEncoder}, DeltaTimeMs={DeltaTimeMs}, DataSize={DataSize}b",
                            isOutOfSync ? "[SYNC-ISSUE] " : "",
                            camera.Configuration.Id,
                            request.EncoderValue, requestTimestamp,
                            frame.EncoderValue, frame.Timestamp,
                            deltaEncoder, deltaTimeMs,
                            frame.Data.Length
                        );
                    });
                }

                if (!_frameProcessingChannels[camera].Writer.TryWrite(frame))
                {
                    _telemetry.IncrementDroppedFrames();
                    _logger.LogWarning("Frame dropped due to full queue for camera {Id}. Encoder: {Encoder} Timestamp: {Timestamp}.", camera.Configuration.Id, request.EncoderValue, requestTimestamp);
                }
            }
            else
            {
                _logger.LogWarning("No Frame Data. Camera[{Id}] E: {Encoder}.", camera.Configuration.Id, request.EncoderValue);
            }
        }

        /// <summary>
        /// Starts processing frames for a specific camera from its channel.
        /// </summary>
        /// <param name="camera">The camera service to process frames for.</param>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>A task representing the asynchronous frame processing operation.</returns>
        private async Task StartFrameProcessingAsync(ICameraService camera, CancellationToken cancellationToken)
        {
            var loggingScope = $"Frame Processing Channel [Camera {camera.Configuration.Id}]";
            Thread.CurrentThread.Name = $"FrameProcessingThread_{camera.Configuration.Id}";
            _logger.LogInformation("{Scope} Awaiting work.", loggingScope);

            try
            {
                await foreach (var frame in _frameProcessingChannels[camera].Reader.ReadAllAsync(cancellationToken))
                {
                    _logger.LogInformation("{Scope} received: {Size}b E:{Encoder} | Frames: {TotalFrames}.", loggingScope, frame.Data.Length, frame.EncoderValue, TotalFramesProcessed);
                    await ProcessFrameAsync(frame);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("{Scope} cancelled.", loggingScope);
            }
            finally
            {
                _logger.LogInformation("{Scope} stopped.", loggingScope);
            }
        }

        /// <summary>
        /// Processes a single frame, sending it to the stream or saving it to disk based on mode.
        /// </summary>
        /// <param name="frame">The frame to process.</param>
        /// <returns>A task representing the asynchronous frame processing operation.</returns>
        private async Task ProcessFrameAsync(ImageFrame frame)
        {
            var cameraService = _cameraServices.FirstOrDefault(c => c.Configuration.Id == frame.CameraId);
            if (cameraService == null || !_cameraStreamingServices.TryGetValue(cameraService, out var cameraStream)) return;

            try
            {
                var imageStrip = new ImageStrip
                {
                    CameraID = (int)frame.CameraId,
                    Image = Google.Protobuf.ByteString.CopyFrom(frame.Data),
                    EncoderValue = (int)frame.RequestEncoderValue
                };

                if (!_serverOptions.ImageProcessingClientSkip)
                {
                    cameraStream.PostImageStrip(imageStrip);
                    _logger.LogDebug("Posted image strip to stream for Camera ID {CameraId}.", frame.CameraId);
                }

                if (ImageAcquisitionMode == ImageAcquisitionMode.Debug)
                {
                    var basePath = _serverOptions.DebugImageSettings.BasePath;
                    var folderPath = _serverOptions.DebugImageSettings.UseCameraIdSubfolder
                        ? Path.Combine(basePath, frame.CameraId.ToString())
                        : basePath;
                    folderPath = _serverOptions.DebugImageSettings.UseDateSubfolder
                        ? Path.Combine(folderPath, frame.Timestamp.ToString("yyyy-MM-dd"))
                        : folderPath;
                    var filePath = Path.Combine(folderPath, $"Frame_{frame.Timestamp:HH-mm-ss-ffffff}-{frame.Timestamp.Ticks}-E-{frame.EncoderValue}.raw");
                    _fileWriteQueue.EnqueueWrite(frame.Data, filePath, frame.Width, frame.Height);
                }

                var processingTime = DateTime.UtcNow - frame.Timestamp;
                _telemetry.AddProcessingTime(processingTime.Milliseconds);
                _telemetry.IncrementFramesProcessed();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing frame for Camera ID {CameraId}.", frame.CameraId);
                _telemetry.AddError(cameraService.Configuration.Id, $"Frame processing failed: {ex.Message}", _logger);
            }
        }

        /// <summary>
        /// Handles the event when a camera stops streaming, initiating a retry.
        /// </summary>
        /// <param name="camera">The camera service that stopped streaming.</param>
        private void Camera_OnStreamingStopped(ICameraService camera)
        {
            _logger.LogWarning("Camera_OnStreamingStopped Event Fired for {Address}.", camera.Configuration.CameraAddress);
            _ = Task.Run(async () =>
            {
                if (_cameraRetrying.TryGetValue(camera, out var isRetrying) && isRetrying) return;

                await _cameraSemaphores[camera].WaitAsync(_cancellationToken);
                try
                {
                    Thread.CurrentThread.Name = $"CameraRetryThread_{camera.Configuration.Id}";
                    _cameraRetrying[camera] = true;

                    try
                    {
                        await camera.StopStreamingAsync();
                    }
                    catch (Exception ex)
                    {
                        _telemetry.AddError(camera.Configuration.Id, $"Cleanup failed before retry: {ex.Message}", _logger);
                    }

                    await RetryUntilConnectedAsync(camera);
                }
                finally
                {
                    _cameraRetrying[camera] = false;
                    _cameraSemaphores[camera].Release();
                }
            }, _cancellationToken);
        }

        /// <summary>
        /// Updates the encoder value and propagates it to all cameras.
        /// </summary>
        /// <param name="encoderValue">The new encoder value.</param>
        private void TrackEncoderValue(long encoderValue)
        {
            if (EncoderValue == encoderValue) return;
            EncoderValue = encoderValue;
            _logger.LogInformation("Encoder updated: {Value}.", encoderValue);
            foreach (var camera in _cameraServices)
            {
                camera.EncoderValue = encoderValue;
            }
        }

        /// <summary>
        /// Retries connecting and streaming a camera until successful.
        /// </summary>
        /// <param name="cameraService">The camera service to connect.</param>
        /// <returns>A task representing the asynchronous retry operation.</returns>
        private async Task RetryUntilConnectedAsync(ICameraService cameraService)
        {
            var retryDelay = TimeSpan.FromMilliseconds(_serverOptions.RetrySettings.InitialRetryDelayMs);
            var maxRetryDelay = TimeSpan.FromMilliseconds(_serverOptions.RetrySettings.MaxRetryDelayMs);

            while (!cameraService.IsConnected || !cameraService.IsStreaming)
            {
                try
                {
                    if (!cameraService.IsConnected && await cameraService.ConnectAsync())
                    {
                        _logger.LogDebug("Connected to camera: {Address}.", cameraService.Configuration.CameraAddress);
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                    else if (!cameraService.IsConnected)
                    {
                        await cameraService.DisconnectAsync();
                        _telemetry.AddError(cameraService.Configuration.Id, cameraService.LastError ?? "Unknown connection error", _logger);
                    }

                    if (cameraService.IsConnected && !cameraService.IsStreaming && await cameraService.StartStreamingAsync())
                    {
                        _logger.LogInformation("Camera is streaming: {Address}.", cameraService.Configuration.CameraAddress);
                        _cameraReadiness[cameraService].TrySetResult(true);
                        return;
                    }
                    else if (cameraService.IsConnected)
                    {
                        _telemetry.AddError(cameraService.Configuration.Id, "Failed to start streaming", _logger);
                    }
                }
                catch (Exception ex)
                {
                    _telemetry.AddError(cameraService.Configuration.Id, $"Unexpected Error: {ex.Message}", _logger);
                }

                await Task.Delay(retryDelay, _cancellationToken);
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
            }
        }

        /// <summary>
        /// Records an error for a specific camera.
        /// </summary>
        /// <param name="camera">The camera service with the error.</param>
        /// <param name="error">The error message.</param>
        public void AddError(ICameraService camera, string error) => _telemetry.AddError(camera.Configuration.Id, error, _logger);

        /// <summary>
        /// Runs periodic health checks for the service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>A task representing the asynchronous health check operation.</returns>
        private async Task HealthCheckRunAsync(CancellationToken cancellationToken)
        {
            Thread.CurrentThread.Name = "HealthCheckThread";
            if (!_serverOptions.CameraHealth.EnableLogging) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                PrintStatus();
                await Task.Delay(TimeSpan.FromMilliseconds(_serverOptions.CameraHealth.PollIntervalMs), cancellationToken);
            }
        }

        /// <summary>
        /// Prints the current server health status.
        /// </summary>
        public void PrintStatus() => _monitorService?.PrintServerHealth(_cancellationToken, this, _serverOptions.CameraHealth.RefreshConsoleOnUpdate);

  
        /// <summary>
        /// Disposes of resources used by the service.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            try
            {
                _cancellationTokenSource?.Dispose();
                _incomingRequestThrottle?.Dispose();
                foreach (var semaphore in _cameraSemaphores.Values)
                {
                    semaphore?.Dispose();
                }
                foreach (var channel in _frameProcessingChannels.Values)
                {
                    channel?.Writer.TryComplete();
                }
                _monitorService?.Dispose();
                _encoderChannel?.Dispose();
                _logger.LogInformation("Image Acquisition Service resources disposed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing Image Acquisition Service resources.");
            }
        }
    }
}
