namespace IA.Vision.App.Tests.ImageAcquisition
{
    using System.Collections.Concurrent;
    using System.Threading.Tasks;
    using IA.Vision.App.Interfaces;
    using IA.Vision.App.Models;
    using IA.Vision.App.Utils;

    public class MockCameraService : ICameraService
    {
        public MockCameraService(string ip)
        {
            Configuration = new CameraConfiguration { CameraAddress = ip };
            FrameBuffer = new ConcurrentQueue<ImageFrame?>();
        }

        public ConcurrentQueue<ImageFrame?> FrameBuffer { get; set; }

        public bool IsConnected { get; private set; } = false;
        public bool IsStreaming { get; private set; } = false;
        public CameraConfiguration Configuration { get; set; }

        public StreamingState StreamingState => StreamingState.NotStarted;

        public double Fps => 1;

        public long FrameCount => 1;

        public string LastError => throw new NotImplementedException();

        public long EncoderValue { get; set; } = -1;

        public event Action? OnStreamingStopped;

        public Task<bool> ConnectAsync()
        {
            IsConnected = true;
            return Task.FromResult(true);
        }

        public Task<bool> StartStreamingAsync()
        {
            IsStreaming = true;
            Task.Run(() =>
            {
                for (int i = 0; i < 10; i++) // Simulate streaming frames
                {
                    byte[] data = new byte[100]; // Simulated image data
                                                 //   frameCallback(data, 640, 480, DateTime.Now.Ticks);
                    Task.Delay(50).Wait(); // Simulate delay between frames
                }
            });
            return Task.FromResult(IsStreaming);
        }

        public Task StopStreamingAsync()
        {
            IsStreaming = false;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ImageFrame? GetFrameByTimestamp(DateTimeOffset timestamp) => FrameBuffer?.Last();

        public ImageFrame[] GetAllFrames() => FrameBuffer?.ToArray();

        public ImageFrame? GetFrameByEncoderValue(long encoderValue) => FrameBuffer?.First();

        public Task ResetSDKAsync()
        {
            throw new NotImplementedException();
        }
    }
}
