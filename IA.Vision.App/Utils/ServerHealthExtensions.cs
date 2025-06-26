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
