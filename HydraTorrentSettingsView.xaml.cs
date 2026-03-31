using HydraTorrent.Models;
using HydraTorrent.Services;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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
            CheckWebView2Status();
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
                        // Сначала пробуем загрузить напрямую
                        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                        {
                            try
                            {
                                var json = await client.GetStringAsync(url);
                                var data = JObject.Parse(json);
                                string name = data["name"]?.ToString() ?? "OK";
                                _nameBlock.Text = name;
                                _entry.Name = name;
                                return;
                            }
                            catch (HttpRequestException ex) when (ex.Message.Contains("403"))
                            {
                                // Cloudflare блокирует - пробуем WebView2
                            }
                            catch (TaskCanceledException)
                            {
                                // Таймаут - пробуем WebView2
                            }
                        }

                        // Fallback на WebView2
                        if (CloudflareBypassService.IsWebView2Available())
                        {
                            _nameBlock.Text = "⏳🔄";

                            try
                            {
                                var cfService = CloudflareBypassService.Instance;
                                var result = await cfService.FetchJsonAsync<FitGirlRoot>(url);

                                if (result != null)
                                {
                                    string name = result.Name ?? "OK";
                                    _nameBlock.Text = name;
                                    _entry.Name = name;
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Settings] WebView2 error: {ex.Message}");
                            }
                        }

                        _nameBlock.Text = "⚠️";
                        _entry.Name = "";
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Settings] Error loading source: {ex.Message}");
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
        private void CheckWebView2Status()
        {
            WebView2StatusPanel.Children.Clear();

            var version = CloudflareBypassService.GetWebView2Version();

            if (version != null)
            {
                var statusText = new TextBlock
                {
                    Text = $"✅ WebView2 Runtime: v{version}",
                    Foreground = System.Windows.Media.Brushes.SpringGreen,
                    Margin = new Thickness(0, 0, 0, 5)
                };
                WebView2StatusPanel.Children.Add(statusText);

                var descText = new TextBlock
                {
                    Text = ResourceProvider.GetString("LOC_HydraTorrent_WebView2Available"),
                    FontSize = 11,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                    TextWrapping = TextWrapping.Wrap
                };
                WebView2StatusPanel.Children.Add(descText);
            }
            else
            {
                var statusText = new TextBlock
                {
                    Text = "⚠️ WebView2 Runtime " + ResourceProvider.GetString("LOC_HydraTorrent_WebView2NotFound"),
                    Foreground = System.Windows.Media.Brushes.Orange,
                    Margin = new Thickness(0, 0, 0, 5)
                };
                WebView2StatusPanel.Children.Add(statusText);

                var descText = new TextBlock
                {
                    Text = ResourceProvider.GetString("LOC_HydraTorrent_WebView2RequiredDesc"),
                    FontSize = 11,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                WebView2StatusPanel.Children.Add(descText);

                var downloadBtn = new Button
                {
                    Content = ResourceProvider.GetString("LOC_HydraTorrent_WebView2Download"),
                    Padding = new Thickness(15, 5, 15, 5),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                downloadBtn.Click += (s, e) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                        UseShellExecute = true
                    });
                };
                WebView2StatusPanel.Children.Add(downloadBtn);
            }
        }
    }
}