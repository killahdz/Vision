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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App.Services
{
    /// <summary>
    /// Service for realtime monitoring, telemetry and diagnostics UI console output
    /// </summary>
    public class VisionMonitorService : BackgroundService
    {
        private const int ConsoleBaseheight = 30;
        private readonly ILogger<VisionMonitorService> logger;
        private readonly object consoleLock = new(); // Lock for console output
        private bool hasStarted = false;

        public VisionMonitorService(ILogger<VisionMonitorService> logger)
        {
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (hasStarted) return;
            logger.LogInformation("ExecuteAsync started. IsCancellationRequested: {IsCancellationRequested}", stoppingToken.IsCancellationRequested);

            hasStarted = true;
            // Keep the service alive until cancellation is requested
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }

        public void PrintServerHealth(CancellationToken cancellationToken, ImageAcquisitionServiceImpl server, bool refreshScreenOnUpdate = false)
        {
            lock (consoleLock)
            {
                if (!hasStarted)
                {
                    _ = ExecuteAsync(cancellationToken);
                    Console.WindowHeight = ConsoleBaseheight + server.CameraServices.Count * 2;     //cameras and streams
                }

                if (refreshScreenOnUpdate)
                {
                    Console.Clear();
                }

                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
                Console.WriteLine($"                                   ∞ Vision Acquisition Server | {server.ServerOptions.IP}:{server.ServerOptions.PortNumber} ∞");
                PrintCameraSummary(server.CameraServices, false);
                PrintPipelineStatus(server, false);
                PrintCameraStreams(server, false);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
                Console.Write($" Last Updated: {DateTime.UtcNow.ToString()}                                                     Image Acquisition Mode: ");
                Console.BackgroundColor = server.ImageAcquisitionMode == Rpc.Services.ImageAcquisitionMode.Run ? ConsoleColor.DarkGreen : ConsoleColor.DarkMagenta;
                Console.WriteLine($" {server.ImageAcquisitionMode} ");
                Console.BackgroundColor = ConsoleColor.Black;
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        private void PrintPipelineStatus(ImageAcquisitionServiceImpl server, bool refreshScreenOnUpdate = false)
        {
            // Pipeline Status section
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine($"  ♣ Pipeline Status                                              ");
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine(" | Encoder  | Encoder Stream            | Conn   | Data In      | Data In/s   | Data Out     | Data Out/s  | Errors   |");
            Console.ForegroundColor = ConsoleColor.White;
            // Encoder Value
            Console.Write(" | ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"{server.EncoderValue,-8}");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | ");

            // Encoder Stream Connected (Address)

            string streamAddress = $"{server.ServerOptions.EncoderStreamSettings.EncoderStreamAddress}:{server.ServerOptions.EncoderStreamSettings.EncoderStreamPort}";
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{streamAddress,-25}");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | ");

            //connected
            Console.ForegroundColor = server.IsEncoderStreamConnected ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"{(server.IsEncoderStreamConnected ? "Y" : "N"),-6}");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | ");

            //Total Requests
            Console.ForegroundColor = server.TotalRequestsProcessed == 0 ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write($"{server.TotalRequestsProcessed,-12}");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(" | ");

            //Requests per second
            Console.ForegroundColor = server.RequestsReceivedPerSecond == 0 ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write($"{server.RequestsReceivedPerSecond,-11:F1}");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(" | ");

            //Total Sent
            Console.ForegroundColor = server.TotalFramesProcessed > 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"{server.TotalFramesProcessed,-12}");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(" | ");

            //Sent Per Second
            Console.ForegroundColor = server.RequestsSentPerSecond == 0 ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write($"{server.RequestsSentPerSecond,-11:F1}");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(" | ");

            // Errors
            Console.ForegroundColor = server.CameraErrorCounts.Count > 0 ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write($"{server.CameraErrorCounts.Count,-8}");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(" | ");
        }

        private void PrintCameraSummary(IEnumerable<ICameraService> cameraServices, bool refreshScreenOnUpdate = false)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine("  ♥ Camera Health ");
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine(" | Camera   | IP Address         | Conn | Receiver Address      | Strm | Resolution | PixelFmt   | FPS     | Frames   |");
            Console.ForegroundColor = ConsoleColor.White;
            foreach (var cameraService in cameraServices)
            {
                var statusColor = cameraService.IsConnected ? ConsoleColor.Green : ConsoleColor.Red;
                var streamingColor = cameraService.IsStreaming ? ConsoleColor.Green : ConsoleColor.Red;
                var fpsColor = cameraService.Fps > 10 ? ConsoleColor.Green : cameraService.Fps == 0 ? ConsoleColor.Red : ConsoleColor.Yellow;
                var config = cameraService.Configuration;
                var accentColor = cameraService.IsConnected && cameraService.IsStreaming ? config.Accent : ConsoleColor.DarkGray;

                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(" | ");
                Console.BackgroundColor = accentColor;
                Console.Write($"{config.Name,-7} ");
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(" | ");
                Console.Write($"{config.CameraAddress,-18} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");
                Console.ForegroundColor = statusColor;
                Console.Write($"{(cameraService.IsConnected ? "Y" : "N"),-4} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");
                Console.Write($"{config.ReceiverAddress + ":" + config.ReceiverPort,-21} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");
                Console.ForegroundColor = streamingColor;
                Console.Write($"{(cameraService.IsStreaming ? "Y" : "N"),-4} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");
                Console.Write($"{config.Width}x{config.Height}".PadRight(11));
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");
                Console.Write($"{config.PixelFormat,-10} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");
                Console.ForegroundColor = fpsColor;
                Console.Write($"{cameraService.Fps:F1}".PadRight(8));
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");
                Console.ForegroundColor = fpsColor;
                Console.Write($"{cameraService.FrameCount}".PadRight(9));
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("|");
            }
        }

        private void PrintCameraStreams(ImageAcquisitionServiceImpl server, bool refreshScreenOnUpdate = false)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine("  ♠ Image Streams ");
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine(" | Camera   | Stream Address            | Subscr | Data In      | Data In/s   | Data Out     | Data Out/s  | Errors   |");
            foreach (var cameraService in server.CameraServices)
            {
                var config = cameraService.Configuration;
                var stream = server.CameraStreamingServices.FirstOrDefault(s => s.Key.Configuration.Id == config.Id).Value;
                var streamWriteTotal = server.GetRequestsAddedToStream(config.Id);
                var streamReadTotal = server.GetRequestsReadFromStream(config.Id);
                var streamWritePerSecond = server.GetRequestsAddedPerSecond(config.Id);
                var streamReadPerSecond = server.GetRequestsReadPerSecond(config.Id);
                var accentColor = stream.RequestsAddedToStream > 0 ? config.Accent : ConsoleColor.DarkGray;

                //camera name
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(" | ");
                Console.BackgroundColor = accentColor;
                Console.Write($"{config.Name,-7} ");
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(" | ");

                //stream address
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{stream.StreamAddress,-25:F1}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(" | ");

                //clients
                Console.ForegroundColor = stream.ConnectedClients == 0 ? ConsoleColor.Red : ConsoleColor.Green;
                Console.Write($"{stream.ConnectedClients,-6}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(" | ");

                //data in
                Console.ForegroundColor = streamWriteTotal > 0 ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write($"{streamWriteTotal,-13}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");

                //data in per second
                Console.ForegroundColor = streamWritePerSecond > 0 ? ConsoleColor.Green : ConsoleColor.Red; ;
                Console.Write($"{streamWritePerSecond,-12:F1}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");

                //data out
                Console.ForegroundColor = streamReadTotal > 0 ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write($"{streamReadTotal,-13}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");

                //data in per second
                Console.ForegroundColor = streamReadPerSecond > 0 ? ConsoleColor.Green : ConsoleColor.Red; ;
                Console.Write($"{streamReadPerSecond,-12:F1}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("| ");

                //errors
                Console.ForegroundColor = stream.ErrorsFromStream > 0 ? ConsoleColor.Red : ConsoleColor.Green;
                Console.Write($"{stream.ErrorsFromStream,-8}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(" |");
            }

            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
        }
    }
}

