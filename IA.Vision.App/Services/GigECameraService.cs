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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// Implements a GigE camera service for connecting, streaming, and processing frames from a GigE Vision camera.
    /// Manages camera connection, frame buffering, and streaming state with robust error handling and logging.
    /// </summary>
    public partial class GigECameraService : BackgroundService, ICameraService
    {
        // Constants and readonly fields
        private readonly int _syncRetries = 3; // Number of retries for synchronizing camera parameters
        private readonly int _frameWaitTimeoutMs = 3000; // Timeout for waiting for the first frame (ms)
        private readonly Stopwatch _stopwatch = new(); // Tracks timing for streaming operations
        private readonly ILogger<GigECameraService> _logger; // Logger for camera-specific operations
        private readonly int _streamingTimeoutMs; // Timeout for detecting streaming interruptions

        // Private fields
        private Gvcp? _gvcp; // GigE Vision Control Protocol instance
        private ICamera? _camera; // Camera interface for GigE Vision operations
        private volatile bool _isStreaming; // Indicates if the camera is actively streaming
        private volatile bool _isConnected; // Indicates if the camera is connected
        private DateTimeOffset? _lastFrameReceivedTime; // Timestamp of the last received frame
        private DateTimeOffset _streamStartTime; // Timestamp when streaming started
        private CancellationTokenSource? _streamingTimeoutCancellationTokenSource; // Controls streaming timeout task
        private Task? _streamingTimeoutTask; // Background task for monitoring streaming timeouts
        private readonly object _eventLock = new(); // Synchronization lock for event handling

        /// <summary>
        /// Gets the camera configuration.
        /// </summary>
        public CameraConfiguration Configuration { get; }

        /// <summary>
        /// Gets or sets the encoder value associated with the camera.
        /// </summary>
        public long EncoderValue { get; set; }

        /// <summary>
        /// Gets or sets the list of GenICam categories for the camera.
        /// </summary>
        public List<ICategory>? Categories { get; set; }

        /// <summary>
        /// Gets or sets the ring buffer for storing image frames.
        /// </summary>
        public RingBuffer FrameBuffer { get; set; }

        /// <summary>
        /// Gets the total number of frames processed by the camera.
        /// </summary>
        public long TotalFramesProcessed { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the camera is connected.
        /// </summary>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// Gets a value indicating whether the camera is actively streaming, considering recent frame activity.
        /// </summary>
        public bool IsStreaming => _isStreaming
            && _lastFrameReceivedTime.HasValue
            && (DateTimeOffset.UtcNow - _lastFrameReceivedTime.Value) <= TimeSpan.FromMilliseconds(_streamingTimeoutMs * 2);

        /// <summary>
        /// Gets the server options for configuration.
        /// </summary>
        public ServerOptions ServerOptions { get; }

        /// <summary>
        /// Gets the current streaming state of the camera.
        /// </summary>
        public StreamingState StreamingState { get; private set; } = StreamingState.NotStarted;

        /// <summary>
        /// Gets the frames-per-second (FPS) based on the ring buffer's calculation.
        /// </summary>
        public double Fps => IsStreaming ? FrameBuffer.Fps : 0;

        /// <summary>
        /// Gets the total number of frames in the buffer.
        /// </summary>
        public long FrameCount => FrameBuffer.FrameCount;

        /// <summary>
        /// Gets the last error message encountered during camera operations.
        /// </summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// Event raised when streaming stops.
        /// </summary>
        public event Action? OnStreamingStopped;

        /// <summary>
        /// Initializes a new instance of the <see cref="GigECameraService"/> class.
        /// </summary>
        /// <param name="config">The camera configuration options.</param>
        /// <param name="logger">The logger for camera-specific operations.</param>
        /// <param name="serverOptions">The server configuration options.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="config"/>, <paramref name="logger"/>, or <paramref name="serverOptions"/> is null.</exception>
        public GigECameraService(IOptions<CameraConfiguration> config, ILogger<GigECameraService> logger, IOptions<ServerOptions> serverOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Configuration = config?.Value ?? throw new ArgumentNullException(nameof(config));
            ServerOptions = serverOptions?.Value ?? throw new ArgumentNullException(nameof(serverOptions));
            FrameBuffer = new RingBuffer((int)ServerOptions.BufferSize);
            _streamingTimeoutMs = ServerOptions.StreamMonitor.PollIntervalMs;
        }

        /// <summary>
        /// Connects to the GigE camera and initializes its parameters.
        /// </summary>
        /// <returns>True if the connection is successful; otherwise, false.</returns>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                LogWithCameraIp(LogLevel.Information, "Attempting to connect to camera.");
                LogWithCameraIp(LogLevel.Information, Configuration.GetConfigurationSummary());

                // Verify network support for jumbo frames if required
                if (!await DiagnoseNetworkSupportAsync().ConfigureAwait(false))
                {
                    LogWithCameraIp(LogLevel.Error, $"Failed to ping jumbo frame to Camera[{Configuration.Id}]:{Configuration.ReceiverAddress}. Check your device connection and network MTU limit settings.");
                    return false;
                }

                // Initialize GigE Vision Control Protocol
                _gvcp = new Gvcp(Configuration.CameraAddress);
                await _gvcp.ReadXmlFileAsync().ConfigureAwait(false);

                if (!_gvcp.IsXmlFileLoaded)
                {
                    LogWithCameraIp(LogLevel.Error, "Camera XML configuration not loaded.");
                    return false;
                }
                LogWithCameraIp(LogLevel.Debug, "XML configuration loaded successfully.");

                // Unhook any existing frame ready event to prevent duplicate handlers
                UnhookFrameReadyEvent();

                // Initialize camera with configuration
                _camera = new Camera(_gvcp)
                {
                    IP = Configuration.CameraAddress,
                    PortRx = (int)Configuration.ReceiverPort,
                    RxIP = Configuration.ReceiverAddress,
                    Width = Configuration.Width,
                    Height = Configuration.Height,
                    PixelFormat = Configuration.PixelFormat,
                    FrameReady = HandleFrameReady!
                };

                // Validate camera initialization
                if (_camera == null ||
                    _camera.IP != Configuration.CameraAddress ||
                    _camera.PortRx != Configuration.ReceiverPort ||
                    _camera.RxIP != Configuration.ReceiverAddress ||
                    _camera.Width != Configuration.Width ||
                    _camera.PixelFormat != Configuration.PixelFormat ||
                    _camera.FrameReady == null)
                {
                    LogWithCameraIp(LogLevel.Error, "Failed to initialize camera or camera is not ready.");
                    return false;
                }

                // Synchronize camera parameters
                var isSynched = await _camera.SyncParameters(_syncRetries).ConfigureAwait(false);
                if (!isSynched)
                {
                    LogWithCameraIp(LogLevel.Error, "Failed to synchronize camera parameters.");
                    return false;
                }
                LogWithCameraIp(LogLevel.Debug, "Camera parameters synchronized successfully.");

                // Check camera availability
                var cameraStatus = await _gvcp.CheckCameraStatusAsync(Configuration.CameraAddress).ConfigureAwait(false);
                if (cameraStatus == CameraStatus.UnAvailable)
                {
                    LogWithCameraIp(LogLevel.Error, $"Camera status is {cameraStatus}. Not ready.");
                    return false;
                }
                LogWithCameraIp(LogLevel.Debug, $"Camera status: {cameraStatus}");

                // Set image dimensions
                await SetImageDimensionsAsync(Configuration.Width, Configuration.Height).ConfigureAwait(false);
                LogWithCameraIp(LogLevel.Debug, $"Image dimensions set: Width = {Configuration.Width}, Height = {Configuration.Height}");

                LogWithCameraIp(LogLevel.Information, $"Camera initialized and connected successfully to IP: {Configuration.CameraAddress}");
                LogWithCameraIp(LogLevel.Information, _camera.GetGigECameraStatus());

                _isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                LogWithCameraIp(LogLevel.Error, $"Error connecting to camera: {ex.Message}");
                _isConnected = false;
                return false;
            }
        }

        /// <summary>
        /// Disconnects the camera and cleans up resources.
        /// </summary>
        /// <returns>A task representing the asynchronous disconnection operation.</returns>
        public async Task DisconnectAsync()
        {
            try
            {
                // Cancel streaming timeout task
                _streamingTimeoutCancellationTokenSource?.Cancel();
                _streamingTimeoutCancellationTokenSource?.Dispose();
                _streamingTimeoutCancellationTokenSource = null;
                _streamingTimeoutTask = null;

                // Clean up camera and GVCP resources
                _gvcp = null;
                _camera = null;

                _isStreaming = false;
                _isConnected = false;
                LogWithCameraIp(LogLevel.Information, "Camera disconnected successfully.");
            }
            catch (Exception ex)
            {
                LogWithCameraIp(LogLevel.Error, $"Error during camera disconnection: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts streaming from the camera, waiting for the first frame to confirm active streaming.
        /// </summary>
        /// <returns>True if streaming starts successfully; otherwise, false.</returns>
        public async Task<bool> StartStreamingAsync()
        {
            if (IsStreaming)
            {
                LogWithCameraIp(LogLevel.Information, "Camera is already streaming.");
                return true;
            }
            if (_camera == null)
            {
                LogWithCameraIp(LogLevel.Error, "Camera is not initialized.");
                return false;
            }

            LogWithCameraIp(LogLevel.Debug, "Starting streaming.");
            StreamingState = StreamingState.Starting;

            try
            {
                _stopwatch.Restart();

                // Start camera streaming
                _isStreaming = await _camera.StartStreamAsync(Configuration.ReceiverAddress, (int)Configuration.ReceiverPort).ConfigureAwait(false);
                await Task.Delay(_gvcp!.ControlSocket.Client.ReceiveTimeout).ConfigureAwait(false);
                if (!_isStreaming)
                {
                    StreamingState = StreamingState.Failed;
                    LogWithCameraIp(LogLevel.Error, "Failed to start streaming.");
                    return false;
                }

                _lastFrameReceivedTime = null; // Reset for new streaming session
                var waitStart = DateTimeOffset.UtcNow;
                LogWithCameraIp(LogLevel.Debug, "Waiting for initial frame...");

                // Wait for the first frame to confirm streaming
                while (DateTimeOffset.UtcNow - waitStart < TimeSpan.FromMilliseconds(_frameWaitTimeoutMs))
                {
                    if (_lastFrameReceivedTime.HasValue && _lastFrameReceivedTime.Value >= _streamStartTime)
                    {
                        LogWithCameraIp(LogLevel.Information, $"First frame received in {(DateTimeOffset.UtcNow - waitStart).TotalSeconds:F2} seconds.");
                        StreamingState = StreamingState.Active;
                        _streamStartTime = _lastFrameReceivedTime.Value;
                        _isStreaming = true;
                        InitializeStreamingTimeoutMonitor();
                        return true;
                    }
                    await Task.Delay(_frameWaitTimeoutMs / 3).ConfigureAwait(false);
                }

                // Timeout if no frame is received
                LogWithCameraIp(LogLevel.Warning, $"No frame received within {_frameWaitTimeoutMs}ms, marking streaming as failed.");
                StreamingState = StreamingState.Failed;
                _isStreaming = false;
                return false;
            }
            catch (Exception ex)
            {
                StreamingState = StreamingState.Failed;
                LogWithCameraIp(LogLevel.Error, $"Error starting streaming: {ex.Message}");
                _isStreaming = false;
                return false;
            }
        }

        /// <summary>
        /// Stops streaming from the camera and cleans up related resources.
        /// </summary>
        /// <returns>A task representing the asynchronous stop operation.</returns>
        public async Task StopStreamingAsync()
        {
            if (!_isStreaming)
            {
                LogWithCameraIp(LogLevel.Information, "Camera is not streaming.");
                return;
            }

            LogWithCameraIp(LogLevel.Information, "Stopping streaming.");

            try
            {
                if (_camera != null && _gvcp != null)
                {
                    UnhookFrameReadyEvent();
                    await _gvcp.LeaveControl().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false); // Wait for heartbeat to pass
                    await _camera.StopStream().ConfigureAwait(false);
                }

                _stopwatch.Stop();
                _isStreaming = false;
                StreamingState = StreamingState.NotStarted;

                // Cancel and await streaming timeout task
                if (_streamingTimeoutCancellationTokenSource != null)
                {
                    _streamingTimeoutCancellationTokenSource.Cancel();
                    if (_streamingTimeoutTask != null)
                    {
                        try
                        {
                            await _streamingTimeoutTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            LogWithCameraIp(LogLevel.Information, "Streaming timeout task canceled.");
                        }
                    }
                    _streamingTimeoutCancellationTokenSource.Dispose();
                    _streamingTimeoutCancellationTokenSource = null;
                    _streamingTimeoutTask = null;
                }

                OnStreamingStopped?.Invoke();
                LogWithCameraIp(LogLevel.Information, "Streaming stopped successfully.");
            }
            catch (Exception ex)
            {
                LogWithCameraIp(LogLevel.Error, $"Error stopping streaming: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the image dimensions for the camera.
        /// </summary>
        /// <param name="width">The desired image width.</param>
        /// <param name="height">The desired image height.</param>
        /// <returns>True if the resolution is set successfully; otherwise, false.</returns>
        /// <exception cref="ApplicationException">Thrown if the resolution is not set correctly.</exception>
        private async Task<bool> SetImageDimensionsAsync(uint width, uint height)
        {
            if (_camera == null)
            {
                LogWithCameraIp(LogLevel.Error, "Camera is null, cannot set resolution.");
                return false;
            }

            try
            {
                await _camera.SetResolutionAsync(width, height).ConfigureAwait(false);

                if (_camera.Width != width || _camera.Height != height)
                {
                    throw new ApplicationException($"Camera resolution is not set correctly. Actual: {_camera.Width}x{_camera.Height}, Expected: {width}x{height}.");
                }

                LogWithCameraIp(LogLevel.Debug, $"Resolution set to: Width = {width}, Height = {height}");
                return true;
            }
            catch (Exception ex)
            {
                LogWithCameraIp(LogLevel.Error, $"Error setting image resolution: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Checks if the network supports jumbo frames for the camera's receiver address.
        /// </summary>
        /// <returns>True if the network supports jumbo frames or if not enforced; otherwise, false.</returns>
        public async Task<bool> DiagnoseNetworkSupportAsync()
        {
            if (!Configuration.EnforceJumboFrames)
            {
                LogWithCameraIp(LogLevel.Information, "Jumbo frame enforcement disabled.");
                return true;
            }

            var ipAddress = Configuration.ReceiverAddress;
            var canPingJumbo = await new NetworkUtils(_logger).CanPingJumboFrameAsync(ipAddress).ConfigureAwait(false);

            if (!canPingJumbo)
            {
                LogWithCameraIp(LogLevel.Information, "Unable to ping jumbo frame without fragmentation. Adjust your network MTU settings.");
                return false;
            }

            LogWithCameraIp(LogLevel.Information, "Network supports jumbo frames.");
            return true;
        }

        /// <summary>
        /// Handles the FrameReady event by processing incoming frames and storing them in the buffer.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="frame">The raw frame data.</param>
        public void HandleFrameReady(object? sender, byte[] frame)
        {
            if (!_isStreaming || frame == null)
                return;

            OnFrameReady(frame);
        }

        /// <summary>
        /// Processes a received frame, creates an <see cref="ImageFrame"/>, and enqueues it in the buffer.
        /// </summary>
        /// <param name="frame">The raw frame data.</param>
        private void OnFrameReady(byte[] frame)
        {
            if (!_isStreaming || frame == null)
                return;

            _stopwatch.Restart();

            var imageFrame = new ImageFrame
            {
                Data = frame,
                Width = (int)_camera!.Width,
                Height = (int)_camera!.Height,
                CameraId = Configuration.Id,
                Timestamp = DateTimeOffset.UtcNow,
                EncoderValue = EncoderValue,
                IsRequestValid = true
            };

            TotalFramesProcessed++;
            _lastFrameReceivedTime = imageFrame.Timestamp;
            EnqueueFrame(imageFrame);

            _stopwatch.Stop();
        }

        #region Buffer Locking Operations

        /// <summary>
        /// Enqueues a frame into the ring buffer.
        /// </summary>
        /// <param name="imageFrame">The frame to enqueue.</param>
        private void EnqueueFrame(ImageFrame imageFrame) => FrameBuffer.Enqueue(imageFrame);

        /// <summary>
        /// Retrieves a frame from the buffer by encoder value.
        /// </summary>
        /// <param name="encoderValue">The encoder value to search for.</param>
        /// <returns>The matching frame, or null if not found.</returns>
        public ImageFrame? GetFrameByEncoderValue(long encoderValue) => FrameBuffer.GetFrameByEncoderValue(encoderValue);

        #endregion Buffer Locking Operations

        /// <summary>
        /// Initializes a background task to monitor streaming timeouts and detect frame reception issues.
        /// </summary>
        private void InitializeStreamingTimeoutMonitor()
        {
            var loggingScope = $"♥ Stream Monitor - thread:{Thread.CurrentThread.ManagedThreadId}";
            LogWithCameraIp(LogLevel.Information, $"{loggingScope} Listening for frames...");

            // Cancel any existing timeout task
            _streamingTimeoutCancellationTokenSource?.Cancel();
            _streamingTimeoutCancellationTokenSource?.Dispose();
            _streamingTimeoutCancellationTokenSource = new CancellationTokenSource();

            // Start a new timeout monitoring task
            _streamingTimeoutTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name = $"StreamTimeoutMonitor_Camera{Configuration.Id}";
                bool firstFrameReceived = false;

                while (!_streamingTimeoutCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_streamingTimeoutMs, _streamingTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
                        if (!_isStreaming)
                            continue;

                        DateTimeOffset startWindow = _lastFrameReceivedTime ?? _streamStartTime;
                        var timeSinceLastFrame = DateTimeOffset.UtcNow - startWindow;
                        if (timeSinceLastFrame > TimeSpan.FromMilliseconds(_streamingTimeoutMs))
                        {
                            LogWithCameraIp(LogLevel.Warning, $"{loggingScope} Timeout: No frame received in the last {_streamingTimeoutMs}ms.");
                            _isStreaming = false;
                            StreamingState = StreamingState.Failed;
                            OnStreamingStopped?.Invoke();
                            return;
                        }

                        if (startWindow == _streamStartTime)
                        {
                            LogWithCameraIp(LogLevel.Information, $"{loggingScope} Awaiting first frame from camera {Configuration.CameraAddress}.");
                        }
                        else
                        {
                            _isStreaming = true;
                            if (ServerOptions.StreamMonitor.EnableLogging)
                            {
                                LogWithCameraIp(LogLevel.Information, $"{loggingScope} Last frame received: {_lastFrameReceivedTime.Value:HH:mm:ss.ffffff}, FPS: {Fps:F2}");
                            }

                            if (!firstFrameReceived)
                            {
                                firstFrameReceived = true;
                                LogWithCameraIp(LogLevel.Information, GetCameraServiceDetails());
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        LogWithCameraIp(LogLevel.Information, $"{loggingScope} Streaming timeout monitor canceled.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogWithCameraIp(LogLevel.Error, $"{loggingScope} Error in streaming timeout monitor: {ex.Message}");
                    }
                }
            }, _streamingTimeoutCancellationTokenSource.Token);
        }

        /// <summary>
        /// Unhooks the FrameReady event handler to prevent memory leaks or duplicate handling.
        /// </summary>
        private void UnhookFrameReadyEvent()
        {
            lock (_eventLock)
            {
                if (_camera != null && _camera.FrameReady != null)
                {
                    _camera.FrameReady -= HandleFrameReady;
                    LogWithCameraIp(LogLevel.Debug, "FrameReady event handler unhooked successfully.");
                }
            }
        }

        /// <summary>
        /// Logs a message with the camera's IP address for traceability.
        /// </summary>
        /// <param name="level">The log level.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="args">Optional arguments for message formatting.</param>
        private void LogWithCameraIp(LogLevel level, string message, params object?[] args)
        {
            var formattedMessage = $"[{Configuration.CameraAddress}] {message}";
            _logger.Log(level, formattedMessage, args);
            if (level == LogLevel.Error)
            {
                LastError = string.Format(formattedMessage, args);
            }
        }

        /// <summary>
        /// Gets detailed status information about the camera service.
        /// </summary>
        /// <returns>A string containing the camera service details.</returns>
        private string GetCameraServiceDetails()
        {
            return $"Camera {Configuration.Id} - IP: {Configuration.CameraAddress}, " +
                   $"Streaming: {IsStreaming}, FPS: {Fps:F2}, Frames: {FrameCount}, " +
                   $"Resolution: {_camera?.Width}x{_camera?.Height}, Status: {StreamingState}";
        }

        /// <summary>
        /// Executes the background service, managed by the hosting infrastructure.
        /// </summary>
        /// <param name="stoppingToken">The cancellation token for stopping the service.</param>
        /// <returns>A task representing the background service execution.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Managed by hosting service; no custom background tasks required
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
