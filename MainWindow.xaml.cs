using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaController;

public partial class MainWindow : Window
{
    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _topTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private IntPtr _hwnd;
    private bool _polling;
    private bool _draggingVolume;
    private double _progressRatio;
    private double _volumeRatio;
    private MediaSnapshot _last = new();

    // Aspecto
    private Color _currentBg = Theme.DefaultBg;
    private string _accentKey = "";

    // Historial (solo en memoria, últimas 5)
    private sealed class HistoryEntry
    {
        public string Key = "";
        public string Title = "";
        public string Text = "";
        public string Source = "";
        public DateTime AddedAt;
    }
    private readonly List<HistoryEntry> _history = new();
    private readonly DispatcherTimer _histCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private int _histAwayTicks;
    private bool _hasSession;

    // Separador entre la info de la canción y los controles
    private const double DefaultInfoWidth = 170;
    private const double MinInfoWidth = 60;
    private const double SplitterWidth = 9;
    private const double BasicsWidth = 94;         // anterior + play/pausa + siguiente (siempre visibles)
    private const double HistoryWidth = 34;        // botón de historial + margen
    private const double ShuffleWidth = 30;
    private const double VolumeButtonWidth = 28;
    private const double VolumeBarWidth = 104;     // barra (72) + espacio y número (32)
    private const double ProgressMaxWidth = 170;   // barra de progreso: ancho máximo (con márgenes)
    private const double ProgressMinWidth = 80;    // ancho que se le reserva al progreso antes de mostrar otro botón
    private double _infoPref = DefaultInfoWidth;   // lo que el usuario eligió (no se pierde si la ventana se achica)
    private bool _draggingSplitter;
    private double _splitStartX, _splitStartInfo;
    private double _windowWidth;

