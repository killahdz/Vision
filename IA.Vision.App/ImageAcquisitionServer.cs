using IA.Vision.App.Services;
using IA.Vision.Rpc.Services;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IA.Vision.App
{
    /// <summary>
    /// The server implementation for the image acquisition application
    /// </summary>
    public class ImageAcquisitionServer : Server, IHostedService
    {
        private readonly ImageAcquisitionServiceImpl imageAcquisitionService;
        private readonly ILogger<ImageAcquisitionServiceImpl> logger;

        public ImageAcquisitionServer(ImageAcquisitionServiceImpl imageAcquisitionService,
                                      IOptions<ServerOptions> options,
                                      ILogger<ImageAcquisitionServiceImpl> implLogger) : base()
        {
            this.imageAcquisitionService = imageAcquisitionService;
            this.logger = implLogger;

            // Register ImageAcquisitionService to the gRPC server
            Services.Add(ImageAcquisition.BindService(imageAcquisitionService));

            // Bind the server to the specified IP and Port from ServerOptions
            var ip = options.Value.IP;
            var port = options.Value.PortNumber;
            Ports.Add(ip, port, ServerCredentials.Insecure);

            // Log the address and port being used by the server
            logger.LogInformation("Binding gRPC server to address {IP}:{Port}", ip, port);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Start the gRPC server
            logger.LogInformation("Starting gRPC server...");
            Start();
            // Start the ImageAcquisition service
            return imageAcquisitionService.StartAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // Stop the ImageAcquisition service and shutdown the server
            logger.LogInformation("Stopping gRPC server...");
            await imageAcquisitionService.StopAsync(cancellationToken);
            await ShutdownAsync();
        }
    }
}
