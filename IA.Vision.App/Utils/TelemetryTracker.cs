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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App.Services
{
    /// <summary>
    /// Tracks telemetry metrics for the image acquisition system, including request counts, frame processing,
    /// errors, and per-camera stream statistics in a thread-safe manner.
    /// </summary>
    public class TelemetryTracker
    {
        private long _totalRequestsProcessed;
        private long _totalCaptureRequests;
        private long _totalFramesProcessed;
        private long _failedRequests;
        private long _droppedFrames;
        private int _activeRequests;
        private long _totalProcessingTimeMs;
        private readonly ConcurrentDictionary<int, int> _cameraErrorCounts = new();
        private readonly ConcurrentQueue<string> _errorQueue;
        private readonly int _errorQueueCapacity;
        private readonly DateTime _startTime;
        private long _lastRequestCount;
        private double _requestsPerSecond;
        private long _lastFrameCount;
        private double _framesProcessedPerSecond;
        private int _isEncoderStreamConnected; // 0 = false, 1 = true for Interlocked
        private Task _rpsTrackingTask;
        private Task _fpsTrackingTask;
        private Task _streamTrackingTask;
        private ILogger _logger;

        // Per-camera telemetry fields
        private readonly ConcurrentDictionary<int, long> _requestsAddedToStream = new();
        private readonly ConcurrentDictionary<int, long> _requestsReadFromStream = new();
        private readonly ConcurrentDictionary<int, long> _errorsFromStream = new();
        private readonly ConcurrentDictionary<int, int> _connectedClients = new();
        private readonly ConcurrentDictionary<int, long> _lastRequestsAddedToStream = new();
        private readonly ConcurrentDictionary<int, double> _requestsAddedPerSecond = new();
        private readonly ConcurrentDictionary<int, long> _lastRequestsReadFromStream = new();
        private readonly ConcurrentDictionary<int, double> _requestsReadPerSecond = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="TelemetryTracker"/> class.
        /// </summary>
        /// <param name="errorQueueCapacity">The maximum number of error messages to store in the queue.</param>
        /// <param name="cancellationToken">The cancellation token for background tracking tasks.</param>
        public TelemetryTracker(int errorQueueCapacity, CancellationToken cancellationToken, ILogger logger)
        {
            _errorQueueCapacity = errorQueueCapacity;
            _errorQueue = new ConcurrentQueue<string>();
            _startTime = DateTime.UtcNow;
            _lastRequestCount = 0;
            _requestsPerSecond = 0;
            _lastFrameCount = 0;
            _framesProcessedPerSecond = 0;
            _isEncoderStreamConnected = 0;
            _rpsTrackingTask = StartRPSTrackingAsync(cancellationToken);
            _fpsTrackingTask = StartFPSTrackingAsync(cancellationToken);
            _streamTrackingTask = StartStreamTrackingAsync(cancellationToken);
            _logger = logger;
        }

        // Public properties (maintaining original access levels)
        /// <summary>
        /// Gets the total number of requests processed.
        /// </summary>
        public long TotalRequestsProcessed => Interlocked.Read(ref _totalRequestsProcessed);

        /// <summary>
        /// Gets the total number of capture requests.
        /// </summary>
        public long TotalCaptureRequests => Interlocked.Read(ref _totalCaptureRequests);

        /// <summary>
        /// Gets the total number of frames processed.
        /// </summary>
        public long TotalFramesProcessed => Interlocked.Read(ref _totalFramesProcessed);

        /// <summary>
        /// Gets the total number of failed requests.
        /// </summary>
        public long FailedRequests => Interlocked.Read(ref _failedRequests);

        /// <summary>
        /// Gets the total number of dropped frames.
        /// </summary>
        public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

        /// <summary>
        /// Gets the current number of active requests.
        /// </summary>
        public int ActiveRequests => Interlocked.CompareExchange(ref _activeRequests, 0, 0);

        /// <summary>
        /// Gets the total processing time in milliseconds.
        /// </summary>
        public long TotalProcessingTimeMs => Interlocked.Read(ref _totalProcessingTimeMs);

        /// <summary>
        /// Gets the start time of the telemetry tracker.
        /// </summary>
        public DateTime StartTime => _startTime;

        /// <summary>
        /// Gets the uptime of the telemetry tracker.
        /// </summary>
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;

        /// <summary>
        /// Gets the dictionary of error counts per camera.
        /// </summary>
        public ConcurrentDictionary<int, int> CameraErrorCounts => _cameraErrorCounts;

        /// <summary>
        /// Gets the queue of error messages.
        /// </summary>
        public ConcurrentQueue<string> ErrorQueue => _errorQueue;

        /// <summary>
        /// Gets the requests received per second.
        /// </summary>
        public double RequestsReceivedPerSecond => Interlocked.CompareExchange(ref _requestsPerSecond, 0, 0);

        /// <summary>
        /// Gets the frames processed per second (sent to stream).
        /// </summary>
        public double RequestsSentPerSecond => Interlocked.CompareExchange(ref _framesProcessedPerSecond, 0, 0);

        /// <summary>
        /// Gets a value indicating whether the encoder stream is connected.
        /// </summary>
        public bool IsEncoderStreamConnected => Interlocked.CompareExchange(ref _isEncoderStreamConnected, 0, 0) == 1;

        /// <summary>
        /// Gets the total number of requests added to streams across all cameras.
        /// </summary>
        public long TotalRequestsAddedToStream => _requestsAddedToStream.Values.Sum();

        /// <summary>
        /// Gets the total number of requests read from streams across all cameras.
        /// </summary>
        public long TotalRequestsReadFromStream => _requestsReadFromStream.Values.Sum();

        /// <summary>
        /// Gets the total number of connected clients across all cameras.
        /// </summary>
        public int TotalConnectedClients => _connectedClients.Values.Sum();

        /// <summary>
        /// Gets the number of requests added to the stream for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        /// <returns>The number of requests added.</returns>
        public long GetRequestsAddedToStream(int cameraId) => _requestsAddedToStream.GetOrAdd(cameraId, 0);

        /// <summary>
        /// Gets the number of requests read from the stream for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        /// <returns>The number of requests read.</returns>
        public long GetRequestsReadFromStream(int cameraId) => _requestsReadFromStream.GetOrAdd(cameraId, 0);

        /// <summary>
        /// Gets the rate of requests added per second for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        /// <returns>The requests added per second.</returns>
        public double GetRequestsAddedPerSecond(int cameraId) => _requestsAddedPerSecond.GetOrAdd(cameraId, 0);

        /// <summary>
        /// Gets the rate of requests read per second for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        /// <returns>The requests read per second.</returns>
        public double GetRequestsReadPerSecond(int cameraId) => _requestsReadPerSecond.GetOrAdd(cameraId, 0);

        /// <summary>
        /// Gets the number of errors from the stream for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        /// <returns>The number of errors.</returns>
        public long GetErrorsFromStream(int cameraId) => _errorsFromStream.GetOrAdd(cameraId, 0);

        // Public methods (maintaining original access levels)
        /// <summary>
        /// Increments the total number of requests processed.
        /// </summary>
        public void IncrementRequestsProcessed() => Interlocked.Increment(ref _totalRequestsProcessed);

        /// <summary>
        /// Increments the total number of capture requests.
        /// </summary>
        public void IncrementCaptureRequests() => Interlocked.Increment(ref _totalCaptureRequests);

        /// <summary>
        /// Increments the total number of frames processed.
        /// </summary>
        public void IncrementFramesProcessed() => Interlocked.Increment(ref _totalFramesProcessed);

        /// <summary>
        /// Increments the total number of failed requests.
        /// </summary>
        public void IncrementFailedRequests() => Interlocked.Increment(ref _failedRequests);

        /// <summary>
        /// Increments the total number of dropped frames.
        /// </summary>
        public void IncrementDroppedFrames() => Interlocked.Increment(ref _droppedFrames);

        /// <summary>
        /// Increments the count of active requests.
        /// </summary>
        public void IncrementActiveRequests() => Interlocked.Increment(ref _activeRequests);

        /// <summary>
        /// Decrements the count of active requests.
        /// </summary>
        public void DecrementActiveRequests() => Interlocked.Decrement(ref _activeRequests);

        /// <summary>
        /// Adds processing time in milliseconds.
        /// </summary>
        /// <param name="milliseconds">The processing time to add.</param>
        public void AddProcessingTime(int milliseconds) => Interlocked.Add(ref _totalProcessingTimeMs, milliseconds);

        /// <summary>
        /// Increments the count of requests added to the stream for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        public void IncrementRequestsAddedToStream(int cameraId)
        {
            _requestsAddedToStream.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        /// <summary>
        /// Increments the count of requests read from the stream for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        public void IncrementRequestsReadFromStream(int cameraId)
        {
            _requestsReadFromStream.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        /// <summary>
        /// Increments the count of errors from the stream for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        public void IncrementErrorsFromStream(int cameraId)
        {
            _errorsFromStream.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        /// <summary>
        /// Sets the encoder stream connection status.
        /// </summary>
        /// <param name="isConnected">True if the encoder stream is connected; otherwise, false.</param>
        public void SetEncoderStreamConnected(bool isConnected)
        {
            Interlocked.CompareExchange(ref _isEncoderStreamConnected, isConnected ? 1 : 0, isConnected ? 0 : 1);
        }

        /// <summary>
        /// Records an error for a specific camera and logs it.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        /// <param name="error">The error message.</param>
        /// <param name="logger">The logger to record the error.</param>
        public void AddError(int cameraId, string error, ILogger logger)
        {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrEmpty(error))
                return;

            var cameraDetails = $"Camera ID: {cameraId}";
            var fullErrorMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {cameraDetails} - {error}";

            logger.LogError(fullErrorMessage);

            if (_errorQueue.Count >= _errorQueueCapacity)
                _errorQueue.TryDequeue(out _);
            _errorQueue.Enqueue(fullErrorMessage);

            _cameraErrorCounts.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        /// <summary>
        /// Sets the number of connected clients for a specific camera.
        /// </summary>
        /// <param name="cameraId">The camera ID.</param>
        /// <param name="count">The number of connected clients.</param>
        public void SetConnectedClients(int cameraId, int count)
        {
            _connectedClients[cameraId] = count;
        }

        /// <summary>
        /// Tracks requests added and read per second for each camera in the background.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the tracking task.</param>
        /// <returns>A task representing the asynchronous tracking operation.</returns>
        private async Task StartStreamTrackingAsync(CancellationToken cancellationToken)
        {
            const int intervalSeconds = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var cameraId in _requestsAddedToStream.Keys)
                    {
                        long currentAdded = GetRequestsAddedToStream(cameraId);
                        long previousAdded = _lastRequestsAddedToStream.GetOrAdd(cameraId, 0);
                        double addedPerSec = (currentAdded - previousAdded) / (double)intervalSeconds;
                        _requestsAddedPerSecond[cameraId] = addedPerSec;
                        _lastRequestsAddedToStream[cameraId] = currentAdded;
                    }

                    foreach (var cameraId in _requestsReadFromStream.Keys)
                    {
                        long currentRead = GetRequestsReadFromStream(cameraId);
                        long previousRead = _lastRequestsReadFromStream.GetOrAdd(cameraId, 0);
                        double readPerSec = (currentRead - previousRead) / (double)intervalSeconds;
                        _requestsReadPerSecond[cameraId] = readPerSec;
                        _lastRequestsReadFromStream[cameraId] = currentRead;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogInformation("Stream tracking task canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error in stream tracking task.");
                }
            }
        }

        /// <summary>
        /// Tracks requests processed per second in the background.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the tracking task.</param>
        /// <returns>A task representing the asynchronous tracking operation.</returns>
        private async Task StartRPSTrackingAsync(CancellationToken cancellationToken)
        {
            const int intervalSeconds = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    long currentCount = TotalRequestsProcessed;
                    long requestsInInterval = currentCount - _lastRequestCount;
                    double rps = requestsInInterval / (double)intervalSeconds;

                    Interlocked.Exchange(ref _requestsPerSecond, rps);
                    _lastRequestCount = currentCount;

                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogInformation("RPS tracking task canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error in RPS tracking task.");
                }
            }
        }

        /// <summary>
        /// Tracks frames processed per second in the background.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the tracking task.</param>
        /// <returns>A task representing the asynchronous tracking operation.</returns>
        private async Task StartFPSTrackingAsync(CancellationToken cancellationToken)
        {
            const int intervalSeconds = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    long currentCount = TotalFramesProcessed;
                    long framesInInterval = currentCount - _lastFrameCount;
                    double fps = framesInInterval / (double)intervalSeconds;

                    Interlocked.Exchange(ref _framesProcessedPerSecond, fps);
                    _lastFrameCount = currentCount;

                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogInformation("FPS tracking task canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error in FPS tracking task.");
                }
            }
        }

        /// <summary>
        /// Waits for all background tracking tasks to complete.
        /// </summary>
        /// <returns>A task representing the completion of all tracking tasks.</returns>
        public async Task WaitForShutdownAsync()
        {
            try
            {
                await Task.WhenAll(_rpsTrackingTask, _fpsTrackingTask, _streamTrackingTask).ConfigureAwait(false);
                _logger?.LogInformation("Telemetry tracking tasks completed.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error waiting for telemetry tracking tasks to complete.");
            }
        }
    }
}
