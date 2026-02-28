# HydraTorrent Plugin — Project Context

## 📌 General Information

**Name:** HydraTorrent  
**Type:** Playnite Plugin (Library Plugin)  
**Version:** 1.1.0  
**Languages:** Russian 🇷🇺, English 🇬🇧  
**Author:** BCDezgun  
**Plugin ID:** `c2177dc7-8179-4098-8b6c-d683ce415279`

**Description:**  
A Playnite plugin for searching and downloading game repacks through qBittorrent API with full library integration. Features smart download queue, speed graphs, localization, torrent management, and **automatic post-download game setup** with executable detection.

---

## 📁 Project Structure
HydraTorrent/
├── 📄 extension.yaml              ← Plugin metadata for Playnite
├── 📄 icon.png                    ← Plugin icon
├── 📄 packages.config             ← NuGet dependencies
├── 📄 HydraTorrent.csproj         ← Visual Studio project file
│
├── 📄 App.xaml                    ← Localization dictionaries (en_US + ru_RU)
│
├── 📂 Localization/
│   ├── en_US.xaml                 ← English language (fallback)
│   └── ru_RU.xaml                 ← Russian language
│
├── 📂 Models/
│   ├── HydraRepack.cs             ← JSON repack model (HydraRepack, FitGirlRoot)
│   ├── TorrentResult.cs           ← Search result model + queue data
│   ├── GameType.cs                ← 🆕 Enum: Repack, Portable, Unknown
│   └── ExecutableCandidate.cs     ← 🆕 Executable candidate with scoring
│
├── 📂 Scrapers/
│   ├── IScraper.cs                ← Scraper interface
│   ├── JsonSourceScraper.cs       ← JSON source parsing
│   └── ScraperService.cs          ← Scraper management + HttpClient
│
├── 📂 Services/
│   ├── TorrentMonitor.cs          ← Download status monitoring (3 sec timer)
│   └── GameSetupService.cs        ← 🆕 Post-download game setup service
│
├── 📂 Views/
│   ├── HydraHubView.xaml          ← Main UI (search + download manager)
│   ├── HydraHubView.xaml.cs       ← UI logic + sorting + graphs
│   ├── DownloadPathWindow.xaml    ← Install path selection window
│   ├── DownloadPathWindow.xaml.cs ← Path window logic
│   ├── ExecutableSelectionWindow.xaml    ← 🆕 Executable selection dialog
│   └── ExecutableSelectionWindow.xaml.cs ← 🆕 Dialog logic
│
├── 📄 HydraTorrent.cs             ← Main plugin class (LibraryPlugin)
├── 📄 HydraTorrentClient.cs       ← LibraryClient (stub)
├── 📄 HydraTorrentSettings.cs     ← Settings + ViewModel
├── 📄 HydraTorrentSettingsView.xaml      ← Settings UI
└── 📄 HydraTorrentSettingsView.xaml.cs   ← Settings logic

## 🏗️ Architecture and Class Relationships

### ────────────────────────────────────────────────────────────────
### 1. Main Class: HydraTorrent.cs
### ────────────────────────────────────────────────────────────────

**Inheritance:** `LibraryPlugin` (Playnite SDK)

**Responsibilities:**
- Plugin entry point
- Download queue management (`DownloadQueue`)
- Torrent data storage (JSON files in `HydraTorrents/`)
- Playnite API integration

**Key Fields:**
```csharp
public static Dictionary<Guid, TorrentStatusInfo> LiveStatus  // Download statuses (updated every 3 sec)
public List<TorrentResult> DownloadQueue                       // Download queue
private ScraperService _scraperService                         // Search service
private TorrentMonitor _monitor                                // Torrent monitoring
```

**Key Methods:**
| Method | Purpose |
|--------|---------|
| GetInstallActions() | Returns install controller for plugin games |
| InstallGame() | Add game to queue/download |
| StartNextInQueueAsync() | Auto-start next game in queue |
| RecalculateQueuePositions() | Recalculate queue positions |
| GetHydraData() / SaveHydraData() | Read/write torrent data (JSON) |
| LoadQueue() / SaveQueue() | Read/write queue (queue.json) |
| RestoreQueueStateAsync() | Restore state after restart |

