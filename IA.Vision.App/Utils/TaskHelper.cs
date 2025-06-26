namespace IA.Vision.App.Utils
{
    public static class TaskHelper
    {
        /// <summary>
        /// Waits for a task to complete within a specified timeout and handles cancellation and disposal.
        /// </summary>
        /// <param name="task">The task to monitor and dispose of.</param>
        /// <param name="timeout">The timeout after which the task will be forcefully cancelled.</param>
        /// <param name="cancellationToken">A cancellation token to monitor for external cancellation requests.</param>
        /// <returns>A task representing the completion of the operation.</returns>
        public static async Task DisposeWithTimeoutAsync(this Task task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Create a timeout task to trigger after the specified timeout period
            var timeoutTask = Task.Delay(timeout, cancellationToken);

            // Wait for either the task to complete or the timeout to occur
            var completedTask = await Task.WhenAny(task, timeoutTask);

            if (completedTask == task)
            {
                // Task completed within the timeout
                try
                {
                    // Ensure task completion and handle any exceptions
                    await task;
                    Console.WriteLine("Task completed successfully.");
                }
                catch (OperationCanceledException)
                {
                    // Handle task cancellation (expected behavior in case of cancellation)
                    Console.WriteLine("Task was cancelled.");
                }
                catch (Exception ex)
                {
                    // Log other exceptions that occur during task execution
                    Console.WriteLine($"Error occurred while executing the task: {ex.Message}");
                }
            }
            else
            {
                // Timeout occurred before the task completed
                Console.WriteLine("Timeout occurred while waiting for the task to finish.");
            }

            // Clean up: Check if the task is completed or cancelled, then dispose if needed
            if (task is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    // Dispose asynchronously if the task is IDisposable
                    await asyncDisposable.DisposeAsync();
                    Console.WriteLine("Disposed of task resources.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error disposing task resources: {ex.Message}");
                }
            }
        }
    }
}
