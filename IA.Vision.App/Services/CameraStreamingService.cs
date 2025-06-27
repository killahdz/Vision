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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using IA.Vision.App.Services;
using IA.Vision.Rpc.Services;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App
{
    /// <summary>
    /// Implements a gRPC streaming service for delivering image strips from a specific camera to clients.
    /// Manages client connections, image strip streaming, and telemetry tracking.
    /// </summary>
    public class CameraStreamingService : ImageProcessing.ImageProcessingBase
    {
        private readonly ILogger<CameraStreamingService> _logger; // Logger for streaming service operations
        private readonly ILogger _messageLogger; // Logger for message output
        private readonly gRPCStreamHandler<ImageStripStreamRequest, ImageStrip> _imageStripStreamHandler = new(); // Handles gRPC stream consumers
        private readonly BlockingCollection<ImageStrip> _outgoingImageStrips = new(); // Queue for outgoing image strips
        private readonly Server _server; // gRPC server instance
        private readonly TelemetryTracker _telemetry; // Telemetry tracker for metrics

        /// <summary>
        /// Gets the camera ID associated with this streaming service.
        /// </summary>
        public int CameraId { get; }

        /// <summary>
        /// Gets the stream address in the format "IP:Port".
        /// </summary>
        public string StreamAddress { get; }

        /// <summary>
        /// Gets the number of currently connected clients.
        /// </summary>
        public int ConnectedClients => _imageStripStreamHandler.ConsumerCount;

        /// <summary>
        /// Gets the total number of requests added to the stream for this camera.
        /// </summary>
        public long RequestsAddedToStream => _telemetry.GetRequestsAddedToStream(CameraId);

        /// <summary>
        /// Gets the total number of requests read from the stream for this camera.
        /// </summary>
        public long RequestsReadFromStream => _telemetry.GetRequestsReadFromStream(CameraId);

        /// <summary>
        /// Gets the total number of errors from the stream for this camera.
        /// </summary>
        public long ErrorsFromStream => _telemetry.GetErrorsFromStream(CameraId);

        /// <summary>
        /// Initializes a new instance of the <see cref="CameraStreamingService"/> class.
        /// </summary>
        /// <param name="ip">The IP address to bind the gRPC server to.</param>
        /// <param name="port">The port to bind the gRPC server to.</param>
        /// <param name="cameraId">The ID of the camera associated with this service.</param>
        /// <param name="logger">The logger for streaming service operations.</param>
        /// <param name="applicationLifetime">The application lifetime for handling shutdown events.</param>
        /// <param name="telemetry">The telemetry tracker for monitoring stream metrics.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="logger"/>, <paramref name="applicationLifetime"/>, or <paramref name="telemetry"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown if <paramref name="ip"/> is empty or <paramref name="port"/> is invalid.</exception>
        public CameraStreamingService(
            string ip,
            int port,
            int cameraId,
            ILogger<CameraStreamingService> logger,
            IHostApplicationLifetime applicationLifetime,
            TelemetryTracker telemetry)
        {
            if (string.IsNullOrEmpty(ip))
                throw new ArgumentException("IP address must not be null or empty.", nameof(ip));
            if (port <= 0)
                throw new ArgumentException("Port number must be positive.", nameof(port));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            CameraId = cameraId;
            _messageLogger = VisionLoggerFactory.CreateMessageOutputLogger(cameraId.ToString());
            StreamAddress = $"{ip}:{port}";

            // Initialize telemetry with zero connected clients
            _telemetry.SetConnectedClients(CameraId, 0);

            // Initialize and start the gRPC server
            _server = new Server();
            _server.Ports.Add(ip, port, ServerCredentials.Insecure);
            _server.Services.Add(ImageProcessing.BindService(this));
            _server.Start();
            _logger.LogInformation("Started gRPC streaming server for camera {CameraId} on {StreamAddress}", CameraId, StreamAddress);

            // Start background task for processing image strips
            Task.Run(() => ProcessImageStripStreamAsync(applicationLifetime?.ApplicationStopping ?? CancellationToken.None));

            // Register shutdown handler
            if (applicationLifetime != null)
            {
                applicationLifetime.ApplicationStopping.Register(() =>
                {
                    try
                    {
                        _outgoingImageStrips.CompleteAdding();
                        _imageStripStreamHandler.Stop();
                        _telemetry.SetConnectedClients(CameraId, 0);
                        _logger.LogInformation("Shutdown initiated for camera {CameraId} streaming service.", CameraId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during shutdown of camera {CameraId} streaming service.", CameraId);
                    }
                });
            }
        }

        /// <summary>
        /// Handles incoming gRPC stream requests, delivering image strips to clients.
        /// </summary>
        /// <param name="request">The stream request containing client parameters.</param>
        /// <param name="responseStream">The server stream writer for sending image strips.</param>
        /// <param name="context">The gRPC server call context.</param>
        /// <returns>A task representing the asynchronous stream handling operation.</returns>
        public override async Task ImageStripStream(ImageStripStreamRequest request, IServerStreamWriter<ImageStrip> responseStream, ServerCallContext context)
        {
            _logger.LogInformation("New image strip stream request for camera {CameraId} from {Peer}", CameraId, context.Peer);

            // Update telemetry for connected clients
            int newClientCount = _imageStripStreamHandler.ConsumerCount + 1;
            _telemetry.SetConnectedClients(CameraId, newClientCount);

            try
            {
                await _imageStripStreamHandler.HandleStreamConsumerAsync(responseStream, context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Image strip stream failed for camera {CameraId}, peer {Peer}", CameraId, context.Peer);
                _telemetry.IncrementErrorsFromStream(CameraId);
            }
            finally
            {
                // Update telemetry on client disconnect or failure
                _telemetry.SetConnectedClients(CameraId, _imageStripStreamHandler.ConsumerCount);
                _logger.LogInformation("Stream closed for camera {CameraId}, peer {Peer}. Current clients: {ConsumerCount}",
                    CameraId, context.Peer, _imageStripStreamHandler.ConsumerCount);
            }
        }

        /// <summary>
        /// Posts an image strip to the outgoing queue for streaming to clients.
        /// </summary>
        /// <param name="imageStrip">The image strip to post.</param>
        public void PostImageStrip(ImageStrip imageStrip)
        {
            if (imageStrip == null)
            {
                _logger.LogWarning("Attempted to post null image strip for camera {CameraId}", CameraId);
                return;
            }

            try
            {
                if (!_outgoingImageStrips.IsAddingCompleted)
                {
                    _outgoingImageStrips.Add(imageStrip);
                    _telemetry.IncrementRequestsAddedToStream(CameraId);
                    _messageLogger.LogInformation(
                        "Posted image strip: CameraID={CameraId}, EncoderValue={EncoderValue}, Size={Size} bytes",
                        imageStrip.CameraID, imageStrip.EncoderValue, imageStrip.Image.Length);
                }
                else
                {
                    _logger.LogWarning("Cannot post image strip for camera {CameraId}: Outgoing queue is completed.", CameraId);
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Error posting image strip for camera {CameraId}: Queue is completed or disposed.", CameraId);
                _telemetry.IncrementErrorsFromStream(CameraId);
            }
        }

        /// <summary>
        /// Processes image strips from the outgoing queue and streams them to connected clients.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the processing task.</param>
        /// <returns>A task representing the asynchronous processing operation.</returns>
        private async Task ProcessImageStripStreamAsync(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var strip in _outgoingImageStrips.GetConsumingEnumerable(cancellationToken))
                {
                    try
                    {
                        await _imageStripStreamHandler.WriteToConsumersAsync(strip).ConfigureAwait(false);
                        _telemetry.IncrementRequestsReadFromStream(CameraId);
                        _messageLogger.LogInformation(
                            "Streamed image strip: CameraID={CameraId}, EncoderValue={EncoderValue}, Consumers={ConsumerCount}",
                            strip.CameraID, strip.EncoderValue, _imageStripStreamHandler.ConsumerCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error streaming image strip for camera {CameraId}, encoder {Encoder}", CameraId, strip.EncoderValue);
                        _telemetry.IncrementErrorsFromStream(CameraId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Image strip streaming canceled for camera {CameraId}.", CameraId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in image strip streaming task for camera {CameraId}.", CameraId);
                _telemetry.IncrementErrorsFromStream(CameraId);
            }
        }

        /// <summary>
        /// Shuts down the streaming service, stopping the gRPC server and completing the outgoing queue.
        /// </summary>
        /// <returns>A task representing the asynchronous shutdown operation.</returns>
        public async Task ShutdownAsync()
        {
            try
            {
                _logger.LogInformation("Shutting down streaming service for camera {CameraId}.", CameraId);

                // Complete adding to the outgoing queue
                if (!_outgoingImageStrips.IsAddingCompleted)
                {
                    _outgoingImageStrips.CompleteAdding();
                    _logger.LogInformation("Outgoing image strip queue completed for camera {CameraId}.", CameraId);
                }

                // Stop the stream handler and gRPC server
                _imageStripStreamHandler.Stop();
                await _server.ShutdownAsync().ConfigureAwait(false);
                _logger.LogInformation("gRPC server shut down successfully for camera {CameraId}.", CameraId);

                // Reset telemetry for connected clients
                _telemetry.SetConnectedClients(CameraId, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error shutting down streaming service for camera {CameraId}.", CameraId);
            }
        }
    }
}
