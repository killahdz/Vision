using IA.Vision.App.Interfaces;
using IA.Vision.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

public class CameraServiceFactory : ICameraServiceFactory
{
    private readonly IServiceProvider serviceProvider;

    public CameraServiceFactory(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public ICameraService Create(CameraConfiguration cameraConfig, ServerOptions server)
    {
        // Define the path to the log file using the camera ID
        var logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", cameraConfig.Id.ToString());
        var logFilePath = Path.Combine(logFolder, "camera_service_log.txt");

        // Ensure the log folder exists
        if (!Directory.Exists(logFolder))
        {
            Directory.CreateDirectory(logFolder);
        }

        // Create a Serilog logger specifically for this camera
        var cameraLogger = new LoggerConfiguration()
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .Enrich.WithProperty("CameraId", cameraConfig.Id)
            .CreateLogger();

        // Create a Microsoft.Extensions.Logging logger using the Serilog logger
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(cameraLogger);
        });

        // Create a logger for the CameraService
        var cameraServiceLogger = loggerFactory.CreateLogger<GigECameraService>();

        // Return the camera service, passing the camera-specific logger
        return new GigECameraService(Options.Create(cameraConfig), cameraServiceLogger, Options.Create(server));
    }
}