**Dependencies:**
```
HydraTorrent
    ├──→ ScraperService (repack search)
    ├──→ TorrentMonitor (status monitoring + post-download processing)
    ├──→ GameSetupService (executable detection)
    ├──→ HydraHubView (UI via CurrentInstance)
    ├──→ PlayniteApi.Database (games, library)
    └──→ QBittorrent.Client (API for downloads)
```

────────────────────────────────────────────────────────────────
### 2. Data Models (Models/)
────────────────────────────────────────────────────────────────

#### HydraRepack.cs
**Purpose:** Deserialize JSON from repack sources

**Classes:**
```
HydraRepack          // Single repack
    ├── Title        // Game title
    ├── Uris         // List of magnet links
    ├── FileSize     // Size (string, e.g. "15.8 GB")
    └── UploadDate   // Upload date (ISO 8601)

FitGirlRoot          // Root JSON object
    ├── Name         // Source name
    └── Downloads    // List of HydraRepack
```

#### TorrentResult.cs
**Purpose:** Search result + download queue item

**Search Fields:**
| Field | Description |
|-------|-------------|
| Name | Torrent name |
| Size | Size (string) |
| SizeBytes | Size (number, for sorting) |
| Magnet | Magnet link |
| Source | Source name |
| UploadDate | Publish date (DateTime?) |
| Year | Year (from UploadDate) |

**Queue Fields:**
| Field | Description |
|-------|-------------|
| GameId | Game ID in Playnite (Guid?) |
| GameName | Game name |
| TorrentHash | Torrent hash (from magnet) |
| QueuePosition | Position in queue (0 = active) |
| QueueStatus | "Queued" \| "Downloading" \| "Paused" \| "Completed" |
| AddedToQueueAt | Time added to queue |
| **DownloadPath** | 🆕 Actual download path (from qBittorrent) |
| **DetectedType** | 🆕 GameType enum (Repack/Portable/Unknown) |
| **IsConfigured** | 🆕 True if game action was created |
| **ExecutablePath** | 🆕 Path to main executable |

> ⚠️ **Important:** `QueuePosition = 0` always means active download!

#### 🆕 GameType.cs
**Purpose:** Enum for game type detection

```csharp
public enum GameType
{
    Unknown = 0,   // Could not determine
    Repack = 1,    // Has setup.exe, installer files
    Portable = 2   // Ready to play, no installation needed
}
```

#### 🆕 ExecutableCandidate.cs
**Purpose:** Model for executable file scoring

**Properties:**
| Property | Type | Description |
|----------|------|-------------|
| FilePath | string | Full path to executable |
| FileName | string | File name (e.g., "Cyberpunk2077.exe") |
| FileSize | long | File size in bytes |
| FileSizeFormatted | string | Human-readable size |
| ProductName | string | From FileVersionInfo |
| FileDescription | string | From FileVersionInfo |
| CompanyName | string | From FileVersionInfo |
| ConfidenceScore | int | Score 0-100 |
| ScoreReasons | List\<string\> | Why this score was assigned |
| IsLauncher | bool | True if file is named "Launcher.exe" |

────────────────────────────────────────────────────────────────
### 3. Scrapers (Scrapers/)
────────────────────────────────────────────────────────────────

#### JsonSourceScraper.cs
**Purpose:** Parse JSON repack sources

**Key Methods:**
| Method | Purpose |
|--------|---------|
| LoadDataAsync() | Load JSON from source (cached) |
| SearchAsync() | Search by game name |
| ParseSizeToBytes() | Convert "15.8 GB" → bytes (for sorting) |

> ⚠️ **Important:** Uses `CultureInfo.InvariantCulture` for number parsing!

#### ScraperService.cs
**Purpose:** Manage multiple sources

**Fields:**
```csharp
private HttpClient _httpClient          // Shared HTTP client (30 sec timeout)
private HydraTorrentSettings _settings  // Settings (source list)
```

