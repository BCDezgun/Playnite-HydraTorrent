using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using Playnite.SDK;
using Playnite.SDK.Models;
using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HydraTorrent.Views
{
    public partial class HydraHubView : UserControl, INotifyPropertyChanged
    {
        private readonly IPlayniteAPI PlayniteApi;
        private readonly HydraTorrent _plugin;
        private readonly ScraperService _scraperService;

        private DispatcherTimer _uiRefreshTimer;
        private long _maxSpeedSeen = 0;
        private long _maxUploadSpeedSeen = 0;
        private bool _maxSpeedIsInKbps = false;
        private bool _maxUploadSpeedIsInKbps = false;
        private Guid _activeGameId = Guid.Empty;
        private string _currentTorrentHash = null;
        private bool _isPaused = false;
        private Guid _lastActiveGameId = Guid.Empty;

        private readonly Queue<long> _speedHistory = new Queue<long>();
        private long _graphMaxSpeed = 1;
        private readonly Queue<long> _uploadHistory = new Queue<long>();
        private long _uploadMaxSpeed = 1;

        private readonly Dictionary<Guid, BitmapImage> _coverCache = new Dictionary<Guid, BitmapImage>();

        private List<TorrentResult> _allResults = new List<TorrentResult>();
        private List<TorrentResult> _filteredResults = new List<TorrentResult>();
        private int _currentPage = 1;
        private const int _itemsPerPage = 10;

        public static HydraHubView CurrentInstance { get; private set; }

        // ────────────────────────────────────────────────────────────────
        // Фильтры источников
        // ────────────────────────────────────────────────────────────────

        public ObservableCollection<SourceFilterItem> FilterSources { get; set; } = new ObservableCollection<SourceFilterItem>();

        private bool _isAllSourcesSelected = true;
        public bool IsAllSourcesSelected
        {
            get => _isAllSourcesSelected;
            set
            {
                if (_isAllSourcesSelected != value)
                {
                    _isAllSourcesSelected = value;
                    OnPropertyChanged();
                    UpdateSourceButtonText();
                    ApplyLocalFilters();
                }
            }
        }

        private string _sourceButtonText = ResourceProvider.GetString("LOC_HydraTorrent_AllSources");
        public string SourceButtonText
        {
            get => _sourceButtonText;
            set { _sourceButtonText = value; OnPropertyChanged(); }
        }

        public HydraHubView(IPlayniteAPI api, HydraTorrent plugin)
        {
            InitializeComponent();
            CurrentInstance = this;

            PlayniteApi = api;
            _plugin = plugin;
            _scraperService = plugin.GetScraperService();

            var settings = _plugin.GetSettings().Settings;
            if (settings.Sources != null)
            {
                foreach (var source in settings.Sources)
                {
                    var item = new SourceFilterItem { Name = source.Name, IsSelected = true };
                    item.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(SourceFilterItem.IsSelected))
                        {
                            UpdateSourceButtonText();
                            ApplyLocalFilters();
                        }
                    };
                    FilterSources.Add(item);
                }
            }

            DataContext = this;
            InitDownloadManager();
            UpdateQueueUI();
            UpdateDownloadUI(null, null);
        }

        // ────────────────────────────────────────────────────────────────
        // Таймер обновления UI загрузок
        // ────────────────────────────────────────────────────────────────

        private void InitDownloadManager()
        {
            _uiRefreshTimer = new DispatcherTimer();
            _uiRefreshTimer.Interval = TimeSpan.FromSeconds(1);
            _uiRefreshTimer.Tick += UIUpdateTimer_Tick;
            _uiRefreshTimer.Start();
        }

        private void UIUpdateTimer_Tick(object sender, EventArgs e)
        {
            // ────────────────────────────────────────────────────────────────
            // ✅ ПРОВЕРКА: Актуальна ли ещё запомненная игра?
            // ────────────────────────────────────────────────────────────────
            if (_lastActiveGameId != Guid.Empty)
            {
                // Проверяем, всё ещё ли эта игра в статусе Downloading или Paused
                var queueItem = _plugin.DownloadQueue.FirstOrDefault(q => q.GameId == _lastActiveGameId);

                // ✅ ИГРА УДАЛЕНА ИЛИ ЗАВЕРШЕНА — сбрасываем запомненную
                if (queueItem == null || queueItem.QueueStatus == "Completed")
                {
                    _lastActiveGameId = Guid.Empty;
                    _activeGameId = Guid.Empty;
                    _currentTorrentHash = null;
                    // ✅ НЕ возвращаем здесь! Даём коду ниже найти новую активную игру
                }
                // ✅ ИГРА ВСЁ ЕЩЁ АКТИВНА — продолжаем показывать её
                else if (queueItem.QueueStatus == "Downloading" || queueItem.QueueStatus == "Paused")
                {
                    var lastActiveStatus = HydraTorrent.LiveStatus.TryGetValue(_lastActiveGameId, out var s) ? s : null;
                    if (lastActiveStatus != null)
                    {
                        var game = PlayniteApi.Database.Games.Get(_lastActiveGameId);
                        if (game != null)
                        {
                            UpdateDownloadUI(game, lastActiveStatus);
                            UpdatePauseButtonState();
                            DrawSpeedGraph();
                            return; // ✅ Возвращаем только если игра действительно существует
                        }
                    }
                    // ✅ Если статус есть но игры нет — сбрасываем и идём дальше
                    _lastActiveGameId = Guid.Empty;
                    _activeGameId = Guid.Empty;
                }
                // ✅ ИГРА В ОЧЕРЕДИ НО НЕ АКТИВНА — сбрасываем
                else
                {
                    _lastActiveGameId = Guid.Empty;
                }
            }

            // ────────────────────────────────────────────────────────────────
            // ✅ ПРИОРИТЕТ 2: Ищем активную загрузку из ОЧЕРЕДИ
            // ────────────────────────────────────────────────────────────────
            var activeFromQueue = _plugin.DownloadQueue
                .FirstOrDefault(q => q.QueueStatus == "Downloading");

            Guid targetGameId = Guid.Empty;

            if (activeFromQueue != null && activeFromQueue.GameId.HasValue)
            {
                targetGameId = activeFromQueue.GameId.Value;

                // ✅ Если новая активная игра — запоминаем её
                if (_lastActiveGameId != targetGameId)
                {
                    _lastActiveGameId = targetGameId;
                }
            }
            else
            {
                // ────────────────────────────────────────────────────────────────
                // ✅ ПРИОРИТЕТ 3: Если нет активных, ищем паузу из ОЧЕРЕДИ
                // ────────────────────────────────────────────────────────────────
                var pausedFromQueue = _plugin.DownloadQueue
                    .Where(q => q.QueueStatus == "Paused")
                    .OrderBy(q => q.QueuePosition)
                    .FirstOrDefault();

                if (pausedFromQueue != null && pausedFromQueue.GameId.HasValue)
                {
                    targetGameId = pausedFromQueue.GameId.Value;
                }
                else
                {
                    // ────────────────────────────────────────────────────────────────
                    // ✅ ПРИОРИТЕТ 4: Если ничего нет в очереди, смотрим LiveStatus
                    // ────────────────────────────────────────────────────────────────
                    var activeDownload = HydraTorrent.LiveStatus
                        .Where(x =>
                            x.Value.Status.Contains("Загрузка") &&
                            !x.Value.Status.Contains("Пауза") &&
                            !x.Value.Status.Contains("paused"))
                        .OrderByDescending(x => x.Value.DownloadSpeed)
                        .FirstOrDefault();

                    if (activeDownload.Key != Guid.Empty)
                    {
                        targetGameId = activeDownload.Key;
                        _lastActiveGameId = targetGameId;
                    }
                    else
                    {
                        var pausedDownload = HydraTorrent.LiveStatus
                            .Where(x =>
                                x.Value.Status.Contains("Пауза") ||
                                x.Value.Status.Contains("paused"))
                            .OrderBy(x => x.Key)
                            .FirstOrDefault();

                        if (pausedDownload.Key != Guid.Empty)
                        {
                            targetGameId = pausedDownload.Key;
                        }
                    }
                }
            }

            // ────────────────────────────────────────────────────────────────
            // ✅ Обновляем UI только если нашли игру
            // ────────────────────────────────────────────────────────────────
            if (targetGameId != Guid.Empty)
            {
                if (_activeGameId != targetGameId)
                {
                    _activeGameId = targetGameId;
                    _currentTorrentHash = _plugin.GetHydraData(
                        PlayniteApi.Database.Games.Get(_activeGameId))?.TorrentHash;
                    _speedHistory.Clear();
                    _uploadHistory.Clear();
                    _graphMaxSpeed = 1;
                    _uploadMaxSpeed = 1;
                    _maxSpeedSeen = 0;
                    _maxUploadSpeedSeen = 0;
                }

                var status = HydraTorrent.LiveStatus.TryGetValue(_activeGameId, out var s) ? s : null;
                var game = PlayniteApi.Database.Games.Get(_activeGameId);

                if (game != null)
                {
                    UpdateDownloadUI(game, status);
                    UpdatePauseButtonState();
                    DrawSpeedGraph();
                }
            }
            else
            {
                if (_activeGameId != Guid.Empty)
                {
                    _lastActiveGameId = Guid.Empty;
                    _activeGameId = Guid.Empty;
                    _currentTorrentHash = null;
                    _speedHistory.Clear();
                    _uploadHistory.Clear();
                    _graphMaxSpeed = 1;
                    _uploadMaxSpeed = 1;
                    _maxSpeedSeen = 0;
                    _maxUploadSpeedSeen = 0;                    
                }

                UpdateDownloadUI(null, null);
                DrawSpeedGraph();
            }
        }

        public void UpdateDownloadUI(Game game, HydraTorrent.TorrentStatusInfo status)
        {
            // ✅ УБРАНО: Dispatcher.Invoke (таймер уже в UI потоке)

            // Всегда обновляем фон (даже при очистке)
            UpdateGameBackground(game);

            if (status == null || game == null)
            {
                // Полная очистка UI после удаления или отсутствия загрузки
                txtCurrentGameName.Text = "";

                // ✅ СКРЫВАЕМ КОНТЕЙНЕРЫ СКОРОСТЕЙ
                if (pnlDownloadSpeed != null)
                    pnlDownloadSpeed.Visibility = Visibility.Hidden;
                if (pnlDownloadMaxSpeed != null)
                    pnlDownloadMaxSpeed.Visibility = Visibility.Hidden;
                if (pnlUploadSpeed != null)
                    pnlUploadSpeed.Visibility = Visibility.Hidden;
                if (pnlUploadMaxSpeed != null)
                    pnlUploadMaxSpeed.Visibility = Visibility.Hidden;

                // ✅ СКРЫВАЕМ ПРОГРЕСС БАР И ИНФОРМАЦИЮ
                if (pnlProgressInfo != null)
                    pnlProgressInfo.Visibility = Visibility.Hidden;
                if (pbDownload != null)
                    pbDownload.Visibility = Visibility.Hidden;
                if (lblDownloadedAmount != null)
                    lblDownloadedAmount.Visibility = Visibility.Hidden;
                if (lblETA != null)
                    lblETA.Visibility = Visibility.Hidden;

                // ✅ СКРЫВАЕМ СИДЫ/ПИРЫ
                if (pnlSeedsPeers != null)
                    pnlSeedsPeers.Visibility = Visibility.Hidden;
                if (pnlPeers != null)
                    pnlPeers.Visibility = Visibility.Hidden;
                if (lblSeeds != null)
                    lblSeeds.Visibility = Visibility.Hidden;
                if (lblPeers != null)
                    lblPeers.Visibility = Visibility.Hidden;

                // ✅ СКРЫВАЕМ ГРАФИКИ СКОРОСТИ
                if (DownloadGraphCanvas != null)
                    DownloadGraphCanvas.Visibility = Visibility.Collapsed;
                if (UploadGraphCanvas != null)
                    UploadGraphCanvas.Visibility = Visibility.Collapsed;
                if (lblLoadingStatus != null)
                {
                    lblLoadingStatus.Visibility = Visibility.Hidden;
                }
                btnPauseResume.Visibility = Visibility.Hidden;
                btnSettings.Visibility = Visibility.Hidden;                
                _maxSpeedSeen = 0;
                return;
            }
            
            // Обычное обновление при наличии статуса
            txtCurrentGameName.Text = game.Name?.ToUpper() ?? "ЗАГРУЗКА...";

            txtCurrentGameName.Visibility = Visibility.Visible;  // ✅ ПОКАЗАТЬ!

            // ✅ ПОКАЗЫВАЕМ КОНТЕЙНЕРЫ СКОРОСТЕЙ
            if (pnlDownloadSpeed != null)
                pnlDownloadSpeed.Visibility = Visibility.Visible;
            if (pnlDownloadMaxSpeed != null)
                pnlDownloadMaxSpeed.Visibility = Visibility.Visible;
            if (pnlUploadSpeed != null)
                pnlUploadSpeed.Visibility = Visibility.Visible;
            if (pnlUploadMaxSpeed != null)
                pnlUploadMaxSpeed.Visibility = Visibility.Visible;

            // ✅ ПОКАЗЫВАЕМ ПРОГРЕСС БАР И ИНФОРМАЦИЮ
            if (pnlProgressInfo != null)
                pnlProgressInfo.Visibility = Visibility.Visible;
            if (pbDownload != null)
                pbDownload.Visibility = Visibility.Visible;
            if (lblDownloadedAmount != null)
                lblDownloadedAmount.Visibility = Visibility.Visible;
            if (lblETA != null)
                lblETA.Visibility = Visibility.Visible;

            // ✅ ПОКАЗЫВАЕМ СИДЫ/ПИРЫ
            if (pnlSeedsPeers != null)
                pnlSeedsPeers.Visibility = Visibility.Visible;
            if (pnlPeers != null)
                pnlPeers.Visibility = Visibility.Visible;
            if (lblSeeds != null)
                lblSeeds.Visibility = Visibility.Visible;
            if (lblPeers != null)
                lblPeers.Visibility = Visibility.Visible;

            // ✅ ПОКАЗЫВАЕМ КНОПКИ
            btnPauseResume.Visibility = Visibility.Visible;
            btnSettings.Visibility = Visibility.Visible;

            long currentSpeedBytes = status.DownloadSpeed;            
            if (currentSpeedBytes > _maxSpeedSeen)
            {
                _maxSpeedSeen = currentSpeedBytes;
                _maxSpeedIsInKbps = (currentSpeedBytes * 8.0) < (1024 * 1024);
            }
            lblCurrentSpeed.Text = FormatSpeed(currentSpeedBytes);
            lblMaxSpeed.Text = FormatSpeed(_maxSpeedSeen);

            _speedHistory.Enqueue(currentSpeedBytes);
            while (_speedHistory.Count > 15)
            {
                _speedHistory.Dequeue();
            }

            if (currentSpeedBytes > _graphMaxSpeed)
            {
                _graphMaxSpeed = currentSpeedBytes;
            }

            long currentUploadBytes = status.UploadSpeed;
            if (currentUploadBytes > _maxUploadSpeedSeen)
            {
                _maxUploadSpeedSeen = currentUploadBytes;
                _maxUploadSpeedIsInKbps = (currentUploadBytes * 8.0) < (1024 * 1024);
            }
            if (lblCurrentUploadSpeed != null)
                lblCurrentUploadSpeed.Text = FormatSpeed(currentUploadBytes);
            if (lblMaxUploadSpeed != null)
                lblMaxUploadSpeed.Text = FormatSpeed(_maxUploadSpeedSeen);

            _uploadHistory.Enqueue(currentUploadBytes);
            while (_uploadHistory.Count > 15)
            {
                _uploadHistory.Dequeue();
            }
            if (currentUploadBytes > _uploadMaxSpeed)
            {
                _uploadMaxSpeed = currentUploadBytes;
            }

            // ✅ СИДЫ/ПИРЫ - ДОБАВИТЬ ЭТОТ БЛОК
            if (lblSeeds != null && status.Seeds.HasValue)
            {
                lblSeeds.Text = string.Format(
                    ResourceProvider.GetString("LOC_HydraTorrent_SeederCount"),
                    status.Seeds.Value);
            }
                
            if (lblPeers != null && status.Peers.HasValue)
            {
                lblPeers.Text = string.Format(
                    ResourceProvider.GetString("LOC_HydraTorrent_PeerCount"),
                    status.Peers.Value);
            }                

            double uiProgress = status.Progress;
            if (uiProgress > 0 && uiProgress <= 1.0)
                uiProgress *= 100;

            pbDownload.Value = uiProgress;

            double downloadedGB = status.DownloadedSize / 1024.0 / 1024.0 / 1024.0;
            double totalGB = status.TotalSize / 1024.0 / 1024.0 / 1024.0;

            lblDownloadedAmount.Text = string.Format(ResourceProvider.GetString("LOC_HydraTorrent_PercentFormat"),uiProgress,downloadedGB,totalGB);

            if (status.ETA.HasValue && status.ETA.Value.TotalSeconds > 0)
            {
                string timeFormat = status.ETA.Value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss";
                lblETA.Text = $"{ResourceProvider.GetString("LOC_HydraTorrent_Remaining")} {status.ETA.Value.ToString(timeFormat)}";
            }
            else
            {
                lblETA.Text = ResourceProvider.GetString("LOC_HydraTorrent_NoETA");
            }

            if (lblLoadingStatus != null)
            {
                lblLoadingStatus.Visibility = Visibility.Visible;
                if (status.Status.Contains("Пауза") || status.Status.Contains("paused"))
                {
                    lblLoadingStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_Idle");
                    lblLoadingStatus.Foreground = new SolidColorBrush(Colors.Gray);
                }
                else
                {
                    lblLoadingStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_Loading");
                    lblLoadingStatus.Foreground = new SolidColorBrush(Color.FromRgb(46, 204, 113));
                }
            }

            btnPauseResume.Visibility = Visibility.Visible;
            btnSettings.Visibility = Visibility.Visible;

            DrawSpeedGraph();
        }

        // ────────────────────────────────────────────────────────────────
        // Загрузка обложки для элемента очереди
        // ────────────────────────────────────────────────────────────────

        private void LoadQueueItemCover(Image imgControl, Guid gameId)
        {
            if (imgControl == null || gameId == Guid.Empty) return;

            // ✅ Проверяем кэш сначала
            if (_coverCache.TryGetValue(gameId, out var cachedImage))
            {
                imgControl.Source = cachedImage;
                return;
            }

            // ✅ Загружаем обложку
            try
            {
                var game = PlayniteApi.Database.Games.Get(gameId);
                if (game == null)
                {
                    SetPlaceholderIcon(imgControl);
                    return;
                }

                string imageFileName = null;

                if (!string.IsNullOrEmpty(game.CoverImage))
                {
                    imageFileName = game.CoverImage;
                }
                else if (!string.IsNullOrEmpty(game.BackgroundImage))
                {
                    imageFileName = game.BackgroundImage;
                }

                if (string.IsNullOrEmpty(imageFileName))
                {
                    SetPlaceholderIcon(imgControl);
                    return;
                }

                // Строим путь к файлу
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string libraryFilesDir = System.IO.Path.Combine(appData, "Playnite", "library", "files");
                string fullImagePath;

                if (imageFileName.Contains("\\"))
                {
                    fullImagePath = System.IO.Path.Combine(libraryFilesDir, imageFileName);
                }
                else
                {
                    string gameFolder = System.IO.Path.Combine(libraryFilesDir, gameId.ToString());
                    fullImagePath = System.IO.Path.Combine(gameFolder, imageFileName);
                }

                if (System.IO.File.Exists(fullImagePath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(fullImagePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 100; // ✅ Оптимизация: не грузим полное разрешение
                    bitmap.EndInit();
                    bitmap.Freeze(); // ✅ Делаем доступным для любого потока

                    _coverCache[gameId] = bitmap;
                    imgControl.Source = bitmap;
                }
                else
                {
                    SetPlaceholderIcon(imgControl);
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Debug($"Не удалось загрузить обложку: {ex.Message}");
                SetPlaceholderIcon(imgControl);
            }
        }

        private void SetPlaceholderIcon(Image imgControl)
        {
            // ✅ Создаём заглушку с иконкой геймпада программно
            var drawingVisual = new DrawingVisual();
            using (var context = drawingVisual.RenderOpen())
            {
                // Серый фон
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(60, 60, 60)), null, new Rect(0, 0, 50, 50));

                // Иконка геймпада (Path geometry)
                var gamepadGeometry = Geometry.Parse("M32 18c-7.7 0-14 6.3-14 14s6.3 14 14 14 14-6.3 14-14-6.3-14-14-14zm0 26c-6.6 0-12-5.4-12-12s5.4-12 12-12 12 5.4 12 12-5.4 12-12 12zm-6-12h12v-2H26v2zm6-6h2v2h-2V26z");
                context.DrawGeometry(Brushes.White, null, gamepadGeometry);
            }

            var rtb = new RenderTargetBitmap(50, 50, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(drawingVisual);

            imgControl.Source = rtb;
        }

        private T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T frameworkElement && frameworkElement.Name == name)
                {
                    return frameworkElement;
                }

                var childOfChild = FindVisualChild<T>(child, name);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }

        // ✅ Метод с повторными попытками для загрузки обложек        
        private async void LoadQueueCoversWithRetry(List<TorrentResult> queuedGames, int attempt)
        {
            if (attempt >= 3) return;

            await Task.Delay(150 * (attempt + 1));
            
            _ = Dispatcher.InvokeAsync(() =>
            {
                bool allLoaded = true;
                int loadedCount = 0;

                foreach (var item in queuedGames)
                {
                    var container = lstQueue.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
                    if (container == null)
                    {
                        allLoaded = false;
                        continue;
                    }

                    var imgControl = FindVisualChild<Image>(container, "imgQueueCover");
                    if (imgControl != null && item.GameId.HasValue)
                    {
                        if (imgControl.Source == null)
                        {
                            LoadQueueItemCover(imgControl, item.GameId.Value);
                            loadedCount++;
                        }
                    }
                    else
                    {
                        allLoaded = false;
                    }
                }

                if (!allLoaded && attempt < 2)
                {
                    LoadQueueCoversWithRetry(queuedGames, attempt + 1);
                }
            }, DispatcherPriority.Loaded);
        }

        private void UpdateGameBackground(Game game)
        {
            if (imgGameBackground == null)
            {
                System.Diagnostics.Debug.WriteLine("[Hydra] imgGameBackground is null");
                return;
            }

            Dispatcher.Invoke(() =>
            {
                try
                {
                    string imageFileName = null;

                    if (!string.IsNullOrEmpty(game.BackgroundImage))
                    {
                        imageFileName = game.BackgroundImage;
                        System.Diagnostics.Debug.WriteLine($"[Hydra] Using BackgroundImage: {imageFileName}");
                    }
                    else if (!string.IsNullOrEmpty(game.CoverImage))
                    {
                        imageFileName = game.CoverImage;
                        System.Diagnostics.Debug.WriteLine($"[Hydra] Using CoverImage: {imageFileName}");
                    }

                    if (string.IsNullOrEmpty(imageFileName))
                    {
                        System.Diagnostics.Debug.WriteLine("[Hydra] No image filename in game metadata");
                        imgGameBackground.Source = null;
                        return;
                    }

                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string libraryFilesDir = System.IO.Path.Combine(appData, "Playnite", "library", "files");

                    string gameFolder = System.IO.Path.Combine(libraryFilesDir, game.Id.ToString());
                    string fullImagePath = System.IO.Path.Combine(gameFolder, imageFileName);

                    if (imageFileName.Contains("\\"))
                    {
                        fullImagePath = System.IO.Path.Combine(libraryFilesDir, imageFileName);
                    }

                    if (System.IO.File.Exists(fullImagePath))
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(fullImagePath, UriKind.Absolute);
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        imgGameBackground.Source = bitmap;
                        System.Diagnostics.Debug.WriteLine($"[Hydra] Image loaded: {fullImagePath}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Hydra] Image file not found: {fullImagePath}");
                        imgGameBackground.Source = null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Hydra] Failed to load image: {ex.Message}");
                    imgGameBackground.Source = null;
                }
            });
        }

        private string FormatSpeed(long bytesPerSecond)
        {
            // Конвертируем байты/сек в биты/сек
            double bitsPerSecond = bytesPerSecond * 8.0;

            // Если меньше 1 Мбит/с — показываем в Кбит/с
            if (bitsPerSecond < 1024 * 1024) // < 1 Мбит/с
            {
                double kbps = bitsPerSecond / 1024;
                return $"{kbps:F0} Кбит/с";
            }
            // Иначе показываем в Мбит/с
            else
            {
                double mbps = bitsPerSecond / (1024 * 1024);
                return $"{mbps:F1} Мбит/с";
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Поиск и фильтры
        // ────────────────────────────────────────────────────────────────

        private void ApplyLocalFilters()
        {
            if (_allResults == null || !_allResults.Any()) return;

            if (IsAllSourcesSelected)
            {
                _filteredResults = _allResults;
            }
            else
            {
                var activeSources = FilterSources.Where(x => x.IsSelected).Select(x => x.Name).ToList();
                _filteredResults = _allResults.Where(r => activeSources.Contains(r.Source)).ToList();
            }

            ShowPage(1);
        }

        private void UpdateSourceButtonText()
        {
            if (IsAllSourcesSelected)
            {
                SourceButtonText = ResourceProvider.GetString("LOC_HydraTorrent_AllSources");
                return;
            }

            var selected = FilterSources.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                SourceButtonText = ResourceProvider.GetString("LOC_HydraTorrent_NoneSelected");
            }
            else if (selected.Count == 1)
            {
                SourceButtonText = selected[0].Name;
            }
            else
            {
                SourceButtonText = $"{selected[0].Name} + {selected.Count - 1}";
            }
        }

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            await PerformSearch();
        }

        private async void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                await PerformSearch();
        }

        private async Task PerformSearch()
        {
            var query = txtSearch.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                txtStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_EnterGameName");
                return;
            }

            var settings = _plugin.GetSettings().Settings;
            if (settings.Sources == null || settings.Sources.Count == 0)
            {
                txtStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_SourcesNotConfigured");
                return;
            }

            if (!settings.SearchHistory.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                settings.SearchHistory.Insert(0, query);
                if (settings.SearchHistory.Count > 20) settings.SearchHistory.RemoveAt(20);
                _plugin.SavePluginSettings(settings);
            }

            txtStatus.Text = string.Format(ResourceProvider.GetString("LOC_HydraTorrent_Searching"),query);
            lstResults.ItemsSource = null;
            btnSearch.IsEnabled = false;
            pnlPagination.Children.Clear();

            try
            {
                var results = await _scraperService.SearchAsync(query);
                _allResults = results ?? new List<TorrentResult>();

                if (_allResults.Count == 0)
                {
                    txtStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_NoResults");
                    _filteredResults = new List<TorrentResult>();
                }
                else
                {
                    ApplyLocalFilters();
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка: {ex.Message}";
            }
            finally
            {
                btnSearch.IsEnabled = true;
            }
        }

        private void ShowPage(int pageNumber)
        {
            if (_filteredResults == null || _filteredResults.Count == 0)
            {
                lstResults.ItemsSource = null;
                pnlPagination.Children.Clear();
                txtStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_NoResultsForFilters");
                return;
            }

            _currentPage = pageNumber;
            var pageData = _filteredResults.Skip((_currentPage - 1) * _itemsPerPage).Take(_itemsPerPage).ToList();
            lstResults.ItemsSource = pageData;

            int totalPages = (int)Math.Ceiling((double)_filteredResults.Count / _itemsPerPage);
            txtStatus.Text = string.Format(ResourceProvider.GetString("LOC_HydraTorrent_PageInfo"),_filteredResults.Count,_currentPage,totalPages);

            UpdatePaginationButtons(totalPages);
        }

        private void UpdatePaginationButtons(int totalPages)
        {
            pnlPagination.Children.Clear();
            if (totalPages <= 1) return;

            for (int i = 1; i <= totalPages; i++)
            {
                var btn = new Button
                {
                    Content = $" {i} ",
                    Tag = i,
                    Margin = new Thickness(3, 0, 3, 0),
                    Cursor = Cursors.Hand,
                    Background = (i == _currentPage) ? Brushes.SkyBlue : Brushes.Transparent
                };

                btn.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is int p)
                        ShowPage(p);
                };

                pnlPagination.Children.Add(btn);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // История поиска и удаление
        // ────────────────────────────────────────────────────────────────

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = txtSearch.Text.ToLower().Trim();
            var history = _plugin.GetSettings().Settings.SearchHistory;

            if (string.IsNullOrEmpty(query) || history == null || history.Count == 0)
            {
                HistoryPopup.IsOpen = false;
                return;
            }

            var filtered = history.Where(h => h.ToLower().Contains(query)).Take(5).ToList();
            if (filtered.Any())
            {
                lstHistory.ItemsSource = filtered;
                HistoryPopup.IsOpen = true;
            }
            else
            {
                HistoryPopup.IsOpen = false;
            }
        }

        private void BtnDeleteHistory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string queryToRemove)
            {
                var settings = _plugin.GetSettings().Settings;
                if (settings.SearchHistory.Contains(queryToRemove))
                {
                    settings.SearchHistory.Remove(queryToRemove);
                    _plugin.SavePluginSettings(settings);
                    TxtSearch_TextChanged(null, null);
                }
            }

            e.Handled = true;
        }

        private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstHistory.SelectedItem is string selectedQuery)
            {
                txtSearch.Text = selectedQuery;
                HistoryPopup.IsOpen = false;
                _ = PerformSearch();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Добавление игры в библиотеку
        // ────────────────────────────────────────────────────────────────

        private void LstResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstResults.SelectedItem is TorrentResult result)
            {
                var existingGame = PlayniteApi.Database.Games.FirstOrDefault(g => g.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));
                if (existingGame != null)
                {
                    if (PlayniteApi.Dialogs.ShowMessage(
                        string.Format(ResourceProvider.GetString("LOC_HydraTorrent_GameAlreadyExists"), existingGame.Name),
                        ResourceProvider.GetString("LOC_HydraTorrent_Attention"),
                        MessageBoxButton.YesNo) == MessageBoxResult.No)
                        return;
                }

                string suggestedName = CleanGameName(result.Name);
                var dialogResult = PlayniteApi.Dialogs.SelectString(
                    ResourceProvider.GetString("LOC_HydraTorrent_EditGameName"),
                    ResourceProvider.GetString("LOC_HydraTorrent_GameNameTitle"),
                    suggestedName);

                if (!dialogResult.Result) return;

                string finalName = dialogResult.SelectedString?.Trim();
                if (string.IsNullOrEmpty(finalName)) return;

                var metadata = new GameMetadata { Name = finalName, Source = new MetadataNameProperty("Hydra Torrent"), IsInstalled = false };
                var importedGame = PlayniteApi.Database.ImportGame(metadata);

                if (importedGame != null)
                {
                    importedGame.PluginId = _plugin.Id;
                    importedGame.Notes = $"Источник: {result.Source}\nMagnet: {result.Magnet}";
                    PlayniteApi.Database.Games.Update(importedGame);
                    _plugin.SaveHydraData(importedGame, result);
                    PlayniteApi.MainView.SelectGame(importedGame.Id);
                    txtStatus.Text = string.Format(
                        ResourceProvider.GetString("LOC_HydraTorrent_GameAdded"),
                        finalName);
                }
            }
        }

        private string CleanGameName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;

            string name = Regex.Replace(rawName, @"\[.*?\]|\(.*?\)|v\.?\d+(\.\d+)*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"(?i)(repack|crack|update|dlc|edition|fitgirl|xatab|mechanics)", "");
            return Regex.Replace(name, @"\s+", " ").Trim('-', '.', ' ');
        }

        // ────────────────────────────────────────────────────────────────
        // Кнопки управления загрузкой
        // ────────────────────────────────────────────────────────────────

        private async void BtnPauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameId == Guid.Empty)
            {
                PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString("LOC_HydraTorrent_NoActiveDownload"),"Hydra");
                return;
            }

            var game = PlayniteApi.Database.Games.Get(_activeGameId);
            if (game == null) return;

            var torrentData = _plugin.GetHydraData(game);
            if (torrentData == null || string.IsNullOrEmpty(torrentData.TorrentHash))
            {
                PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString("LOC_HydraTorrent_TorrentHashNotFound"),ResourceProvider.GetString("LOC_HydraTorrent_Error"));
                return;
            }

            var qb = _plugin.GetSettings().Settings;
            var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");

            using var client = new QBittorrentClient(url);

            try
            {
                await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");

                // ✅ 1. Получаем текущий статус ПЕРЕД командой
                var torrents = await client.GetTorrentListAsync();
                var torrent = torrents.FirstOrDefault(t =>
                    t.Hash.Equals(torrentData.TorrentHash, StringComparison.OrdinalIgnoreCase));

                if (torrent == null)
                {
                    PlayniteApi.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOC_HydraTorrent_TorrentNotFound"),
                        ResourceProvider.GetString("LOC_HydraTorrent_Error"));
                    return;
                }

                bool isCurrentlyPaused = torrent.State.ToString().Contains("Paused");

                // ✅ 2. МГНОВЕННО переключаем UI (для отзывчивости)
                _isPaused = !isCurrentlyPaused;
                UpdatePauseButtonState();

                // ✅ 3. СРАЗУ обновляем QueueStatus (это предотвращает авто-возобновление!)
                var queueItem = _plugin.DownloadQueue.FirstOrDefault(q => q.GameId == _activeGameId);
                if (queueItem != null)
                {
                    queueItem.QueueStatus = _isPaused ? "Paused" : "Downloading";
                    _plugin.SaveQueue();
                    HydraTorrent.logger.Info($"Обновлён статус очереди: {queueItem.Name} = {queueItem.QueueStatus}");
                    _lastActiveGameId = _activeGameId;
                }

                // ✅ 4. Отправляем команду в qBittorrent
                if (_isPaused)
                {
                    await client.PauseAsync(torrentData.TorrentHash);
                }
                else
                {
                    await client.ResumeAsync(torrentData.TorrentHash);
                }

                // ✅ 5. Ждём обработки команды
                await Task.Delay(500);

                // ✅ 6. Проверяем реальный статус ТОЛЬКО для обновления данных (НЕ для _isPaused!)
                torrents = await client.GetTorrentListAsync();
                torrent = torrents.FirstOrDefault(t =>
                    t.Hash.Equals(torrentData.TorrentHash, StringComparison.OrdinalIgnoreCase));

                if (torrent != null)
                {
                    // ⚠️ НЕ меняем _isPaused здесь! Оставляем то значение, которое установили выше.

                    // Обновляем LiveStatus (данные: скорость, прогресс и т.д.)
                    var newStatus = new HydraTorrent.TorrentStatusInfo
                    {
                        Progress = torrent.Progress,
                        Status = torrent.State.ToString(),
                        DownloadSpeed = torrent.DownloadSpeed,
                        TotalSize = torrent.TotalSize ?? 0,
                        DownloadedSize = torrent.Downloaded ?? 0,
                        ETA = torrent.EstimatedTime
                    };

                    HydraTorrent.LiveStatus[_activeGameId] = newStatus;
                    UpdateDownloadUI(game, newStatus);
                }

                // ✅ 7. Уведомление
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "Hydra",
                    _isPaused ?
                        string.Format(ResourceProvider.GetString("LOC_HydraTorrent_Paused"), game.Name) :
                        string.Format(ResourceProvider.GetString("LOC_HydraTorrent_Resumed"), game.Name),
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                PlayniteApi.Dialogs.ShowErrorMessage($"Ошибка управления: {ex.Message}", "Hydra");

                // При ошибке — запрашиваем актуальный статус и синхронизируем
                try
                {
                    using var errorClient = new QBittorrentClient(url);
                    await errorClient.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");
                    var errorTorrents = await errorClient.GetTorrentListAsync();
                    var errorTorrent = errorTorrents.FirstOrDefault(t =>
                        t.Hash.Equals(torrentData.TorrentHash, StringComparison.OrdinalIgnoreCase));

                    if (errorTorrent != null)
                    {
                        _isPaused = errorTorrent.State.ToString().Contains("Paused");
                        UpdatePauseButtonState();
                    }
                }
                catch { }
            }
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsContextMenu != null)
            {
                SettingsContextMenu.PlacementTarget = btnSettings;
                SettingsContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                SettingsContextMenu.IsOpen = true;
            }
        }

        private async void DeleteTorrentAndFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameId == Guid.Empty)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOC_HydraTorrent_NoDownloadToDelete"),
                    "Hydra");
                return;
            }

            // ✅ 1. СОХРАНЯЕМ GameId В ЛОКАЛЬНУЮ ПЕРЕМЕННУЮ
            Guid gameIdToDelete = _activeGameId;

            var game = PlayniteApi.Database.Games.Get(gameIdToDelete);
            if (game == null)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOC_HydraTorrent_GameNotFound"),
                    ResourceProvider.GetString("LOC_HydraTorrent_Error"));
                return;
            }

            var torrentData = _plugin.GetHydraData(game);
            if (torrentData == null || string.IsNullOrEmpty(torrentData.TorrentHash))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOC_HydraTorrent_TorrentHashNotFound"),
                    ResourceProvider.GetString("LOC_HydraTorrent_Error"));
                return;
            }

            // ────────────────────────────────────────────────────────────────
            // ❓ ВОПРОС 1: Удалить торрент и файлы?
            // ────────────────────────────────────────────────────────────────

            var confirmTorrent = PlayniteApi.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ConfirmDeleteTorrent"), game.Name),
                ResourceProvider.GetString("LOC_HydraTorrent_DeleteTorrentTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmTorrent != MessageBoxResult.Yes)
                return;

            var qb = _plugin.GetSettings().Settings;
            var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");

            using var client = new QBittorrentClient(url);

            try
            {
                await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");
                await client.DeleteAsync(torrentData.TorrentHash, deleteDownloadedData: true);

                // ✅ 2. УДАЛЯЕМ ИЗ ОЧЕРЕДИ (используем gameIdToDelete)
                var queueItem = _plugin.DownloadQueue.FirstOrDefault(q => q.GameId == gameIdToDelete);
                if (queueItem != null)
                {
                    _plugin.DownloadQueue.Remove(queueItem);                    
                    _plugin.SaveQueue();
                    HydraTorrent.logger.Info($"Удалено из очереди: {game.Name}");
                }

                // ────────────────────────────────────────────────────────────────
                // ❓ ВОПРОС 2: Удалить игру из библиотеки?
                // ────────────────────────────────────────────────────────────────

                var confirmLibrary = PlayniteApi.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ConfirmDeleteLibrary"), game.Name),
                    ResourceProvider.GetString("LOC_HydraTorrent_DeleteFromLibraryTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                bool deleteFromLibrary = (confirmLibrary == MessageBoxResult.Yes);

                if (deleteFromLibrary)
                {
                    var torrentDataPath = System.IO.Path.Combine(
                        _plugin.GetPluginUserDataPath(),
                        "HydraTorrents",
                        $"{gameIdToDelete}.json"
                    );
                    if (System.IO.File.Exists(torrentDataPath))
                    {
                        System.IO.File.Delete(torrentDataPath);
                        HydraTorrent.logger.Info($"Удалён файл данных: {torrentDataPath}");
                    }

                    PlayniteApi.Database.Games.Remove(game);
                    txtStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_GameAndTorrentDeleted");
                    HydraTorrent.logger.Info($"Удалена игра из библиотеки: {game.Name}");

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "Hydra",
                        string.Format(ResourceProvider.GetString("LOC_HydraTorrent_DeletedFromLibrary"), game.Name),
                        NotificationType.Info));
                }
                else
                {
                    game.IsInstalling = false;
                    game.IsInstalled = false;
                    game.Notes = game.Notes?.Replace($"Источник: {torrentData.Source}\nMagnet: {torrentData.Magnet}", "")
                                              .Replace("[NEEDS_METADATA]", "")
                                              .Trim();
                    PlayniteApi.Database.Games.Update(game);
                    txtStatus.Text = ResourceProvider.GetString("LOC_HydraTorrent_TorrentDeletedGameKept");
                    HydraTorrent.logger.Info($"Торрент удалён, игра и данные сохранены: {game.Name}");

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "Hydra",
                        string.Format(ResourceProvider.GetString("LOC_HydraTorrent_TorrentDeletedDataSaved"), game.Name),
                        NotificationType.Info));
                }

                // ✅ 3. ТОЛЬКО ТЕПЕРЬ очищаем статус и UI
                HydraTorrent.LiveStatus.Remove(gameIdToDelete);

                // Проверяем, есть ли ещё загрузки в очереди
                var nextActive = _plugin.DownloadQueue.FirstOrDefault(q => q.QueueStatus == "Downloading");

                if (nextActive != null && nextActive.GameId.HasValue)
                {
                    _activeGameId = nextActive.GameId.Value;
                    _currentTorrentHash = nextActive.TorrentHash;
                    _speedHistory.Clear();
                    _graphMaxSpeed = 1;

                    var nextGame = PlayniteApi.Database.Games.Get(_activeGameId);
                    if (nextGame != null)
                    {
                        var nextStatus = HydraTorrent.LiveStatus.TryGetValue(_activeGameId, out var s) ? s : null;
                        UpdateDownloadUI(nextGame, nextStatus);
                        DrawSpeedGraph();
                    }
                }
                else
                {
                    _activeGameId = Guid.Empty;
                    _currentTorrentHash = null;
                    _speedHistory.Clear();
                    _graphMaxSpeed = 1;
                    UpdateDownloadUI(null, null);
                    DrawSpeedGraph();
                }

                // ✅ 4. Обновляем список очереди (теперь без удалённой игры)
                UpdateQueueUI();

                // ✅ 5. АВТОСТАРТ СЛЕДУЮЩЕЙ В ОЧЕРЕДИ
                await _plugin.StartNextInQueueAsync();

                // ✅ 6. Обновляем список очереди ПОСЛЕ автостарта
                UpdateQueueUI();
            }
            catch (Exception ex)
            {
                PlayniteApi.Dialogs.ShowErrorMessage($"Ошибка удаления: {ex.Message}", "Hydra");
            }
        }

        private void UpdatePauseButtonState()
        {
            if (btnPauseResume == null) return;

            // Просто переключаем иконку и тултип по локальному состоянию
            if (_isPaused)
            {
                // Иконка Play (возобновить)
                var pauseIcon = btnPauseResume.Template.FindName("PauseIcon", btnPauseResume) as Path;
                var playIcon = btnPauseResume.Template.FindName("PlayIcon", btnPauseResume) as Path;

                if (pauseIcon != null && playIcon != null)
                {
                    pauseIcon.Visibility = Visibility.Collapsed;
                    playIcon.Visibility = Visibility.Visible;
                }

                btnPauseResume.ToolTip = "Возобновить загрузку";
            }
            else
            {
                // Иконка Pause (остановить)
                var pauseIcon = btnPauseResume.Template.FindName("PauseIcon", btnPauseResume) as Path;
                var playIcon = btnPauseResume.Template.FindName("PlayIcon", btnPauseResume) as Path;

                if (pauseIcon != null && playIcon != null)
                {
                    pauseIcon.Visibility = Visibility.Visible;
                    playIcon.Visibility = Visibility.Collapsed;
                }

                btnPauseResume.ToolTip = "Поставить на паузу";
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Управление UI
        // ────────────────────────────────────────────────────────────────        

        private void DrawSpeedGraph()
        {
            // ✅ Проверка: есть ли активная игра и данные
            if (_speedHistory.Count == 0 || _activeGameId == Guid.Empty)
            {
                if (DownloadGraphCanvas != null)
                {
                    DownloadGraphCanvas.Visibility = Visibility.Collapsed;
                    DownloadGraphCanvas.Children.Clear();
                }
                if (UploadGraphCanvas != null)
                {
                    UploadGraphCanvas.Visibility = Visibility.Collapsed;
                    UploadGraphCanvas.Children.Clear();
                }
                return;
            }
            else
            {
                if (DownloadGraphCanvas != null)
                    DownloadGraphCanvas.Visibility = Visibility.Visible;
                if (UploadGraphCanvas != null)
                    UploadGraphCanvas.Visibility = Visibility.Visible;
            }

            if (DownloadGraphCanvas == null || UploadGraphCanvas == null) return;
            if (_speedHistory.Count == 0 || _uploadHistory.Count == 0) return;

            DownloadGraphCanvas.Children.Clear();
            UploadGraphCanvas.Children.Clear();

            double downloadCanvasWidth = DownloadGraphCanvas.ActualWidth;
            double downloadCanvasHeight = DownloadGraphCanvas.ActualHeight;
            double uploadCanvasWidth = UploadGraphCanvas.ActualWidth;
            double uploadCanvasHeight = UploadGraphCanvas.ActualHeight;

            if (downloadCanvasWidth <= 0 || downloadCanvasHeight <= 0) return;
            if (uploadCanvasWidth <= 0 || uploadCanvasHeight <= 0) return;

            double barWidth = downloadCanvasWidth / 15;
            double spacing = barWidth * 0.4;
            double barEffectiveWidth = barWidth - spacing;

            var downloadSpeeds = _speedHistory.ToArray();
            var uploadSpeeds = _uploadHistory.ToArray();

            // ────────────────────────────────────────────────────────────────
            // ✅ ВЕРХНИЙ CANVAS: График загрузки (синий, растёт снизу вверх)
            // ────────────────────────────────────────────────────────────────

            for (int i = 0; i < downloadSpeeds.Length; i++)
            {
                long speed = downloadSpeeds[i];

                double height = (speed / (double)_graphMaxSpeed) * downloadCanvasHeight * 0.85;

                if (height < 4) height = 4;

                var bar = new Rectangle
                {
                    Width = barEffectiveWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromRgb(79, 195, 247)), // #4FC3F7 — синий
                    RadiusX = 0,
                    RadiusY = 0
                };

                double left = downloadCanvasWidth - (downloadSpeeds.Length - i) * barWidth + spacing / 2;
                Canvas.SetLeft(bar, left);
                Canvas.SetBottom(bar, 0); // ✅ Растёт от низа Canvas вверх

                DownloadGraphCanvas.Children.Add(bar);
            }

            // ────────────────────────────────────────────────────────────────
            // ✅ НИЖНИЙ CANVAS: График отдачи (зелёный, растёт СВЕРХУ ВНИЗ)
            // ────────────────────────────────────────────────────────────────

            for (int i = 0; i < uploadSpeeds.Length; i++)
            {
                long speed = uploadSpeeds[i];

                double height = (speed / (double)_uploadMaxSpeed) * uploadCanvasHeight * 0.85;

                if (height < 4) height = 4;

                var bar = new Rectangle
                {
                    Width = barEffectiveWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromRgb(46, 204, 113)), // #2ECC71 — зелёный
                    RadiusX = 0,
                    RadiusY = 0
                };

                double left = uploadCanvasWidth - (uploadSpeeds.Length - i) * barWidth + spacing / 2;
                Canvas.SetLeft(bar, left);
                Canvas.SetTop(bar, 0); // ✅ ИСПРАВЛЕНО: Растёт от верха Canvas вниз (было SetBottom)

                UploadGraphCanvas.Children.Add(bar);
            }

            // ────────────────────────────────────────────────────────────────
            // ✅ Добавляем градиент прозрачности слева (затухание) для обоих Canvas
            // ────────────────────────────────────────────────────────────────

            var mask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.02));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.70));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1.0));

            DownloadGraphCanvas.OpacityMask = mask;
            UploadGraphCanvas.OpacityMask = mask;
        }

        // ────────────────────────────────────────────────────────────────
        // INotifyPropertyChanged
        // ────────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }        

        // ────────────────────────────────────────────────────────────────
        // Управление очередью загрузок (UI)
        // ────────────────────────────────────────────────────────────────

        // ✅ Публичный метод для обновления из других классов
        public async void RefreshQueueUI()
        {
            if (lstQueue == null || txtQueueEmpty == null) return;

            // ✅ Ждём пока UI будет готов
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateQueueUI();

                // ✅ ФОРСИРУЕМ обновление таймера (сбрасываем _activeGameId чтобы он пересчитался)
                // Это поможет если активная игра изменилась пока View был закрыт
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateQueueUI()
        {
            if (lstQueue == null || txtQueueEmpty == null) return;

            var queue = _plugin.DownloadQueue;

            if (queue == null || !queue.Any())
            {
                lstQueue.ItemsSource = null;
                txtQueueEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                // ✅ ИСКЛЮЧАЕМ активную загрузку из списка очереди
                var queuedGames = queue
                    .Where(q => q.QueueStatus != "Completed" && q.QueueStatus != "Downloading")
                    .OrderBy(q => q.QueuePosition)
                    .ToList();

                lstQueue.ItemsSource = queuedGames;
                txtQueueEmpty.Visibility = queuedGames.Any() ? Visibility.Collapsed : Visibility.Visible;

                // ✅ ЗАГРУЖАЕМ ОБЛОЖКИ С ПОВТОРНЫМИ ПОПЫТКАМИ
                LoadQueueCoversWithRetry(queuedGames, 0);
            }
        }

        private void BtnQueueUp_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is TorrentResult item)
            {
                MoveQueueItem(item, -1);
            }
        }

        private void BtnQueueDown_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is TorrentResult item)
            {
                MoveQueueItem(item, 1);
            }
        }

        private void MoveQueueItem(TorrentResult item, int direction)
        {
            var queue = _plugin.DownloadQueue;
            if (queue == null || !queue.Any()) return;

            // Находим текущий индекс элемента в СПИСКЕ
            var currentIndex = queue.IndexOf(item);
            if (currentIndex < 0) return;

            // Вычисляем новый индекс
            int newIndex = currentIndex + direction;

            // Проверяем границы (не позволяем переместить на позицию 0 - это для активной загрузки)
            if (newIndex < 1 || newIndex >= queue.Count) return;

            // Не позволяем перемещать активную загрузку
            if (item.QueueStatus == "Downloading") return;

            // ✅ МЕНЯЕМ МЕСТАМИ ЭЛЕМЕНТЫ В СПИСКЕ (это критически важно!)
            var temp = queue[currentIndex];
            queue[currentIndex] = queue[newIndex];
            queue[newIndex] = temp;

            // ✅ Пересчитываем позиции по новому порядку списка
            _plugin.RecalculateQueuePositions();

            // Обновляем UI
            UpdateQueueUI();

            HydraTorrent.logger.Info($"Элемент перемещён: {item.Name} с индекса {currentIndex} на {newIndex}");
        }

        private async void BtnQueueForceStart_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is TorrentResult item)
            {
                if (!item.GameId.HasValue)
                {
                    PlayniteApi.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOC_HydraTorrent_NoGameId"),
                        ResourceProvider.GetString("LOC_HydraTorrent_Error"));
                    return;
                }

                var confirm = PlayniteApi.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ConfirmForceStart"), item.Name),
                    ResourceProvider.GetString("LOC_HydraTorrent_ForceStartTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                // 1. Находим текущую активную загрузку и ставим на паузу
                var currentActive = _plugin.DownloadQueue.FirstOrDefault(q => q.QueueStatus == "Downloading");
                if (currentActive != null && currentActive.GameId.HasValue)
                {
                    currentActive.QueueStatus = "Paused";

                    var qb = _plugin.GetSettings().Settings;
                    var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");
                    using var client = new QBittorrentClient(url);
                    await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");
                    await client.PauseAsync(currentActive.TorrentHash);
                }

                // 2. Запускаем выбранную игру
                item.QueueStatus = "Downloading";

                // 3. Перемещаем её на первое место в списке
                var queue = _plugin.DownloadQueue;
                var currentIndex = queue.IndexOf(item);
                if (currentIndex > 0)
                {
                    queue.RemoveAt(currentIndex);
                    queue.Insert(0, item);
                }

                // 4. Пересчитываем позиции
                _plugin.RecalculateQueuePositions();

                // 5. Возобновляем торрент в qBittorrent
                var qb2 = _plugin.GetSettings().Settings;
                var url2 = new Uri($"http://{qb2.QBittorrentHost}:{qb2.QBittorrentPort}");
                using var client2 = new QBittorrentClient(url2);
                await client2.LoginAsync(qb2.QBittorrentUsername, qb2.QBittorrentPassword ?? "");
                await client2.ResumeAsync(item.TorrentHash);

                // ✅ 6. ОБНОВЛЯЕМ _activeGameId и _lastActiveGameId (это отсутствовало!)
                _activeGameId = item.GameId.Value;
                _lastActiveGameId = item.GameId.Value;
                _isPaused = false;
                UpdatePauseButtonState();

                // 7. Обновляем UI очереди
                UpdateQueueUI();

                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "Hydra",
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_Started"), item.Name),
                    NotificationType.Info));
            }
        }

        private async void BtnQueueRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is TorrentResult item)
            {
                if (!item.GameId.HasValue)
                {
                    PlayniteApi.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOC_HydraTorrent_NoGameId"),
                        ResourceProvider.GetString("LOC_HydraTorrent_Error"));
                    return;
                }

                var confirm = PlayniteApi.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ConfirmRemoveFromQueue"), item.Name),
                    ResourceProvider.GetString("LOC_HydraTorrent_RemoveFromQueueTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;

                var qb = _plugin.GetSettings().Settings;
                var url = new Uri($"http://{qb.QBittorrentHost}:{qb.QBittorrentPort}");

                using var client = new QBittorrentClient(url);

                try
                {
                    await client.LoginAsync(qb.QBittorrentUsername, qb.QBittorrentPassword ?? "");
                    await client.PauseAsync(item.TorrentHash);

                    var queueItem = _plugin.DownloadQueue.FirstOrDefault(q => q.GameId == item.GameId);
                    if (queueItem != null)
                    {
                        _plugin.DownloadQueue.Remove(queueItem);
                        _plugin.RecalculateQueuePositions();
                        _plugin.SaveQueue();

                        if (_coverCache.ContainsKey(item.GameId.Value))
                        {
                            _coverCache.Remove(item.GameId.Value);
                        }
                    }

                    HydraTorrent.LiveStatus.Remove(item.GameId.Value);

                    // ✅ ОБНОВЛЯЕМ UI СРАЗУ
                    UpdateQueueUI();

                    // ✅ АВТОСТАРТ СЛЕДУЮЩЕЙ В ОЧЕРЕДИ (ЭТОГО НЕ ХВАТАЛО!)
                    await _plugin.StartNextInQueueAsync();

                    // ✅ ОБНОВЛЯЕМ UI ПОСЛЕ АВТОСТАРТА
                    UpdateQueueUI();

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "Hydra",
                        string.Format(ResourceProvider.GetString("LOC_HydraTorrent_RemovedFromQueue"), item.Name),
                        NotificationType.Info));
                }
                catch (Exception ex)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage($"Ошибка: {ex.Message}", "Hydra");
                }
            }
        }
    }

    public class SourceFilterItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Name { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }



    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
    }

    public class ColumnWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double actualWidth && parameter is string multiplierStr)
            {
                if (double.TryParse(multiplierStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double multiplier))
                {
                    double finalWidth = (actualWidth - 30) * multiplier;
                    return finalWidth < 0 ? 0 : finalWidth;
                }
            }
            return 100;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}