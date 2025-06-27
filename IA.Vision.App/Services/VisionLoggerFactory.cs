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
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;

namespace IA.Vision.App.Services
{

    /*
    * Logging Directory Structure
    * ==========================
    * The application creates a logging directory structure under the base directory of the application
    * (AppDomain.CurrentDomain.BaseDirectory). Logs are organized into global and camera-specific
    * subdirectories, with daily rolling log files for each component.
    *
    * Base Directory
    * └── Logs/
    *     ├── app_log.txt              // Daily rolling log for non-error application events (Information, Debug, etc.)
    *     ├── errors.txt               // Daily rolling log for error events only
    *     ├── file_operations.txt      // Daily rolling log for global file operations (when cameraId is not specified)
    *     └── <CameraId>/              // Subdirectory for each camera (e.g., Camera1, Camera2)
    *         ├── camera_service_log.txt // Daily rolling log for camera service operations
    *         ├── messages_out.txt       // Daily rolling log for message output from CameraStreamingService
    *         └── file_operations.txt    // Daily rolling log for camera-specific file operations
    *
    * Notes:
    * - All log files roll daily (e.g., app_log.2025-06-27.txt, errors.2025-06-27.txt).
    * - The Logs directory and camera-specific subdirectories are created automatically if they don't exist.
    * - Log files use a consistent format: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}".
    * - Camera-specific logs include a "CameraId" property for traceability.
    */

    /// <summary>
    /// Provides factory methods for creating specialized loggers for various components in the vision system,
    /// with support for global and camera-specific logging configurations.
    /// </summary>
    public static class VisionLoggerFactory
    {
        /// <summary>
        /// Creates a specialized logger for a specific component, optionally scoped to a camera.
        /// </summary>
        /// <param name="componentName">The name of the component for which the logger is created.</param>
        /// <param name="logFileName">The name of the log file to write to.</param>
        /// <param name="cameraId">The optional camera ID to scope the logger to a specific camera.</param>
        /// <returns>An <see cref="ILogger"/> instance configured for the specified component and camera (if provided).</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="componentName"/> or <paramref name="logFileName"/> is null or empty.</exception>
        public static Microsoft.Extensions.Logging.ILogger CreateLogger(string componentName, string logFileName, string? cameraId = null)
        {
            if (string.IsNullOrEmpty(componentName))
                throw new ArgumentNullException(nameof(componentName), "Component name must not be null or empty.");
            if (string.IsNullOrEmpty(logFileName))
                throw new ArgumentNullException(nameof(logFileName), "Log file name must not be null or empty.");

            string logFilePath;

            try
            {
                // Determine log file path based on whether cameraId is provided
                if (string.IsNullOrEmpty(cameraId))
                {
                    // Global component log
                    var logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                    logFilePath = Path.Combine(logsDirectory, logFileName);

                    if (!Directory.Exists(logsDirectory))
                    {
                        Directory.CreateDirectory(logsDirectory);
                    }
                }
                else
                {
                    // Camera-specific component log
                    var logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", cameraId);
                    logFilePath = Path.Combine(logFolder, logFileName);

                    if (!Directory.Exists(logFolder))
                    {
                        Directory.CreateDirectory(logFolder);
                    }
                }
            }
            catch (Exception ex)
            {
                // Fallback to console logging if directory creation fails
                Console.WriteLine($"Error creating log directory for {componentName}: {ex.Message}");
                throw new IOException($"Failed to create log directory for {componentName}.", ex);
            }

            // Configure Serilog with file output and component enrichment
            var loggerConfiguration = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .Enrich.WithProperty("Component", componentName);

            if (!string.IsNullOrEmpty(cameraId))
            {
                loggerConfiguration = loggerConfiguration.Enrich.WithProperty("CameraId", cameraId);
            }

            var serilogLogger = loggerConfiguration.CreateLogger();

            // Create logger factory and integrate Serilog
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(serilogLogger, dispose: true);
            });

            return loggerFactory.CreateLogger(componentName);
        }

        /// <summary>
        /// Creates a logger for camera service operations, scoped to a specific camera.
        /// </summary>
        /// <param name="cameraId">The ID of the camera to scope the logger to.</param>
        /// <returns>An <see cref="ILogger"/> instance for camera service logging.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="cameraId"/> is null or empty.</exception>
        public static Microsoft.Extensions.Logging.ILogger CreateCameraServiceLogger(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId), "Camera ID must not be null or empty.");

            return CreateLogger("CameraService", "camera_service_log.txt", cameraId);
        }

        /// <summary>
        /// Creates a logger for message output operations in the CameraStreamingService, scoped to a specific camera.
        /// </summary>
        /// <param name="cameraId">The ID of the camera to scope the logger to.</param>
        /// <returns>An <see cref="ILogger"/> instance for message output logging.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="cameraId"/> is null or empty.</exception>
        public static Microsoft.Extensions.Logging.ILogger CreateMessageOutputLogger(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId), "Camera ID must not be null or empty.");

            return CreateLogger("MessageOutput", "messages_out.txt", cameraId);
        }

        /// <summary>
        /// Creates a logger for file operations, optionally scoped to a specific camera.
        /// </summary>
        /// <param name="cameraId">The optional camera ID to scope the logger to.</param>
        /// <returns>An <see cref="ILogger"/> instance for file operations logging.</returns>
        public static Microsoft.Extensions.Logging.ILogger CreateFileOperationsLogger(string? cameraId = null)
        {
            return CreateLogger("FileOperations", "file_operations.txt", cameraId);
        }
    }
}