**Method SearchAsync():**
```
1. Create scraper for each source
2. Run search in parallel (Task.WhenAll)
3. Combine results into single list
4. Log errors per source (don't interrupt search)
```

**TLS configuration in static constructor:**
```csharp
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
ServicePointManager.DefaultConnectionLimit = 100;
```

────────────────────────────────────────────────────────────────
### 4. Monitoring (Services/TorrentMonitor.cs)
────────────────────────────────────────────────────────────────

**Purpose:** Poll qBittorrent API for download status updates

**Timer:** 3 seconds (`_timer.Elapsed`)

**Main Tasks:**
```
UpdateGameProgress() — Update LiveStatus for each game
ManageQueueAsync() — Pause non-active downloads in qBittorrent
CheckCompletedDownloadsAsync() — Check completed downloads + 🆕 trigger post-processing
```

**Logic in ManageQueueAsync():**
```
Priority 1: QueuePosition == 0 && Status == "Downloading" → RESUME
Priority 2: QueueStatus == "Queued" || "Paused" → PAUSE
```
> ⚠️ **Important:** Synchronizes queue state with actual qBittorrent state!

**🆕 Post-Download Processing (in CheckCompletedDownloadsAsync):**
```csharp
// When download completes (Progress >= 1.0):
if (torrent.Progress >= 1.0)
{
    // Trigger GameSetupService
    await _gameSetupService.ProcessDownloadedGameAsync(
        item.GameId.Value, 
        item.DownloadPath,
        item.TorrentHash);  // For getting actual path from qBittorrent
}
```

────────────────────────────────────────────────────────────────
### 🆕 5. Game Setup Service (Services/GameSetupService.cs)
────────────────────────────────────────────────────────────────

**Purpose:** Automatic post-download game configuration

**Main Methods:**
| Method | Purpose |
|--------|---------|
| ProcessDownloadedGameAsync() | Entry point after download completes |
| GetActualDownloadPathAsync() | Get real folder from qBittorrent API |
| DetectGameType() | Determine if Repack or Portable |
| ProcessPortableGameAsync() | Find and configure executable for portable |
| ProcessRepackGameAsync() | Configure Install action for repacks |
| ConfigureGameAction() | Create Play action in Playnite |
| ConfigureInstallAction() | Create Install action for repacks |
| AnalyzePortableGameAsync() | Score all executable candidates |
| CalculateConfidenceScore() | Scoring algorithm for single executable |

**Scoring Algorithm (Confidence Score 0-100):**

| Category | Points | Condition |
|----------|--------|-----------|
| **Name Similarity** | | |
| Exact match | +40 | FileName == GameName |
| Contains game name | +20 | FileName contains GameName |
| Partial match | +25 | GameName contains FileName |
| Fuzzy match | +0-25 | Levenshtein similarity |
| **File Size** | | |
| Very large (>200MB) | +40 | Bonus for large executables |
| Large (>20MB) | +30 | Typical for game executables |
| Medium (5-20MB) | +10 | Acceptable size |
| Small (<5MB) | -20 | Penalty, likely config tool |
| **Version Info** | | |
| ProductName matches | +40 | Strong indicator |
| Known publisher | +20 | Rockstar, Ubisoft, EA, etc. |
| Installer company | -40 | Inno Setup, NSIS, etc. |
| Setup description | -30 | "setup", "install" in description |
| **DLL Dependencies** | | |
| Steam API DLL | +25 | steam_api.dll present |
| Unity DLL | +25 | unityplayer.dll present |
| Game engine DLL | +15 | d3d11, dxgi, etc. |
| **Folder Structure** | | |
| Game folders | +20 | textures, audio, models, etc. |
| Game files | +20 | .pak, .assets, .rpf, etc. |
| Installer files | -15 | .bin, .r00 in root |
| **Launcher.exe Special** | | |
| Large launcher | +30 | >20MB launcher |
| ProductName match | +20 | Launcher with game name |
| Known publisher | +15 | From known game company |

