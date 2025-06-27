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
    /// <summary>
    /// Main entry point for the Image Acquisition Server application.
    /// Configures and starts the host with dependency injection, logging, and configuration settings.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Application entry point. Initializes and runs the host asynchronously.
        /// </summary>
        /// <param name="args">Command-line arguments for configuration overrides.</param>
        /// <returns>A task representing the asynchronous operation of the application.</returns>
        public static async Task Main(string[] args)
        {
            await CreateHostBuilder(args).RunConsoleAsync();
        }

        /// <summary>
        /// Configures logging with Serilog for file and console output, with separate logs for errors.
        /// </summary>
        /// <param name="logging">The logging builder to configure.</param>
        /// <param name="logLevel">Minimum log level for the application (default: Information).</param>
        private static void ConfigureLogging(ILoggingBuilder logging, LogLevel logLevel = LogLevel.Information)
        {
            // Clear any default logging providers to ensure consistent logging configuration
            logging.ClearProviders();

            // Ensure the logs directory exists for file-based logging
            var logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(logsDirectory);

            // Configure Serilog with separate sinks for application logs and error logs
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                // Application logs (excluding errors) written to a rolling daily file
                .WriteTo.Logger(lc => lc
                    .Filter.ByExcluding(e => e.Level == Serilog.Events.LogEventLevel.Error)
                    .WriteTo.File(
                        Path.Combine(logsDirectory, "app_log.txt"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
                    ))
                // Error logs written to a separate rolling daily file
                .WriteTo.Logger(lc => lc
                    .Filter.ByIncludingOnly(e => e.Level == Serilog.Events.LogEventLevel.Error)
                    .WriteTo.File(
                        Path.Combine(logsDirectory, "errors.txt"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
                    ))
                .Enrich.FromLogContext()
                .CreateLogger();

            // Add Serilog to the Microsoft.Extensions.Logging pipeline
            logging.AddSerilog();

            // Configure console logging with timestamps and color support
            logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "[HH:mm:ss] ";
                options.UseUtcTimestamp = true;
                options.ColorBehavior = LoggerColorBehavior.Enabled;
            });

            // Add debug logging for development environments
            logging.AddDebug();
            logging.SetMinimumLevel(logLevel);
        }

        /// <summary>
        /// Creates and configures the host builder with configuration, services, and logging.
        /// </summary>
        /// <param name="args">Command-line arguments for configuration overrides.</param>
        /// <returns>A configured host builder ready to run the application.</returns>
        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureHostConfiguration(configHost =>
                {
                    // Set base path for configuration files
                    configHost.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                    // Load host settings from JSON file
                    configHost.AddJsonFile("hostsettings.json", optional: false);
                    // Include environment variables with DOTNET_ prefix
                    configHost.AddEnvironmentVariables(prefix: "DOTNET_");
                    // Include command-line arguments if provided
                    if (args != null)
                    {
                        configHost.AddCommandLine(args);
                    }
                })
                .ConfigureAppConfiguration((hostContext, configBuilder) =>
                {
                    // Set base path for application configuration files
                    configBuilder.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                    // Load main application settings
                    configBuilder.AddJsonFile("appsettings.json", optional: false);
                    // Load environment-specific settings (optional)
                    configBuilder.AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true);
                    // Include command-line arguments if provided
                    if (args != null)
                    {
                        configBuilder.AddCommandLine(args);
                    }
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // Configure dependency injection services
                    ConfigureServices(hostContext.Configuration, services);
                })
                .ConfigureLogging((hostContext, logging) =>
                {
                    // Retrieve logging level from configuration, default to Information
                    var serverOptions = hostContext.Configuration.GetSection(nameof(ServerOptions)).Get<ServerOptions>();
                    ConfigureLogging(logging, serverOptions?.LoggingLevel ?? LogLevel.Information);
                });
        }

        /// <summary>
        /// Configures dependency injection services for the application.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="services">The service collection to register services with.</param>
        public static void ConfigureServices(IConfiguration configuration, IServiceCollection services)
        {
            // Bind configuration sections to strongly-typed option classes
            services.Configure<ServerOptions>(configuration.GetSection(nameof(ServerOptions)));
            services.Configure<CameraOptions>(configuration.GetSection(nameof(CameraOptions)));

            // Register core services with dependency injection
            services.AddLogging();
            services.AddSingleton<VisionMonitorService>();
            services.AddSingleton<ImageAcquisitionServiceImpl>();
            services.AddHostedService<ImageAcquisitionServer>();
            services.AddSingleton<ICameraServiceFactory, CameraServiceFactory>();

            // Register camera services based on configuration
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
