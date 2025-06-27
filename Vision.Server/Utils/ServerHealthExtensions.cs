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
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IA.Vision.App.Interfaces;
using IA.Vision.App.Services;
using GigeVision.Core.Interfaces;

namespace IA.Vision.App.Utils
{
    public static class ServerHealthExtensions
    {
        public static string GetCameraServiceDetails(this ICameraService cameraService)
        {
            var config = cameraService.Configuration;

            return $@"
───────────────────────────────────────────────────────────────────────────────
                           Camera Service Details
───────────────────────────────────────────────────────────────────────────────
Is Connected        : {cameraService.IsConnected}
Is Streaming        : {cameraService.IsStreaming}
Streaming State     : {cameraService.StreamingState}
Camera Address      : {config.CameraAddress}
Receiver Address    : {config.ReceiverAddress}:{config.ReceiverPort}
ImageStrip Port     : {config.EndpointPort}
Resolution          : {config.Width}x{config.Height}
Pixel Format        : {config.PixelFormat}
Offset (X, Y)       : ({config.OffsetX}, {config.OffsetY})
───────────────────────────────────────────────────────────────────────────────
";
        }

        public static string GetGigECameraStatus(this ICamera camera)
        {
            if (camera == null) { return string.Empty; }

            return $@"
───────────────────────────────────────────────────────────────────────────────
                         GigE Camera Configuration
───────────────────────────────────────────────────────────────────────────────
Camera Address      : {camera?.IP}
Receiver Address    : {camera?.RxIP}:{camera?.PortRx}
Resolution          : {camera?.Width}x{camera?.Height}
Pixel Format        : {camera?.PixelFormat}
Is Streaming        : {camera?.IsStreaming}
Is Multicast        : {camera?.IsMulticast}
Is Raw Frame        : {camera?.IsRawFrame}
───────────────────────────────────────────────────────────────────────────────
";
        }

        public static string GetConfigurationSummary(this CameraConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return $@"
───────────────────────────────────────────────────────────────────────────────
                             Camera Configuration
───────────────────────────────────────────────────────────────────────────────
ID                : {config.Id}
Name              : {config.Name}
Camera Address    : {config.CameraAddress}
Receiver Address  : {config.ReceiverAddress}:{config.ReceiverPort}
ImageStrip Port   : {config.EndpointPort}
Resolution        : {config.Width}x{config.Height}
Pixel Format      : {config.PixelFormat}
Offset (X, Y)     : ({config.OffsetX}, {config.OffsetY})
Accent            : {config.Accent}
───────────────────────────────────────────────────────────────────────────────
";
        }
    }
}