**Auto-Configuration Threshold:**
- **≥70%:** Auto-configure without dialog
- **<70%:** Show selection dialog with top 5 candidates

**Excluded Patterns:**
Files matching these patterns are excluded from candidates:
```
uninstall, setup, install, config, settings, 
redist, directx, vcredist, dotnet, updater, patch
```

────────────────────────────────────────────────────────────────
### 6. UI (Views/)
────────────────────────────────────────────────────────────────

#### HydraHubView.xaml.cs
**Purpose:** Main plugin UI (2 tabs)

**Tab 1: Repack Search**
- Search across all sources
- Filter by sources
- Sort by size and date
- Search history (20 entries)
- Pagination (10 results per page)

**Tab 2: Download Manager**
- Active download (background, progress, speeds)
- Speed graphs (download/upload)
- Download queue with covers
- Controls: pause, start, delete, move

**Key Fields:**
```csharp
private Guid _activeGameId           // Current active download
private Guid _lastActiveGameId       // Last active (for state persistence)
private bool _isPaused               // Pause state
private string _sortByColumn         // "Size", "UploadDate", null
private bool _sortAscending          // Sort direction
private Queue<long> _speedHistory    // Speed history (15 values)
```

**UI Update Timer:** 1 second (DispatcherTimer)

**Sorting Methods:**
| Method | Purpose |
|--------|---------|
| HeaderSize_Click() | Sort by size |
| HeaderDate_Click() | Sort by date |
| ApplySorting() | Apply sorting |
| UpdateSortIndicators() | Update ↑↓ arrows in headers |

> ⚠️ **Important:** Sorting resets on new search (`PerformSearch()`)!

#### DownloadPathWindow.xaml.cs
**Purpose:** Install path selection dialog  
**Usage:** Called from `HydraTorrent.InstallGame()` if default path not set

#### 🆕 ExecutableSelectionWindow.xaml(.cs)
**Purpose:** Dialog for selecting executable when confidence < 70%

**Features:**
- Shows top 5 candidates with scores
- Displays confidence percentage
- Shows score reasons for each candidate
- "Browse" button for manual selection
- File size and description display

**Usage:**
```csharp
var window = new ExecutableSelectionWindow(candidates, gameName, _api);
var windowHost = _api.Dialogs.CreateWindow(new WindowCreationOptions { ... });
windowHost.Content = window;
windowHost.ShowDialog();
var selected = window.SelectedCandidate;
```

────────────────────────────────────────────────────────────────
### 7. Settings (HydraTorrentSettings.cs)
────────────────────────────────────────────────────────────────

#### HydraTorrentSettings
**Fields:**
```csharp
// qBittorrent
QBittorrentHost       // "127.0.0.1"
QBittorrentPort       // 8080
QBittorrentUsername   // "admin"
QBittorrentPassword   // ""
UseQbittorrent        // true

// Paths
UseDefaultDownloadPath  // true
DefaultDownloadPath     // ""

// Sources
Sources               // List<SourceEntry>
SearchHistory         // List<string> (max 20)
```

**SourceEntry:**
| Field | Description |
|-------|-------------|
| Name | Source name (auto-filled from JSON) |
| Url | JSON file URL |

#### HydraTorrentSettingsViewModel
**Inheritance:** `ObservableObject, ISettings`

**Settings lifecycle:**
| Method | Action |
|--------|--------|
| BeginEdit() | Clone current settings |
| CancelEdit() | Restore from clone |
| EndEdit() | Save (including `SaveSources()` from UI) |

────────────────────────────────────────────────────────────────
### 8. Localization (Localization/)
────────────────────────────────────────────────────────────────

**Files:**
```
en_US.xaml — English (loaded first, fallback)
ru_RU.xaml — Russian (loaded second, overrides)
```

**Key prefix:** `LOC_HydraTorrent_*`

**Usage in XAML:**
```xml
<TextBlock Text="{DynamicResource LOC_HydraTorrent_SearchButton}"/>
```

