using HydraTorrent.Models;
using HydraTorrent.Scrapers;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq; // ИЗМЕНЕНИЕ: Добавлено для Skip() и Take()
using System.Text.RegularExpressions;   // ← обязательно для мощной очистки
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls; // ИЗМЕНЕНИЕ: Для динамического создания кнопок
using System.Windows.Input;
using System.Windows.Media; // ИЗМЕНЕНИЕ: Для оформления кнопок


namespace HydraTorrent
{
    public partial class SearchWindow : Window
    {
        private readonly IPlayniteAPI PlayniteApi;
        private readonly ScraperService _scraperService;
        private readonly HydraTorrent _plugin;
        // Обновляем конструктор
        public SearchWindow(IPlayniteAPI api, HydraTorrent plugin)
        {
            InitializeComponent();
            PlayniteApi = api;
            _plugin = plugin; // Сохраняем ссылку на плагин
        }

        // === ИЗМЕНЕНИЕ: Поля для управления страницами ===
        private List<TorrentResult> _allResults = new List<TorrentResult>();
        private int _currentPage = 1;
        private const int _itemsPerPage = 10;

        public SearchWindow(IPlayniteAPI api)
        {
            PlayniteApi = api;
            InitializeComponent();
            txtSearch.Focus();
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

            txtStatus.Text = $"🔎 Ищем «{query}» по всем источникам...";
            lstResults.ItemsSource = null;
            btnSearch.IsEnabled = false;

            // ИЗМЕНЕНИЕ: Сброс текущей страницы при новом поиске
            _currentPage = 1;

            try
            {
                // ИЗМЕНЕНИЕ: Сохраняем все результаты в буфер _allResults
                _allResults = await _scraperService.SearchAsync(query);

                if (_allResults.Count == 0)
                {
                    txtStatus.Text = "Ничего не найдено 😔";
                    if (pnlPagination != null) pnlPagination.Children.Clear();
                }
                else
                {
                    // ИЗМЕНЕНИЕ: Вместо прямой привязки вызываем метод отображения страницы
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

        // === ИЗМЕНЕНИЕ: Новый метод для переключения страниц ===
        private void ShowPage(int pageNumber)
        {
            _currentPage = pageNumber;

            // Вырезаем нужную "порцию" данных
            var pageData = _allResults
                .Skip((_currentPage - 1) * _itemsPerPage)
                .Take(_itemsPerPage)
                .ToList();

            lstResults.ItemsSource = pageData;

            // Расчет общего количества страниц
            int totalPages = (int)Math.Ceiling((double)_allResults.Count / _itemsPerPage);
            txtStatus.Text = $"Найдено: {_allResults.Count} (Страница {_currentPage} из {totalPages})";

            UpdatePaginationButtons(totalPages);
        }

        // === ИЗМЕНЕНИЕ: Динамическое создание кнопок [1] [2] [3] ===
        private void UpdatePaginationButtons(int totalPages)
        {
            if (pnlPagination == null) return;
            pnlPagination.Children.Clear();

            // Если результатов меньше чем на одну страницу, кнопки не нужны
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
                    // Выделяем текущую страницу цветом
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

        private void LstResults_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lstResults.SelectedItem is TorrentResult result)
            {
                var confirm = MessageBox.Show(
                    $"Добавить игру «{result.Name}» в библиотеку Playnite?",
                    "Подтверждение добавления",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                // Предлагаем очищенное название для удобства
                string suggestedName = CleanGameName(result.Name);

                // === РУЧНОЕ РЕДАКТИРОВАНИЕ ===
                var dialogResult = PlayniteApi.Dialogs.SelectString(
                    "Отредактируйте название игры для правильной загрузки метаданных.\n" +
                    "Уберите repack, версии, DLC, год и т.д. (оставьте только чистое название):",
                    "Редактирование названия игры",
                    suggestedName);

                if (!dialogResult.Result) return; // пользователь нажал Отмена

                string finalName = dialogResult.SelectedString?.Trim();

                if (string.IsNullOrEmpty(finalName))
                {
                    MessageBox.Show("Название не может быть пустым.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                        // Заполняем заметки (как у тебя и было)
                        importedGame.Notes = $"Источник: {result.Source}\n" +
                                             $"Оригинальное название: {result.Name}\n" +
                                             $"Magnet: {result.Magnet}";

                        // Добавляем тег
                        var tag = PlayniteApi.Database.Tags.Add("Hydra Torrent");
                        if (importedGame.TagIds == null) importedGame.TagIds = new List<Guid>();
                        importedGame.TagIds.Add(tag.Id);

                        // !!! ВАЖНО: Сохраняем данные торрента в файл !!!
                        // Убедись, что у тебя в SearchWindow есть ссылка на экземпляр плагина 'plugin'
                        // Если нет, её нужно передать в конструктор окна.
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

        // Метод очистки имени (обязательно добавь его ниже в класс)
        private string CleanGameName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;

            string name = rawName.Trim();

            // Убираем всё в [] и ()
            name = Regex.Replace(name, @"\[.*?\]", "");
            name = Regex.Replace(name, @"\(.*?\)", "");

            // Убираем версии, краки, репакеры и мусор
            name = Regex.Replace(name, @"v\.?\d+(\.\d+)*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"(repack|crack|fixed|update|dlc|multi|ultimate|deluxe|edition|goty|complete|reloaded|codex|empress|flt|skidrow|fitgirl|xatab|by xatab|rg mechanics|decepticon)", "", RegexOptions.IgnoreCase);

            name = Regex.Replace(name, @"\s+", " ").Trim();
            return name.Trim('-', '.', ' ');
        }
    }
}