using NAudio.CoreAudioApi;

namespace MediaController;

/// <summary>Volumen maestro de Windows (el mismo que mueve el control de volumen de la barra de tareas).</summary>
public sealed class VolumeService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    // Se pide el dispositivo en cada llamada para que siga funcionando si cambias de parlantes/audífonos.
    private MMDevice Device() => _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

    public float GetVolume()
    {
        using var d = Device();
        return d.AudioEndpointVolume.MasterVolumeLevelScalar;
    }

    public void SetVolume(float value)
    {
        using var d = Device();
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    public bool GetMute()
    {
        using var d = Device();
        return d.AudioEndpointVolume.Mute;
    }

    public void SetMute(bool mute)
    {
        using var d = Device();
        d.AudioEndpointVolume.Mute = mute;
    }

    public void Dispose() => _enumerator.Dispose();
}
