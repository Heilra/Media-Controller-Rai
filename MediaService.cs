using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using MediaPlaybackType = Windows.Media.MediaPlaybackType;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace MediaController;

/// <summary>Foto del estado actual de la reproducción, lista para pintar en la interfaz.</summary>
public sealed class MediaSnapshot
{
    public bool HasSession;
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string Source = "";
    public bool IsPlaying;
    public bool IsVideo;
    public TimeSpan Position;
    public TimeSpan Duration;
    public bool CanPrev, CanNext, CanSeek;
    public bool CanShuffle, ShuffleActive;
    public BitmapSource? Cover;
    public Color? CoverColor;
}

/// <summary>
/// Lee y controla lo que suena en Windows usando SMTC (System Media Transport Controls),
/// la misma fuente que usa el panel multimedia nativo de Windows.
/// </summary>
public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSession? _subscribed;
    private string? _sessionId;

    private volatile bool _propsDirty = true;
    private DateTime _lastPropsFetch = DateTime.MinValue;
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private BitmapSource? _cover;
    private Color? _coverColor;
    private bool _coverIsRectangular;
    private int _coverRetries;
    private TimeSpan _startTime = TimeSpan.Zero;

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    }

    /// <summary>
    /// Elige la fuente activa: si la que Windows considera "actual" está sonando, esa;
    /// si no, cualquiera que esté sonando; si ninguna suena, la actual.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? Pick()
    {
        if (_manager == null) return null;

        var current = _manager.GetCurrentSession();
        if (current != null && IsPlaying(current)) return current;

        foreach (var s in _manager.GetSessions())
            if (IsPlaying(s)) return s;

        return current ?? _manager.GetSessions().FirstOrDefault();
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == PlaybackStatus.Playing; }
        catch { return false; }
    }

    public async Task<MediaSnapshot> PollAsync()
    {
        var snap = new MediaSnapshot();
        if (_manager == null) return snap;

        GlobalSystemMediaTransportControlsSession? s;
        try { s = Pick(); } catch { s = null; }
        _session = s;

        if (s == null)
        {
            Unsubscribe();
            _sessionId = null;
            ResetProps();
            return snap;
        }

        try
        {
            if (s.SourceAppUserModelId != _sessionId)
                Subscribe(s);

            // Metadatos: cuando Windows avisa, o cada 3 s como respaldo.
            if (_propsDirty || (DateTime.UtcNow - _lastPropsFetch) > TimeSpan.FromSeconds(3))
                await RefreshPropsAsync(s);

            var playback = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            var controls = playback.Controls;

            snap.Title = _title;
            snap.Artist = _artist;
            snap.Album = _album;
            snap.Cover = _cover;
            snap.CoverColor = _coverColor;
            snap.Source = s.SourceAppUserModelId ?? "";
            snap.IsPlaying = playback.PlaybackStatus == PlaybackStatus.Playing;
            snap.CanPrev = controls.IsPreviousEnabled;
            snap.CanNext = controls.IsNextEnabled;
            snap.CanSeek = controls.IsPlaybackPositionEnabled;
            snap.CanShuffle = controls.IsShuffleEnabled;
            snap.ShuffleActive = playback.IsShuffleActive == true;

            // ¿Video o audio? 1) el tipo que informa Windows; 2) si no sirve, la forma de la miniatura
            // (rectangular = video, cuadrada = audio); 3) si no hay miniatura, se trata como audio.
            MediaPlaybackType? type = playback.PlaybackType;
            if (type == MediaPlaybackType.Video) snap.IsVideo = true;
            else if (type == MediaPlaybackType.Music) snap.IsVideo = false;
            else snap.IsVideo = _coverIsRectangular;

            _startTime = timeline.StartTime;
            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;

            // Windows no actualiza la posición cada segundo: la estimamos desde la última actualización.
            if (snap.IsPlaying && duration > TimeSpan.Zero)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(6))
                    position += elapsed;
            }

            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration) position = duration;

            snap.Position = position;
            snap.Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            snap.HasSession = true;
        }
        catch
        {
            // La sesión pudo cerrarse justo mientras la leíamos; el próximo ciclo se corrige solo.
            snap.HasSession = false;
        }

        return snap;
    }

    private async Task RefreshPropsAsync(GlobalSystemMediaTransportControlsSession s)
    {
        _propsDirty = false;
        _lastPropsFetch = DateTime.UtcNow;

        var props = await s.TryGetMediaPropertiesAsync();
        string title = props.Title ?? "";
        string artist = props.Artist ?? "";
        if (string.IsNullOrEmpty(artist)) artist = props.AlbumArtist ?? "";
        string album = props.AlbumTitle ?? "";

        bool changed = title != _title || artist != _artist || album != _album;
        _title = title;
        _artist = artist;
        _album = album;

        if (changed)
        {
            _cover = null;
            _coverColor = null;
            _coverIsRectangular = false;
            _coverRetries = 0;
        }

        if (_cover == null && _coverRetries < 5 && props.Thumbnail != null)
        {
            _coverRetries++;
            try
            {
                _cover = await LoadCoverAsync(props.Thumbnail);
                if (_cover != null)
                {
                    _coverColor = Theme.AverageColor(_cover);
                    _coverIsRectangular = _cover.PixelWidth >= 1.2 * _cover.PixelHeight;
                }
            }
            catch { }
        }
    }

    private static async Task<BitmapSource?> LoadCoverAsync(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0) return null;

        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 128;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void Subscribe(GlobalSystemMediaTransportControlsSession s)
    {
        Unsubscribe();
        _subscribed = s;
        s.MediaPropertiesChanged += OnPropsChanged;
        _sessionId = s.SourceAppUserModelId;
        ResetProps();
    }

    private void Unsubscribe()
    {
        if (_subscribed == null) return;
        try { _subscribed.MediaPropertiesChanged -= OnPropsChanged; }
        catch { }
        _subscribed = null;
    }

    private void ResetProps()
    {
        _title = "";
        _artist = "";
        _album = "";
        _cover = null;
        _coverColor = null;
        _coverIsRectangular = false;
        _coverRetries = 0;
        _propsDirty = true;
    }

    private void OnPropsChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _propsDirty = true;

    // ---- Comandos ----

    public async Task TogglePlayPauseAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SetShuffleAsync(bool active)
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryChangeShuffleActiveAsync(active); } catch { }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryChangePlaybackPositionAsync((position + _startTime).Ticks); } catch { }
    }

    public void Dispose() => Unsubscribe();
}
