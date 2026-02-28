using HydraTorrent.Models;
using HydraTorrent.Views;
using Playnite.SDK;
using Playnite.SDK.Models;
using QBittorrent.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace HydraTorrent.Services
{
    /// <summary>
    /// Сервис для анализа загруженных игр и настройки запуска
    /// </summary>
    public class GameSetupService
    {
        private readonly HydraTorrent _plugin;
        private readonly IPlayniteAPI _api;

        // ────────────────────────────────────────────────────────────────
        // Константы порогов уверенности
        // ────────────────────────────────────────────────────────────────

        /// <summary>Порог для автоматической настройки без диалога</summary>
        private const int CONFIDENCE_AUTO_CONFIGURE = 70;

        /// <summary>Минимальный порог для показа в списке кандидатов</summary>
        private const int CONFIDENCE_SHOW_IN_LIST = 10;

        // ────────────────────────────────────────────────────────────────
        // Константы очков скоринга
        // ────────────────────────────────────────────────────────────────

        // Схожесть имени
        private const int SCORE_EXACT_NAME_MATCH = 40;
        private const int SCORE_PARTIAL_NAME_MATCH = 25;
        private const int SCORE_CONTAINS_GAME_NAME = 20;

        // Размер файла
        private const int SCORE_SIZE_LARGE = 30;      // > 20 MB
        private const int SCORE_SIZE_MEDIUM = 10;     // 5-20 MB
        private const int SCORE_SIZE_SMALL = -20;     // < 5 MB (штраф)

        // Version Info
        private const int SCORE_PRODUCT_NAME_MATCH = 40;
        private const int SCORE_COMPANY_GAME_PUBLISHER = 20;
        private const int SCORE_COMPANY_INSTALLER = -40;
        private const int SCORE_DESCRIPTION_SETUP = -30;

        // Зависимости (DLL)
        private const int SCORE_STEAM_API_DLL = 25;
        private const int SCORE_UNITY_DLL = 25;
        private const int SCORE_GAME_ENGINE_DLL = 15;

        // Структура папок
        private const int SCORE_GAME_FOLDERS_PRESENT = 20;
        private const int SCORE_INSTALLER_FILES_PRESENT = -15;

        // Launcher special
        private const int SCORE_LAUNCHER_WITH_GAME_INDICATORS = 30;

        // ────────────────────────────────────────────────────────────────
        // Списки для фильтрации и анализа
        // ────────────────────────────────────────────────────────────────

        private static readonly string[] ExcludedExePatterns = new[]
        {
            "uninstall", "uninst", "unins", "remove",
            "setup", "install", "installer",
            "config", "settings", "options", "configure",
            "redist", "directx", "vcredist", "vc_redist",
            "dxsetup", "dxwebsetup", "dotnet", "netfx",
            "crashreporter", "errorreport", "bugreport",
            "updater", "update", "patch"
        };

        private static readonly string[] InstallerCompanyPatterns = new[]
        {
            "inno setup", "nullsoft", "installshield",
            "nsis", "wise", "setup factory"
        };

        private static readonly string[] GamePublisherPatterns = new[]
        {
            "rockstar", "ubisoft", "ea", "electronic arts",
            "activision", "bethesda", "capcom", "sega",
            "square enix", "bandai", "namco", "sony",
            "microsoft", "cd projekt", "2k", "devolver"
        };

        private static readonly string[] GameFolderIndicators = new[]
        {
            "textures", "texture", "audio", "sound",
            "models", "model", "assets", "data",
            "scripts", "shaders", "videos", "movies",
            "levels", "maps", "characters", "anim"
        };

        private static readonly string[] GameFileExtensions = new[]
        {
            ".pak", ".rpf", ".assets", ".bundle",
            ".dat", ".arc", ".cpk", ".big",
            ".vpp_pc", ".sqfs", ".ff"
        };

        private static readonly string[] GameDllPatterns = new[]
        {
            "steam_api", "steam_api64",
            "unityplayer", "unity",
            "d3d11", "dxgi", "d3d12",
            "game", "engine"
        };

        public GameSetupService(HydraTorrent plugin)
        {
            _plugin = plugin;
            _api = plugin.PlayniteApi;
        }

        // ────────────────────────────────────────────────────────────────
        // PUBLIC: Точка входа после завершения загрузки
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Обрабатывает загруженную игру: определяет тип и настраивает запуск
        /// </summary>
        /// <param name="gameId">ID игры в Playnite</param>
        /// <param name="downloadPath">Базовый путь загрузки</param>
        /// <param name="torrentHash">Хэш торрента (для получения реального пути)</param>
        public async Task ProcessDownloadedGameAsync(Guid gameId, string downloadPath, string torrentHash = null)
        {
            if (string.IsNullOrEmpty(downloadPath) || !Directory.Exists(downloadPath))
            {
                HydraTorrent.logger.Warn($"Путь загрузки не существует: {downloadPath}");
                return;
            }

            var game = _api.Database.Games.Get(gameId);
            if (game == null)
            {
                HydraTorrent.logger.Warn($"Игра не найдена: {gameId}");
                return;
            }

            HydraTorrent.logger.Info($"Обработка загруженной игры: {game.Name}");

            var torrentData = _plugin.GetHydraData(game);
            if (torrentData == null)
            {
                HydraTorrent.logger.Warn($"Данные торрента не найдены для: {game.Name}");
                return;
            }

            // Получаем реальный путь загрузки из qBittorrent
            string actualPath = await GetActualDownloadPathAsync(downloadPath, torrentHash ?? torrentData.TorrentHash);

            if (string.IsNullOrEmpty(actualPath) || !Directory.Exists(actualPath))
            {
                HydraTorrent.logger.Warn($"Реальный путь не найден, используем базовый: {downloadPath}");
                actualPath = downloadPath;
            }
            else
            {
                // Обновляем путь в данных торрента
                torrentData.DownloadPath = actualPath;
                HydraTorrent.logger.Info($"Реальный путь загрузки: {actualPath}");
            }

            // Определяем тип игры
            var gameType = DetectGameType(actualPath);
            torrentData.DetectedType = gameType;

            HydraTorrent.logger.Info($"Тип определён: {gameType} для {game.Name}");

            // Обрабатываем в зависимости от типа
            switch (gameType)
            {
                case GameType.Portable:
                    await ProcessPortableGameAsync(game, torrentData, actualPath);
                    break;

                case GameType.Repack:
                    await ProcessRepackGameAsync(game, torrentData, actualPath);
                    break;

                default:
                    HydraTorrent.logger.Warn($"Не удалось определить тип для: {game.Name}");
                    ShowManualSetupNotification(game, actualPath);
                    break;
            }

            // Сохраняем данные игры
            _plugin.SaveHydraData(game, torrentData);

            // Обновляем queue.json
            UpdateQueueItem(torrentData);
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Получение реального пути загрузки
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Получает реальный путь к папке с игрой из содержимого торрента
        /// </summary>
        private async Task<string> GetActualDownloadPathAsync(string baseDownloadPath, string torrentHash)
        {
            try
            {
                if (string.IsNullOrEmpty(torrentHash))
                    return baseDownloadPath;

                var client = CreateQBittorrentClient();
                if (client == null)
                    return baseDownloadPath;

                var contents = await client.GetTorrentContentsAsync(torrentHash);
                if (contents == null || !contents.Any())
                    return baseDownloadPath;

                // Находим корневую папку торрента
                var firstFile = contents.FirstOrDefault();
                if (firstFile != null)
                {
                    var filePath = firstFile.Name;
                    var firstSeparatorIndex = filePath.IndexOfAny(new[] { '/', '\\' });

                    if (firstSeparatorIndex > 0)
                    {
                        // Есть подпапка - это корневая папка торрента
                        var rootFolder = filePath.Substring(0, firstSeparatorIndex);
                        var actualPath = Path.Combine(baseDownloadPath, rootFolder);

                        if (Directory.Exists(actualPath))
                        {
                            HydraTorrent.logger.Info($"Найдена корневая папка торрента: {actualPath}");
                            return actualPath;
                        }
                    }
                }

                return baseDownloadPath;
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, $"Ошибка получения реального пути: {ex.Message}");
                return baseDownloadPath;
            }
        }

        /// <summary>
        /// Создаёт клиент qBittorrent
        /// </summary>
        private QBittorrentClient CreateQBittorrentClient()
        {
            try
            {
                var settings = _plugin.GetSettings().Settings;
                if (!settings.UseQbittorrent)
                    return null;

                var uri = new Uri($"http://{settings.QBittorrentHost}:{settings.QBittorrentPort}");
                var client = new QBittorrentClient(uri);

                if (!string.IsNullOrEmpty(settings.QBittorrentUsername))
                {
                    client.LoginAsync(settings.QBittorrentUsername, settings.QBittorrentPassword ?? "").Wait();
                }

                return client;
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Ошибка создания qBittorrent клиента");
                return null;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // PUBLIC: Анализ портативной игры
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Анализирует портативную игру и находит исполняемый файл
        /// </summary>
        public async Task ProcessPortableGameAsync(Game game, TorrentResult torrentData, string downloadPath)
        {
            var candidates = await AnalyzePortableGameAsync(downloadPath, game.Name);

            if (candidates.Count == 0)
            {
                HydraTorrent.logger.Warn($"Не найдены .exe файлы для: {game.Name}");
                ShowNoExecutableNotification(game, downloadPath);
                return;
            }

            // Сортируем по очкам
            candidates = candidates.OrderByDescending(c => c.ConfidenceScore).ToList();

            var bestCandidate = candidates.First();

            HydraTorrent.logger.Info($"Лучший кандидат: {bestCandidate.FileName} ({bestCandidate.ConfidenceScore}%)");

            // Проверяем порог уверенности
            if (bestCandidate.ConfidenceScore >= CONFIDENCE_AUTO_CONFIGURE)
            {
                // Автоматическая настройка
                ConfigureGameAction(game, bestCandidate);
                torrentData.ExecutablePath = bestCandidate.FilePath;
                torrentData.IsConfigured = true;

                _api.Notifications.Add(new NotificationMessage(
                    "HydraTorrent",
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_AutoConfigured"), game.Name, bestCandidate.FileName),
                    NotificationType.Info));

                HydraTorrent.logger.Info($"Автонастройка: {game.Name} → {bestCandidate.FileName}");
            }
            else
            {
                // Показываем диалог выбора
                var topCandidates = candidates.Take(5).ToList();
                var selected = await ShowExecutableSelectionDialogAsync(game.Name, topCandidates);

                if (selected != null)
                {
                    ConfigureGameAction(game, selected);
                    torrentData.ExecutablePath = selected.FilePath;
                    torrentData.IsConfigured = true;

                    _api.Notifications.Add(new NotificationMessage(
                        "HydraTorrent",
                        string.Format(ResourceProvider.GetString("LOC_HydraTorrent_SetupComplete"), game.Name),
                        NotificationType.Info));
                }
                else
                {
                    HydraTorrent.logger.Info($"Пользователь отменил выбор для: {game.Name}");
                }
            }
        }

        /// <summary>
        /// Анализирует репак и настраивает кнопку установки
        /// </summary>
        public async Task ProcessRepackGameAsync(Game game, TorrentResult torrentData, string downloadPath)
        {
            // Ищем setup.exe
            var setupExe = FindSetupExecutable(downloadPath);

            if (!string.IsNullOrEmpty(setupExe))
            {
                // Настраиваем действие "Install"
                ConfigureInstallAction(game, setupExe);
                torrentData.ExecutablePath = setupExe;
                torrentData.IsConfigured = true;

                _api.Notifications.Add(new NotificationMessage(
                    "HydraTorrent",
                    string.Format(ResourceProvider.GetString("LOC_HydraTorrent_RepackReady"), game.Name),
                    NotificationType.Info));

                HydraTorrent.logger.Info($"Репак настроен: {game.Name} → {setupExe}");
            }
            else
            {
                HydraTorrent.logger.Warn($"setup.exe не найден для: {game.Name}");
                ShowSetupNotFoundNotification(game, downloadPath);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Анализирует папку и возвращает список кандидатов с очками
        /// </summary>
        public async Task<List<ExecutableCandidate>> AnalyzePortableGameAsync(string downloadPath, string gameName)
        {
            return await Task.Run(() =>
            {
                var executables = GetAllExecutables(downloadPath);
                var candidates = new List<ExecutableCandidate>();

                foreach (var exe in executables)
                {
                    var candidate = CalculateConfidenceScore(exe, gameName, downloadPath);
                    if (candidate.ConfidenceScore >= CONFIDENCE_SHOW_IN_LIST)
                    {
                        candidates.Add(candidate);
                    }
                }

                return candidates;
            });
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Определение типа игры
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Определяет тип загруженной игры (репак или портативная)
        /// </summary>
        private GameType DetectGameType(string downloadPath)
        {
            try
            {
                var allFiles = Directory.GetFiles(downloadPath, "*.*", SearchOption.TopDirectoryOnly);

                // Ищем признаки репака
                bool hasSetup = allFiles.Any(f =>
                    Path.GetFileName(f).Equals("setup.exe", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(f).Equals("install.exe", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(Path.GetFileName(f), @"^setup.*\.exe$", RegexOptions.IgnoreCase));

                bool hasBinFiles = allFiles.Any(f =>
                    Path.GetExtension(f).Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(f).Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(f).Equals(".r00", StringComparison.OrdinalIgnoreCase));

                // Ищем признаки портативной версии
                bool hasGameFolders = Directory.GetDirectories(downloadPath)
                    .Any(d => GameFolderIndicators.Any(ind =>
                        ContainsIgnoreCase(Path.GetFileName(d), ind)));

                bool hasGameFiles = allFiles.Any(f =>
                    GameFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

                // Логика определения
                if (hasSetup && hasBinFiles)
                {
                    // Явный репак: setup.exe + .bin файлы
                    return GameType.Repack;
                }

                if (hasGameFolders || hasGameFiles)
                {
                    // Признаки установленной игры
                    return GameType.Portable;
                }

                // Проверяем наличие .exe файлов
                var exeFiles = Directory.GetFiles(downloadPath, "*.exe", SearchOption.AllDirectories);
                if (exeFiles.Length > 0)
                {
                    // Есть .exe файлы — скорее всего портативная
                    // Но проверим, нет ли setup.exe в корне
                    if (hasSetup)
                    {
                        // Есть setup.exe, но нет .bin — может быть простой установщик
                        return GameType.Repack;
                    }

                    return GameType.Portable;
                }

                return GameType.Unknown;
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, $"Ошибка определения типа: {downloadPath}");
                return GameType.Unknown;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Поиск исполняемых файлов
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Рекурсивно находит все .exe файлы, исключая служебные
        /// </summary>
        private List<FileInfo> GetAllExecutables(string directory)
        {
            var result = new List<FileInfo>();

            try
            {
                var dirInfo = new DirectoryInfo(directory);

                // Ищем все .exe файлы
                var allExes = dirInfo.GetFiles("*.exe", SearchOption.AllDirectories);

                foreach (var exe in allExes)
                {
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(exe.Name);

                    // Проверяем, не исключён ли файл
                    bool isExcluded = ExcludedExePatterns.Any(pattern =>
                        ContainsIgnoreCase(nameWithoutExt, pattern));

                    if (!isExcluded)
                    {
                        result.Add(exe);
                    }
                    else
                    {
                        // Исключённые файлы тоже добавляем, но с низким приоритетом
                        // (на случай, если пользователь выберет вручную)
                        HydraTorrent.logger.Debug($"Исключённый .exe: {exe.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, $"Ошибка поиска .exe в: {directory}");
            }

            return result;
        }

        /// <summary>
        /// Находит setup.exe в папке
        /// </summary>
        private string FindSetupExecutable(string downloadPath)
        {
            try
            {
                // Приоритет: корневая папка
                var rootSetup = Directory.GetFiles(downloadPath, "setup.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(rootSetup))
                    return rootSetup;

                // Ищем setup*.exe в корне
                var setupVariants = Directory.GetFiles(downloadPath, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^setup.*\.exe$", RegexOptions.IgnoreCase))
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(setupVariants))
                    return setupVariants;

                // Ищем в подпапках (не глубоко)
                return Directory.GetFiles(downloadPath, "setup.exe", SearchOption.AllDirectories)
                    .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, $"Ошибка поиска setup.exe: {downloadPath}");
                return null;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Алгоритм скоринга
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Вычисляет очки уверенности для кандидата
        /// </summary>
        private ExecutableCandidate CalculateConfidenceScore(FileInfo file, string gameName, string rootPath)
        {
            var candidate = new ExecutableCandidate
            {
                FilePath = file.FullName,
                FileName = file.Name,
                FileSize = file.Length
            };

            int totalScore = 0;

            // 1. Скоринг по имени файла
            var nameScore = ScoreByNameSimilarity(file.Name, gameName, candidate);
            totalScore += nameScore;

            // 2. Скоринг по размеру файла
            var sizeScore = ScoreByFileSize(file.Length, candidate);
            totalScore += sizeScore;

            // 3. Скоринг по FileVersionInfo
            var versionScore = ScoreByVersionInfo(file, gameName, candidate);
            totalScore += versionScore;

            // 4. Скоринг по зависимостям (DLL в папке)
            var dllScore = ScoreByDependencies(file.DirectoryName, candidate);
            totalScore += dllScore;

            // 5. Скоринг по структуре папок
            var folderScore = ScoreByFolderStructure(file.DirectoryName, rootPath, candidate);
            totalScore += folderScore;

            // 6. Специальная обработка Launcher.exe
            if (IsLauncherExe(file.Name))
            {
                candidate.IsLauncher = true;
                var launcherScore = ScoreLauncherExe(file, gameName, candidate);
                totalScore += launcherScore;
            }

            // Ограничиваем диапазон
            candidate.ConfidenceScore = Math.Max(0, Math.Min(100, totalScore));

            return candidate;
        }

        /// <summary>
        /// Скоринг по схожести имени файла с названием игры
        /// </summary>
        private int ScoreByNameSimilarity(string fileName, string gameName, ExecutableCandidate candidate)
        {
            if (string.IsNullOrEmpty(gameName)) return 0;

            string normalizedFileName = NormalizeName(Path.GetFileNameWithoutExtension(fileName));
            string normalizedGameName = NormalizeName(gameName);

            int score = 0;

            // Точное совпадение
            if (string.Equals(normalizedFileName, normalizedGameName, StringComparison.OrdinalIgnoreCase))
            {
                score = SCORE_EXACT_NAME_MATCH;
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonExactMatch"));
            }
            // Файл содержит название игры
            else if (ContainsIgnoreCase(normalizedFileName, normalizedGameName))
            {
                score = SCORE_CONTAINS_GAME_NAME;
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonContainsName"));
            }
            // Название игры содержится в имени файла
            else if (ContainsIgnoreCase(normalizedGameName, normalizedFileName))
            {
                score = SCORE_PARTIAL_NAME_MATCH;
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonPartialMatch"));
            }
            else
            {
                // Fuzzy matching — расстояние Левенштейна
                int similarity = CalculateStringSimilarity(normalizedFileName, normalizedGameName);
                if (similarity >= 70)
                {
                    score = (int)(SCORE_PARTIAL_NAME_MATCH * (similarity / 100.0));
                    candidate.ScoreReasons.Add(string.Format(
                        ResourceProvider.GetString("LOC_HydraTorrent_ReasonSimilarMatch"), similarity));
                }
            }

            return score;
        }

        /// <summary>
        /// Скоринг по размеру файла
        /// </summary>
        private int ScoreByFileSize(long fileSize, ExecutableCandidate candidate)
        {
            const long MB = 1024 * 1024;

            int score;

            if (fileSize > 200 * MB) // > 200 MB
            {
                score = SCORE_SIZE_LARGE + 10; // Бонус за очень большой размер
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonVeryLargeSize"));
            }
            else if (fileSize > 20 * MB) // > 20 MB
            {
                score = SCORE_SIZE_LARGE;
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonLargeSize"));
            }
            else if (fileSize >= 5 * MB) // 5-20 MB
            {
                score = SCORE_SIZE_MEDIUM;
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonMediumSize"));
            }
            else // < 5 MB
            {
                score = SCORE_SIZE_SMALL;
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonSmallSize"));
            }

            return score;
        }

        /// <summary>
        /// Скоринг по информации о версии файла
        /// </summary>
        private int ScoreByVersionInfo(FileInfo file, string gameName, ExecutableCandidate candidate)
        {
            int score = 0;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(file.FullName);

                candidate.ProductName = info.ProductName;
                candidate.FileDescription = info.FileDescription;
                candidate.CompanyName = info.CompanyName;

                string normalizedGameName = NormalizeName(gameName);

                // ProductName содержит название игры
                if (!string.IsNullOrEmpty(info.ProductName))
                {
                    string normalizedProduct = NormalizeName(info.ProductName);

                    if (ContainsIgnoreCase(normalizedProduct, normalizedGameName) ||
                        ContainsIgnoreCase(normalizedGameName, normalizedProduct))
                    {
                        score += SCORE_PRODUCT_NAME_MATCH;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonProductName"));
                    }
                }

                // CompanyName — известный издатель игры
                if (!string.IsNullOrEmpty(info.CompanyName))
                {
                    string company = info.CompanyName.ToLowerInvariant();

                    if (GamePublisherPatterns.Any(p => ContainsIgnoreCase(company, p)))
                    {
                        score += SCORE_COMPANY_GAME_PUBLISHER;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonKnownPublisher"));
                    }

                    // CompanyName — установщик
                    if (InstallerCompanyPatterns.Any(p => ContainsIgnoreCase(company, p)))
                    {
                        score += SCORE_COMPANY_INSTALLER;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonInstallerCompany"));
                    }
                }

                // FileDescription содержит признаки установщика
                if (!string.IsNullOrEmpty(info.FileDescription))
                {
                    string desc = info.FileDescription.ToLowerInvariant();

                    if (desc.Contains("setup") || desc.Contains("install") || desc.Contains("uninstall"))
                    {
                        score += SCORE_DESCRIPTION_SETUP;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonSetupDescription"));
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Debug($"Не удалось прочитать VersionInfo: {file.Name} — {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// Скоринг по DLL-зависимостям в папке
        /// </summary>
        private int ScoreByDependencies(string directory, ExecutableCandidate candidate)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return 0;

            int score = 0;

            try
            {
                var files = Directory.GetFiles(directory, "*.dll")
                    .Select(Path.GetFileName)
                    .Where(n => n != null)
                    .Select(n => n.ToLowerInvariant())
                    .ToList();

                // Steam API
                if (files.Any(f => f.StartsWith("steam_api")))
                {
                    score += SCORE_STEAM_API_DLL;
                    candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonSteamDll"));
                }

                // Unity
                if (files.Any(f => f.Contains("unity")))
                {
                    score += SCORE_UNITY_DLL;
                    candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonUnityDll"));
                }

                // Другие игровые DLL
                foreach (var pattern in GameDllPatterns)
                {
                    if (files.Any(f => ContainsIgnoreCase(f, pattern) && !pattern.Equals("steam_api")))
                    {
                        score += SCORE_GAME_ENGINE_DLL;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Debug($"Ошибка проверки зависимостей: {directory} — {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// Скоринг по структуре папок
        /// </summary>
        private int ScoreByFolderStructure(string exeDirectory, string rootPath, ExecutableCandidate candidate)
        {
            int score = 0;

            try
            {
                // Проверяем папку с .exe
                if (!string.IsNullOrEmpty(exeDirectory) && Directory.Exists(exeDirectory))
                {
                    var subdirs = Directory.GetDirectories(exeDirectory)
                        .Select(Path.GetFileName)
                        .Where(n => n != null)
                        .Select(n => n.ToLowerInvariant())
                        .ToList();

                    // Игровые папки
                    foreach (var indicator in GameFolderIndicators)
                    {
                        if (subdirs.Any(s => ContainsIgnoreCase(s, indicator)))
                        {
                            score += SCORE_GAME_FOLDERS_PRESENT;
                            candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonGameFolders"));
                            break;
                        }
                    }

                    // Игровые файлы
                    var files = Directory.GetFiles(exeDirectory, "*.*", SearchOption.TopDirectoryOnly);
                    if (files.Any(f => GameFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                    {
                        score += SCORE_GAME_FOLDERS_PRESENT;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonGameFiles"));
                    }
                }

                // Проверяем корневую папку на наличие файлов установщика
                if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
                {
                    var rootFiles = Directory.GetFiles(rootPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName)
                        .Where(n => n != null)
                        .Select(n => n.ToLowerInvariant())
                        .ToList();

                    if (rootFiles.Any(f => f.EndsWith(".bin") || f.EndsWith(".r00") || f.Contains("setup")))
                    {
                        score += SCORE_INSTALLER_FILES_PRESENT;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonInstallerFiles"));
                    }
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Debug($"Ошибка анализа структуры папок: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// Специальный скоринг для Launcher.exe
        /// </summary>
        private int ScoreLauncherExe(FileInfo file, string gameName, ExecutableCandidate candidate)
        {
            int score = 0;

            // Launcher.exe без признаков игры — низкий приоритет
            // Но если есть признаки игры — бонус

            // Проверяем размер
            if (file.Length > 20 * 1024 * 1024) // > 20 MB
            {
                score += SCORE_LAUNCHER_WITH_GAME_INDICATORS;
                candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonLauncherLarge"));
            }

            // Проверяем ProductName
            try
            {
                var info = FileVersionInfo.GetVersionInfo(file.FullName);
                if (!string.IsNullOrEmpty(info.ProductName))
                {
                    string normalizedProduct = NormalizeName(info.ProductName);
                    string normalizedGameName = NormalizeName(gameName);

                    if (ContainsIgnoreCase(normalizedProduct, normalizedGameName))
                    {
                        score += 20;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonLauncherProductName"));
                    }
                }

                // Известный издатель
                if (!string.IsNullOrEmpty(info.CompanyName))
                {
                    string company = info.CompanyName.ToLowerInvariant();
                    if (GamePublisherPatterns.Any(p => ContainsIgnoreCase(company, p)))
                    {
                        score += 15;
                        candidate.ScoreReasons.Add(ResourceProvider.GetString("LOC_HydraTorrent_ReasonLauncherPublisher"));
                    }
                }
            }
            catch
            {
                // Ignore
            }

            return score;
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Вспомогательные методы
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Проверяет, содержит ли строка подстроку (без учёта регистра)
        /// </summary>
        private static bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
                return false;

            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Нормализует название для сравнения
        /// </summary>
        private string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            // Удаляем спецсимволы, трейлемарк, регионы и т.д.
            string normalized = Regex.Replace(name, @"[™®©\-:_.,!'()]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            normalized = normalized.ToLowerInvariant();

            return normalized;
        }

        /// <summary>
        /// Проверяет, является ли файл Launcher.exe
        /// </summary>
        private bool IsLauncherExe(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            return name.Contains("launcher");
        }

        /// <summary>
        /// Вычисляет схожесть строк (0-100%)
        /// </summary>
        private int CalculateStringSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0;

            int maxLength = Math.Max(s1.Length, s2.Length);
            if (maxLength == 0) return 100;

            int distance = LevenshteinDistance(s1, s2);
            int similarity = (int)((1.0 - (double)distance / maxLength) * 100);

            return similarity;
        }

        /// <summary>
        /// Расстояние Левенштейна
        /// </summary>
        private int LevenshteinDistance(string s1, string s2)
        {
            int[,] matrix = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                matrix[i, 0] = i;

            for (int j = 0; j <= s2.Length; j++)
                matrix[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[s1.Length, s2.Length];
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Настройка игры в Playnite
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Настраивает Play Action для игры
        /// </summary>
        private void ConfigureGameAction(Game game, ExecutableCandidate candidate)
        {
            try
            {
                // Для портативной игры: УЖЕ УСТАНОВЛЕНА и готова к запуску
                game.IsInstalled = true;     // ✅ Игра установлена
                game.IsInstalling = false;   // ✅ Установка завершена
                game.InstallDirectory = candidate.Directory;

                // Создаём действие для запуска
                var playAction = new GameAction
                {
                    Type = GameActionType.File,
                    Path = candidate.FilePath,
                    WorkingDir = candidate.Directory,
                    Name = "Play",
                    IsPlayAction = true  // ✅ Это Play действие
                };

                // Удаляем старые действия
                if (game.GameActions == null)
                {
                    game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>();
                }
                else
                {
                    // Удаляем старые Play действия
                    var playActions = game.GameActions.Where(a => a.IsPlayAction).ToList();
                    foreach (var action in playActions)
                    {
                        game.GameActions.Remove(action);
                    }

                    // ❌ УДАЛИТЬ старые Install действия если есть
                    var installActions = game.GameActions.Where(a => !a.IsPlayAction).ToList();
                    foreach (var action in installActions)
                    {
                        game.GameActions.Remove(action);
                    }
                }

                game.GameActions.Add(playAction);

                _api.Database.Games.Update(game);

                HydraTorrent.logger.Info($"Игра настроена: {game.Name} → {candidate.FileName}");
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, $"Ошибка настройки игры: {game.Name}");
            }
        }

        /// <summary>
        /// Настраивает Install Action для репака
        /// </summary>
        private void ConfigureInstallAction(Game game, string setupPath)
        {
            try
            {
                // Для репака: игра ГОТОВА К УСТАНОВКЕ, но ещё НЕ УСТАНОВЛЕНА
                game.IsInstalled = false;        // ✅ ДОБАВИТЬ! Игра ещё не установлена
                game.IsInstalling = false;        // ✅ ИЗМЕНИТЬ! Установка ещё не началась - ждём пользователя
                game.InstallDirectory = Path.GetDirectoryName(setupPath);

                // Создаём действие для установки
                var installAction = new GameAction
                {
                    Type = GameActionType.File,
                    Path = setupPath,
                    WorkingDir = Path.GetDirectoryName(setupPath),
                    Name = "Install",
                    IsPlayAction = false  // Это НЕ Play действие
                };

                // Добавляем действие
                if (game.GameActions == null)
                {
                    game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>();
                }

                // Удаляем старые Install действия
                var oldInstallActions = game.GameActions
                    .Where(a => !a.IsPlayAction && a.Name == "Install")
                    .ToList();
                foreach (var action in oldInstallActions)
                {
                    game.GameActions.Remove(action);
                }

                // ❌ УДАЛИТЬ старые Play действия если есть (на случай повторной обработки)
                var oldPlayActions = game.GameActions.Where(a => a.IsPlayAction).ToList();
                foreach (var action in oldPlayActions)
                {
                    game.GameActions.Remove(action);
                }

                game.GameActions.Add(installAction);

                _api.Database.Games.Update(game);

                HydraTorrent.logger.Info($"Install действие создано: {game.Name} → {setupPath}");
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, $"Ошибка создания Install действия: {game.Name}");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Обновление queue.json
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Обновляет элемент в queue.json
        /// </summary>
        private void UpdateQueueItem(TorrentResult torrentData)
        {
            try
            {
                var queueItem = _plugin.DownloadQueue.FirstOrDefault(q => q.GameId == torrentData.GameId);
                if (queueItem != null)
                {
                    queueItem.ExecutablePath = torrentData.ExecutablePath;
                    queueItem.DetectedType = torrentData.DetectedType;
                    queueItem.IsConfigured = torrentData.IsConfigured;
                    queueItem.DownloadPath = torrentData.DownloadPath;

                    _plugin.SaveQueue();

                    HydraTorrent.logger.Info($"queue.json обновлён для: {torrentData.GameName}");
                }
            }
            catch (Exception ex)
            {
                HydraTorrent.logger.Error(ex, "Ошибка обновления queue.json");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Диалог выбора исполняемого файла
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Показывает диалог выбора исполняемого файла
        /// </summary>
        private async Task<ExecutableCandidate> ShowExecutableSelectionDialogAsync(string gameName, List<ExecutableCandidate> candidates)
        {
            ExecutableCandidate result = null;

            // Используем Dispatcher для показа диалога в UI потоке
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var selectionControl = new ExecutableSelectionWindow(candidates, gameName, _api);

                    var window = _api.Dialogs.CreateWindow(new WindowCreationOptions
                    {
                        ShowMinimizeButton = false,
                        ShowMaximizeButton = false,
                        ShowCloseButton = true
                    });

                    window.Content = selectionControl;
                    window.Title = ResourceProvider.GetString("LOC_HydraTorrent_SelectExecutable");
                    window.Width = 600;
                    window.Height = 450;
                    window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                    window.ShowDialog();

                    result = selectionControl.SelectedCandidate;
                }
                catch (Exception ex)
                {
                    HydraTorrent.logger.Error(ex, "Ошибка показа диалога выбора");
                }
            });

            return result;
        }

        // ────────────────────────────────────────────────────────────────
        // PRIVATE: Уведомления
        // ────────────────────────────────────────────────────────────────

        private void ShowRepackNotification(Game game, string downloadPath)
        {
            _api.Notifications.Add(new NotificationMessage(
                "HydraTorrent",
                string.Format(ResourceProvider.GetString("LOC_HydraTorrent_RepackDetected"), game.Name),
                NotificationType.Info));
        }

        private void ShowNoExecutableNotification(Game game, string downloadPath)
        {
            _api.Notifications.Add(new NotificationMessage(
                "HydraTorrent",
                string.Format(ResourceProvider.GetString("LOC_HydraTorrent_NoExecutablesFound"), game.Name),
                NotificationType.Info));
        }

        private void ShowManualSetupNotification(Game game, string downloadPath)
        {
            _api.Notifications.Add(new NotificationMessage(
                "HydraTorrent",
                string.Format(ResourceProvider.GetString("LOC_HydraTorrent_ManualSetupRequired"), game.Name),
                NotificationType.Info));
        }

        private void ShowSetupNotFoundNotification(Game game, string downloadPath)
        {
            _api.Notifications.Add(new NotificationMessage(
                "HydraTorrent",
                string.Format(ResourceProvider.GetString("LOC_HydraTorrent_SetupNotFound"), game.Name, downloadPath),
                NotificationType.Info));
        }
    }
}