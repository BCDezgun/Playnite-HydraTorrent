using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace HydraTorrent.Views
{
    public partial class HydraHubView : UserControl
    {
        private readonly IPlayniteAPI PlayniteApi;
        private readonly HydraTorrent _plugin;
        private readonly ScraperService _scraperService;

        private List<TorrentResult> _allResults = new List<TorrentResult>();
        private int _currentPage = 1;
        private const int _itemsPerPage = 10;

        public HydraHubView(IPlayniteAPI api, HydraTorrent plugin)
        {
            InitializeComponent();
            PlayniteApi = api;
            _plugin = plugin;
            _scraperService = plugin.GetScraperService();
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            PlayniteApi.MainView.SwitchToLibraryView();
        }

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

        private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstHistory.SelectedItem is string selectedQuery)
            {
                txtSearch.Text = selectedQuery;
                HistoryPopup.IsOpen = false;
                _ = PerformSearch();
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
            _currentPage = 1;

            try
            {
                _allResults = await _scraperService.SearchAsync(query);
                if (_allResults == null || _allResults.Count == 0)
                {
                    txtStatus.Text = "Ничего не найдено 😔";
                    pnlPagination.Children.Clear();
                }
                else
                {
                    ShowPage(1);
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
            _currentPage = pageNumber;
            var pageData = _allResults.Skip((_currentPage - 1) * _itemsPerPage).Take(_itemsPerPage).ToList();
            lstResults.ItemsSource = pageData;
            int totalPages = (int)Math.Ceiling((double)_allResults.Count / _itemsPerPage);
            txtStatus.Text = $"Найдено: {_allResults.Count} (Страница {_currentPage} из {totalPages})";
            UpdatePaginationButtons(totalPages);
        }

        private void UpdatePaginationButtons(int totalPages)
        {
            pnlPagination.Children.Clear();
            if (totalPages <= 1) return;
            for (int i = 1; i <= totalPages; i++)
            {
                var btn = new Button { Content = $" {i} ", Tag = i, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, Background = (i == _currentPage) ? Brushes.SkyBlue : Brushes.Transparent };
                btn.Click += (s, e) => { if (s is Button b && b.Tag is int p) ShowPage(p); };
                pnlPagination.Children.Add(btn);
            }
        }

        private void LstResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstResults.SelectedItem is TorrentResult result)
            {
                var existingGame = PlayniteApi.Database.Games.FirstOrDefault(g => g.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));
                if (existingGame != null)
                {
                    if (PlayniteApi.Dialogs.ShowMessage($"Игра «{existingGame.Name}» уже есть. Добавить еще раз?", "Внимание", MessageBoxButton.YesNo) == MessageBoxResult.No) return;
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
    }

    // КОНВЕРТЕР ДЛЯ ПРОЦЕНТНОЙ ШИРИНЫ КОЛОНОК
    public class ColumnWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double actualWidth && parameter is string multiplierStr)
            {
                if (double.TryParse(multiplierStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double multiplier))
                {
                    // Вычитаем 30 пикселей на возможный ScrollBar, чтобы не было дерганий
                    double finalWidth = (actualWidth - 30) * multiplier;
                    return finalWidth < 0 ? 0 : finalWidth;
                }
            }
            return 100; // Дефолт
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}