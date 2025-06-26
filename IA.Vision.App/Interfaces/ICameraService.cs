using IA.Vision.App.Models;

namespace IA.Vision.App.Interfaces
{
    public interface ICameraService
    {
        Task<bool> ConnectAsync();

        Task<bool> StartStreamingAsync();

        Task StopStreamingAsync();

        Task DisconnectAsync();

        ImageFrame? GetFrameByEncoderValue(long encoderValue);

        bool IsConnected { get; }

        bool IsStreaming { get; }

        long EncoderValue { get; set; }

        double Fps { get; }

        long FrameCount { get; }

        string LastError { get; }

        StreamingState StreamingState { get; }

        CameraConfiguration Configuration { get; }

        //Handle in the event streaming stops
        event Action? OnStreamingStopped;
    }

    public enum StreamingState
    {
        NotStarted,
        Starting,
        Active,
        Failed
    }
}
