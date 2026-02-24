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
            var activeDownload = HydraTorrent.LiveStatus.FirstOrDefault(x =>
                x.Value.Status.Contains("Загрузка") && !x.Value.Status.Contains("Пауза"));

            if (activeDownload.Key == Guid.Empty)
            {
                activeDownload = HydraTorrent.LiveStatus.FirstOrDefault();
            }

            if (activeDownload.Key != Guid.Empty)
            {
                var status = activeDownload.Value;
                var game = PlayniteApi.Database.Games.Get(activeDownload.Key);
                if (game != null)
                {
                    UpdateDownloadUI(game, status);
                }
            }
        }

        public void UpdateDownloadUI(Game game, HydraTorrent.TorrentStatusInfo status)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateGameBackground(game);

                if (status == null) return;

                if (txtCurrentGameName != null)
                    txtCurrentGameName.Text = game?.Name?.ToUpper() ?? "ЗАГРУЗКА...";

                long currentSpeedBytes = status.DownloadSpeed;
                if (currentSpeedBytes > _maxSpeedSeen) _maxSpeedSeen = currentSpeedBytes;

                lblCurrentSpeed.Text = FormatSpeed(currentSpeedBytes);
                lblMaxSpeed.Text = FormatSpeed(_maxSpeedSeen);

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

                System.Diagnostics.Debug.WriteLine($"[Hydra] UI Updated: {uiProgress:F1}%");
            });
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
        // INotifyPropertyChanged
        // ────────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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