**Usage in C#:**
```csharp
ResourceProvider.GetString("LOC_HydraTorrent_SearchButton")
string.Format(ResourceProvider.GetString("LOC_HydraTorrent_PageInfo"), count, page, total)
```

**Key Categories:**
| Category | Example Key |
|----------|-------------|
| Tabs | LOC_HydraTorrent_TabSearch |
| Search | LOC_HydraTorrent_SearchButton |
| Filters | LOC_HydraTorrent_Sources |
| Download Manager | LOC_HydraTorrent_Loading |
| Speed | LOC_HydraTorrent_Download |
| Progress | LOC_HydraTorrent_PercentFormat |
| Seeds/Peers | LOC_HydraTorrent_SeederCount |
| Queue | LOC_HydraTorrent_Position |
| Management | LOC_HydraTorrent_Pause |
| Confirmations | LOC_HydraTorrent_ConfirmDeleteTorrent |
| Notifications | LOC_HydraTorrent_Started |
| Settings | LOC_HydraTorrent_QBittorrentSettings |
| Path Window | LOC_HydraTorrent_InstallTitle |
| Statuses | LOC_HydraTorrent_StatusDownloading |
| Errors | LOC_HydraTorrent_DownloadError |
| Columns | LOC_HydraTorrent_ColumnSize |
| 🆕 Game Setup | LOC_HydraTorrent_AutoConfigured |
| 🆕 Scoring Reasons | LOC_HydraTorrent_ReasonExactMatch |

**Total keys:** 130+

---

## 🔄 Data Flow

────────────────────────────────────────────────────────────────
### Search and Add Game
────────────────────────────────────────────────────────────────

```
1. User enters name → TxtSearch_KeyDown (Enter)
        ↓
2. PerformSearch() → _scraperService.SearchAsync(query)
        ↓
3. JsonSourceScraper.SearchAsync() → HTTP GET JSON
        ↓
4. Return List<TorrentResult> → _allResults
        ↓
5. ApplyLocalFilters() → _filteredResults (by selected sources)
        ↓
6. ShowPage(1) → lstResults.ItemsSource = pageData
        ↓
7. User double-click → LstResults_MouseDoubleClick
        ↓
8. ImportGame() → PlayniteApi.Database.ImportGame(metadata)
        ↓
9. SaveHydraData() → JSON file in HydraTorrents/{GameId}.json
        ↓
10. InstallGame() → Add to queue/download
```

────────────────────────────────────────────────────────────────
### Download Queue System
────────────────────────────────────────────────────────────────

```
1. InstallGame() → Check duplicates
        ↓
2. ExtractHashFromMagnet() → Extract hash
        ↓
3. ShowCustomInstallPathDialog() → Path selection (if needed)
        ↓
4. QBittorrent.AddTorrentsAsync() → Add torrent
        ↓
5. If active download exists:
   - QueueStatus = "Queued"
   - Paused = true
   ↓
6. If no active:
   - QueueStatus = "Downloading"
   - QueuePosition = 0
   ↓
7. SaveQueue() → queue.json
        ↓
8. TorrentMonitor (every 3 sec):
   - ManageQueueAsync() → Sync with qBittorrent
   - CheckCompletedDownloadsAsync() → Check completion
        ↓
9. On completion (Progress >= 1.0):
   - QueueStatus = "Completed"
   - 🆕 GameSetupService.ProcessDownloadedGameAsync()
   - StartNextInQueueAsync() → Next game
```

────────────────────────────────────────────────────────────────
### 🆕 Post-Download Game Setup Flow
────────────────────────────────────────────────────────────────

