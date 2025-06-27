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
using IA.Vision.App.Interfaces;
using IA.Vision.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace IA.Vision.App
{
    /// <summary>
    /// Factory for creating <see cref="ICameraService"/> instances, specifically <see cref="GigECameraService"/>,
    /// with camera-specific logging and configuration.
    /// </summary>
    public class CameraServiceFactory : ICameraServiceFactory
    {
        private readonly IServiceProvider _serviceProvider; // Service provider for dependency injection

        /// <summary>
        /// Initializes a new instance of the <see cref="CameraServiceFactory"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="serviceProvider"/> is null.</exception>
        public CameraServiceFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        /// <summary>
        /// Creates a new <see cref="ICameraService"/> instance for a specific camera with its configuration and logger.
        /// </summary>
        /// <param name="cameraConfig">The camera configuration.</param>
        /// <param name="server">The server configuration options.</param>
        /// <returns>An <see cref="ICameraService"/> instance configured for the specified camera.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="cameraConfig"/> or <paramref name="server"/> is null.</exception>
        /// <exception cref="IOException">Thrown if the log directory cannot be created.</exception>
        public ICameraService Create(CameraConfiguration cameraConfig, ServerOptions server)
        {
            if (cameraConfig == null)
                throw new ArgumentNullException(nameof(cameraConfig));
            if (server == null)
                throw new ArgumentNullException(nameof(server));

            // Define the log file path for the camera
            var logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", cameraConfig.Id.ToString());
            var logFilePath = Path.Combine(logFolder, "camera_service_log.txt");

            // Ensure the log folder exists
            try
            {
                if (!Directory.Exists(logFolder))
                {
                    Directory.CreateDirectory(logFolder);
                }
            }
            catch (Exception ex)
            {
                // Log to console as a fallback and throw an exception
                Console.WriteLine($"Error creating log directory for camera {cameraConfig.Id}: {ex.Message}");
                throw new IOException($"Failed to create log directory for camera {cameraConfig.Id}.", ex);
            }

            // Configure a Serilog logger for the camera
            var cameraLogger = new LoggerConfiguration()
                .MinimumLevel.Debug() // Set minimum level to Debug for detailed logging
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .Enrich.WithProperty("CameraId", cameraConfig.Id)
                .CreateLogger();

            // Create a logger factory and integrate Serilog
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(cameraLogger, dispose: true); // Ensure Serilog logger is disposed
            });

            // Create a logger for GigECameraService
            var cameraServiceLogger = loggerFactory.CreateLogger<GigECameraService>();

            // Create and return the camera service
            return new GigECameraService(
                Options.Create(cameraConfig),
                cameraServiceLogger,
                Options.Create(server));
        }
    }
}
