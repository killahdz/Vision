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
using System.Threading;
using System.Threading.Tasks;
using IA.Vision.App.Services;
using IA.Vision.Rpc.Services;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IA.Vision.App
{
    /// <summary>
    /// Implements the gRPC server for the image acquisition application, managing the lifecycle of the
    /// <see cref="ImageAcquisitionServiceImpl"/> and binding it to a specified IP and port.
    /// </summary>
    public class ImageAcquisitionServer : Server, IHostedService
    {
        private readonly ImageAcquisitionServiceImpl _imageAcquisitionService; // Core service for image acquisition
        private readonly ILogger<ImageAcquisitionServiceImpl> _logger; // Logger for server operations

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageAcquisitionServer"/> class.
        /// </summary>
        /// <param name="imageAcquisitionService">The image acquisition service implementation.</param>
        /// <param name="options">The server configuration options.</param>
        /// <param name="implLogger">The logger for server and service operations.</param>
        /// <exception cref="ArgumentNullException">Thrown if any parameter is null.</exception>
        public ImageAcquisitionServer(
            ImageAcquisitionServiceImpl imageAcquisitionService,
            IOptions<ServerOptions> options,
            ILogger<ImageAcquisitionServiceImpl> implLogger) : base()
        {
            _imageAcquisitionService = imageAcquisitionService ?? throw new ArgumentNullException(nameof(imageAcquisitionService));
            _logger = implLogger ?? throw new ArgumentNullException(nameof(implLogger));

            // Validate server options
            if (options?.Value == null)
                throw new ArgumentNullException(nameof(options), "Server options must not be null.");

            // Register the image acquisition service with the gRPC server
            Services.Add(ImageAcquisition.BindService(_imageAcquisitionService));

            // Bind the server to the configured IP and port
            var ip = options.Value.IP;
            var port = options.Value.PortNumber;
            Ports.Add(ip, port, ServerCredentials.Insecure);

            _logger.LogInformation("Binding gRPC server to address {IP}:{Port}", ip, port);
        }

        /// <summary>
        /// Starts the gRPC server and the underlying image acquisition service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the start operation.</param>
        /// <returns>A task representing the asynchronous start operation.</returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Starting gRPC server...");
                Start(); // Start the gRPC server
                await _imageAcquisitionService.StartAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("gRPC server and image acquisition service started successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting gRPC server or image acquisition service.");
                throw;
            }
        }

        /// <summary>
        /// Stops the gRPC server and the underlying image acquisition service gracefully.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the stop operation.</param>
        /// <returns>A task representing the asynchronous stop operation.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Stopping gRPC server...");
                await _imageAcquisitionService.StopAsync(cancellationToken).ConfigureAwait(false);
                await ShutdownAsync().ConfigureAwait(false);
                _logger.LogInformation("gRPC server and image acquisition service stopped successfully.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stop operation was canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping gRPC server or image acquisition service.");
            }
        }
    }
}