```
1. TorrentMonitor detects Progress >= 1.0
        ↓
2. GameSetupService.ProcessDownloadedGameAsync(gameId, downloadPath, torrentHash)
        ↓
3. GetActualDownloadPathAsync() → Query qBittorrent for real folder
   Example: D:\Games → D:\Games\TerraTech (2018)
        ↓
4. DetectGameType() → Repack or Portable?
        ↓
   ┌─────────────────────────────────────────┐
   │ PORTABLE PATH:                          │
   │                                         │
   │ 5a. AnalyzePortableGameAsync()          │
   │     - GetAllExecutables()               │
   │     - CalculateConfidenceScore()        │
   │     - Sort by ConfidenceScore           │
   │         ↓                               │
   │ 6a. Best candidate ≥70%?                │
   │     YES → Auto-configure                │
   │     NO → Show selection dialog          │
   │         ↓                               │
   │ 7a. ConfigureGameAction()               │
   │     - Create Play Action                │
   │     - Set IsInstalled = true            │
   │     - Update database                   │
   └─────────────────────────────────────────┘
        ↓
   ┌─────────────────────────────────────────┐
   │ REPACK PATH:                            │
   │                                         │
   │ 5b. FindSetupExecutable()               │
   │     - Look for setup.exe                │
   │     - Search root then subfolders       │
   │         ↓                               │
   │ 6b. ConfigureInstallAction()            │
   │     - Create Install Action             │
   │     - Set IsInstalling = true           │
   │     - Update database                   │
   └─────────────────────────────────────────┘
        ↓
8. SaveUpdatedTorrentData()
   - Save to {GameId}.json
   - Update queue.json
        ↓
9. Show notification (configured/ready to install)
```

────────────────────────────────────────────────────────────────
### Download UI Update
────────────────────────────────────────────────────────────────

```
1. DispatcherTimer (1 sec) → UIUpdateTimer_Tick()
        ↓
2. Check _lastActiveGameId → Is game still valid?
        ↓
3. Priority 1: QueueStatus == "Downloading" → targetGameId
        ↓
4. Priority 2: QueueStatus == "Paused" → targetGameId
        ↓
5. Priority 3: LiveStatus → targetGameId
        ↓
6. UpdateDownloadUI(game, status)
        ↓
7. Update all UI elements:
   - txtCurrentGameName
   - pnlDownloadSpeed / pnlUploadSpeed
   - pbDownload (progress)
   - lblSeeds / lblPeers
   - lblETA
        ↓
8. DrawSpeedGraph() → Speed graphs
```

---

## ⚙️ Dependencies (NuGet)

| Package | Version | Purpose |
|---------|---------|---------|
| PlayniteSDK | 6.15.0 | Playnite API |
| QBittorrent.Client | 1.9.24285.1 | qBittorrent Web API |
| Newtonsoft.Json | 13.0.3 | JSON serialization |
| HtmlAgilityPack | 1.11.74 | HTML parsing (if needed) |

**.NET Framework:** 4.6.2

---

## 🎯 Critical Logic

────────────────────────────────────────────────────────────────
### 1. Download Queue
────────────────────────────────────────────────────────────────

**Rule:** `QueuePosition = 0` always means active download!

**Method RecalculateQueuePositions():**
```csharp
foreach (var item in DownloadQueue)
{
    if (item.QueueStatus == "Downloading")
        item.QueuePosition = 0;  // Active
    else if (item.QueueStatus == "Queued" || "Paused")
        item.QueuePosition = ++pos;  // Others by order
}
```
> ⚠️ **Important:** Iterate by actual list order, NOT sorted!

────────────────────────────────────────────────────────────────
### 2. Pause Synchronization
────────────────────────────────────────────────────────────────

**Problem:** On pause, UI must update instantly, but qBittorrent responds with delay.

**Solution in BtnPauseResume_Click():**
```csharp
// 1. Get current status BEFORE command
bool isCurrentlyPaused = torrent.State.ToString().Contains("Paused");

// 2. INSTANTLY switch UI (for responsiveness)
_isPaused = !isCurrentlyPaused;
UpdatePauseButtonState();

// 3. IMMEDIATELY update QueueStatus (prevents auto-resume!)
var queueItem = _plugin.DownloadQueue.FirstOrDefault(q => q.GameId == _activeGameId);
if (queueItem != null)
{
    queueItem.QueueStatus = _isPaused ? "Paused" : "Downloading";
    _plugin.SaveQueue();
}

// 4. Send command to qBittorrent
if (_isPaused)
    await client.PauseAsync(torrentData.TorrentHash);
else
    await client.ResumeAsync(torrentData.TorrentHash);

// 5. Wait for command processing
await Task.Delay(500);

// 6. Check real status ONLY for data update (NOT for _isPaused!)
// Update LiveStatus (data: speed, progress, etc.)
```

