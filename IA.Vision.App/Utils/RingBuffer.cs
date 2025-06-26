using System.Collections.Concurrent;
using IA.Vision.App.Models;

namespace IA.Vision.App.Utils
{
    /// <summary>
    /// A thread-safe circular buffer for storing and retrieving image frames in real-time video processing.
    /// Supports efficient frame history, FPS calculation, and encoder-based retrieval.
    /// </summary>
    public sealed class RingBuffer : IDisposable
    {
        private readonly ImageFrame[] frameBuffer; // Circular buffer for all frames
        private int bufferIndex; // Current write position
        private readonly int capacity; // Fixed buffer size
        private long frameCount; // Total frames processed
        private readonly ReaderWriterLockSlim @lock = new(); // Thread synchronization
        private bool isDisposed; // Disposal flag

        // Store frames by encoder value (allows duplicates) and timestamp (for FPS)
        private readonly ConcurrentDictionary<long, LinkedList<ImageFrame>> framesByEncoder = new();

        private readonly SortedList<DateTimeOffset, ImageFrame> framesByTimestamp = new();

        /// <summary>
        /// Gets or sets the time window (in seconds) for FPS calculation. Must be positive.
        /// </summary>
        public int FpsWindowSeconds
        {
            get => _fpsWindowSeconds;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "FPS window must be positive.");
                _fpsWindowSeconds = value;
            }
        }

        private int _fpsWindowSeconds = 5;

        /// <summary>
        /// Initializes a new instance of the RingBuffer with the specified capacity.
        /// </summary>
        /// <param name="capacity">The maximum number of frames to store. Must be greater than 0.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if capacity is not positive.</exception>
        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Buffer capacity must be greater than 0.");

            this.capacity = capacity;
            frameBuffer = new ImageFrame[capacity];
            bufferIndex = 0;
            frameCount = 0;
            isDisposed = false;
        }

        /// <summary>
        /// Gets the total number of frames processed by the buffer.
        /// </summary>
        public long FrameCount => Interlocked.Read(ref frameCount);

        /// <summary>
        /// Calculates the frames-per-second (FPS) based on frames within the FPS window.
        /// </summary>
        public double Fps
        {
            get
            {
                @lock.EnterReadLock();
                try
                {
                    if (framesByTimestamp.Count < 2)
                        return 0.0;

                    var timeSpan = framesByTimestamp.Keys.Last() - framesByTimestamp.Keys.First();
                    double totalSeconds = timeSpan.TotalSeconds;
                    return totalSeconds > 0 ? (framesByTimestamp.Count - 1) / totalSeconds : 0.0;
                }
                finally
                {
                    @lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Adds a new frame to the buffer, updating the circular buffer and lookup collections.
        /// </summary>
        /// <param name="frame">The frame to enqueue. Must not be null and must have non-empty data.</param>
        /// <exception cref="ArgumentNullException">Thrown if frame is null.</exception>
        public void Enqueue(ImageFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (frame.Data == null || frame.Data.Length == 0)
                return; // Silently ignore invalid frames

            @lock.EnterWriteLock();
            try
            {
                // Add to circular buffer
                frameBuffer[bufferIndex] = frame;
                bufferIndex = (bufferIndex + 1) % capacity;
                Interlocked.Increment(ref frameCount);

                // Add to timestamp list for FPS calculation (unique by timestamp)
                if (!framesByTimestamp.ContainsKey(frame.Timestamp))
                    framesByTimestamp.Add(frame.Timestamp, frame);

                // Add to encoder dictionary (allow duplicates via LinkedList)
                framesByEncoder.AddOrUpdate(
                    frame.EncoderValue,
                    _ => new LinkedList<ImageFrame>(new[] { frame }),
                    (_, list) =>
                    {
                        list.AddLast(frame);
                        return list;
                    });

                // Trim old frames outside FPS window
                TrimOldFrames();
            }
            finally
            {
                @lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Retrieves the most recent frame matching the specified encoder value. If no exact match exists,
        /// returns the latest frame in the buffer.
        /// </summary>
        /// <param name="encoderValue">The encoder value to search for.</param>
        /// <returns>The latest matching frame, latest buffered frame if no match, or null if buffer is empty.</returns>
        public ImageFrame? GetFrameByEncoderValue(long encoderValue)
        {
            @lock.EnterReadLock();
            try
            {
                // Try to get the latest frame for the exact encoder value
                if (framesByEncoder.TryGetValue(encoderValue, out var frameList) && frameList.Count > 0)
                {
                    var frame = frameList.Last.Value;
                    frame.RequestEncoderValue = encoderValue;
                    return frame;
                }

                // Fallback to the most recent frame in the circular buffer
                if (frameCount > 0)
                {
                    int latestIndex = (bufferIndex - 1 + capacity) % capacity;
                    var latestFrame = frameBuffer[latestIndex];
                    if (latestFrame != null)
                    {
                        latestFrame.RequestEncoderValue = encoderValue;
                        return latestFrame;
                    }
                }

                return null; // Buffer is empty or uninitialized
            }
            finally
            {
                @lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Removes frames older than the FPS window to maintain performance and accuracy.
        /// </summary>
        private void TrimOldFrames()
        {
            var cutoffTime = DateTimeOffset.UtcNow.AddSeconds(-FpsWindowSeconds);
            while (framesByTimestamp.Count > 0 && framesByTimestamp.Keys[0] < cutoffTime)
            {
                var oldestFrame = framesByTimestamp.Values[0];
                framesByTimestamp.RemoveAt(0);

                // Remove from encoder dictionary if it’s the last frame for that encoder
                if (framesByEncoder.TryGetValue(oldestFrame.EncoderValue, out var list))
                {
                    list.Remove(oldestFrame);
                    if (list.Count == 0)
                        framesByEncoder.TryRemove(oldestFrame.EncoderValue, out _);
                }
            }
        }

        /// <summary>
        /// Disposes of the RingBuffer, releasing all resources.
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
                return;

            @lock.EnterWriteLock();
            try
            {
                framesByEncoder.Clear();
                framesByTimestamp.Clear();
                Array.Clear(frameBuffer, 0, capacity);
                isDisposed = true;
            }
            finally
            {
                @lock.ExitWriteLock();
                @lock.Dispose();
            }
        }
    }
}
