using IA.Vision.App.Interfaces;
using IA.Vision.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Serilog;

namespace IA.Vision.App
{
    public static class Program
    {
        private static async Task Main(string[] args)
        {
            await CreateHostBuilder(args).RunConsoleAsync();
        }

        private static void ConfigureLogging(ILoggingBuilder logging, LogLevel logLevel = LogLevel.Information)
        {
            logging.ClearProviders();

            // Ensure logs directory exists
            var logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logsDirectory))
            {
                Directory.CreateDirectory(logsDirectory);
            }

            // Configure Serilog with multiple sinks
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                // Global application log (excluding errors)
                .WriteTo.Logger(lc => lc
                    .Filter.ByExcluding(e => e.Level == Serilog.Events.LogEventLevel.Error)
                    .WriteTo.File(
                        Path.Combine(logsDirectory, "app_log.txt"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
                    ))
                // Global error log
                .WriteTo.Logger(lc => lc
                    .Filter.ByIncludingOnly(e => e.Level == Serilog.Events.LogEventLevel.Error)
                    .WriteTo.File(
                        Path.Combine(logsDirectory, "errors.txt"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
                    ))
                .Enrich.FromLogContext()
                .CreateLogger();

            logging.AddSerilog();

            logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "[HH:mm:ss] ";
                options.UseUtcTimestamp = true;
                options.ColorBehavior = LoggerColorBehavior.Enabled;
            });

            logging.AddDebug();
            logging.SetMinimumLevel(logLevel);
        }

        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            return new HostBuilder()
                .ConfigureHostConfiguration(configHost =>
                {
                    configHost.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                    configHost.AddJsonFile("hostsettings.json", optional: false);
                    configHost.AddEnvironmentVariables(prefix: "DOTNET_");
                    if (args != null)
                    {
                        configHost.AddCommandLine(args);
                    }
                })
                .ConfigureAppConfiguration((hostContext, configBuilder) =>
                {
                    configBuilder.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                    configBuilder.AddJsonFile("appsettings.json", optional: false);
                    configBuilder.AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true);
                    if (args != null)
                    {
                        configBuilder.AddCommandLine(args);
                    }
                })
                .ConfigureServices((hostContext, services) =>
                {
                    ConfigureServices(hostContext.Configuration, services);
                })
                .ConfigureLogging((hostContext, logging) =>
                {
                    var serverOptions = hostContext.Configuration.GetSection(nameof(ServerOptions)).Get<ServerOptions>();
                    ConfigureLogging(logging, serverOptions?.LoggingLevel ?? LogLevel.Information);
                });
        }

        public static void ConfigureServices(IConfiguration configuration, IServiceCollection services)
        {
            services.Configure<ServerOptions>(configuration.GetSection(nameof(ServerOptions)));
            services.Configure<CameraOptions>(configuration.GetSection(nameof(CameraOptions)));

            services.AddLogging();
            services.AddSingleton<VisionMonitorService>();
            services.AddSingleton<ImageAcquisitionServiceImpl>();
            services.AddHostedService<ImageAcquisitionServer>();
            services.AddSingleton<ICameraServiceFactory, CameraServiceFactory>();

            var cameraOptions = configuration.GetSection(nameof(CameraOptions)).Get<CameraOptions>();
            var serverOptions = configuration.GetSection(nameof(ServerOptions)).Get<ServerOptions>();
            if (cameraOptions?.Cameras != null)
            {
                foreach (var cameraConfig in cameraOptions.Cameras)
                {
                    services.AddTransient<ICameraService>(sp =>
                    {
                        var factory = sp.GetRequiredService<ICameraServiceFactory>();
                        return factory.Create(cameraConfig, serverOptions);
                    });
                }
            }
        }
    }
}
