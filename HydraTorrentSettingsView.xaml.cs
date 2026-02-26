using Playnite.SDK;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QBittorrent.Client;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace HydraTorrent
{
    public partial class HydraTorrentSettingsView : UserControl
    {
        private readonly HydraTorrentSettingsViewModel viewModel;
        private readonly List<SourceRow> _sourceRows = new List<SourceRow>();

        public HydraTorrentSettingsView(HydraTorrentSettingsViewModel vm)
        {
            viewModel = vm;
            InitializeComponent();
            DataContext = vm;

            // Связываем View и ViewModel для корректного сохранения
            viewModel.SettingsView = this;

            txtPassword.Password = viewModel.Settings.QBittorrentPassword ?? "";
            LoadSources();
        }

        // ────────────────────────────────────────────────────────────────
        // События элементов управления
        // ────────────────────────────────────────────────────────────────

        private void txtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (viewModel.Settings != null)
            {
                viewModel.Settings.QBittorrentPassword = txtPassword.Password;
            }
        }

        private void BrowseDefaultPath_Click(object sender, RoutedEventArgs e)
        {
            var path = API.Instance.Dialogs.SelectFolder();
            if (!string.IsNullOrEmpty(path))
            {
                viewModel.Settings.DefaultDownloadPath = path;
                DefaultPathText.Text = path;
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            var settings = viewModel.Settings;

            // Проверка активности qBittorrent
            if (!settings.UseQbittorrent)
            {
                API.Instance.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOC_HydraTorrent_QBittorrentDisabled"),
                    ResourceProvider.GetString("LOC_HydraTorrent_ConnectionTest"));
                return;
            }

            string password = txtPassword.Password ?? "";
            var url = new Uri($"http://{settings.QBittorrentHost}:{settings.QBittorrentPort}");
            var client = new QBittorrentClient(url);

            try
            {
                await client.LoginAsync(settings.QBittorrentUsername, password);
                var version = await client.GetApiVersionAsync();

                // Успешное сообщение
                API.Instance.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ConnectionSuccess"), version),
                    ResourceProvider.GetString("LOC_HydraTorrent_Success"));
            }
            catch (Exception ex)
            {
                // Ошибка
                API.Instance.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ConnectionError"), ex.Message),
                    ResourceProvider.GetString("LOC_HydraTorrent_ConnectionErrorTitle"));
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Управление источниками
        // ────────────────────────────────────────────────────────────────

        private void LoadSources()
        {
            SourcesPanel.Children.Clear();
            _sourceRows.Clear();

            foreach (var source in viewModel.Settings.Sources)
            {
                AddSourceRow(source);
            }

            if (_sourceRows.Count == 0)
            {
                AddSourceRow(new SourceEntry());
            }
        }

        private void AddSource_Click(object sender, RoutedEventArgs e)
        {
            AddSourceRow(new SourceEntry());
        }

        private void AddSourceRow(SourceEntry entry)
        {
            var row = new SourceRow(entry, RemoveRow);
            SourcesPanel.Children.Add(row);
            _sourceRows.Add(row);
        }

        private void RemoveRow(SourceRow row)
        {
            SourcesPanel.Children.Remove(row);
            _sourceRows.Remove(row);
        }

        public void SaveSources()
        {
            viewModel.Settings.Sources.Clear();

            foreach (var row in _sourceRows)
            {
                var entry = row.GetEntry();
                if (!string.IsNullOrWhiteSpace(entry.Url))
                {
                    viewModel.Settings.Sources.Add(entry);
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Вспомогательный класс строки источника
        // ────────────────────────────────────────────────────────────────

        private class SourceRow : Grid
        {
            private readonly TextBox _urlBox;
            private readonly TextBlock _nameBlock;
            private readonly Button _removeBtn;
            private readonly SourceEntry _entry;
            private readonly DispatcherTimer _typingTimer;

            public SourceRow(SourceEntry entry, Action<SourceRow> onRemove)
            {
                _entry = entry;
                Margin = new Thickness(0, 5, 0, 5);

                // Таймер задержки для авто-загрузки имени (700 мс)
                _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _typingTimer.Tick += async (s, e) =>
                {
                    _typingTimer.Stop();
                    await LoadNameAsync();
                };

                // Колонки: URL (растягивается), Имя (авто), Кнопка удаления
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                _urlBox = new TextBox
                {
                    Text = entry.Url,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = "Введите URL JSON-источника..."
                };

                _urlBox.TextChanged += (s, e) =>
                {
                    _typingTimer.Stop();
                    _typingTimer.Start();
                };

                Grid.SetColumn(_urlBox, 0);

                _nameBlock = new TextBlock
                {
                    Text = entry.Name ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = System.Windows.Media.Brushes.SpringGreen,
                    Margin = new Thickness(5, 0, 10, 0),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                Grid.SetColumn(_nameBlock, 1);

                _removeBtn = new Button
                {
                    Content = "✕",
                    Width = 30,
                    Height = 25,
                    Foreground = System.Windows.Media.Brushes.Red,
                    ToolTip = "Удалить источник"
                };

                _removeBtn.Click += (s, e) => onRemove(this);
                Grid.SetColumn(_removeBtn, 2);

                Children.Add(_urlBox);
                Children.Add(_nameBlock);
                Children.Add(_removeBtn);

                // Если URL уже есть — пробуем подтянуть имя сразу
                if (!string.IsNullOrEmpty(entry.Url))
                {
                    Task.Run(async () => await LoadNameAsync());
                }
            }

            private async Task LoadNameAsync()
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var url = _urlBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http"))
                    {
                        _nameBlock.Text = "";
                        return;
                    }

                    _nameBlock.Text = "⏳";

                    try
                    {
                        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                        {
                            var json = await client.GetStringAsync(url);
                            var data = JObject.Parse(json);
                            string name = data["name"]?.ToString() ?? "OK";
                            _nameBlock.Text = name;
                            _entry.Name = name;
                        }
                    }
                    catch
                    {
                        _nameBlock.Text = "⚠️";
                        _entry.Name = "";
                    }
                });
            }

            public SourceEntry GetEntry()
            {
                _entry.Url = _urlBox.Text.Trim();
                return _entry;
            }
        }
    }
}