using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using Playnite.SDK;
using Playnite.SDK.Models;
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
using System.Windows.Threading;
using System.Windows.Shapes;
using QBittorrent.Client;

namespace HydraTorrent.Views
{
    public partial class HydraHubView : UserControl, INotifyPropertyChanged
    {
        private readonly IPlayniteAPI PlayniteApi;
        private readonly HydraTorrent _plugin;
        private readonly ScraperService _scraperService;

        private DispatcherTimer _uiRefreshTimer;
        private long _maxSpeedSeen = 0;
        private Guid _activeGameId = Guid.Empty;
        private string _currentTorrentHash = null;
        private bool _isPaused = false;
        private Guid _lastActiveGameId = Guid.Empty;  // ✅ Запоминаем последнюю активную игру

        private readonly Queue<long> _speedHistory = new Queue<long>(); // последние 15 скоростей (байты/с)
        private long _graphMaxSpeed = 1; // текущий максимум для масштаба (минимум 1, чтобы не делить на 0)

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

        private string _sourceButtonText = "Все источники";
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

                if (queueItem == null || queueItem.QueueStatus == "Completed")
                {
                    // Игра завершена или удалена — сбрасываем запомненную
                    _lastActiveGameId = Guid.Empty;
                }
                else if (queueItem.QueueStatus == "Downloading" || queueItem.QueueStatus == "Paused")
                {
                    // Игра всё ещё активна — продолжаем показывать её
                    var lastActiveStatus = HydraTorrent.LiveStatus.TryGetValue(_lastActiveGameId, out var s) ? s : null;
                    if (lastActiveStatus != null)
                    {
                        var game = PlayniteApi.Database.Games.Get(_lastActiveGameId);
                        if (game != null)
                        {
                            UpdateDownloadUI(game, lastActiveStatus);
                            UpdatePauseButtonState();
                            DrawSpeedGraph();
                            return;
                        }
                    }
                }
                else
                {
                    // Игра в очереди но не активна — сбрасываем
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
                    _graphMaxSpeed = 1;
                    _maxSpeedSeen = 0;
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
                    _graphMaxSpeed = 1;
                    _maxSpeedSeen = 0;

                    UpdateDownloadUI(null, null);
                    DrawSpeedGraph();
                }
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
                lblCurrentSpeed.Text = "0 Мбит/с";
                lblMaxSpeed.Text = "0 Мбит/с";
                pbDownload.Value = 0;
                lblDownloadedAmount.Text = "0 ГБ / 0 ГБ";
                lblETA.Text = "Осталось: --:--:--";
                SpeedGraphCanvas.Visibility = Visibility.Collapsed;

                if (lblLoadingStatus != null)
                {
                    lblLoadingStatus.Visibility = Visibility.Collapsed;
                }

                btnPauseResume.Visibility = Visibility.Collapsed;
                btnSettings.Visibility = Visibility.Collapsed;
                txtStatus.Text = "Очередь загрузок появится здесь...";
                _maxSpeedSeen = 0;
                return;
            }

            // Обычное обновление при наличии статуса
            txtCurrentGameName.Text = game.Name?.ToUpper() ?? "ЗАГРУЗКА...";

            long currentSpeedBytes = status.DownloadSpeed;
            if (currentSpeedBytes > _maxSpeedSeen) _maxSpeedSeen = currentSpeedBytes;

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

            double uiProgress = status.Progress;
            if (uiProgress > 0 && uiProgress <= 1.0)
                uiProgress *= 100;

            pbDownload.Value = uiProgress;

            double downloadedGB = status.DownloadedSize / 1024.0 / 1024.0 / 1024.0;
            double totalGB = status.TotalSize / 1024.0 / 1024.0 / 1024.0;

            lblDownloadedAmount.Text = $"{uiProgress:F1}% ({downloadedGB:F1} ГБ / {totalGB:F1} ГБ)";

            if (status.ETA.HasValue && status.ETA.Value.TotalSeconds > 0)
            {
                string timeFormat = status.ETA.Value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss";
                lblETA.Text = $"Осталось примерно: {status.ETA.Value.ToString(timeFormat)}";
            }
            else
            {
                lblETA.Text = "Осталось: --:--:--";
            }

