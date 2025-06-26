using System.Net.NetworkInformation;
using IA.Vision.App.Services;
using Microsoft.Extensions.Logging;

namespace IA.Vision.App.Utils
{
    public class NetworkUtils
    {
        private readonly ILogger<GigECameraService> logger;
        private readonly int jumboFrameSize = 9000;

        public NetworkUtils(ILogger<GigECameraService> logger)
        {
            this.logger = logger;
        }

        public async Task<bool> CanPingJumboFrameAsync(string ipAddress)
        {
            try
            {
                // Prepare the ping object with a desired payload size (jumbo frame size)
                var pingSender = new Ping();
                var options = new PingOptions(128, true); // TTL = 128, Don't fragment

                // Create a byte array with the size of the jumbo frame
                var buffer = new byte[jumboFrameSize];

                // Perform the ping and check the response
                var reply = await pingSender.SendPingAsync(ipAddress, 1000, buffer, options); // 1000 ms timeout

                if (reply.Status == IPStatus.Success)
                {
                    logger.LogInformation($"Successfully pinged {ipAddress} with {jumboFrameSize} bytes without fragmentation.");
                    return true;
                }
                else
                {
                    logger.LogWarning($"Failed to ping {ipAddress} with {jumboFrameSize} bytes. Status: {reply.Status}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error pinging jumbo frame size to {ipAddress}: {ex.Message}");
                return false;
            }
        }
    }
}