────────────────────────────────────────────────────────────────
### 3. Exclude Active Download from Queue List
────────────────────────────────────────────────────────────────

**Method UpdateQueueUI():**
```csharp
var queuedGames = queue
    .Where(q => q.QueueStatus != "Completed" 
             && q.QueuePosition > 0)  // ← Key condition!
    .OrderBy(q => q.QueuePosition)
    .ToList();
```
**Result:** Active download (position 0) not shown in queue list.

────────────────────────────────────────────────────────────────
### 🆕 4. Download Completion Detection
────────────────────────────────────────────────────────────────

**Problem:** qBittorrent states like "StalledUP", "Uploading" don't contain "Complete".

**Solution:** Check only `Progress >= 1.0`, ignore state name:
```csharp
// OLD (incorrect):
if (torrent.Progress >= 1.0 && torrent.State.ToString().Contains("Complete"))

// NEW (correct):
if (torrent.Progress >= 1.0)  // 1.0 = 100%
```

────────────────────────────────────────────────────────────────
### 🆕 5. Actual Download Path Resolution
────────────────────────────────────────────────────────────────

**Problem:** User selects `D:\Games`, but torrent creates subfolder `D:\Games\GameName`.

**Solution:** Query qBittorrent API for torrent contents:
```csharp
var contents = await client.GetTorrentContentsAsync(torrentHash);
var firstFile = contents.First();
// firstFile.Name = "GameName/data/file.pak"
var rootFolder = firstFile.Name.Split('/')[0]; // "GameName"
var actualPath = Path.Combine(baseDownloadPath, rootFolder);
```

---

## 📞 Contacts and Links

**GitHub:** https://github.com/BCDezgun/Playnite-HydraTorrent  
**Issues:** https://github.com/BCDezgun/Playnite-HydraTorrent/issues  
**Author:** BCDezgun

---

## 📝 Changelog

### v1.1.0 (Current)
- ✅ 🆕 Automatic post-download game setup
- ✅ 🆕 Game type detection (Repack/Portable)
- ✅ 🆕 Executable detection with confidence scoring
- ✅ 🆕 Auto-create Play Action for portable games
- ✅ 🆕 Auto-create Install Action for repacks
- ✅ 🆕 Selection dialog for low-confidence cases
- ✅ 🆕 Real download path resolution via qBittorrent API
- ✅ 🆕 Fixed completion detection (Progress-based)
- ✅ 🆕 Added 25+ localization keys for game setup

### v1.0.0
- ✅ Full RU/EN localization (105+ keys)
- ✅ Repack search across JSON sources
- ✅ Smart download queue with auto-switching
- ✅ Real-time speed graphs
- ✅ qBittorrent API integration
- ✅ Automatic game import to Playnite library
- ✅ Sort results by size and date
- ✅ 20+ bug fixes

---

## 💡 Developer Notes

### When adding new features:
- Add localization keys to `ru_RU.xaml` and `en_US.xaml`
- Use `ResourceProvider.GetString()` in C#
- Use `{DynamicResource}` in XAML
- Update `CONTEXT.md` if architecture changes

### When modifying download queue:
- Always call `RecalculateQueuePositions()` after changes
- Call `SaveQueue()` after position changes
- Update UI via `UpdateQueueUI()`

### When working with qBittorrent API:
- Always check `UseQbittorrent` before connecting
- Use try-catch for all API calls
- Log errors via `HydraTorrent.logger`

### 🆕 When working with GameSetupService:
- Use `_plugin.GetSettings().Settings` for qBittorrent settings
- Always pass torrentHash for accurate path resolution
- Check `torrentData.IsConfigured` before re-processing
- Update both game JSON and queue.json after configuration

---

**Last updated:** March 2026  
**Document version:** 1.1
