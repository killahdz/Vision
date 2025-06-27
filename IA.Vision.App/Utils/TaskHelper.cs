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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App.Utils
{
    /// <summary>
    /// Provides utility methods for managing tasks, including timeout handling and resource cleanup.
    /// </summary>
    public static class TaskHelper
    {
        /// <summary>
        /// Waits for a task to complete within a specified timeout, handles cancellation, and disposes of resources if applicable.
        /// </summary>
        /// <param name="task">The task to monitor and potentially dispose of.</param>
        /// <param name="timeout">The maximum time to wait for the task to complete.</param>
        /// <param name="cancellationToken">A cancellation token to monitor for external cancellation requests.</param>
        /// <param name="logger">The logger for recording task execution and disposal events.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="task"/> or <paramref name="logger"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="timeout"/> is negative or zero.</exception>
        public static async Task DisposeWithTimeoutAsync(this Task task, TimeSpan timeout, CancellationToken cancellationToken, ILogger logger)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

            // Create a timeout task to enforce the specified timeout period
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                // Wait for either the task to complete or the timeout/cancellation to occur
                var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, linkedCts.Token)).ConfigureAwait(false);

                if (completedTask == task)
                {
                    try
                    {
                        // Ensure task completion and handle any exceptions
                        await task.ConfigureAwait(false);
                        logger.LogInformation("Task completed successfully.");
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogInformation("Task was canceled.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error occurred while executing the task.");
                    }
                }
                else
                {
                    logger.LogWarning("Timeout occurred while waiting for the task to finish after {TimeoutMs}ms.", timeout.TotalMilliseconds);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Task wait operation was canceled.");
            }

            // Clean up disposable resources if the task supports it
            if (task is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    logger.LogInformation("Task resources disposed successfully.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error disposing task resources.");
                }
            }
        }
    }
}
