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
        private readonly HydraTorrent _plugin;
        // УБИРАЕМ создание нового сервиса здесь!
        private readonly ScraperService _scraperService;

        private List<TorrentResult> _allResults = new List<TorrentResult>();
        private int _currentPage = 1;
        private const int _itemsPerPage = 10;

        public HydraHubView(IPlayniteAPI api, HydraTorrent plugin)
        {
            InitializeComponent();
            PlayniteApi = api;
            _plugin = plugin;

            // ПОЛУЧАЕМ сервис из плагина (который мы создали в HydraTorrent.cs)
            // Для этого в HydraTorrent.cs добавь публичное поле или свойство для _scraperService
            _scraperService = plugin.GetScraperService();
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

            // ПРОВЕРКА: Если источников нет, сразу предупреждаем
            var settings = _plugin.GetSettings().Settings;
            if (settings.Sources == null || settings.Sources.Count == 0)
            {
                txtStatus.Text = "⚠️ Источники не настроены! Добавьте их в настройках плагина.";
                return;
            }

            txtStatus.Text = $"🔎 Ищем «{query}»...";
            lstResults.ItemsSource = null;
            btnSearch.IsEnabled = false;
            _currentPage = 1;

            try
            {
                // Используем правильный сервис
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
                HydraTorrent.logger.Error(ex, "Search failed in Hub");
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
                // 1. ПРОВЕРКА: А вдруг такая игра уже есть?
                var existingGame = PlayniteApi.Database.Games.FirstOrDefault(g =>
                    g.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));

                if (existingGame != null)
                {
                    var res = PlayniteApi.Dialogs.ShowMessage(
                        $"Игра с похожим названием уже есть в библиотеке ({existingGame.Name}). Всё равно добавить новую версию?",
                        "Внимание", MessageBoxButton.YesNo);
                    if (res == MessageBoxResult.No) return;
                }

                var confirm = MessageBox.Show(
                    $"Добавить игру «{result.Name}» в библиотеку Playnite?",
                    "Подтверждение",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                string suggestedName = CleanGameName(result.Name);
                var dialogResult = PlayniteApi.Dialogs.SelectString(
                    "Отредактируйте название игры для корректного поиска метаданных:",
                    "Название игры",
                    suggestedName);

                if (!dialogResult.Result) return;

                string finalName = dialogResult.SelectedString?.Trim();
                if (string.IsNullOrEmpty(finalName)) return;

                try
                {
                    var metadata = new GameMetadata
                    {
                        Name = finalName,
                        Source = new MetadataNameProperty("Hydra Torrent"),
                        IsInstalled = false
                        // PluginId здесь НЕ НУЖЕН и вызывает ошибку
                    };

                    // Импортируем игру в базу данных
                    var importedGame = PlayniteApi.Database.ImportGame(metadata);

                    if (importedGame != null)
                    {
                        // А вот здесь мы уже работаем с реальным объектом Game из базы
                        // и назначаем ему ID нашего плагина
                        importedGame.PluginId = _plugin.Id;

                        importedGame.Notes = $"Источник: {result.Source}\n" +
                                             $"Оригинальное название: {result.Name}\n" +
                                             $"Magnet: {result.Magnet}";

                        // Добавляем тег
                        var tag = PlayniteApi.Database.Tags.Add("Hydra Torrent");
                        if (importedGame.TagIds == null) importedGame.TagIds = new List<Guid>();
                        if (!importedGame.TagIds.Contains(tag.Id)) importedGame.TagIds.Add(tag.Id);

                        // Сохраняем данные для торрента
                        _plugin.SaveHydraData(importedGame, result);

                        // Обновляем игру в базе, чтобы сохранить PluginId, Notes и Tags
                        PlayniteApi.Database.Games.Update(importedGame);

                        PlayniteApi.MainView.SelectGame(importedGame.Id);

                        txtStatus.Text = $"✅ «{finalName}» добавлена!";
                    }
                }
                catch (Exception ex)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Ошибка добавления");
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