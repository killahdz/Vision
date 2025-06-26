namespace IA.Vision.App.Models
{
    // ImageFrame class to represent a frame with data and metadata
    public class ImageFrame
    {
        public byte[] Data { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public long CameraId { get; init; }
        public long RequestEncoderValue { get; set; }
        public long EncoderValue { get; set; }
        public bool IsRequestValid { get; set; }
    }
}
