using System.Collections.Concurrent;
using IA.Vision.App.Services;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App.Utils
{
    public class FileWriteQueue
    {
        private readonly ConcurrentQueue<(byte[] data, string filePath, int width, int height)> writeQueue = new();
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly Task processingTask;
        private readonly ILogger logger;
        private readonly SemaphoreSlim semaphore;
        private volatile bool isAddingCompleted = false;
        private readonly object counterLock = new();
        private long fileCounter = 0;

        public FileWriteQueue(CancellationTokenSource cancellationTokenSource, int maxConcurrentWrites)
        {
            this.cancellationTokenSource = cancellationTokenSource;
            this.logger = VisionLoggerFactory.CreateFileOperationsLogger();
            semaphore = new SemaphoreSlim(maxConcurrentWrites == 0 ? Environment.ProcessorCount : maxConcurrentWrites);
            processingTask = Task.Run(ProcessQueueAsync, this.cancellationTokenSource.Token);
        }

        public void EnqueueWrite(byte[] data, string filePath, int width, int height)
        {
            if (isAddingCompleted)
            {
                throw new InvalidOperationException("Cannot enqueue new writes after completion has been signaled.");
            }

            writeQueue.Enqueue((data, filePath, width, height));
        }

        public void Complete()
        {
            isAddingCompleted = true;
        }

        public async Task WaitForCompletionAsync()
        {
            Complete();
            await processingTask;
        }

        private async Task ProcessQueueAsync()
        {
            while (!cancellationTokenSource.Token.IsCancellationRequested || !isAddingCompleted || !writeQueue.IsEmpty)
            {
                if (writeQueue.TryDequeue(out var item))
                {
                    await semaphore.WaitAsync(cancellationTokenSource.Token); // Limit concurrent writes
                    _ = ProcessItemAsync(item).ContinueWith(_ => semaphore.Release()); // Release semaphore when done
                }
                else
                {
                    await Task.Yield(); // Yield to avoid busy-waiting
                }
            }
        }

        private async Task ProcessItemAsync((byte[] data, string filePath, int width, int height) item)
        {
            try
            {
                var directory = Path.GetDirectoryName(item.filePath) ?? string.Empty;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var filepath = GetFilePathWithCounter(item.filePath);
                await File.WriteAllBytesAsync(filepath, item.data);
            }
            catch (Exception)
            {
                //should log the exception
            }
        }

        private string GetFilePathWithCounter(string filePath)
        {
            long counter;
            lock (counterLock)
            {
                counter = ++fileCounter;
            }

            var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);

            return Path.Combine(directory, $"{fileNameWithoutExtension}_{counter}{extension}");
        }

        public void Stop()
        {
            cancellationTokenSource.Cancel();
            processingTask.Wait();
        }

        public int GetQueueCount()
        {
            return writeQueue.Count;
        }

        //More efficient way to get a count without locking
        public int GetQueueCountNoLock()
        {
            return writeQueue.Count;
        }
    }
}
