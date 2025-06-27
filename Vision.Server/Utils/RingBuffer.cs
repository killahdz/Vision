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
using System.Collections.Generic;
using System.Threading;
using IA.Vision.App.Models;

namespace IA.Vision.App.Utils
{
    /// <summary>
    /// A thread-safe circular buffer for storing and retrieving image frames in real-time video processing.
    /// Supports efficient frame history management, FPS calculation, and encoder-based frame retrieval.
    /// </summary>
    public sealed class RingBuffer : IDisposable
    {
        private readonly ImageFrame[] _frameBuffer; // Circular buffer for storing frames
        private int _bufferIndex; // Current write position in the circular buffer
        private readonly int _capacity; // Fixed size of the buffer
        private long _frameCount; // Total number of frames processed
        private readonly ReaderWriterLockSlim _lock = new(); // Synchronization for thread-safe access
        private bool _isDisposed; // Flag to track disposal status
        private int _fpsWindowSeconds = 5; // Default FPS window (5 seconds)

        // Lookup collections for frames by encoder value and timestamp
        private readonly ConcurrentDictionary<long, LinkedList<ImageFrame>> _framesByEncoder = new();
        private readonly SortedList<DateTimeOffset, ImageFrame> _framesByTimestamp = new();

        /// <summary>
        /// Gets or sets the time window (in seconds) for FPS calculation. Must be positive.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is not positive.</exception>
        public int FpsWindowSeconds
        {
            get => _fpsWindowSeconds;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "FPS window must be positive.");
                _fpsWindowSeconds = value;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RingBuffer"/> class with the specified capacity.
        /// </summary>
        /// <param name="capacity">The maximum number of frames to store. Must be greater than 0.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if capacity is not positive.</exception>
        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Buffer capacity must be greater than 0.");

            _capacity = capacity;
            _frameBuffer = new ImageFrame[capacity];
            _bufferIndex = 0;
            _frameCount = 0;
            _isDisposed = false;
        }

        /// <summary>
        /// Gets the total number of frames processed by the buffer in a thread-safe manner.
        /// </summary>
        public long FrameCount => Interlocked.Read(ref _frameCount);

        /// <summary>
        /// Calculates the frames-per-second (FPS) based on frames within the configured FPS window.
        /// </summary>
        /// <remarks>
        /// Returns 0 if there are fewer than two frames or if the time span is zero.
        /// </remarks>
        public double Fps
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    if (_framesByTimestamp.Count < 2)
                        return 0.0;

                    var timeSpan = _framesByTimestamp.Keys.Last() - _framesByTimestamp.Keys.First();
                    double totalSeconds = timeSpan.TotalSeconds;
                    return totalSeconds > 0 ? (_framesByTimestamp.Count - 1) / totalSeconds : 0.0;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Adds a new frame to the buffer, updating the circular buffer and lookup collections.
        /// </summary>
        /// <param name="frame">The frame to enqueue. Must not be null and must have non-empty data.</param>
        /// <exception cref="ArgumentNullException">Thrown if the frame is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown if the buffer has been disposed.</exception>
        public void Enqueue(ImageFrame frame)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(RingBuffer));
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (frame.Data == null || frame.Data.Length == 0)
                return; // Silently ignore invalid frames

            _lock.EnterWriteLock();
            try
            {
                // Store in circular buffer
                _frameBuffer[_bufferIndex] = frame;
                _bufferIndex = (_bufferIndex + 1) % _capacity;
                Interlocked.Increment(ref _frameCount);

                // Add to timestamp list for FPS calculation (unique by timestamp)
                if (!_framesByTimestamp.ContainsKey(frame.Timestamp))
                    _framesByTimestamp.Add(frame.Timestamp, frame);

                // Add to encoder dictionary (allow duplicates via LinkedList)
                _framesByEncoder.AddOrUpdate(
                    frame.EncoderValue,
                    _ => new LinkedList<ImageFrame>(new[] { frame }),
                    (_, list) =>
                    {
                        list.AddLast(frame);
                        return list;
                    });

                // Remove old frames to maintain performance
                TrimOldFrames();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Retrieves the most recent frame matching the specified encoder value or the latest frame if no match exists.
        /// </summary>
        /// <param name="encoderValue">The encoder value to search for.</param>
        /// <returns>The latest matching frame, the latest buffered frame if no match, or null if the buffer is empty.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the buffer has been disposed.</exception>
        public ImageFrame? GetFrameByEncoderValue(long encoderValue)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(RingBuffer));

            _lock.EnterReadLock();
            try
            {
                // Try to retrieve the latest frame for the encoder value
                if (_framesByEncoder.TryGetValue(encoderValue, out var frameList) && frameList.Count > 0)
                {
                    var frame = frameList.Last.Value;
                    frame.RequestEncoderValue = encoderValue;
                    return frame;
                }

                // Fallback to the most recent frame in the circular buffer
                if (_frameCount > 0)
                {
                    int latestIndex = (_bufferIndex - 1 + _capacity) % _capacity;
                    var latestFrame = _frameBuffer[latestIndex];
                    if (latestFrame != null)
                    {
                        latestFrame.RequestEncoderValue = encoderValue;
                        return latestFrame;
                    }
                }

                return null; // Buffer is empty
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Removes frames older than the FPS window to maintain performance and accuracy.
        /// </summary>
        private void TrimOldFrames()
        {
            var cutoffTime = DateTimeOffset.UtcNow.AddSeconds(-FpsWindowSeconds);
            while (_framesByTimestamp.Count > 0 && _framesByTimestamp.Keys[0] < cutoffTime)
            {
                var oldestFrame = _framesByTimestamp.Values[0];
                _framesByTimestamp.RemoveAt(0);

                // Remove from encoder dictionary if no frames remain for that encoder
                if (_framesByEncoder.TryGetValue(oldestFrame.EncoderValue, out var list))
                {
                    list.Remove(oldestFrame);
                    if (list.Count == 0)
                        _framesByEncoder.TryRemove(oldestFrame.EncoderValue, out _);
                }
            }
        }

        /// <summary>
        /// Disposes of the <see cref="RingBuffer"/>, releasing all resources.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _lock.EnterWriteLock();
            try
            {
                _framesByEncoder.Clear();
                _framesByTimestamp.Clear();
                Array.Clear(_frameBuffer, 0, _capacity);
                _isDisposed = true;
            }
            finally
            {
                _lock.ExitWriteLock();
                _lock.Dispose();
            }
        }
    }
}