            if (lblLoadingStatus != null)
            {
                lblLoadingStatus.Visibility = Visibility.Visible;

                if (status.Status.Contains("Пауза") || status.Status.Contains("paused"))
                {
                    lblLoadingStatus.Text = "Простаивает";
                    lblLoadingStatus.Foreground = new SolidColorBrush(Colors.Gray);
                }
                else
                {
                    lblLoadingStatus.Text = "Загружается";
                    lblLoadingStatus.Foreground = new SolidColorBrush(Color.FromRgb(46, 204, 113));
                }
            }

            btnPauseResume.Visibility = Visibility.Visible;
            btnSettings.Visibility = Visibility.Visible;

            DrawSpeedGraph();
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
            double mbps = (bytesPerSecond * 8.0) / (1024 * 1024);
            return $"{mbps:F1} Мбит/с";
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
                SourceButtonText = "Все источники";
                return;
            }

            var selected = FilterSources.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                SourceButtonText = "Не выбрано";
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
                txtStatus.Text = "Введите название игры!";
                return;
            }

            var settings = _plugin.GetSettings().Settings;
            if (settings.Sources == null || settings.Sources.Count == 0)
            {
                txtStatus.Text = "⚠️ Источники не настроены!";
                return;
            }

            if (!settings.SearchHistory.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                settings.SearchHistory.Insert(0, query);
                if (settings.SearchHistory.Count > 20) settings.SearchHistory.RemoveAt(20);
                _plugin.SavePluginSettings(settings);
            }

            txtStatus.Text = $"🔎 Ищем «{query}»...";
            lstResults.ItemsSource = null;
            btnSearch.IsEnabled = false;
            pnlPagination.Children.Clear();

            try
            {
                var results = await _scraperService.SearchAsync(query);
                _allResults = results ?? new List<TorrentResult>();

                if (_allResults.Count == 0)
                {
                    txtStatus.Text = "Ничего не найдено 😔";
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
                txtStatus.Text = "Нет результатов для выбранных фильтров";
                return;
            }

            _currentPage = pageNumber;
            var pageData = _filteredResults.Skip((_currentPage - 1) * _itemsPerPage).Take(_itemsPerPage).ToList();
            lstResults.ItemsSource = pageData;

            int totalPages = (int)Math.Ceiling((double)_filteredResults.Count / _itemsPerPage);
            txtStatus.Text = $"Найдено: {_filteredResults.Count} (Страница {_currentPage} из {totalPages})";

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
                    if (PlayniteApi.Dialogs.ShowMessage($"Игра «{existingGame.Name}» уже есть. Добавить еще раз?", "Внимание", MessageBoxButton.YesNo) == MessageBoxResult.No)
                        return;
                }

                string suggestedName = CleanGameName(result.Name);
                var dialogResult = PlayniteApi.Dialogs.SelectString("Отредактируйте название:", "Название игры", suggestedName);

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
                    txtStatus.Text = $"✅ «{finalName}» добавлена!";
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
                PlayniteApi.Dialogs.ShowMessage("Нет активной загрузки.", "Hydra");
                return;
            }

            var game = PlayniteApi.Database.Games.Get(_activeGameId);
            if (game == null) return;

            var torrentData = _plugin.GetHydraData(game);
            if (torrentData == null || string.IsNullOrEmpty(torrentData.TorrentHash))
            {
                PlayniteApi.Dialogs.ShowMessage("Не найден хеш торрента.", "Ошибка");
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
                    PlayniteApi.Dialogs.ShowMessage("Торрент не найден в qBittorrent.", "Ошибка");
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
                    _isPaused ? $"На паузе: {game.Name}" : $"Возобновлено: {game.Name}",
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
                PlayniteApi.Dialogs.ShowMessage("Нет активной загрузки для удаления.", "Hydra");
                return;
            }

            // ✅ 1. СОХРАНЯЕМ GameId В ЛОКАЛЬНУЮ ПЕРЕМЕННУЮ
            Guid gameIdToDelete = _activeGameId;

            var game = PlayniteApi.Database.Games.Get(gameIdToDelete);
            if (game == null)
            {
                PlayniteApi.Dialogs.ShowMessage("Игра не найдена.", "Ошибка");
                return;
            }

            var torrentData = _plugin.GetHydraData(game);
            if (torrentData == null || string.IsNullOrEmpty(torrentData.TorrentHash))
            {
                PlayniteApi.Dialogs.ShowMessage("Не найден хеш торрента.", "Ошибка");
                return;
            }

            // ────────────────────────────────────────────────────────────────
            // ❓ ВОПРОС 1: Удалить торрент и файлы?
            // ────────────────────────────────────────────────────────────────

            var confirmTorrent = PlayniteApi.Dialogs.ShowMessage(
                $"Удалить торрент «{game.Name}»\nи все скачанные файлы?",
                "Удаление торрента",
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
                    $"Удалить игру «{game.Name}»\nиз библиотеки Playnite?",
                    "Удаление из библиотеки",
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
                    txtStatus.Text = "Игра и торрент удалены";
                    HydraTorrent.logger.Info($"Удалена игра из библиотеки: {game.Name}");

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "Hydra",
                        $"«{game.Name}» удалена из библиотеки",
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
                    txtStatus.Text = "Торрент удалён, игра осталась в библиотеке";
                    HydraTorrent.logger.Info($"Торрент удалён, игра и данные сохранены: {game.Name}");

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "Hydra",
                        $"Торрент «{game.Name}» удалён. Данные сохранены для повторной загрузки.",
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
            if (_speedHistory.Count == 0 || _activeGameId == Guid.Empty)
            {
                SpeedGraphCanvas.Visibility = Visibility.Collapsed;
                SpeedGraphCanvas.Children.Clear();
                return;
            }
            else
            {
                SpeedGraphCanvas.Visibility = Visibility.Visible;
            }

            if (SpeedGraphCanvas == null || _speedHistory.Count == 0)
            {
                SpeedGraphCanvas?.Children.Clear();
                return;
            }

            SpeedGraphCanvas.Children.Clear();

            double canvasWidth = SpeedGraphCanvas.ActualWidth;
            double canvasHeight = SpeedGraphCanvas.ActualHeight;

            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            double barWidth = canvasWidth / 15;           // ширина одного столбика
            double spacing = barWidth * 0.4;              // отступ между столбиками (20% ширины)
            double barEffectiveWidth = barWidth - spacing;

            // Преобразуем очередь в массив для удобства (слева — старое, справа — новое)
            var speeds = _speedHistory.ToArray();

            for (int i = 0; i < speeds.Length; i++)
            {
                long speed = speeds[i];

                // Высота столбика (динамический масштаб)
                double height = (speed / (double)_graphMaxSpeed) * canvasHeight * 0.85; // 85% от высоты — запас сверху

                // Минимальная высота, чтобы даже 1 Мбит/с был виден
                if (height < 4) height = 4;

                var bar = new Rectangle
                {
                    Width = barEffectiveWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromRgb(79, 195, 247)), // #4FC3F7 — синий как в Steam
                    RadiusX = 0, //скругление
                    RadiusY = 0  //скругление
                };

                // Позиция: слева направо double left = i * barWidth + spacing / 2;
                double left = canvasWidth - (speeds.Length - i) * barWidth + spacing / 2;
                Canvas.SetLeft(bar, left);
                Canvas.SetBottom(bar, 0); // столбики растут снизу вверх

                SpeedGraphCanvas.Children.Add(bar);
            }

            // Добавляем градиент прозрачности слева (затухание)
            var mask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));   // полностью прозрачно слева
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.02));   // до 40% ширины — всё ещё прозрачно
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.70));         // к 50% — полностью непрозрачно
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1.0));          // дальше всё видно

            SpeedGraphCanvas.OpacityMask = mask;
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
                    PlayniteApi.Dialogs.ShowMessage("Не найден ID игры.", "Ошибка");
                    return;
                }

                var confirm = PlayniteApi.Dialogs.ShowMessage(
                    $"Запустить «{item.Name}»?\n\nТекущая активная загрузка будет поставлена на паузу.",
                    "Принудительный старт",
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
                    $"Запущено: {item.Name}",
                    NotificationType.Info));
            }
        }

        private async void BtnQueueRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is TorrentResult item)
            {
                if (!item.GameId.HasValue)
                {
                    PlayniteApi.Dialogs.ShowMessage("Не найден ID игры.", "Ошибка");
                    return;
                }

                var confirm = PlayniteApi.Dialogs.ShowMessage(
                    $"Удалить «{item.Name}» из очереди?\n\nТоррент будет остановлен, но файлы останутся на диске.",
                    "Удаление из очереди",
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
                    }

                    HydraTorrent.LiveStatus.Remove(item.GameId.Value);
                    UpdateQueueUI();

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "Hydra",
                        $"Удалено из очереди: {item.Name}",
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