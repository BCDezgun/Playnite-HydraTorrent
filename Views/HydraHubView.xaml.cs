using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HydraTorrent.Views
{
    public partial class HydraHubView : UserControl
    {
        private readonly IPlayniteAPI PlayniteApi;
        private readonly ScraperService _scraperService = new ScraperService();
        private readonly HydraTorrent _plugin;

        private List<TorrentResult> _allResults = new List<TorrentResult>();
        private int _currentPage = 1;
        private const int _itemsPerPage = 10;

        public HydraHubView(IPlayniteAPI api, HydraTorrent plugin)
        {
            InitializeComponent();
            PlayniteApi = api;
            _plugin = plugin;
        }

        // ==================== КНОПКА НАЗАД ====================
        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            PlayniteApi.MainView.SwitchToLibraryView();
        }

        // ==================== ПОИСК ====================
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

            txtStatus.Text = $"🔎 Ищем «{query}» по всем источникам...";
            lstResults.ItemsSource = null;
            btnSearch.IsEnabled = false;
            _currentPage = 1;

            try
            {
                _allResults = await _scraperService.SearchAsync(query);

                if (_allResults.Count == 0)
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
            var pageData = _allResults
                .Skip((_currentPage - 1) * _itemsPerPage)
                .Take(_itemsPerPage)
                .ToList();

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
                var btn = new Button
                {
                    Content = $" {i} ",
                    Tag = i,
                    Margin = new Thickness(3, 0, 3, 0),
                    Padding = new Thickness(5),
                    Cursor = Cursors.Hand,
                    Background = (i == _currentPage) ? Brushes.SkyBlue : Brushes.Transparent,
                    BorderBrush = Brushes.Gray
                };

                btn.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is int p)
                        ShowPage(p);
                };

                pnlPagination.Children.Add(btn);
            }
        }

        private void LstResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstResults.SelectedItem is TorrentResult result)
            {
                var confirm = MessageBox.Show(
                    $"Добавить игру «{result.Name}» в библиотеку Playnite?",
                    "Подтверждение добавления",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                string suggestedName = CleanGameName(result.Name);

                var dialogResult = PlayniteApi.Dialogs.SelectString(
                    "Отредактируйте название игры...\nУберите repack, версии, DLC, год и т.д.:",
                    "Редактирование названия игры",
                    suggestedName);

                if (!dialogResult.Result) return;

                string finalName = dialogResult.SelectedString?.Trim();
                if (string.IsNullOrEmpty(finalName))
                {
                    MessageBox.Show("Название не может быть пустым.", "Ошибка");
                    return;
                }

                try
                {
                    Guid myPluginId = Guid.Parse("c2177dc7-8179-4098-8b6c-d683ce415279");

                    var metadata = new GameMetadata
                    {
                        Name = finalName,
                        Source = new MetadataNameProperty("Hydra Torrent"),
                        IsInstalled = false
                    };

                    var importedGame = PlayniteApi.Database.ImportGame(metadata);

                    if (importedGame != null)
                    {
                        importedGame.PluginId = myPluginId;
                        importedGame.Notes = $"Источник: {result.Source}\n" +
                                             $"Оригинальное название: {result.Name}\n" +
                                             $"Magnet: {result.Magnet}";

                        var tag = PlayniteApi.Database.Tags.Add("Hydra Torrent");
                        if (importedGame.TagIds == null) importedGame.TagIds = new List<Guid>();
                        importedGame.TagIds.Add(tag.Id);

                        _plugin.SaveHydraData(importedGame, result);
                        PlayniteApi.Database.Games.Update(importedGame);
                        PlayniteApi.MainView.SelectGame(importedGame.Id);

                        txtStatus.Text = $"✅ Игра «{finalName}» успешно добавлена!";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string CleanGameName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;
            string name = rawName.Trim();
            name = Regex.Replace(name, @"\[.*?\]", "");
            name = Regex.Replace(name, @"\(.*?\)", "");
            name = Regex.Replace(name, @"v\.?\d+(\.\d+)*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"(repack|crack|fixed|update|dlc|multi|ultimate|deluxe|edition|goty|complete|reloaded|codex|empress|flt|skidrow|fitgirl|xatab|by xatab|rg mechanics|decepticon)", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s+", " ").Trim();
            return name.Trim('-', '.', ' ');
        }
    }
}