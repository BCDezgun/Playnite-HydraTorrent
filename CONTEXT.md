# HydraTorrent Plugin — Project Context

## 📌 General Information

**Name:** HydraTorrent  
**Type:** Playnite Plugin (Library Plugin)  
**Version:** 1.0.0 (Stable)  
**Languages:** Russian 🇷🇺, English 🇬🇧  
**Author:** BCDezgun  
**Plugin ID:** `c2177dc7-8179-4098-8b6c-d683ce415279`

**Description:**  
A Playnite plugin for searching and downloading game repacks through qBittorrent API with full library integration. Features smart download queue, speed graphs, localization, and torrent management.

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
│   └── TorrentResult.cs           ← Search result model + queue data
│
├── 📂 Scrapers/
│   ├── IScraper.cs                ← Scraper interface
│   ├── JsonSourceScraper.cs       ← JSON source parsing
│   └── ScraperService.cs          ← Scraper management + HttpClient
│
├── 📂 Services/
│   └── TorrentMonitor.cs          ← Download status monitoring (3 sec timer)
│
├── 📂 Views/
│   ├── HydraHubView.xaml          ← Main UI (search + download manager)
│   ├── HydraHubView.xaml.cs       ← UI logic + sorting + graphs
│   ├── DownloadPathWindow.xaml    ← Install path selection window
│   └── DownloadPathWindow.xaml.cs ← Path window logic
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

Method,Purpose
GetInstallActions(),Returns install controller for plugin games
InstallGame(),Add game to queue/download
StartNextInQueueAsync(),Auto-start next game in queue
RecalculateQueuePositions(),Recalculate queue positions
GetHydraData() / SaveHydraData(),Read/write torrent data (JSON)
LoadQueue() / SaveQueue(),Read/write queue (queue.json)
RestoreQueueStateAsync(),Restore state after restart

Dependencies:
HydraTorrent
    ├──→ ScraperService (repack search)
    ├──→ TorrentMonitor (status monitoring)
    ├──→ HydraHubView (UI via CurrentInstance)
    ├──→ PlayniteApi.Database (games, library)
    └──→ QBittorrent.Client (API for downloads)

────────────────────────────────────────────────────────────────
2. Data Models (Models/)
────────────────────────────────────────────────────────────────

HydraRepack.cs
Purpose: Deserialize JSON from repack sources
Classes:
HydraRepack          // Single repack
    ├── Title        // Game title
    ├── Uris         // List of magnet links
    ├── FileSize     // Size (string, e.g. "15.8 GB")
    └── UploadDate   // Upload date (ISO 8601)

FitGirlRoot          // Root JSON object
    ├── Name         // Source name
    └── Downloads    // List of HydraRepack
	
TorrentResult.cs
Purpose: Search result + download queue item
Search Fields:
Name              // Torrent name
Size              // Size (string)
SizeBytes         // Size (number, for sorting)
Magnet            // Magnet link
Source            // Source name
UploadDate        // Publish date (DateTime?)
Year              // Year (from UploadDate)

Queue Fields:
GameId            // Game ID in Playnite (Guid?)
GameName          // Game name
TorrentHash       // Torrent hash (from magnet)
QueuePosition     // Position in queue (0 = active)
QueueStatus       // "Queued" | "Downloading" | "Paused" | "Completed"
AddedToQueueAt    // Time added to queue

Important: QueuePosition = 0 always means active download!

────────────────────────────────────────────────────────────────
3. Scrapers (Scrapers/)
────────────────────────────────────────────────────────────────

JsonSourceScraper.cs
Purpose: Parse JSON repack sources
Key Methods:

Method,Purpose
LoadDataAsync(),Load JSON from source (cached)
SearchAsync(),Search by game name
ParseSizeToBytes(),"Convert ""15.8 GB"" → bytes (for sorting)"

