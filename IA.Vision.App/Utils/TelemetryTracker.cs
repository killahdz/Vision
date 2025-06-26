using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App.Services
{
    public class TelemetryTracker
    {
        private long totalRequestsProcessed;
        private long totalCaptureRequests;
        private long totalFramesProcessed;
        private long failedRequests;
        private long droppedFrames;
        private int activeRequests;
        private long totalProcessingTimeMs;
        private readonly ConcurrentDictionary<int, int> cameraErrorCounts = new(); // Changed to int
        private readonly ConcurrentQueue<string> errorQueue;
        private readonly int errorQueueCapacity;
        private readonly DateTime startTime;
        private long lastRequestCount;
        private double requestsPerSecond;
        private Task rpsTrackingTask;
        private long lastFrameCount;
        private double framesProcessedPerSecond;
        private Task fpsTrackingTask;
        private int isEncoderStreamConnected; // 0 = false, 1 = true for Interlocked

        // New per-camera telemetry fields with int keys
        private readonly ConcurrentDictionary<int, long> requestsAddedToStream = new();

        private readonly ConcurrentDictionary<int, long> requestsReadFromStream = new();
        private readonly ConcurrentDictionary<int, long> errorsFromStream = new();
        private readonly ConcurrentDictionary<int, int> connectedClients = new();
        private readonly ConcurrentDictionary<int, long> lastRequestsAddedToStream = new();
        private readonly ConcurrentDictionary<int, double> requestsAddedPerSecond = new();
        private readonly ConcurrentDictionary<int, long> lastRequestsReadFromStream = new();
        private readonly ConcurrentDictionary<int, double> requestsReadPerSecond = new();
        private Task streamTrackingTask;

        public TelemetryTracker(int errorQueueCapacity, CancellationToken cancellationToken)
        {
            this.errorQueueCapacity = errorQueueCapacity;
            errorQueue = new ConcurrentQueue<string>();
            startTime = DateTime.UtcNow;
            lastRequestCount = 0;
            requestsPerSecond = 0;
            lastFrameCount = 0;
            framesProcessedPerSecond = 0;
            isEncoderStreamConnected = 0; // Initially disconnected
            rpsTrackingTask = StartRPSTrackingAsync(cancellationToken);
            fpsTrackingTask = StartFPSTrackingAsync(cancellationToken);
            streamTrackingTask = StartStreamTrackingAsync(cancellationToken);
        }

        // Existing properties
        public long TotalRequestsProcessed => Interlocked.Read(ref totalRequestsProcessed);

        public long TotalCaptureRequests => Interlocked.Read(ref totalCaptureRequests);
        public long TotalFramesProcessed => Interlocked.Read(ref totalFramesProcessed);
        public long FailedRequests => Interlocked.Read(ref failedRequests);
        public long DroppedFrames => Interlocked.Read(ref droppedFrames);
        public int ActiveRequests => Interlocked.CompareExchange(ref activeRequests, 0, 0);
        public long TotalProcessingTimeMs => Interlocked.Read(ref totalProcessingTimeMs);
        public DateTime StartTime => startTime;
        public TimeSpan Uptime => DateTime.UtcNow - startTime;
        public ConcurrentDictionary<int, int> CameraErrorCounts => cameraErrorCounts; // Changed to int
        public ConcurrentQueue<string> ErrorQueue => errorQueue;
        public double RequestsReceivedPerSecond => Interlocked.CompareExchange(ref requestsPerSecond, 0, 0);
        public double RequestsSentPerSecond => Interlocked.CompareExchange(ref framesProcessedPerSecond, 0, 0);
        public bool IsEncoderStreamConnected => Interlocked.CompareExchange(ref isEncoderStreamConnected, 0, 0) == 1;

        // New per-camera properties (aggregate totals)
        public long TotalRequestsAddedToStream => requestsAddedToStream.Values.Sum();

        public long TotalRequestsReadFromStream => requestsReadFromStream.Values.Sum();
        public int TotalConnectedClients => connectedClients.Values.Sum();

        // Per-camera accessors with int keys
        public long GetRequestsAddedToStream(int cameraId) => requestsAddedToStream.GetOrAdd(cameraId, 0);

        public long GetRequestsReadFromStream(int cameraId) => requestsReadFromStream.GetOrAdd(cameraId, 0);

        public double GetRequestsAddedPerSecond(int cameraId) => requestsAddedPerSecond.GetOrAdd(cameraId, 0);

        public double GetRequestsReadPerSecond(int cameraId) => requestsReadPerSecond.GetOrAdd(cameraId, 0);

        public long GetErrorsFromStream(int cameraId) => errorsFromStream.GetOrAdd(cameraId, 0);

        // Existing methods
        public void IncrementRequestsProcessed() => Interlocked.Increment(ref totalRequestsProcessed);

        public void IncrementCaptureRequests() => Interlocked.Increment(ref totalCaptureRequests);

        public void IncrementFramesProcessed() => Interlocked.Increment(ref totalFramesProcessed);

        public void IncrementFailedRequests() => Interlocked.Increment(ref failedRequests);

        public void IncrementDroppedFrames() => Interlocked.Increment(ref droppedFrames);

        public void IncrementActiveRequests() => Interlocked.Increment(ref activeRequests);

        public void DecrementActiveRequests() => Interlocked.Decrement(ref activeRequests);

        public void AddProcessingTime(int milliseconds) => Interlocked.Add(ref totalProcessingTimeMs, milliseconds);

        public void IncrementRequestsAddedToStream(int cameraId)
        {
            requestsAddedToStream.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        public void IncrementRequestsReadFromStream(int cameraId)
        {
            requestsReadFromStream.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        public void IncrementErrorsFromStream(int cameraId)
        {
            errorsFromStream.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        public void SetEncoderStreamConnected(bool isConnected)
        {
            if (isConnected)
            {
                Interlocked.CompareExchange(ref isEncoderStreamConnected, 1, 0);
            }
        }

        public void AddError(int cameraId, string error, ILogger logger) // Changed to int
        {
            var cameraDetails = $"Camera ID: {cameraId}";
            var fullErrorMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {cameraDetails} - {error}";

            logger.LogError(fullErrorMessage);

            if (errorQueue.Count >= errorQueueCapacity)
                errorQueue.TryDequeue(out _);
            errorQueue.Enqueue(fullErrorMessage);

            cameraErrorCounts.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
        }

        private async Task StartStreamTrackingAsync(CancellationToken cancellationToken)
        {
            const int intervalSeconds = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var cameraId in requestsAddedToStream.Keys)
                    {
                        long currentAdded = GetRequestsAddedToStream(cameraId);
                        long previousAdded = lastRequestsAddedToStream.GetOrAdd(cameraId, 0);
                        double addedPerSec = (currentAdded - previousAdded) / (double)intervalSeconds;
                        requestsAddedPerSecond[cameraId] = addedPerSec;
                        lastRequestsAddedToStream[cameraId] = currentAdded;
                    }

                    foreach (var cameraId in requestsReadFromStream.Keys)
                    {
                        long currentRead = GetRequestsReadFromStream(cameraId);
                        long previousRead = lastRequestsReadFromStream.GetOrAdd(cameraId, 0);
                        double readPerSec = (currentRead - previousRead) / (double)intervalSeconds;
                        requestsReadPerSecond[cameraId] = readPerSec;
                        lastRequestsReadFromStream[cameraId] = currentRead;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Stream tracking error: {ex.Message}");
                }
            }
        }

        public void SetConnectedClients(int cameraId, int count)
        {
            connectedClients[cameraId] = count; // Direct set since this is an external update
        }

        private async Task StartRPSTrackingAsync(CancellationToken cancellationToken)
        {
            const int intervalSeconds = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    long currentCount = TotalRequestsProcessed;
                    long requestsInInterval = currentCount - lastRequestCount;
                    double rps = requestsInInterval / (double)intervalSeconds;

                    Interlocked.Exchange(ref requestsPerSecond, rps);
                    lastRequestCount = currentCount;

                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RPS tracking error: {ex.Message}");
                }
            }
        }

        private async Task StartFPSTrackingAsync(CancellationToken cancellationToken)
        {
            const int intervalSeconds = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    long currentCount = TotalFramesProcessed;
                    long framesInInterval = currentCount - lastFrameCount;
                    double fps = framesInInterval / (double)intervalSeconds;

                    Interlocked.Exchange(ref framesProcessedPerSecond, fps);
                    lastFrameCount = currentCount;

                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RequestsSentPerSecond tracking error: {ex.Message}");
                }
            }
        }

        public Task WaitForShutdownAsync() => Task.WhenAll(rpsTrackingTask, fpsTrackingTask, streamTrackingTask);
    }
}
