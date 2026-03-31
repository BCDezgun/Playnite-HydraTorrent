using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace HydraTorrent.Services
{
    public class CloudflareBypassService : IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static CloudflareBypassService _instance;
        private static readonly object _lock = new object();

        private Window _browserWindow;
        private WebView2 _webView;
        private CoreWebView2Environment _environment;
        private string _userDataFolder;
        private bool _isDisposed = false;

        private readonly SemaphoreSlim _requestSemaphore = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<string> _currentTcs;
        private bool _challengeDetected = false;
        private IntPtr _playniteHwnd = IntPtr.Zero;
        private bool _webViewInitialized = false;

        // Win32 API для управления окнами
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        // Константы для SetWindowPos
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private static readonly IntPtr HWND_TOP = new IntPtr(0);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        // Константы для ShowWindow
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_SHOW = 5;
        private const int SW_SHOWNA = 8;
        private const int SW_SHOWMINNOACTIVE = 7;

        public static CloudflareBypassService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new CloudflareBypassService();
                        }
                    }
                }
                return _instance;
            }
        }

        private CloudflareBypassService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _userDataFolder = Path.Combine(appData, "Playnite", "HydraTorrent", "WebView2Data");
            Directory.CreateDirectory(_userDataFolder);
            Logger.Info($"CloudflareBypassService created, userDataFolder: {_userDataFolder}");
        }

        public async Task<bool> InitializeAsync()
        {
            if (_isDisposed) return false;

            try
            {
                if (_environment == null)
                {
                    _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
                    Logger.Info("CoreWebView2Environment pre-created");
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to pre-create environment");
                return false;
            }
        }

        public async Task<T> FetchJsonAsync<T>(string url) where T : class
        {
            await _requestSemaphore.WaitAsync();
            try
            {
                return await FetchInternalAsync<T>(url);
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        private async Task<T> FetchInternalAsync<T>(string url) where T : class
        {
            if (_isDisposed) return null;

            _currentTcs = new TaskCompletionSource<string>();
            _challengeDetected = false;

            try
            {
                // Получаем HWND главного окна Playnite
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var mainWindow = Application.Current.MainWindow;
                    if (mainWindow != null)
                    {
                        var helper = new WindowInteropHelper(mainWindow);
                        _playniteHwnd = helper.Handle;
                        Logger.Info($"Playnite HWND: {_playniteHwnd}");
                    }
                });

                // Создаём окно если нужно
                if (_browserWindow == null)
                {
                    Logger.Info("Creating WebView2 window...");

                    // Создаём environment ДО создания окна
                    if (_environment == null)
                    {
                        _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _browserWindow = new Window
                        {
                            Title = "Cloudflare Challenge - HydraTorrent",
                            Width = 1000,
                            Height = 800,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen,
                            WindowStyle = WindowStyle.SingleBorderWindow,
                            ResizeMode = ResizeMode.CanResize,
                            Topmost = false,
                            ShowActivated = false,
                            Visibility = Visibility.Hidden  // ВАЖНО: создаём скрытым!
                        };

                        _webView = new WebView2();
                        _browserWindow.Content = _webView;

                        _browserWindow.Closed += (s, e) =>
                        {
                            Logger.Info("Browser window closed by user");
                            _browserWindow = null;
                            _webView = null;
                            _webViewInitialized = false;
                            _currentTcs?.TrySetResult(null);
                        };

                        // Подписываемся на событие SourceInitialized - оно вызывается когда HWND готов
                        _browserWindow.SourceInitialized += (s, e) =>
                        {
                            var helper = new WindowInteropHelper(_browserWindow);
                            var hwnd = helper.Handle;
                            Logger.Info($"SourceInitialized, HWND: {hwnd}");

                            // Помещаем окно позади всех окон ДО показа
                            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                        };

                        // Показываем окно через Win32 API чтобы оно не активировалось
                        _browserWindow.Show();

                        // После Show() окно видимо, но мы уже установили его позицию
                        var browserHelper = new WindowInteropHelper(_browserWindow);
                        browserHelper.EnsureHandle();
                        var browserHwnd = browserHelper.Handle;

                        // Показываем окно БЕЗ активации
                        ShowWindow(browserHwnd, SW_SHOWNOACTIVATE);

                        // Убеждаемся что оно позади
                        SetWindowPos(browserHwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

                        // Активируем Playnite
                        if (_playniteHwnd != IntPtr.Zero)
                        {
                            SetForegroundWindow(_playniteHwnd);
                        }

                        Logger.Info($"Browser window created behind all windows, HWND: {browserHwnd}");
                    });

                    // Инициализируем WebView2
                    await _webView.EnsureCoreWebView2Async(_environment);

                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        _webView.CoreWebView2.Settings.IsScriptEnabled = true;
                        _webViewInitialized = true;
                        Logger.Info("CoreWebView2 initialized");
                    }
                    else
                    {
                        Logger.Error("CoreWebView2 is null");
                        return null;
                    }
                }
                else
                {
                    // Окно уже есть - показываем его ПОЗАДИ Playnite без мерцания
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_browserWindow != null && !_browserWindow.IsVisible)
                        {
                            var browserHelper = new WindowInteropHelper(_browserWindow);
                            var browserHwnd = browserHelper.Handle;

                            // Сначала устанавливаем позицию ПОЗАДИ
                            SetWindowPos(browserHwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

                            // Потом показываем без активации
                            ShowWindow(browserHwnd, SW_SHOWNOACTIVATE);

                            // Активируем Playnite
                            if (_playniteHwnd != IntPtr.Zero)
                            {
                                SetForegroundWindow(_playniteHwnd);
                            }
                        }
                    });
                }

                // Подписываемся на события
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                Logger.Info($"Navigating to {url}");
                _webView.CoreWebView2.Navigate(url);

                // Таймаут
                var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                cts.Token.Register(() =>
                {
                    Logger.Warn("Request timeout");
                    _currentTcs?.TrySetResult(null);
                });

                var result = await _currentTcs.Task;

                // Отписываемся
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;

                if (!string.IsNullOrEmpty(result))
                {
                    // Скрываем окно после успеха
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_browserWindow != null)
                        {
                            var helper = new WindowInteropHelper(_browserWindow);
                            var hwnd = helper.Handle;
                            ShowWindow(hwnd, SW_HIDE);
                            _browserWindow.Visibility = Visibility.Hidden;
                        }
                    });

                    try
                    {
                        return JsonConvert.DeserializeObject<T>(result);
                    }
                    catch (Newtonsoft.Json.JsonReaderException ex)
                    {
                        Logger.Error(ex, "JSON parse error");
                        return null;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FetchInternalAsync error");
                return null;
            }
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (_webView?.CoreWebView2 == null || _currentTcs == null)
                {
                    _currentTcs?.TrySetResult(null);
                    return;
                }

                var title = await _webView.CoreWebView2.ExecuteScriptAsync("document.title");
                title = title?.Trim('"');
                Logger.Info($"Page title: {title}");

                if (title != null && (title.Contains("Just a moment") ||
                    title.Contains("Checking your browser") ||
                    title.Contains("Security check") ||
                    title.Contains("Один момент")))
                {
                    Logger.Info("Challenge detected! Bringing window to front...");
                    _challengeDetected = true;

                    // Показываем окно НА ПЕРЕДНЕМ плане для прохождения challenge
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_browserWindow == null) return;

                        var browserHelper = new WindowInteropHelper(_browserWindow);
                        var browserHwnd = browserHelper.Handle;

                        // Устанавливаем окно поверх всех
                        SetWindowPos(browserHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW);

                        // Активируем окно
                        SetForegroundWindow(browserHwnd);
                        BringWindowToTop(browserHwnd);
                        _browserWindow.Focus();

                        Logger.Info($"Browser window brought to front, HWND: {browserHwnd}");
                    });

                    // Скрипт ожидания прохождения challenge
                    await _webView.CoreWebView2.ExecuteScriptAsync(@"
                        (function() {
                            var checkInterval = setInterval(function() {
                                var title = document.title || '';
                                if (!title.includes('Just a moment') &&
                                    !title.includes('Checking your browser') &&
                                    !title.includes('Security check') &&
                                    !title.includes('Один момент') &&
                                    !title.includes('Проверка')) {
                                    
                                    clearInterval(checkInterval);
                                    setTimeout(function() {
                                        var pre = document.querySelector('pre');
                                        if (pre) {
                                            window.chrome.webview.postMessage(pre.textContent);
                                        } else {
                                            var body = document.body.textContent || '';
                                            window.chrome.webview.postMessage(body);
                                        }
                                    }, 1000);
                                }
                            }, 500);
                        })();
                    ");
                    return;
                }

                Logger.Info("No challenge, extracting content...");
                await ExtractContentAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "OnNavigationCompleted error");
                _currentTcs?.TrySetResult(null);
            }
        }

        private async Task ExtractContentAsync()
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                (function() {
                    var pre = document.querySelector('pre');
                    if (pre) {
                        window.chrome.webview.postMessage(pre.textContent);
                    } else {
                        window.chrome.webview.postMessage(document.body.textContent || '');
                    }
                })();
            ");
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var content = e.WebMessageAsJson;
                content = content.Trim('"');
                content = Regex.Unescape(content);

                Logger.Info($"WebMessage length: {content?.Length ?? 0}");

                // После успешного получения данных - возвращаем окно "под" Playnite
                if (!string.IsNullOrEmpty(content) && _challengeDetected)
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (_browserWindow == null) return;

                        var browserHelper = new WindowInteropHelper(_browserWindow);
                        var browserHwnd = browserHelper.Handle;

                        // Сначала убираем topmost
                        SetWindowPos(browserHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

                        // Потом помещаем позади всех
                        SetWindowPos(browserHwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

                        // Возвращаем фокус Playnite
                        if (_playniteHwnd != IntPtr.Zero)
                        {
                            SetForegroundWindow(_playniteHwnd);
                        }

                        Logger.Info("Browser window moved to background");
                    }));
                }

                if (!string.IsNullOrEmpty(content))
                {
                    var trimmed = content.TrimStart();
                    if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                    {
                        Logger.Info("Valid JSON received!");
                        _currentTcs?.TrySetResult(content);
                        return;
                    }
                }
                _currentTcs?.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "OnWebMessageReceived error");
                _currentTcs?.TrySetResult(null);
            }
        }

        public static bool IsWebView2Available()
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrEmpty(version);
            }
            catch
            {
                return false;
            }
        }

        public static string GetWebView2Version()
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_browserWindow != null)
                        {
                            var helper = new WindowInteropHelper(_browserWindow);
                            var hwnd = helper.Handle;
                            if (hwnd != IntPtr.Zero)
                            {
                                ShowWindow(hwnd, SW_HIDE);
                            }
                            _browserWindow.Close();
                        }
                        _browserWindow = null;
                        _webView = null;
                        _webViewInitialized = false;
                    }
                    catch { }
                }));
                _instance = null;
            }
            catch { }
        }
    }
}