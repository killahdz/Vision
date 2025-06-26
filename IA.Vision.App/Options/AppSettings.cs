public class AppSettings
{
    public ServerOptions ServerOptions { get; set; }
    public CameraOptions Camera { get; set; }

    public void PostConfigure()
    {
        if (Camera?.Cameras?.Any() ?? false)
        {
            throw new ApplicationException("No Cameras configured in appSettings");
        }
    }
}
