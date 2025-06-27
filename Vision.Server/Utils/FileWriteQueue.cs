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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IA.Vision.App.Services;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App.Utils
{
    /// <summary>
    /// Manages a thread-safe queue for asynchronous file writing operations, ensuring controlled concurrent writes.
    /// </summary>
    public class FileWriteQueue
    {
        private readonly ConcurrentQueue<(byte[] data, string filePath, int width, int height)> _writeQueue = new();
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _processingTask;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _semaphore;
        private volatile bool _isAddingCompleted;
        private readonly object _counterLock = new();
        private long _fileCounter;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileWriteQueue"/> class.
        /// </summary>
        /// <param name="cancellationTokenSource">The cancellation token source to manage task cancellation.</param>
        /// <param name="maxConcurrentWrites">The maximum number of concurrent write operations. If 0, defaults to the number of processor cores.</param>
        public FileWriteQueue(CancellationTokenSource cancellationTokenSource, int maxConcurrentWrites)
        {
            _cancellationTokenSource = cancellationTokenSource ?? throw new ArgumentNullException(nameof(cancellationTokenSource));
            _logger = VisionLoggerFactory.CreateFileOperationsLogger();
            _semaphore = new SemaphoreSlim(maxConcurrentWrites == 0 ? Environment.ProcessorCount : maxConcurrentWrites);
            _processingTask = Task.Run(ProcessQueueAsync, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Enqueues a file write operation with the specified data and metadata.
        /// </summary>
        /// <param name="data">The byte array to write to the file.</param>
        /// <param name="filePath">The target file path.</param>
        /// <param name="width">The width of the image (for metadata purposes).</param>
        /// <param name="height">The height of the image (for metadata purposes).</param>
        /// <exception cref="InvalidOperationException">Thrown if attempting to enqueue after completion is signaled.</exception>
        public void EnqueueWrite(byte[] data, string filePath, int width, int height)
        {
            if (_isAddingCompleted)
            {
                throw new InvalidOperationException("Cannot enqueue new writes after completion has been signaled.");
            }

            if (data == null || string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("Invalid enqueue attempt: Data or file path is null or empty.");
                return;
            }

            _writeQueue.Enqueue((data, filePath, width, height));
        }

        /// <summary>
        /// Signals that no more items will be enqueued, allowing the queue to process remaining items.
        /// </summary>
        public void Complete()
        {
            _isAddingCompleted = true;
            _logger.LogInformation("File write queue marked as complete. Processing remaining items.");
        }

        /// <summary>
        /// Waits for all queued write operations to complete.
        /// </summary>
        /// <returns>A task representing the asynchronous completion of all write operations.</returns>
        public async Task WaitForCompletionAsync()
        {
            Complete();
            try
            {
                await _processingTask.ConfigureAwait(false);
                _logger.LogInformation("File write queue processing completed.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("File write queue processing was canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing file write queue processing.");
            }
        }

        /// <summary>
        /// Processes items from the queue asynchronously, writing files with controlled concurrency.
        /// </summary>
        /// <returns>A task representing the asynchronous queue processing operation.</returns>
        private async Task ProcessQueueAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested || !_isAddingCompleted || !_writeQueue.IsEmpty)
            {
                if (_writeQueue.TryDequeue(out var item))
                {
                    try
                    {
                        await _semaphore.WaitAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
                        _ = ProcessItemAsync(item).ContinueWith(t =>
                        {
                            _semaphore.Release();
                            if (t.IsFaulted)
                            {
                                _logger.LogError(t.Exception, "Error processing file write item.");
                            }
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("File write queue processing canceled.");
                        _semaphore.Release();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error dequeuing item from file write queue.");
                        _semaphore.Release();
                    }
                }
                else
                {
                    await Task.Yield(); // Yield to avoid busy-waiting
                }
            }

            _logger.LogInformation("File write queue processing stopped.");
        }

        /// <summary>
        /// Processes a single queue item by writing the data to a file.
        /// </summary>
        /// <param name="item">The tuple containing the data, file path, and image dimensions.</param>
        /// <returns>A task representing the asynchronous file write operation.</returns>
        private async Task ProcessItemAsync((byte[] data, string filePath, int width, int height) item)
        {
            try
            {
                var directory = Path.GetDirectoryName(item.filePath) ?? string.Empty;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}.", directory);
                }

                var filepath = GetFilePathWithCounter(item.filePath);
                await File.WriteAllBytesAsync(filepath, item.data, _cancellationTokenSource.Token).ConfigureAwait(false);
                _logger.LogDebug("Wrote file: {FilePath}, Size: {Size} bytes.", filepath, item.data.Length);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("File write operation canceled for {FilePath}.", item.filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing file: {FilePath}.", item.filePath);
            }
        }

        /// <summary>
        /// Generates a unique file path by appending a counter to the file name.
        /// </summary>
        /// <param name="filePath">The base file path.</param>
        /// <returns>A unique file path with a counter suffix.</returns>
        private string GetFilePathWithCounter(string filePath)
        {
            long counter;
            lock (_counterLock)
            {
                counter = ++_fileCounter;
            }

            var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);

            return Path.Combine(directory, $"{fileNameWithoutExtension}_{counter}{extension}");
        }

        /// <summary>
        /// Stops the file write queue, canceling pending operations and waiting for completion.
        /// </summary>
        public void Stop()
        {
            try
            {
                _cancellationTokenSource.Cancel();
                _processingTask.Wait(5000); // Wait up to 5 seconds for graceful shutdown
                _logger.LogInformation("File write queue stopped.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("File write queue stop operation canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping file write queue.");
            }
        }

        /// <summary>
        /// Gets the current number of items in the queue (thread-safe).
        /// </summary>
        /// <returns>The number of items in the queue.</returns>
        public int GetQueueCount()
        {
            return _writeQueue.Count;
        }

        /// <summary>
        /// Gets the current number of items in the queue without locking (less accurate but more efficient).
        /// </summary>
        /// <returns>The approximate number of items in the queue.</returns>
        public int GetQueueCountNoLock()
        {
            return _writeQueue.Count;
        }
    }
}
