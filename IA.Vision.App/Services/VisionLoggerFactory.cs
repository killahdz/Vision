namespace IA.Vision.App.Services
{
    using System;
    using System.IO;
    using Microsoft.Extensions.Logging;
    using Serilog;

    public static class VisionLoggerFactory
    {
        /// <summary>
        /// Creates a specialized logger for a specific component, optionally scoped to a camera
        /// </summary>
        public static Microsoft.Extensions.Logging.ILogger CreateLogger(string componentName, string logFileName, string cameraId = null)
        {
            string logFilePath;

            if (string.IsNullOrEmpty(cameraId))
            {
                // Global component log
                var logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                logFilePath = Path.Combine(logsDirectory, logFileName);

                if (!Directory.Exists(logsDirectory))
                    Directory.CreateDirectory(logsDirectory);
            }
            else
            {
                // Camera-specific component log
                var logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", cameraId);
                logFilePath = Path.Combine(logFolder, logFileName);

                if (!Directory.Exists(logFolder))
                    Directory.CreateDirectory(logFolder);
            }

            var logger = new LoggerConfiguration()
                .WriteTo.File(
                    logFilePath,
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .Enrich.WithProperty("Component", componentName);

            if (!string.IsNullOrEmpty(cameraId))
                logger = logger.Enrich.WithProperty("CameraId", cameraId);

            var serilogLogger = logger.CreateLogger();

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(serilogLogger);
            });

            return loggerFactory.CreateLogger(componentName);
        }

        /// <summary>
        /// Creates a camera service logger
        /// </summary>
        public static Microsoft.Extensions.Logging.ILogger CreateCameraServiceLogger(string cameraId)
        {
            return CreateLogger("CameraService", "camera_service_log.txt", cameraId);
        }

        /// <summary>
        /// Creates a message output logger for CameraStreamingService
        /// </summary>
        public static Microsoft.Extensions.Logging.ILogger CreateMessageOutputLogger(string cameraId)
        {
            return CreateLogger("MessageOutput", "messages_out.txt", cameraId);
        }

        /// <summary>
        /// Creates a file operations logger, optionally scoped to a camera
        /// </summary>
        public static Microsoft.Extensions.Logging.ILogger CreateFileOperationsLogger(string cameraId = null)
        {
            return CreateLogger("FileOperations", "file_operations.txt", cameraId);
        }
    }
}
