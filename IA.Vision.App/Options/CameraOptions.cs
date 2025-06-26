using GigeVision.Core.Enums;

public class CameraOptions
{
    public uint GvcpPort { get; set; }
    public List<CameraConfiguration> Cameras { get; set; } = new();
}

public class CameraConfiguration
{
    public int Id { get; set; } = 0;
    public string Name { get; set; } = string.Empty;
    public string CameraAddress { get; set; } = string.Empty;
    public string ReceiverAddress { get; set; } = string.Empty;
    public uint ReceiverPort { get; set; }
    public int EndpointPort { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint OffsetX { get; set; }
    public uint OffsetY { get; set; }
    public PixelFormat PixelFormat { get; set; } = PixelFormat.BayerRG8;
    public bool EnforceJumboFrames { get; set; } = true;
    public ConsoleColor Accent { get; set; } = ConsoleColor.White;
    public string MacAddress { get; set; }
}