Important: Uses CultureInfo.InvariantCulture for number parsing!
ScraperService.cs
Purpose: Manage multiple sources
Fields:
private HttpClient _httpClient          // Shared HTTP client (30 sec timeout)
private HydraTorrentSettings _settings  // Settings (source list)

Method SearchAsync():

    Create scraper for each source
    Run search in parallel (Task.WhenAll)
    Combine results into single list
    Log errors per source (don't interrupt search)

TLS configuration in static constructor:
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
ServicePointManager.DefaultConnectionLimit = 100;

────────────────────────────────────────────────────────────────
4. Monitoring (Services/TorrentMonitor.cs)
────────────────────────────────────────────────────────────────

Purpose: Poll qBittorrent API for download status updates
Timer: 3 seconds (_timer.Elapsed)
Main Tasks:

    UpdateGameProgress() — Update LiveStatus for each game
    ManageQueueAsync() — Pause non-active downloads in qBittorrent
    CheckCompletedDownloadsAsync() — Check completed downloads

Logic in ManageQueueAsync():
Priority 1: QueuePosition == 0 && Status == "Downloading" → RESUME
Priority 2: QueueStatus == "Queued" || "Paused" → PAUSE
Important: Synchronizes queue state with actual qBittorrent state!

────────────────────────────────────────────────────────────────
5. UI (Views/)
────────────────────────────────────────────────────────────────

HydraHubView.xaml.cs
Purpose: Main plugin UI (2 tabs)
Tab 1: Repack Search

    Search across all sources
    Filter by sources
    Sort by size and date
    Search history (20 entries)
    Pagination (10 results per page)

Tab 2: Download Manager

    Active download (background, progress, speeds)
    Speed graphs (download/upload)
    Download queue with covers
    Controls: pause, start, delete, move

Key Fields:
private Guid _activeGameId           // Current active download
private Guid _lastActiveGameId       // Last active (for state persistence)
private bool _isPaused               // Pause state
private string _sortByColumn         // "Size", "UploadDate", null
private bool _sortAscending          // Sort direction
private Queue<long> _speedHistory    // Speed history (15 values)

UI Update Timer: 1 second (DispatcherTimer)
Sorting Methods:
HeaderSize_Click()    // Sort by size
HeaderDate_Click()    // Sort by date
ApplySorting()        // Apply sorting
UpdateSortIndicators()// Update ↑↓ arrows in headers

Important: Sorting resets on new search (PerformSearch())!
DownloadPathWindow.xaml.cs
Purpose: Install path selection dialog
Usage: Called from HydraTorrent.InstallGame() if default path not set

────────────────────────────────────────────────────────────────
6. Settings (HydraTorrentSettings.cs)
────────────────────────────────────────────────────────────────

HydraTorrentSettings
Fields:
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

SourceEntry
Name    // Source name (auto-filled from JSON)
Url     // JSON file URL

HydraTorrentSettingsViewModel
Inheritance: ObservableObject, ISettings
Settings lifecycle:
BeginEdit()  → Clone current settings
CancelEdit() → Restore from clone
EndEdit()    → Save (including SaveSources() from UI)

────────────────────────────────────────────────────────────────
7. Localization (Localization/)
────────────────────────────────────────────────────────────────

Files:

    en_US.xaml — English (loaded first, fallback)
    ru_RU.xaml — Russian (loaded second, overrides)

Key prefix: LOC_HydraTorrent_*
Usage in XAML:
<TextBlock Text="{DynamicResource LOC_HydraTorrent_SearchButton}"/>
Usage in C#:
ResourceProvider.GetString("LOC_HydraTorrent_SearchButton")
string.Format(ResourceProvider.GetString("LOC_HydraTorrent_PageInfo"), count, page, total)

Key Categories:
Category,Example Key
Tabs,LOC_HydraTorrent_TabSearch
Search,LOC_HydraTorrent_SearchButton
Filters,LOC_HydraTorrent_Sources
Download Manager,LOC_HydraTorrent_Loading
Speed,LOC_HydraTorrent_Download
Progress,LOC_HydraTorrent_PercentFormat
Seeds/Peers,LOC_HydraTorrent_SeederCount
Queue,LOC_HydraTorrent_Position
Management,LOC_HydraTorrent_Pause
Confirmations,LOC_HydraTorrent_ConfirmDeleteTorrent
Notifications,LOC_HydraTorrent_Started
Settings,LOC_HydraTorrent_QBittorrentSettings
Path Window,LOC_HydraTorrent_InstallTitle
Statuses,LOC_HydraTorrent_StatusDownloading
Errors,LOC_HydraTorrent_DownloadError
Columns,LOC_HydraTorrent_ColumnSize
Total keys: 105+

🔄 Data Flow
────────────────────────────────────────────────────────────────
Search and Add Game
────────────────────────────────────────────────────────────────

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

────────────────────────────────────────────────────────────────
Download Queue System
────────────────────────────────────────────────────────────────

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
9. On completion:
   - QueueStatus = "Completed"
   - StartNextInQueueAsync() → Next game
   
────────────────────────────────────────────────────────────────
Download UI Update
────────────────────────────────────────────────────────────────

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

⚙️ Dependencies (NuGet)
Package,Version,Purpose
PlayniteSDK,6.15.0,Playnite API
QBittorrent.Client,1.9.24285.1,qBittorrent Web API
Newtonsoft.Json,13.0.3,JSON serialization
HtmlAgilityPack,1.11.74,HTML parsing (if needed)
.NET Framework: 4.6.2

🎯 Critical Logic
────────────────────────────────────────────────────────────────
1. Download Queue
────────────────────────────────────────────────────────────────
Rule: QueuePosition = 0 always active download!
Method RecalculateQueuePositions():
foreach (var item in DownloadQueue)
{
    if (item.QueueStatus == "Downloading")
        item.QueuePosition = 0;  // Active
    else if (item.QueueStatus == "Queued" || "Paused")
        item.QueuePosition = ++pos;  // Others by order
}
Important: Iterate by actual list order, NOT sorted!

────────────────────────────────────────────────────────────────
2. Pause Synchronization
────────────────────────────────────────────────────────────────
Problem: On pause, UI must update instantly, but qBittorrent responds with delay.
Solution in BtnPauseResume_Click():
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

────────────────────────────────────────────────────────────────
3. Exclude Active Download from Queue List
────────────────────────────────────────────────────────────────

Method UpdateQueueUI():
var queuedGames = queue
    .Where(q => q.QueueStatus != "Completed" 
             && q.QueuePosition > 0)  // ← Key condition!
    .OrderBy(q => q.QueuePosition)
    .ToList();
Result: Active download (position 0) not shown in queue list.

📞 Contacts and Links
GitHub: https://github.com/BCDezgun/Playnite-HydraTorrent
Issues: https://github.com/BCDezgun/Playnite-HydraTorrent/issues
Author: BCDezgun
📝 Changelog (v1.0.0)

    ✅ Full RU/EN localization (105+ keys)
    ✅ Repack search across JSON sources
    ✅ Smart download queue with auto-switching
    ✅ Real-time speed graphs
    ✅ qBittorrent API integration
    ✅ Automatic game import to Playnite library
    ✅ Sort results by size and date
    ✅ 20+ bug fixes

💡 Developer Notes
When adding new features:

    Add localization keys to ru_RU.xaml and en_US.xaml
    Use ResourceProvider.GetString() in C#
    Use {DynamicResource} in XAML
    Update CONTEXT.md if architecture changes

When modifying download queue:

    Always call RecalculateQueuePositions() after changes
    Call SaveQueue() after position changes
    Update UI via UpdateQueueUI()

When working with qBittorrent API:

    Always check UseQbittorrent before connecting
    Use try-catch for all API calls
    Log errors via HydraTorrent.logger

Last updated: February 2026
Document version: 1.0
