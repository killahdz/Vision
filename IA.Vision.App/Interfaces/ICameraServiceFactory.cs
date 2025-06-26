namespace IA.Vision.App.Interfaces
{
    public interface ICameraServiceFactory
    {
        ICameraService Create(CameraConfiguration cameraConfig, ServerOptions server);
    }
}