    public MainWindow()
    {
        InitializeComponent();

        _pollTimer.Tick += async (_, _) => await PollAsync();
        _topTimer.Tick += TopTimer_Tick;
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveBounds();
        };
        _histCloseTimer.Tick += HistCloseTimer_Tick;
    }

    // ------------------------------------------------------------------
    // Arranque y posición
    // ------------------------------------------------------------------

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // No aparece en Alt+Tab y hacer clic no roba el foco a la app que estás usando.
        int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        ApplySavedBounds();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _media.InitAsync();
        }
        catch
        {
            TitleText.Text = "No se pudo acceder a los controles multimedia";
            ArtistText.Text = "Requiere Windows 10 (2004) o superior";
        }

        _pollTimer.Start();
        _topTimer.Start();
        await PollAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _topTimer.Stop();
        SaveBounds();
        _media.Dispose();
        _volume.Dispose();
    }

    private void ApplySavedBounds()
    {
        _infoPref = Math.Clamp(_settings.InfoWidth, MinInfoWidth, 2000);
        Width = Math.Clamp(_settings.Width, MinWidth, 4000);
        Height = Math.Clamp(_settings.Height, MinHeight, MaxHeight);

        if (_settings.Left is double l && _settings.Top is double t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            PlaceNextToClock();
        }
    }

    private bool IsOnScreen(double left, double top)
    {
        double cx = left + Width / 2;
        double cy = top + Height / 2;
        return cx >= SystemParameters.VirtualScreenLeft
            && cx <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && cy >= SystemParameters.VirtualScreenTop
            && cy <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    }

    /// <summary>Posición por defecto: sobre la barra de tareas, a la izquierda del área del reloj.</summary>
    private void PlaceNextToClock()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;

        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        var work = SystemParameters.WorkArea;

        double taskbarTop = work.Bottom;
        double taskbarH = screenH - work.Bottom;   // > 0 solo si la barra está abajo

        if (taskbarH <= 20)
        {
            // Barra oculta o en otro borde: esquina inferior derecha del área de trabajo.
            Left = work.Right - Width - 8;
            Top = work.Bottom - Height - 4;
            return;
        }

        Height = Math.Min(Height, taskbarH);

        // Intentamos ubicar el borde izquierdo del área de notificaciones (bandeja + reloj).
        double trayLeft = screenW - 300;   // estimación si no se puede leer
        try
        {
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                IntPtr tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tray != IntPtr.Zero && NativeMethods.GetWindowRect(tray, out var r))
                {
                    double candidate = r.Left / sx;
                    // Solo lo aceptamos si es razonable (entre 100 y 700 px desde el borde derecho).
                    if (candidate >= screenW - 700 && candidate <= screenW - 100)
                        trayLeft = candidate;
                }
            }
        }
        catch { }

        Left = Math.Max(0, trayLeft - Width - 8);
        Top = taskbarTop + (taskbarH - Height) / 2;
    }

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.InfoWidth = _infoPref;
        _settings.Save();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _windowWidth = e.NewSize.Width;
        HistPanel.Width = Math.Max(120, _windowWidth);   // el historial acompaña el ancho del controlador

        double box = Math.Max(24, e.NewSize.Height - 10);
        CoverBox.Width = box;
        CoverBox.Height = box;

        ApplyLayout();

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Reparte el espacio: portada | info | separador | (espacio libre) | aleatorio | anterior-play-siguiente
    /// | progreso | volumen | historial. El ancho de la info lo elige el usuario con el separador.
    /// Con el espacio que queda a la derecha, los elementos aparecen en este orden:
    /// 1) anterior/play/siguiente (siempre), 2) barra de progreso, 3) historial, 4) aleatorio,
    /// 5) botón de volumen, 6) barra de volumen.
    /// El progreso es el elemento elástico: usa el espacio que sobre (hasta su máximo) y cada botón nuevo
    /// que aparece le "come" espacio en vez de empujar el resto de golpe.
    /// </summary>
    private void ApplyLayout()
    {
        if (_windowWidth <= 0) return;

        // ancho útil: ventana menos borde (2) y márgenes internos (12)
        double total = _windowWidth - 16;   // 14 + 2 px de holgura para que nada se corte
        double fixedLeft = CoverBox.Width + CoverBox.Margin.Right + SplitterWidth;

        double infoMax = Math.Max(40, total - fixedLeft - BasicsWidth);
        double info = Math.Clamp(_infoPref, Math.Min(MinInfoWidth, infoMax), infoMax);
        InfoCol.Width = new GridLength(info);

        double avail = total - fixedLeft - info;   // espacio a la derecha del separador
        double used = BasicsWidth;

        // Cada elemento aparece solo si, después de sumarlo, al progreso le quedan al menos ProgressMinWidth.
        bool showHistory = avail >= used + HistoryWidth + ProgressMinWidth;
        if (showHistory) used += HistoryWidth;

        bool showShuffle = showHistory && avail >= used + ShuffleWidth + ProgressMinWidth;
        if (showShuffle) used += ShuffleWidth;

        bool showVolButton = showShuffle && avail >= used + VolumeButtonWidth + ProgressMinWidth;
        if (showVolButton) used += VolumeButtonWidth;

        bool showVolBar = showVolButton && avail >= used + VolumeBarWidth + ProgressMinWidth;
        if (showVolBar) used += VolumeBarWidth;

        // Progreso: ocupa lo que sobra (hasta su máximo). Si falta espacio se encoge de a poco hasta desaparecer;
        // los márgenes y los tiempos se ajustan al ancho.
        double progressW = Math.Clamp(avail - used, 0, ProgressMaxWidth);
        bool showProgress = progressW >= 6;
        if (showProgress)
        {
            double m = Math.Min(8, progressW / 2);
            ProgressPanel.Margin = new Thickness(m, 0, m, 0);
            ProgressPanel.Width = Math.Max(0, progressW - 2 * m);   // el ancho total (barra + márgenes) es progressW
            // si no caben los dos tiempos, se ocultan (el espacio se conserva para que nada se mueva)
            TimesRow.Visibility = (progressW - 2 * m) >= 60 ? Visibility.Visible : Visibility.Hidden;
        }

        HistBtn.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        ShuffleBtn.Visibility = showShuffle ? Visibility.Visible : Visibility.Collapsed;
        VolumePanel.Visibility = showVolButton ? Visibility.Visible : Visibility.Collapsed;
        VolumeBar.Visibility = showVolBar ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

        if (!showHistory && HistPopup.IsOpen) CloseHistory();
    }

    // ------------------------------------------------------------------
    // Separador arrastrable
    // ------------------------------------------------------------------

    private void Splitter_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // evita que se mueva la ventana entera
        _draggingSplitter = true;
        _splitStartX = e.GetPosition(Root).X;
        _splitStartInfo = InfoCol.ActualWidth;
        Splitter.CaptureMouse();
        SplitterLine.Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    }

    private void Splitter_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSplitter) return;
        double dx = e.GetPosition(Root).X - _splitStartX;
        _infoPref = Math.Clamp(_splitStartInfo + dx, MinInfoWidth, 2000);
        ApplyLayout();
    }

    private void Splitter_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingSplitter) return;
        _draggingSplitter = false;
        Splitter.ReleaseMouseCapture();
        SplitterLine.Fill = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Splitter_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_draggingSplitter) SplitterLine.Fill = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    }

    private void Splitter_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_draggingSplitter) SplitterLine.Fill = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
    }

    private void ResetSplitter_Click(object sender, RoutedEventArgs e)
    {
        _infoPref = DefaultInfoWidth;
        ApplyLayout();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Windows 11 a veces sube la barra de tareas por encima de otras ventanas "siempre visibles".
    /// Reafirmamos la posición cada segundo. Si hay una app a pantalla completa, nos ocultamos.
    /// </summary>
    private void TopTimer_Tick(object? sender, EventArgs e)
    {
        bool fullscreen = false;
        try { fullscreen = NativeMethods.IsFullscreenAppRunning(); } catch { }

        if (fullscreen)
        {
            if (IsVisible) Hide();
            return;
        }

        if (!IsVisible) Show();

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------
    // Lectura del estado multimedia
    // ------------------------------------------------------------------

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var snap = await _media.PollAsync();
            _last = snap;
            ApplySnapshot(snap);
            if (!_draggingVolume) RefreshVolume();
        }
        catch { }
        finally
        {
            _polling = false;
        }
    }

    private void ApplySnapshot(MediaSnapshot s)
    {
        if (_hasSession != s.HasSession)
        {
            _hasSession = s.HasSession;
            if (HistPopup.IsOpen) RebuildHistoryList();
        }

        if (!s.HasSession)
        {
            TitleText.Text = "Nada reproduciéndose";
            ArtistText.Text = "Abre Spotify, YouTube Music, VLC…";
            CoverImage.Source = null;
            PlayBtn.Content = "\uE768";
            PlayBtn.IsEnabled = false;
            PrevBtn.IsEnabled = false;
            NextBtn.IsEnabled = false;
            ShuffleBtn.IsEnabled = false;
            ShuffleDot.Visibility = Visibility.Collapsed;
            ShuffleIcon.Margin = new Thickness(0);
            PosText.Text = "0:00";
            DurText.Text = "0:00";
            SetProgress(0);
            ApplyColors(null, "", false);
            return;
        }

        string title = string.IsNullOrWhiteSpace(s.Title) ? "(sin título)" : s.Title;
        if (TitleText.Text != title) TitleText.Text = title;
        if (ArtistText.Text != s.Artist) ArtistText.Text = s.Artist;

        string tip = $"{title}\n{s.Artist}\nFuente: {SourceInfo.FriendlyName(s.Source)}";
        if ((TitleText.ToolTip as string) != tip)
        {
            TitleText.ToolTip = tip;
            ArtistText.ToolTip = tip;
        }

        if (!ReferenceEquals(CoverImage.Source, s.Cover))
            CoverImage.Source = s.Cover;

        PlayBtn.IsEnabled = true;
        PlayBtn.Content = s.IsPlaying ? "\uE769" : "\uE768";
        PrevBtn.IsEnabled = s.CanPrev;
        NextBtn.IsEnabled = s.CanNext;

        // Aleatorio: se atenúa si la app no lo permite; el punto indica que está activo.
        ShuffleBtn.IsEnabled = s.CanShuffle;
        ShuffleBtn.Foreground = new SolidColorBrush(s.ShuffleActive ? Colors.White : Color.FromRgb(0xA8, 0xA8, 0xA8));
        ShuffleDot.Visibility = s.ShuffleActive ? Visibility.Visible : Visibility.Collapsed;
        // Cuando el punto está visible, el ícono sube para darle espacio; apagado queda centrado como los demás.
        ShuffleIcon.Margin = s.ShuffleActive ? new Thickness(0, 0, 0, 8) : new Thickness(0);

        PosText.Text = Format(s.Position);
        if (s.Duration > TimeSpan.Zero)
        {
            DurText.Text = Format(s.Duration);
            SetProgress(s.Position.TotalSeconds / s.Duration.TotalSeconds);
        }
        else
        {
            DurText.Text = "--:--";
            SetProgress(0);
        }

        ApplyColors(s.CoverColor, s.Source, s.IsVideo);
        TrackHistory(s);
    }

    /// <summary>Fondo según la portada, color de texto secundario y color de la barra de progreso.</summary>
    private void ApplyColors(Color? cover, string source, bool isVideo)
    {
        Color bg = cover.HasValue ? Theme.BackgroundFor(cover.Value) : Theme.DefaultBg;
        if (bg != _currentBg)
        {
            _currentBg = bg;
            var bgBrush = new SolidColorBrush(bg);
            bgBrush.Freeze();
            Background = bgBrush;
            HistPanel.Background = bgBrush;

            var dim = new SolidColorBrush(Theme.SecondaryText(bg));
            dim.Freeze();
            ArtistText.Foreground = dim;
            PosText.Foreground = dim;
            DurText.Foreground = dim;
        }

        string key = source + "|" + isVideo;
        if (key != _accentKey)
        {
            _accentKey = key;
            var accent = new SolidColorBrush(SourceInfo.Accent(source, isVideo));
            accent.Freeze();
            ProgressFill.Background = accent;
        }
    }

    // ------------------------------------------------------------------
    // Historial: últimas 5 canciones (no se guarda en disco)
    // ------------------------------------------------------------------

    private void TrackHistory(MediaSnapshot s)
    {
        if (string.IsNullOrWhiteSpace(s.Title)) return;

        string app = SourceInfo.FriendlyName(s.Source);
        string text;
        if (s.IsVideo)
        {
            text = s.Title;
            if (!string.IsNullOrWhiteSpace(s.Artist)) text += " – " + s.Artist;
        }
        else
        {
            text = s.Title;
            if (!string.IsNullOrWhiteSpace(s.Artist)) text += " – " + s.Artist;
            if (!string.IsNullOrWhiteSpace(s.Album)) text += ", " + s.Album;
        }
        text += " · " + app;

        string key = app + "|" + text;
        var lastEntry = _history.Count > 0 ? _history[^1] : null;
        if (lastEntry != null && lastEntry.Key == key) return;   // sin repetidos seguidos

        // Windows a veces entrega el título antes que el artista: si es la misma canción
        // y acaba de empezar, se completa la entrada en vez de crear otra.
        if (lastEntry != null && lastEntry.Title == s.Title && lastEntry.Source == app
            && (DateTime.UtcNow - lastEntry.AddedAt) < TimeSpan.FromSeconds(5))
        {
            lastEntry.Key = key;
            lastEntry.Text = text;
        }
        else
        {
            _history.Add(new HistoryEntry { Key = key, Title = s.Title, Text = text, Source = app, AddedAt = DateTime.UtcNow });
            while (_history.Count > 6) _history.RemoveAt(0);   // 5 anteriores + la que suena
        }

        if (HistPopup.IsOpen) RebuildHistoryList();
    }

    private void RebuildHistoryList()
    {
        // Solo lo ya reproducido: si hay algo sonando, la última entrada es la actual y no se muestra.
        int count = _hasSession ? _history.Count - 1 : _history.Count;
        if (count > 5) count = 5;
        int first = (_hasSession ? _history.Count - 1 : _history.Count) - count;

        HistList.Children.Clear();
        if (count <= 0)
        {
            HistList.Children.Add(new TextBlock
            {
                Text = "Aún no hay canciones anteriores.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4)),
                Margin = new Thickness(0, 3, 0, 3)
            });
            return;
        }

        // Más antigua arriba, más reciente abajo. La opacidad baja de a poco hacia arriba (mínimo ~60 %).
        for (int i = 0; i < count; i++)
        {
            int rankFromNewest = count - 1 - i;
            HistList.Children.Add(new TextBlock
            {
                Text = _history[first + i].Text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                Opacity = 1.0 - 0.10 * rankFromNewest,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 3)
            });
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (HistPopup.IsOpen)
        {
            CloseHistory();
            return;
        }
        RebuildHistoryList();
        HistPanel.Width = Math.Max(120, Root.ActualWidth);
        HistPopup.VerticalOffset = -4;
        HistPopup.IsOpen = true;
        _histAwayTicks = 0;
        _histCloseTimer.Start();
    }

    private void CloseHistory()
    {
        HistPopup.IsOpen = false;
        _histCloseTimer.Stop();
    }

    /// <summary>La ventana no toma el foco, así que el panel no se cierra solo: lo cerramos al alejar el mouse.</summary>
    private void HistCloseTimer_Tick(object? sender, EventArgs e)
    {
        if (!HistPopup.IsOpen)
        {
            _histCloseTimer.Stop();
            return;
        }
        bool over = IsMouseOver || HistPanel.IsMouseOver;
        _histAwayTicks = over ? 0 : _histAwayTicks + 1;
        if (_histAwayTicks >= 3) CloseHistory();   // ~2 s fuera
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        await _media.SetShuffleAsync(!_last.ShuffleActive);
        await Task.Delay(200);
        await PollAsync();
    }

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ------------------------------------------------------------------
    // Botones de reproducción
    // ------------------------------------------------------------------

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        await _media.PreviousAsync();
        await Task.Delay(200);
        await PollAsync();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        await _media.TogglePlayPauseAsync();
        await Task.Delay(150);
        await PollAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await _media.NextAsync();
        await Task.Delay(200);
        await PollAsync();
    }

    // ------------------------------------------------------------------
    // Barra de progreso (clic para saltar a un punto)
    // ------------------------------------------------------------------

    private void SetProgress(double ratio)
    {
        _progressRatio = double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);
        ProgressFill.Width = ProgressHost.ActualWidth * _progressRatio;
    }

    private void Progress_SizeChanged(object sender, SizeChangedEventArgs e) => SetProgress(_progressRatio);

    private async void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_last.HasSession || !_last.CanSeek || _last.Duration <= TimeSpan.Zero) return;

        double w = ProgressHost.ActualWidth;
        if (w <= 0) return;

        double ratio = Math.Clamp(e.GetPosition(ProgressHost).X / w, 0, 1);
        SetProgress(ratio);
        await _media.SeekAsync(TimeSpan.FromTicks((long)(_last.Duration.Ticks * ratio)));
    }

    // ------------------------------------------------------------------
    // Volumen (volumen maestro de Windows)
    // ------------------------------------------------------------------

    private void RefreshVolume()
    {
        try
        {
            float v = _volume.GetVolume();
            bool muted = _volume.GetMute();
            ShowVolume(muted ? 0 : v, muted || v <= 0.001f);
        }
        catch { }
    }

    private void ShowVolume(double ratio, bool mutedIcon)
    {
        _volumeRatio = Math.Clamp(ratio, 0, 1);
        VolumeFill.Width = VolumeHost.ActualWidth * _volumeRatio;
        MuteBtn.Content = mutedIcon ? "\uE74F" : "\uE767";
        VolText.Text = ((int)Math.Round(_volumeRatio * 100)).ToString();
    }

    private void Volume_SizeChanged(object sender, SizeChangedEventArgs e)
        => VolumeFill.Width = VolumeHost.ActualWidth * _volumeRatio;

    private void SetVolumeFromMouse(MouseEventArgs e)
    {
        double w = VolumeHost.ActualWidth;
        if (w <= 0) return;

        float v = (float)Math.Clamp(e.GetPosition(VolumeHost).X / w, 0, 1);
        try
        {
            if (v > 0 && _volume.GetMute()) _volume.SetMute(false);
            _volume.SetVolume(v);
        }
        catch { }
        ShowVolume(v, v <= 0.001f);
    }

    private void Volume_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _draggingVolume = true;
        VolumeHost.CaptureMouse();
        SetVolumeFromMouse(e);
    }

    private void Volume_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingVolume) SetVolumeFromMouse(e);
    }

    private void Volume_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingVolume = false;
        VolumeHost.ReleaseMouseCapture();
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        try
        {
            float v = Math.Clamp(_volume.GetVolume() + (e.Delta > 0 ? 0.05f : -0.05f), 0f, 1f);
            if (v > 0 && _volume.GetMute()) _volume.SetMute(false);
            _volume.SetVolume(v);
        }
        catch { }
        RefreshVolume();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        try { _volume.SetMute(!_volume.GetMute()); }
        catch { }
        RefreshVolume();
    }

    // ------------------------------------------------------------------
    // Mover la ventana y menú (clic derecho)
    // ------------------------------------------------------------------

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Los botones y barras marcan el evento como manejado, así que esto solo
        // se ejecuta al arrastrar desde una zona libre del widget.
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Menu_Opened(object sender, RoutedEventArgs e)
        => StartupItem.IsChecked = StartupManager.IsEnabled();

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _settings.ForgetPosition();
        PlaceNextToClock();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
        => StartupManager.SetEnabled(StartupItem.IsChecked);

    private void Exit_Click(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();
}
