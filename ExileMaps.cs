using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using ExileCore2;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Enums;

using GameOffsets2.Native;

using ImGuiNET;

using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;

namespace ExileMaps;

public class ExileMapsCore : BaseSettingsPlugin<ExileMapsSettings>
{
    #region Declarations
    public static ExileMapsCore Main;

    public const float MapWeightSliderMin = -25.0f;
    public const float MapWeightSliderMax = 50.0f;

    private const string defaultMapsPath = "json\\maps.json";
    private const string defaultModsPath = "json\\mods.json";
    private const string defaultBiomesPath = "json\\biomes.json";
    private const string defaultContentPath = "json\\content.json";
    private const string ArrowPath = "textures\\arrow.png";
    private const string IconsFile = "Icons.png";
    private static readonly Color UnknownContentColor = Color.FromArgb(180, 180, 180, 180);
    private static readonly Color UnknownModColor = Color.FromArgb(180, 180, 180, 180);
    private static readonly string[] ContentRingDrawOrder = [
        "Breach",
        "Delirium",
        "Expedition",
        "Ritual",
        "Abyss",
        "Map Boss",
        "Anomaly Map Boss",
        "Powerful Map Boss",
        "Deadly Map Boss",
        "Cleansed",
        "Corrupted",
        "Corrupted Nexus",
        "Irradiated",
        "Unique Map",
        "Tower"
    ];
    private static readonly HashSet<string> ContentRingDrawOrderLookup = new(ContentRingDrawOrder, StringComparer.OrdinalIgnoreCase);
    // New custom sprite sheet for the plugin (drop the generated PNG in textures/). Loaded if present;
    // grid layout (SpriteSheetCols x SpriteSheetRows) to be set once the sheet is finalized.
    // Custom sprite atlas (SpriteAtlas): 1024x768, 8x6, 128px cells, 48 desaturated icons.
    private const string CustomIconsPath = "textures\\Icons_Desaturated.png";
    private const string CustomIconsName = SpriteAtlas.FileName;
    
    public IngameUIElements UI;
    public AtlasPanel AtlasPanel;

    private Vector2 screenCenter;
    private List<Node> selectedNodes = [];
    private RectangleF cachedScreenRect;
    private readonly List<RectangleF> cachedExcludeRects = [];
    private Dictionary<Vector2i, Node> mapCache = [];
    public bool refreshCache = false;
    private int cacheTicks = 0;
    private bool refreshingCache = false;
    private bool clearCacheOnNextRefresh;
    private Job? refreshCacheJob;
    private int atlasWarmupTicks;
    private float maxMapWeight = MapWeightSliderMax;
    private float minMapWeight = MapWeightSliderMin;
    private readonly object mapCacheLock = new();
    private readonly object mapTypesLock = new();
    private readonly object mapContentLock = new();
    private DateTime lastRefresh = DateTime.Now;
    private bool weightsDirty = false;
    private DateTime lastWeightRecalc = DateTime.Now;
    private int TickCount { get; set; }

    private Vector2 atlasOffset;
    private Vector2 atlasDelta;

    internal IntPtr iconsId;
    internal IntPtr arrowId;
    internal IntPtr customIconsId;
    private bool customIconsLoaded = false;

    private bool AtlasHasBeenClosed = true;
    private bool gameFilesScraped = false;
    private bool WaypointPanelIsOpen = false;
    private bool ShowMinimap = false;
    private readonly object unknownLogLock = new();
    private readonly HashSet<string> loggedUnknownMaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> loggedUnknownContentTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> loggedUnknownMods = new(StringComparer.OrdinalIgnoreCase);


    #endregion

    #region ExileCore Methods
    public override bool Initialise()
    {
        Main = this;        
        
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            LogError("UnobservedTaskException: " + e.Exception + "\n" + e.Exception?.StackTrace);
            e.SetObserved();
        };
        
        RegisterHotkeys();
        SubscribeToEvents();

        LoadDefaultBiomes();
        LoadDefaultContentTypes();
        LoadDefaultMaps();
        LoadDefaultMods();
        
        Graphics.InitImage(IconsFile);
        iconsId = Graphics.GetTextureId(IconsFile);
        Graphics.InitImage("arrow.png", Path.Combine(DirectoryFullName, ArrowPath));
        arrowId = Graphics.GetTextureId("arrow.png");

        // Load the custom sprite sheet if it's been added. Guarded so the plugin still runs before
        // the PNG exists; once present, address sprites via GetSpriteUV(col, row).
        var customIconsFull = Path.Combine(DirectoryFullName, CustomIconsPath);
        if (File.Exists(customIconsFull)) {
            Graphics.InitImage(CustomIconsName, customIconsFull);
            customIconsId = Graphics.GetTextureId(CustomIconsName);
            customIconsLoaded = true;
        }

        CanUseMultiThreading = true;

        return true;
    }
    public override void AreaChange(AreaInstance area)
    {
        refreshCache = true;
    }

    public override void Tick()
    {
        // Defensive null checks for UI during scene transitions
        if (GameController?.Game?.IngameState?.IngameUi == null)
            return;

        UI = GameController.Game.IngameState.IngameUi;
        
        // WorldMap or AtlasPanel can be null during area transitions
        if (UI.WorldMap == null)
            return;

        AtlasPanel = UI.WorldMap.AtlasPanel;

        if (AtlasPanel == null || !AtlasPanel.IsVisible) {
            if (!AtlasHasBeenClosed)
                ClearCachedAtlasState();

            AtlasHasBeenClosed = true;
            WaypointPanelIsOpen = false;
            return;
        }

        if (AtlasHasBeenClosed) {
            atlasWarmupTicks = 0;
            RequestMapCacheRefresh(clearCache: true);
        }

        AtlasHasBeenClosed = false;

        // Scrape map types, content types and biomes from the game's endgame files once they're
        // available (files may not be loaded yet at Initialise). Adds anything missing from the json
        // seeds without overwriting them. All three load together, so gate on the map scrape succeeding.
        if (!gameFilesScraped && UpdateMapData(false)) {
            UpdateContentData(false);
            UpdateBiomeData(false);
            gameFilesScraped = true;
        }

        if (atlasWarmupTicks < 3)
            atlasWarmupTicks++;

        cacheTicks++;
        if (cacheTicks >= 100) {
            cacheTicks = 0;
            int cacheCount;
            lock (mapCacheLock)
                cacheCount = mapCache.Count;
            if (SafeAtlasDescriptionCount() > cacheCount)
                refreshCache = true;
        }

        screenCenter = GameController.Window.GetWindowRectangle().Center - GameController.Window.GetWindowRectangle().Location;

        if (refreshCache && atlasWarmupTicks >= 3)
            TryStartRefreshCacheJob();

        // Coalesce settings-change weight recalcs. Slider drags (and quick-edit edits) fire
        // PropertyChanged many times per second; mark dirty and recompute at most ~2x/sec (500ms debounce).
        if (weightsDirty && !refreshingCache && DateTime.Now.Subtract(lastWeightRecalc).TotalMilliseconds > 500) {
            weightsDirty = false;
            lastWeightRecalc = DateTime.Now;
            RecalculateWeights(recalculateNodeWeights: true);
        }

        // Waypoint paths only change when nodes get visited (which triggers a cache refresh) or when
        // waypoints are added/removed. RefreshMapCache and Add/RemoveWaypoint already recompute them,
        // so there's no need to rerun the full per-waypoint BFS + closest-visited-node scan every tick.

        return;
    }

    public override void Render()
    {
        ProcessPendingWeightFile();

        CheckKeybinds();

        if (WaypointPanelIsOpen) DrawWaypointPanel();

        if (quickEditOpen) DrawQuickEditPanel();

        if (debugNodeOpen) DrawNodeDebugPanel();

        TickCount++;

        if (AtlasPanel == null || !AtlasPanel.IsVisible)
            return;

        if (refreshingCache || atlasWarmupTicks < 3 || clearCacheOnNextRefresh)
            return;

        int cacheCount;
        lock (mapCacheLock)
            cacheCount = mapCache.Count;

        if (cacheCount == 0) {
            RequestMapCacheRefresh();
            return;
        }

        // Cache panel/tooltip bounds once per frame so IsOnScreen avoids repeated game-memory reads.
        UpdateScreenBounds();

        // Recompute the on-screen node set only every RenderNTicks frames (the filter is expensive:
        // per-node GetClientRect + IsOnScreen over the whole atlas), but redraw the cached set every
        // frame so the immediate-mode overlay never flickers. Node positions stay live because
        // RenderNode reads GetClientRect fresh each frame.
        if (TickCount % Settings.Graphics.RenderNTicks.Value == 0 || selectedNodes.Count == 0)
            selectedNodes = SnapshotMapCacheValues()
                .Where(x => Settings.Features.ProcessVisitedNodes || !x.IsVisited || x.IsAttempted)
                .Where(x => (Settings.Features.ProcessHiddenNodes && !x.IsVisible) || x.IsVisible || x.IsTower)
                .Where(x => (Settings.Features.ProcessLockedNodes && !x.IsUnlocked) || x.IsUnlocked)
                .Where(x => (Settings.Features.ProcessUnlockedNodes && x.IsUnlocked) || !x.IsUnlocked)
                .Where(x => TryGetNodeRect(x, out RectangleF rect) && IsOnScreen(rect.Center))
                .ToList();

        if (!ShowMinimap) {
            // Resolve each node's screen rect once per frame, then draw in fixed z-layers across the
            // whole set: lines -> node fills -> rings -> labels. Layered passes (rather than one full
            // draw per node) keep the z-order globally consistent and stop lines flickering over
            // circles/labels.
            var nodePositions = new List<(Node node, RectangleF rect)>(selectedNodes.Count);
            foreach (var node in selectedNodes) {
                if (TryGetNodeRect(node, out RectangleF rect))
                    nodePositions.Add((node, rect));
            }

            if (Settings.Features.DebugMode) {
                foreach (var (node, _) in nodePositions)
                    DrawDebugging(node);
            } else {
                // 1. Lines
                foreach (var (node, rect) in nodePositions)
                    DrawNodeLines(node, rect);
                // 2. Node fills
                foreach (var (node, rect) in nodePositions) {
                    try { DrawMapNode(node, rect); }
                    catch (Exception e) { LogError("Error drawing node fill: " + e.Message); }
                }
                // 3. Rings
                foreach (var (node, rect) in nodePositions)
                    DrawNodeRings(node, rect);
                // 3b. Favorite star markers (above rings, below labels)
                foreach (var (node, rect) in nodePositions)
                    DrawFavoriteIndicator(node, rect);
                // 3c. Special map markers (icon above the node instead of a covering fill)
                foreach (var (node, rect) in nodePositions)
                    DrawSpecialIndicator(node, rect);
                // 3d. Atlas-point markers (small silver star just above the node)
                foreach (var (node, rect) in nodePositions)
                    DrawAtlasPointIndicator(node, rect);
                // 3e. Atlas-quest markers (small golden exclamation above the node)
                foreach (var (node, rect) in nodePositions)
                    DrawAtlasQuestIndicator(node, rect);
                // 4. Labels
                foreach (var (node, rect) in nodePositions)
                    DrawNodeLabels(node, rect);
            }
        }

        try {
            List<string> waypointNames = SnapshotMapTypes()
                .Where(x => x.Value.DrawLine)
                .Select(x => x.Value.Name)
                .ToList();
            if (Settings.Features.DrawLines && waypointNames.Count > 0) {
                List<Node> waypointNodes = SnapshotMapCacheValues()
                    .Where(x => !x.IsVisited || x.IsAttempted)
                    .Where(x => waypointNames.Contains(x.Name))
                    .Where(x => !Settings.Features.WaypointsUseAtlasRange ||
                        TryGetNodeRect(x, out RectangleF rect) &&
                        Vector2.Distance(screenCenter, rect.Center) <= (Settings.Features.AtlasRange ?? 2000))
                    .ToList();
                
                waypointNodes.ForEach(DrawWaypointLine);
            }
        } catch (Exception e) {
            LogError("Error drawing waypoint lines: " + e.Message + "\n" + e.StackTrace);
        }

        try {
            foreach (var (k,waypoint) in Settings.Waypoints.Waypoints) {
                DrawWaypoint(waypoint);
                DrawWaypointArrow(waypoint);
            }
        }
        catch (Exception e) {
            LogError("Error drawing waypoints: " + e.Message + "\n" + e.StackTrace);
        }

    }
    #endregion

    #region Keybinds & Events

    ///MARK: SubscribeToEvents
    /// <summary>
    /// Subscribes to events that trigger a refresh of the map cache.
    /// </summary>
    private void SubscribeToEvents() {
        try {
            Settings.MapTypes.Maps ??= [];
            Settings.Biomes.Biomes ??= [];
            Settings.MapContent.ContentTypes ??= [];
            Settings.MapMods.MapModTypes ??= [];

            Settings.MapTypes.Maps.CollectionChanged += (_, _) => { weightsDirty = true; };
            Settings.MapTypes.Maps.PropertyChanged += (_, _) => { weightsDirty = true; };
            Settings.Biomes.Biomes.PropertyChanged += (_, _) => { weightsDirty = true; };
            Settings.Biomes.Biomes.CollectionChanged += (_, _) => { weightsDirty = true; };
            Settings.MapContent.ContentTypes.CollectionChanged += (_, _) => { weightsDirty = true; };
            Settings.MapContent.ContentTypes.PropertyChanged += (_, _) => { weightsDirty = true; };
            Settings.MapMods.MapModTypes.CollectionChanged += (_, _) => { weightsDirty = true; };
            Settings.MapMods.MapModTypes.PropertyChanged += (_, _) => { weightsDirty = true; };
        } catch (Exception e) {
            LogError("Error subscribing to events: " + e.Message);
        }
    }

    ///MARK: RegisterHotkeys
    /// <summary>
    /// Registers the hotkeys defined in the settings.
    /// </summary>
    private void RegisterHotkeys() {
        RegisterHotkey(Settings.Keybinds.RefreshMapCacheHotkey);
        RegisterHotkey(Settings.Keybinds.DebugKey);
        RegisterHotkey(Settings.Keybinds.ToggleDebugModeHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleWaypointPanelHotkey);
        RegisterHotkey(Settings.Keybinds.AddWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.QuickEditNodeHotkey);
        RegisterHotkey(Settings.Keybinds.DebugNodeHotkey);
        RegisterHotkey(Settings.Keybinds.DeleteWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.ShowTowerRangeHotkey);
        RegisterHotkey(Settings.Keybinds.UpdateMapsKey);
        RegisterHotkey(Settings.Keybinds.UpdateContentKey);
        RegisterHotkey(Settings.Keybinds.UpdateBiomesKey);
        RegisterHotkey(Settings.Keybinds.ToggleLockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleUnlockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleVisitedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleHiddenNodesHotkey);
        RegisterHotkey(Settings.Keybinds.AtlasOffsetLeftHotkey);
        RegisterHotkey(Settings.Keybinds.AtlasOffsetRightHotkey);
        RegisterHotkey(Settings.Keybinds.AtlasOffsetUpHotkey);
        RegisterHotkey(Settings.Keybinds.AtlasOffsetDownHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleWaypointsHotkey);
    }
    
    private static void RegisterHotkey(HotkeyNode hotkey)
    {
        Input.RegisterKey(hotkey);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey); };
    }
    private void CheckKeybinds() {
        // Defensive null check - AtlasPanel can be null during scene transitions
        if (AtlasPanel == null || !AtlasPanel.IsVisible)
            return;

        HandleAtlasOffsetHotkeys();

        if (Settings.Keybinds.RefreshMapCacheHotkey.PressedOnce()) {
            RequestMapCacheRefresh(clearCache: true);
        }

        if (Settings.Keybinds.DebugKey.PressedOnce())
            DoDebugging();

        if (Settings.Keybinds.ToggleDebugModeHotkey.PressedOnce())
            Settings.Features.DebugMode.Value = !Settings.Features.DebugMode.Value;

        if (Settings.Keybinds.UpdateMapsKey.PressedOnce())
            UpdateMapData();

        if (Settings.Keybinds.UpdateContentKey.PressedOnce())
            UpdateContentData();

        if (Settings.Keybinds.UpdateBiomesKey.PressedOnce())
            UpdateBiomeData();

        if (Settings.Keybinds.ToggleWaypointPanelHotkey.PressedOnce()) {  
            WaypointPanelIsOpen = !WaypointPanelIsOpen;
        }

        if (Settings.Keybinds.AddWaypointHotkey.PressedOnce())
            AddWaypoint(GetClosestNodeToCursor());

        if (Settings.Keybinds.QuickEditNodeHotkey.PressedOnce()) {
            var editNode = GetClosestNodeToCursor();
            if (editNode != null) { quickEditNode = editNode; quickEditOpen = true; }
        }

        if (Settings.Keybinds.DebugNodeHotkey.PressedOnce()) {
            var dbgNode = GetClosestNodeToCursor();
            if (dbgNode != null) { debugNode = dbgNode; debugNodeOpen = true; }
        }

        if (Settings.Keybinds.DeleteWaypointHotkey.PressedOnce())        
            RemoveWaypoint(GetClosestNodeToCursor());

        if (Settings.Keybinds.ToggleLockedNodesHotkey.PressedOnce())        
            Settings.Features.ProcessLockedNodes.Value = !Settings.Features.ProcessLockedNodes.Value;
        
        if (Settings.Keybinds.ToggleUnlockedNodesHotkey.PressedOnce())        
            Settings.Features.ProcessUnlockedNodes.Value = !Settings.Features.ProcessUnlockedNodes.Value;

        if (Settings.Keybinds.ToggleVisitedNodesHotkey.PressedOnce())
            Settings.Features.ProcessVisitedNodes.Value = !Settings.Features.ProcessVisitedNodes.Value;

        if (Settings.Keybinds.ToggleHiddenNodesHotkey.PressedOnce())
            Settings.Features.ProcessHiddenNodes.Value = !Settings.Features.ProcessHiddenNodes.Value;

        if (Settings.Keybinds.ToggleWaypointsHotkey.PressedOnce()) {
            bool show = !Settings.Waypoints.ShowWaypoints;
            Settings.Waypoints.ShowWaypoints = show;
            Settings.Waypoints.ShowWaypointArrows = show;
        }

        if (Settings.Keybinds.ShowTowerRangeHotkey.PressedOnce()) {
            var closestNode = GetClosestNodeToCursor();
            if (closestNode == null)
                return;

            TryGetCachedNode(closestNode.Coordinates, out Node node);
            if (node != null) {
                SnapshotMapCacheValues()
                    .Where(x => x.DrawTowers && x.Address != node.Address)
                    .ToList()
                    .ForEach(x => x.DrawTowers = false);
                node.DrawTowers = !node.DrawTowers;
            }

        }

    }

    private void HandleAtlasOffsetHotkeys()
    {
        if (!Settings.Features.EnableAtlasOffsetCorrection.Value)
            return;

        int step = Settings.Features.AtlasOffsetHotkeyStep.Value;

        if (Settings.Keybinds.AtlasOffsetLeftHotkey.PressedOnce())
            Settings.Features.AtlasOffsetX.Value = Math.Clamp(Settings.Features.AtlasOffsetX.Value - step, -500, 500);

        if (Settings.Keybinds.AtlasOffsetRightHotkey.PressedOnce())
            Settings.Features.AtlasOffsetX.Value = Math.Clamp(Settings.Features.AtlasOffsetX.Value + step, -500, 500);

        if (Settings.Keybinds.AtlasOffsetUpHotkey.PressedOnce())
            Settings.Features.AtlasOffsetY.Value = Math.Clamp(Settings.Features.AtlasOffsetY.Value - step, -500, 500);

        if (Settings.Keybinds.AtlasOffsetDownHotkey.PressedOnce())
            Settings.Features.AtlasOffsetY.Value = Math.Clamp(Settings.Features.AtlasOffsetY.Value + step, -500, 500);
    }

    #endregion

    #region Load Defaults
    private void LoadDefaultMods() {
        try {
            if (Settings.MapMods.MapModTypes == null)
                Settings.MapMods.MapModTypes = new ObservableDictionary<string, Mod>();

            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultModsPath));
            var mods = JsonSerializer.Deserialize<Dictionary<string, Mod>>(jsonFile);
            if (mods == null)
                return;

            foreach (var mod in mods.OrderBy(x => x.Value.Name))
                Settings.MapMods.MapModTypes.TryAdd(mod.Key, mod.Value);

            LogMessage("Loaded Mods");
        } catch (Exception e) {
            LogError("Error loading default mod: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void LoadDefaultBiomes() {
        try {
            Settings.Biomes.Biomes ??= [];

            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultBiomesPath));
            var biomes = JsonSerializer.Deserialize<Dictionary<string, Biome>>(jsonFile);
            if (biomes == null)
                return;

            foreach (var biome in biomes.Where(x => x.Value.Name != "").OrderBy(x => x.Value.Name))
                Settings.Biomes.Biomes.TryAdd(biome.Key, biome.Value);  

            LogMessage("Loaded Biomes");
        } catch (Exception e) {
            LogError("Error loading default biomes: " + e.Message + "\n" + e.StackTrace);
        }
            
    }

    private void LoadDefaultContentTypes() {
        try {
            Settings.MapContent.ContentTypes ??= [];

            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultContentPath));
            var contentTypes = JsonSerializer.Deserialize<Dictionary<string, Content>>(jsonFile);
            if (contentTypes == null)
                return;

            foreach (var content in contentTypes.OrderBy(x => x.Value.Name))
                Settings.MapContent.ContentTypes.TryAdd(content.Key, content.Value);   

            LogMessage("Loaded Content Types");
        } catch (Exception e) {
            LogError("Error loading default content types: " + e.Message + "\n" + e.StackTrace);
        }

    }
    
    public void LoadDefaultMaps()
    {
        try {
            Settings.MapTypes.Maps ??= [];

            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultMapsPath));
            var maps = JsonSerializer.Deserialize<Dictionary<string, Map>>(jsonFile);
            if (maps == null)
                return;

            foreach (var (key,map) in maps.OrderBy(x => x.Value.Name)) {

                // Update legacy map settings
                if(Settings.MapTypes.Maps.TryGetValue(map.Name.Replace(" ", ""), out Map existingMap) && existingMap.IDs.Length == 0) {
                    Settings.MapTypes.Maps.Remove(existingMap.Name.Replace(" ",""));
                    existingMap.ID = key;
                    MergeDefaultMapData(existingMap, map, key);
                    Settings.MapTypes.Maps.TryAdd(key, existingMap);                
                } else if (Settings.MapTypes.Maps.TryGetValue(key, out existingMap)) {
                    MergeDefaultMapData(existingMap, map, key);
                } else {
                    // add new map
                    Settings.MapTypes.Maps.TryAdd(key, map);
                }
            }

            MergeDuplicateMapsByName();
        } catch (Exception e) {
            LogError("Error loading default maps: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private static void MergeDefaultMapData(Map existingMap, Map defaultMap, string defaultKey)
    {
        if (existingMap == null || defaultMap == null)
            return;

        if (string.IsNullOrWhiteSpace(existingMap.Name) && !string.IsNullOrWhiteSpace(defaultMap.Name))
            existingMap.Name = defaultMap.Name;

        if (string.IsNullOrWhiteSpace(existingMap.ID))
            existingMap.ID = defaultKey;

        existingMap.IDs = MergeDistinctNonEmpty(existingMap.IDs, defaultMap.IDs);

        if (string.IsNullOrWhiteSpace(existingMap.ShortestId) && !string.IsNullOrWhiteSpace(defaultMap.ShortestId))
            existingMap.ShortestId = defaultMap.ShortestId;

        existingMap.Biomes = MergeDistinctNonEmpty(existingMap.Biomes, defaultMap.Biomes);
    }

    private static string[] MergeDistinctNonEmpty(string[] existingValues, string[] defaultValues)
    {
        List<string> merged = [];

        AddDistinctNonEmpty(merged, existingValues);
        AddDistinctNonEmpty(merged, defaultValues);

        return [.. merged];
    }

    private static void AddDistinctNonEmpty(List<string> values, string[] source)
    {
        if (source == null)
            return;

        foreach (var value in source)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            string trimmed = value.Trim();
            if (!values.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)))
                values.Add(trimmed);
        }
    }

    // Collapses map-type entries that share a display Name into a single entry, merging their ids and
    // removing the redundant rows. Different ids can share a Name (e.g. the "Precursor Tower" towers),
    // and json seeding can leave several such rows; node matching uses MatchID(IDs), so the union of ids
    // on the kept entry preserves matching.
    private void MergeDuplicateMapsByName() {
        var dupeGroups = Settings.MapTypes.Maps
            .Where(kv => kv.Value != null && !string.IsNullOrWhiteSpace(kv.Value.Name))
            .GroupBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in dupeGroups) {
            var keep = group.First().Value;
            keep.IDs = [.. group
                .SelectMany(kv => kv.Value.IDs ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (string.IsNullOrEmpty(keep.ShortestId))
                keep.ShortestId = keep.IDs.OrderBy(x => x.Length).FirstOrDefault();

            foreach (var dup in group.Skip(1))
                Settings.MapTypes.Maps.Remove(dup.Key);
        }
    }

    #endregion

    #region Weight Import/Export
    // The host is a DirectX overlay (ClickableTransparentOverlay), not a WinForms app, so a WinForms
    // ShowDialog joined on the render thread freezes/faults the overlay. Instead we open a native
    // comdlg32 dialog on a short-lived background STA thread (no join, render keeps running), stash the
    // chosen path in a volatile field, and let ProcessPendingWeightFile (called from Render) do the
    // actual read/write on the plugin thread.
    private volatile string pendingExportPath;
    private volatile string pendingImportPath;
    private volatile string pendingExportSettingsPath;
    private volatile string pendingImportSettingsPath;
    private volatile bool weightDialogBusy;

    // Called from a button in settings. Launches the Save dialog asynchronously.
    public void ExportWeights() => OpenWeightFileDialogAsync(save: true);

    // Called from a button in settings. Launches the Open dialog asynchronously.
    public void ImportWeights() => OpenWeightFileDialogAsync(save: false);

    // Called from a button in settings. Save/load the entire settings object (all categories).
    public void ExportSettings() => OpenSettingsFileDialogAsync(save: true);
    public void ImportSettings() => OpenSettingsFileDialogAsync(save: false);

    // Applies any path the dialog thread produced. Runs on the plugin thread (from Render) so all
    // settings access stays on the same thread the UI uses.
    public void ProcessPendingWeightFile() {
        var export = pendingExportPath;
        if (export != null) {
            pendingExportPath = null;
            WriteWeights(export);
        }

        var import = pendingImportPath;
        if (import != null) {
            pendingImportPath = null;
            ReadWeights(import);
        }

        var exportSettings = pendingExportSettingsPath;
        if (exportSettings != null) {
            pendingExportSettingsPath = null;
            WriteSettings(exportSettings);
        }

        var importSettings = pendingImportSettingsPath;
        if (importSettings != null) {
            pendingImportSettingsPath = null;
            ReadSettings(importSettings);
        }
    }

    // Exports every weight (maps, content, biomes, mods), keyed by the settings dictionary key so
    // ImportWeights can match them back. Only weights are saved.
    private void WriteWeights(string path) {
        try {
            var data = new WeightExport {
                Maps = Settings.MapTypes.Maps.ToDictionary(x => x.Key, x => x.Value.Weight),
                Content = Settings.MapContent.ContentTypes.ToDictionary(x => x.Key, x => x.Value.Weight),
                Biomes = Settings.Biomes.Biomes.ToDictionary(x => x.Key, x => x.Value.Weight),
                Mods = Settings.MapMods.MapModTypes?.ToDictionary(x => x.Key, x => x.Value.Weight) ?? [],
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            LogMessage($"Exported weights to {path} ({data.Maps.Count} maps, {data.Content.Count} content, {data.Biomes.Count} biomes, {data.Mods.Count} mods)");
        } catch (Exception e) {
            LogError("Error exporting weights: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Imports weights from a JSON file written by WriteWeights, applying them by key. For maps, falls
    // back to matching by Name (spaces ignored) so presets survive id/key churn between leagues.
    // Setting Weight via the property fires PropertyChanged, which marks weightsDirty for the recalc.
    private void ReadWeights(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing weights: file not found ({path}).");
                return;
            }

            var data = JsonSerializer.Deserialize<WeightExport>(File.ReadAllText(path));
            if (data == null) {
                LogError("Error importing weights: file was empty or invalid.");
                return;
            }

            int maps = 0, content = 0, biomes = 0, mods = 0;

            foreach (var (key, weight) in data.Maps ?? []) {
                if (Settings.MapTypes.Maps.TryGetValue(key, out var map)) {
                    map.Weight = weight; maps++;
                } else {
                    // Fallback: match by Name (ignoring spaces) for presets made on a different install.
                    var byName = Settings.MapTypes.Maps.Values.FirstOrDefault(m =>
                        m.Name == key || m.Name?.Replace(" ", "") == key);
                    if (byName != null) { byName.Weight = weight; maps++; }
                }
            }

            foreach (var (key, weight) in data.Content ?? [])
                if (Settings.MapContent.ContentTypes.TryGetValue(key, out var c)) { c.Weight = weight; content++; }

            foreach (var (key, weight) in data.Biomes ?? [])
                if (Settings.Biomes.Biomes.TryGetValue(key, out var b)) { b.Weight = weight; biomes++; }

            if (Settings.MapMods.MapModTypes != null)
                foreach (var (key, weight) in data.Mods ?? [])
                    if (Settings.MapMods.MapModTypes.TryGetValue(key, out var m)) { m.Weight = weight; mods++; }

            weightsDirty = true;
            LogMessage($"Imported weights from {path} ({maps} maps, {content} content, {biomes} biomes, {mods} mods)");
        } catch (Exception e) {
            LogError("Error importing weights: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Serializes the entire settings object with Newtonsoft (the same serializer ExileCore2 uses to
    // persist settings) so every category round-trips. CustomNode/EmptyNode UI fields are [JsonIgnore]
    // and skipped automatically.
    private void WriteSettings(string path) {
        try {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(Settings, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
            LogMessage($"Exported settings to {path}");
        } catch (Exception e) {
            LogError("Error exporting settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Imports a settings file written by WriteSettings. Uses PopulateObject so the live Settings instance
    // (held by ExileCore2) is mutated in place rather than replaced - existing object/dictionary
    // references stay valid, and dictionary entries merge by key. Forces a full recalc + cache refresh.
    private void ReadSettings(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing settings: file not found ({path}).");
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) {
                LogError("Error importing settings: file was empty or invalid.");
                return;
            }

            Newtonsoft.Json.JsonConvert.PopulateObject(json, Settings);

            weightsDirty = true;
            refreshCache = true;
            LogMessage($"Imported settings from {path}");
        } catch (Exception e) {
            LogError("Error importing settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Opens a native common dialog on a background STA thread. Does NOT block the render thread; the
    // selected path is picked up by ProcessPendingWeightFile on a later frame.
    private void OpenWeightFileDialogAsync(bool save) {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string initialDir = DirectoryFullName;
                string chosen = save
                    ? NativeFileDialog.ShowSave("Export Weights", "exilemaps_weights.json", initialDir)
                    : NativeFileDialog.ShowOpen("Import Weights", initialDir);

                if (!string.IsNullOrEmpty(chosen)) {
                    if (save) pendingExportPath = chosen;
                    else pendingImportPath = chosen;
                }
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    // Same async-dialog pattern as OpenWeightFileDialogAsync, but for the full settings file.
    private void OpenSettingsFileDialogAsync(bool save) {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string initialDir = DirectoryFullName;
                string chosen = save
                    ? NativeFileDialog.ShowSave("Export Settings", "exilemaps_settings.json", initialDir)
                    : NativeFileDialog.ShowOpen("Import Settings", initialDir);

                if (!string.IsNullOrEmpty(chosen)) {
                    if (save) pendingExportSettingsPath = chosen;
                    else pendingImportSettingsPath = chosen;
                }
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }
    #endregion

    #region Map Processing
    ///MARK: RenderNode
    /// <summary>
    /// Renders a map node on the atlas panel.
    /// </summary>
    /// <param name="cachedNode"></param>
    // Node rendering runs as separate passes over the whole node set, one per z-layer, so the draw
    // order is globally fixed instead of per-node. Render calls these in order:
    //   1. DrawNodeLines  (connections + tower range)
    //   2. DrawMapNode    (the node fill circle)
    //   3. DrawNodeRings  (content rings)
    //   4. DrawNodeLabels (map name, weight, tower mod boxes)
    // Then waypoint lines/icons/arrows draw on top (see Render). This guarantees a later node's line
    // never lands over an earlier node's circle/ring/label, which was the source of the flicker.
    private void DrawNodeLines(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawConnections(cachedNode, nodeCurrentPosition);
            DrawTowerRange(cachedNode);
        } catch (Exception e) {
            LogError("Error drawing node lines: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawNodeRings(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawContentRings(cachedNode, nodeCurrentPosition);
        } catch (Exception e) {
            LogError("Error drawing node rings: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawNodeLabels(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawTowerMods(cachedNode, nodeCurrentPosition);
            DrawMapName(cachedNode, nodeCurrentPosition);
            DrawWeight(cachedNode, nodeCurrentPosition);
        } catch (Exception e) {
            LogError("Error drawing node labels: " + e.Message + " - " + e.StackTrace);
        }
    }
    #endregion

    #region Debugging
    private void DoDebugging() {
        var closestNode = GetClosestNodeToCursor();
        if (closestNode == null)
            return;

        if (TryGetCachedNode(closestNode.Coordinates, out Node cachedNode))
            LogMessage(BuildNodeDebugText(cachedNode));

    }

    private void DrawDebugging(Node cachedNode) {
        if (!TryGetNodeRect(cachedNode, out RectangleF nodeRect))
            return;

        using (Graphics.SetTextScale(Settings.MapMods.MapModScale))
            DrawCenteredTextWithBackground(BuildNodeDebugText(cachedNode), nodeRect.Center, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

    }

    private string BuildNodeDebugText(Node cachedNode)
    {
        StringBuilder sb = new(cachedNode.DebugText());
        AppendRawContentIdentityDebug(cachedNode, sb);
        AppendRawContentTextureDebug(cachedNode, sb);
        return sb.ToString();
    }

    private static void AppendRawContentIdentityDebug(Node cachedNode, StringBuilder sb)
    {
        try
        {
            var contentIdentity = cachedNode.MapNode?.Element?.ContentIdentity;
            if (contentIdentity == null)
            {
                sb.AppendLine("RawContentIdentity: <null>");
                return;
            }

            var ids = contentIdentity
                .Select(x => x?.Id)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            sb.AppendLine(ids.Count == 0
                ? "RawContentIdentity: <empty>"
                : $"RawContentIdentity: {string.Join(", ", ids)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"RawContentIdentity: <error: {ex.Message}>");
        }
    }

    private static void AppendRawContentTextureDebug(Node cachedNode, StringBuilder sb)
    {
        try
        {
            var first = cachedNode.MapNode?.Element?.GetChildAtIndex(0);
            var second = first?.GetChildAtIndex(0);
            var children = second?.Children;
            if (children == null)
            {
                sb.AppendLine("RawContentTextures: <null>");
                return;
            }

            var textureNames = children
                .Select(x => x?.TextureName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            sb.AppendLine(textureNames.Count == 0
                ? "RawContentTextures: <empty>"
                : $"RawContentTextures: {string.Join(", ", textureNames)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"RawContentTextures: <error: {ex.Message}>");
        }
    }

    // Map types are scraped from the game's endgame map file list (GameController.Files.EndgameMaps)
    // rather than the live atlas, so the full set is known without visiting/seeing nodes. The json seed
    // (LoadDefaultMaps) still supplies curated Weight/Color/Highlight/Biomes for existing types; this
    // only adds missing types and refreshes their IDs. Returns false if the file list isn't loaded yet
    // (e.g. called before the game finishes loading files) so the caller can retry.
    private bool UpdateMapData(bool writeToFile = true) {
      try {
        // Collapse any entries that share a display Name into a single row (union of ids). Different map
        // ids can legitimately share a Name (e.g. the various "Precursor Tower" towers); we show one row
        // per Name. Node matching falls back to MatchID(IDs), so every merged id still resolves.
        MergeDuplicateMapsByName();

        var endgameMaps = GameController.Files.EndgameMaps?.EntriesList;
        if (endgameMaps == null || endgameMaps.Count == 0)
            return false;

        int added = 0, updated = 0;
        foreach (var endgameMap in endgameMaps) {
            var area = endgameMap?.Area;
            var id = area?.Id;
            var name = area?.Name;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                continue;

            // Skip dev/unused placeholder maps.
            if (id.Contains("DNT-UNUSED") || name.Contains("DNT-UNUSED"))
                continue;

            // Existing map-type matching strips the _NoBoss suffix to form the key (see CacheNewMapNode).
            var shortID = id.Replace("_NoBoss", "");

            // Merge identically-named maps into a single entry, collecting all their ids. The json seed
            // and prior scrapes already store Name, so a Name match covers both. Node matching falls back
            // to MatchID(IDs), so every merged id still resolves even though the entry has one key.
            var mapType = Settings.MapTypes.Maps.Values.FirstOrDefault(m => m.Name == name);
            if (mapType != null) {
                if (!mapType.IDs.Contains(id))
                    mapType.IDs = [.. mapType.IDs, id];
                if (string.IsNullOrEmpty(mapType.ShortestId))
                    mapType.ShortestId = shortID;
                updated++;
            } else if (Settings.MapTypes.Maps.TryGetValue(name.Replace(" ", ""), out mapType) || Settings.MapTypes.Maps.TryGetValue(shortID, out mapType)) {
                // Migrate any legacy name-keyed entry to the id key and make sure this id is recorded.
                Settings.MapTypes.Maps.Remove(name.Replace(" ", ""));
                mapType.Name = name;
                mapType.ShortestId = shortID;
                if (!mapType.IDs.Contains(id))
                    mapType.IDs = [.. mapType.IDs, id];
                Settings.MapTypes.Maps.TryAdd(shortID, mapType);
                updated++;
            } else {
                Settings.MapTypes.Maps.TryAdd(shortID, new Map {
                    Name = name,
                    IDs = [id],
                    ShortestId = shortID });
                added++;
            }
        }

        if (writeToFile) {
            var json = JsonSerializer.Serialize(Settings.MapTypes.Maps, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultMapsPath), json);
        }

        LogMessage($"Updated Map Data from game files ({added} new, {updated} updated)");
        return true;
      } catch (Exception e) {
        LogError("Error updating map data from game files: " + e.Message);
        return false;
      }
    }

    // Scans the whole atlas for content types (AtlasPanelNode.ContentIdentity[].Id) and adds any not
    // already in the Content data source, then writes content.json. Content types come from the game's
    // EndgameMapContent file list (Name + Id), with icons from EndgameMapContentVisualIdentity (matched
    // by Id). New entries are keyed by Id; legacy entries keyed by the spaced Name are migrated to the
    // Id key (deduped via space-stripped compare). Returns false if the file list isn't loaded yet.
    private bool UpdateContentData(bool writeToFile = true) {
      try {
        var contentEntries = GameController.Files.EndgameMapContent?.EntriesList;
        if (contentEntries == null || contentEntries.Count == 0)
            return false;

        // Build Id -> AtlasIcon lookup from the visual identity file list.
        var iconLookup = new Dictionary<string, string>();
        var visuals = GameController.Files.EndgameMapContentVisualIdentity?.EntriesList;
        if (visuals != null)
            foreach (var vi in visuals) {
                var vid = vi?.Id;
                if (!string.IsNullOrEmpty(vid))
                    iconLookup[vid] = vi.AtlasIcon?.ToString();
            }

        int added = 0, updated = 0;
        foreach (var entry in contentEntries) {
            var id = entry?.Id;
            var name = entry?.Name;
            if (string.IsNullOrEmpty(id))
                continue;
            if (string.IsNullOrEmpty(name))
                name = id;

            iconLookup.TryGetValue(id, out var icon);

            // Find an existing entry under the Id key or a legacy spaced-Name key.
            var existingKey = Settings.MapContent.ContentTypes.Keys.FirstOrDefault(k => k == id || k.Replace(" ", "") == id);
            if (existingKey != null) {
                var existing = Settings.MapContent.ContentTypes[existingKey];
                existing.Name = name;
                if (!string.IsNullOrEmpty(icon))
                    existing.AtlasIcon = icon;
                if (existingKey != id) {
                    Settings.MapContent.ContentTypes.Remove(existingKey);
                    Settings.MapContent.ContentTypes.TryAdd(id, existing);
                }
                updated++;
            } else if (Settings.MapContent.ContentTypes.TryAdd(id, new Content { Name = name, Weight = 0.0f, AtlasIcon = icon })) {
                added++;
                LogMessage($"Added Content Type: {id}");
            }
        }

        if (writeToFile) {
            var json = JsonSerializer.Serialize(Settings.MapContent.ContentTypes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultContentPath), json);
        }

        LogMessage($"Updated Content Data from game files ({added} new, {updated} updated)");
        return true;
      } catch (Exception e) {
        LogError("Error updating content data from game files: " + e.Message);
        return false;
      }
    }

    // Biomes come from the game's EndgameMapBiomes file list (Id only). Keyed by Id, matching the
    // existing biomes.json convention. Returns false if the file list isn't loaded yet.
    private bool UpdateBiomeData(bool writeToFile = true) {
      try {
        var biomeEntries = GameController.Files.EndgameMapBiomes?.EntriesList;
        if (biomeEntries == null || biomeEntries.Count == 0)
            return false;

        int added = 0;
        foreach (var entry in biomeEntries) {
            var id = entry?.Id;
            if (string.IsNullOrEmpty(id))
                continue;

            if (Settings.Biomes.Biomes.ContainsKey(id))
                continue;

            if (Settings.Biomes.Biomes.TryAdd(id, new Biome { Name = id, Weight = 0.0f })) {
                added++;
                LogMessage($"Added Biome: {id}");
            }
        }

        if (writeToFile) {
            var json = JsonSerializer.Serialize(Settings.Biomes.Biomes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultBiomesPath), json);
        }

        LogMessage($"Updated Biome Data from game files ({added} new)");
        return true;
      } catch (Exception e) {
        LogError("Error updating biome data from game files: " + e.Message);
        return false;
      }
    }

    #endregion

    private List<Node> FindShortestPath(Node start, Node end)
    {
        if (start == null || end == null)
            return null;

        var visited = new HashSet<Vector2i>();
        var queue = new Queue<(Node node, List<Node> path)>();

        queue.Enqueue((start, new List<Node> { start }));
        visited.Add(start.Coordinates);

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();

            if (current.Coordinates == end.Coordinates)
                return path;

            foreach (var neighbor in current.Neighbors.Values)
            {
                if (neighbor != null && !visited.Contains(neighbor.Coordinates))
                {
                    var newPath = new List<Node>(path) { neighbor };
                    queue.Enqueue((neighbor, newPath));
                    visited.Add(neighbor.Coordinates);
                }
            }
        }

        return null; // No path found
    }

    // Multi-source BFS seeded with every visited node at distance 0. Each unvisited node's value is
    // the minimum number of steps to reach the explored region (same semantics as a waypoint's
    // PathFromStart step count, but computed for the whole graph in a single O(V+E) pass so the
    // atlas list can sort/display steps without a per-node BFS). Unreachable nodes are omitted.
    private Dictionary<Vector2i, int> ComputeStepCounts()
    {
        var stepCounts = new Dictionary<Vector2i, int>();
        var queue = new Queue<Node>();

        lock (mapCacheLock)
        {
            foreach (var node in mapCache.Values.Where(x => x.IsVisited))
            {
                stepCounts[node.Coordinates] = 0;
                queue.Enqueue(node);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int nextDist = stepCounts[current.Coordinates] + 1;
                foreach (var neighbor in current.Neighbors.Values)
                {
                    if (neighbor == null || stepCounts.ContainsKey(neighbor.Coordinates))
                        continue;
                    stepCounts[neighbor.Coordinates] = nextDist;
                    queue.Enqueue(neighbor);
                }
            }
        }

        return stepCounts;
    }

    private Node FindClosestVisitedNode(Node waypointNode)
    {
        if (waypointNode == null)
            return null;

        if (!TryGetNodeRect(waypointNode, out RectangleF waypointRect))
            return null;

        return SnapshotMapCacheValues()
            .Where(x => x.IsVisited)
            .Select(x => new { Node = x, HasRect = TryGetNodeRect(x, out RectangleF rect), Rect = rect })
            .Where(x => x.HasRect)
            .OrderBy(x => Vector2.Distance(waypointRect.Center, x.Rect.Center))
            .Select(x => x.Node)
            .FirstOrDefault();
    }
    private Node GetClosestNodeToCursor() {
        Vector2 cursorPosition = GameController.Game.IngameState.UIHoverElement.GetClientRect().Center;
        var closestNode = AtlasPanel.Descriptions
            .OrderBy(x => Vector2.Distance(cursorPosition, ApplyAtlasScreenOffset(x.Element.GetClientRect()).Center))
            .FirstOrDefault();

        if (closestNode == null)
            return null;

        if (TryGetCachedNode(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else
            return null;
    }

    private Node GetClosestNodeToCenterScreen() {
        var closestNode = AtlasPanel.Descriptions
            .OrderBy(x => Vector2.Distance(screenCenter, ApplyAtlasScreenOffset(x.Element.GetClientRect()).Center))
            .FirstOrDefault();
        if (closestNode == null)
            return null;
        if (TryGetCachedNode(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else 
            return null;
    }

    #region Map Cache

    private void RequestMapCacheRefresh(bool clearCache = false)
    {
        if (clearCache) {
            clearCacheOnNextRefresh = true;
            ClearCachedAtlasState();
        }

        refreshCache = true;
        TryStartRefreshCacheJob();
    }

    private void TryStartRefreshCacheJob()
    {
        if (refreshingCache)
            return;

        if (refreshCacheJob is { IsCompleted: false })
            return;

        if (AtlasPanel == null || !AtlasPanel.IsVisible || atlasWarmupTicks < 3)
            return;

        bool shouldRunNow = refreshCache
            && (DateTime.Now.Subtract(lastRefresh).TotalSeconds > Settings.Graphics.MapCacheRefreshRate
                || MapCacheCount() == 0);

        if (!shouldRunNow)
            return;

        refreshCacheJob = new Job($"{nameof(ExileMaps)}RefreshCache", () =>
        {
            try
            {
                bool clearCache = clearCacheOnNextRefresh;
                clearCacheOnNextRefresh = false;
                RefreshMapCache(clearCache);
            }
            catch (Exception ex)
            {
                LogError("Error during RefreshMapCache: " + ex.Message + "\n" + ex.StackTrace);
                refreshCache = true;
            }
        });
        refreshCacheJob.Start();
    }

    private int MapCacheCount()
    {
        lock (mapCacheLock)
            return mapCache.Count;
    }

    private void ClearCachedAtlasState()
    {
        lock (mapCacheLock)
        {
            mapCache.Clear();
            selectedNodes = [];
        }
    }

    private List<Node> SnapshotMapCacheValues()
    {
        lock (mapCacheLock)
            return mapCache.Values.ToList();
    }

    private List<KeyValuePair<Vector2i, Node>> SnapshotMapCache()
    {
        lock (mapCacheLock)
            return mapCache.ToList();
    }

    private bool TryGetCachedNode(Vector2i coordinates, out Node node)
    {
        Node? cachedNode;
        lock (mapCacheLock)
            mapCache.TryGetValue(coordinates, out cachedNode);

        if (cachedNode != null)
        {
            node = cachedNode;
            return true;
        }

        node = null!;
        return false;
    }

    private bool TryGetNodeRect(Node? node, out RectangleF rect)
    {
        rect = default;
        try
        {
            if (node?.MapNode?.Element == null)
                return false;

            rect = ApplyAtlasScreenOffset(node.MapNode.Element.GetClientRect());

            return rect.Width > 1 && rect.Height > 1;
        }
        catch
        {
            return false;
        }
    }

    private RectangleF ApplyAtlasScreenOffset(RectangleF rect)
    {
        if (!Settings.Features.EnableAtlasOffsetCorrection.Value)
            return rect;

        float offsetX = Settings.Features.AtlasOffsetX.Value;
        float offsetY = Settings.Features.AtlasOffsetY.Value;

        return new RectangleF(
            rect.X + offsetX,
            rect.Y + offsetY,
            rect.Width,
            rect.Height
        );
    }

    private int SafeAtlasDescriptionCount()
    {
        try
        {
            return AtlasPanel?.Descriptions?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private List<AtlasNodeDescription> SnapshotAtlasDescriptions()
    {
        try
        {
            var descriptions = AtlasPanel?.Descriptions;
            return descriptions == null ? [] : [.. descriptions.Where(x => x != null)];
        }
        catch
        {
            return [];
        }
    }

    private List<KeyValuePair<string, Map>> SnapshotMapTypes()
    {
        try
        {
            lock (mapTypesLock)
                return Settings.MapTypes.Maps.ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private bool TryGetMapType(string key, out Map map)
    {
        lock (mapTypesLock)
            return Settings.MapTypes.Maps.TryGetValue(key, out map);
    }

    private bool TryGetContentType(string key, out Content content)
    {
        lock (mapContentLock)
            return Settings.MapContent.ContentTypes.TryGetValue(key, out content);
    }

    private List<KeyValuePair<string, Content>> SnapshotContentTypes()
    {
        try
        {
            lock (mapContentLock)
                return Settings.MapContent.ContentTypes.ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Atlas nodes can exist before <see cref="AtlasPanelNode.Area"/> is readable; the getter may throw.
    /// </summary>
    private static bool TryGetAtlasNodeArea(AtlasNodeDescription? node, out WorldArea area)
    {
        area = null!;
        if (node?.Element == null)
            return false;

        try
        {
            area = node.Element.Area;
            return area != null;
        }
        catch (NullReferenceException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void RefreshMapCache(bool clearCache = false)
    {
        if (AtlasPanel == null || !AtlasPanel.IsVisible)
            return;

        List<AtlasNodeDescription> atlasNodes = SnapshotAtlasDescriptions();
        if (atlasNodes.Count == 0)
            return;

        refreshingCache = true;
        bool cacheWasCleared = false;
        bool refreshAgain = false;

        if (clearCache) {
            ClearCachedAtlasState();
            cacheWasCleared = true;
        }

        try
        {
            // Snapshot connection points once and build O(1) forward + reverse lookups, instead of
            // scanning AtlasPanel.Points twice per node (previously O(N^2) over the whole atlas, with a
            // fresh game-memory read on every scan).
            var points = AtlasPanel.Points?.ToList() ?? [];
            var forwardPoints = new Dictionary<Vector2i, List<Vector2i>>(points.Count);
            var reverseNeighbors = new Dictionary<Vector2i, List<Vector2i>>(points.Count);
            foreach (var point in points) {
                forwardPoints[point.Source] = point.Targets;

                foreach (var neighbor in point.Targets) {
                    if (neighbor == default)
                        continue;

                    if (!reverseNeighbors.TryGetValue(neighbor, out var sources))
                        reverseNeighbors[neighbor] = sources = [];

                    sources.Add(point.Source);
                }
            }

            var timer = new Stopwatch();
            timer.Start();
            long count = 0;
            foreach (var node in atlasNodes) {
                if (node == null)
                    continue;

                try
                {
                    Node? cachedNode;
                    bool hasCachedNode;
                    lock (mapCacheLock)
                        hasCachedNode = mapCache.TryGetValue(node.Coordinate, out cachedNode);

                    if (hasCachedNode && cachedNode != null)
                        count += RefreshCachedMapNode(node, cachedNode);
                    else
                        count += CacheNewMapNode(node);
                }
                catch (Exception ex)
                {
                    LogError($"Error caching atlas node {node.Address:X}: {ex.Message}");
                }
            }

            List<Node> cachedNodes;
            lock (mapCacheLock)
                cachedNodes = mapCache.Values.ToList();

            foreach (var cachedNodeToConnect in cachedNodes) {
                try
                {
                    CacheMapConnections(cachedNodeToConnect, forwardPoints, reverseNeighbors);
                }
                catch (Exception ex)
                {
                    LogError($"Error caching connections for {cachedNodeToConnect.Coordinates}: {ex.Message}");
                }
            }

            timer.Stop();
            long time = timer.ElapsedMilliseconds;
            float average = count > 0 ? (float)time / count : 0;
            //LogMessage($"Map cache refreshed in {time}ms, {count} nodes processed, average time per node: {average:0.00}ms");

            RecalculateWeights(Settings.Features.RecalculateNodeWeightsOnRefresh);
            SyncFavoriteWaypoints();
            UpdateWaypointPaths();
        }
        catch (Exception ex)
        {
            LogError("Error during RefreshMapCache: " + ex.Message + "\n" + ex.StackTrace);
            if (cacheWasCleared)
                refreshAgain = true;
        }
        finally
        {
            refreshingCache = false;
            refreshCache = refreshAgain;
            clearCacheOnNextRefresh = false;
            lastRefresh = DateTime.Now;
        }
    }

    private void DrawWaypointPath(Waypoint waypoint)
    {
        if (waypoint.PathFromStart == null || waypoint.PathFromStart.Count <= 1)
            return;

        for (int i = 0; i < waypoint.PathFromStart.Count - 1; i++)
        {
            var currentNode = waypoint.PathFromStart[i];
            var nextNode = waypoint.PathFromStart[i + 1];

            if (currentNode == null ||
                nextNode == null ||
                !TryGetNodeRect(currentNode, out RectangleF currentRect) ||
                !TryGetNodeRect(nextNode, out RectangleF nextRect) ||
                !IsOnScreen(currentRect.Center) ||
                !IsOnScreen(nextRect.Center))
                continue;

            Vector2 start = currentRect.Center;
            Vector2 end = nextRect.Center;

            if (!IsLineVisible(start, end))
                continue;

            // Draw path line with a different color/style to distinguish from regular connections
            Graphics.DrawLine(start, end, Settings.Graphics.WaypointLineWidth , Settings.Graphics.PathLineColor);
        }
    }

    private void RecalculateWeights(bool recalculateNodeWeights) {

        // Snapshot under the lock; the background refresh job mutates mapCache concurrently.
        List<Node> cachedNodes;
        lock (mapCacheLock)
            cachedNodes = mapCache.Values.ToList();

        if (recalculateNodeWeights)
            foreach (var node in cachedNodes)
                node.RecalculateWeight();

        bool hasWeights = false;
        float observedMinWeight = float.MaxValue;
        float observedMaxWeight = float.MinValue;

        foreach (var node in cachedNodes) {
            if (node.IsVisited)
                continue;

            hasWeights = true;
            observedMinWeight = Math.Min(observedMinWeight, node.Weight);
            observedMaxWeight = Math.Max(observedMaxWeight, node.Weight);
        }

        if (!hasWeights) {
            minMapWeight = MapWeightSliderMin;
            maxMapWeight = MapWeightSliderMax;
            return;
        }

        minMapWeight = Math.Min(MapWeightSliderMin, observedMinWeight);
        maxMapWeight = Math.Max(MapWeightSliderMax, observedMaxWeight);

        if (maxMapWeight <= minMapWeight)
            maxMapWeight = minMapWeight + 1f;
    }

    private Map ResolveMapType(AtlasNodeDescription node)
    {
        if (!TryGetAtlasNodeArea(node, out WorldArea area))
            return AddUnknownMapType(node, string.Empty, string.Empty);

        string mapId = area.Id?.Trim() ?? string.Empty;
        string shortID = NormalizeMapId(mapId);

        if (!string.IsNullOrEmpty(shortID) && TryGetMapType(shortID, out Map exactMap))
            return exactMap;

        Map? matchedMap = SnapshotMapTypes()
            .Where(x => x.Value.MatchID(mapId) || x.Value.MatchID(shortID))
            .Select(x => x.Value)
            .FirstOrDefault();

        return matchedMap ?? AddUnknownMapType(node, mapId, shortID);
    }

    private Map AddUnknownMapType(AtlasNodeDescription node, string mapId, string shortID)
    {
        string key = !string.IsNullOrWhiteSpace(shortID) ? shortID : $"UnknownMap_{node.Address:X}";

        string mapName = key;
        if (TryGetAtlasNodeArea(node, out WorldArea area) && !string.IsNullOrWhiteSpace(area.Name))
            mapName = area.Name.Trim();

        lock (mapTypesLock)
        {
            if (Settings.MapTypes.Maps.TryGetValue(key, out Map existingMap))
                return existingMap;
        }

        string[] ids = BuildMapIds(mapId, shortID);
        Map unknownMap = new()
        {
            Name = mapName,
            IDs = ids,
            ShortestId = shortID,
            Weight = 1.0f,
            NameColor = Color.White,
            BackgroundColor = Color.FromArgb(220, 0, 0, 0),
            NodeColor = Color.FromArgb(200, 155, 155, 155),
            Highlight = true
        };

        lock (mapTypesLock)
            Settings.MapTypes.Maps.TryAdd(key, unknownMap);

        LogUnknownOnce(loggedUnknownMaps, key, $"Discovered unknown map type: {mapName} ({string.Join(", ", ids)})");

        return TryGetMapType(key, out Map addedMap) ? addedMap : unknownMap;
    }

    private static string NormalizeMapId(string mapId) => (mapId ?? string.Empty).Trim().Replace("_NoBoss", "");

    private static string[] BuildMapIds(string mapId, string shortID)
    {
        List<string> ids = [];

        if (!string.IsNullOrWhiteSpace(mapId))
            ids.Add(mapId.Trim());

        if (!string.IsNullOrWhiteSpace(shortID))
        {
            ids.Add(shortID.Trim());
            ids.Add($"{shortID.Trim()}_NoBoss");
        }

        return [.. ids.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private Content? ResolveContentType(string contentName)
    {
        if (string.IsNullOrWhiteSpace(contentName))
            return null;

        string key = contentName.Trim();

        if (TryGetContentType(key, out Content knownContent))
            return knownContent;

        Content unknownContent = new()
        {
            Name = key,
            Weight = 0.0f,
            Color = UnknownContentColor,
            Highlight = true
        };

        lock (mapContentLock)
            Settings.MapContent.ContentTypes.TryAdd(key, unknownContent);

        LogUnknownOnce(loggedUnknownContentTypes, key, $"Discovered unknown map content type: {key}");

        return TryGetContentType(key, out knownContent) ? knownContent : unknownContent;
    }

    private void AddContentToNode(Node node, string contentName)
    {
        Content? content = ResolveContentType(contentName);
        if (content == null)
            return;

        node.Content.TryAdd(content.Name, content);
    }

    private void LogUnknownOnce(HashSet<string> seen, string key, string message)
    {
        if (!Settings.Features.DebugMode || string.IsNullOrWhiteSpace(key))
            return;

        lock (unknownLogLock)
        {
            if (seen.Add(key))
                LogMessage(message);
        }
    }

    private int CacheNewMapNode(AtlasNodeDescription node)
    {
        if (!TryGetAtlasNodeArea(node, out WorldArea area))
            return 0;

        string mapId = area.Id?.Trim() ?? string.Empty;
        Node newNode = new()
        {
            IsUnlocked = node.Element?.IsUnlocked ?? false,
            IsVisible = node.Element?.IsVisible ?? false,
            IsVisited = node.Element?.IsVisited ?? false,
            IsActive = node.Element?.IsActive ?? false,
            ParentAddress = node.Address,
            Address = node.Element?.Address ?? 0,
            Coordinates = node.Coordinate,
            Name = area.Name ?? "Unknown",
            Id = mapId,
            MapNode = node,
            MapType = ResolveMapType(node)
        };

        // Set Content
        if (!newNode.IsVisited) {
            // Check if the map has content
            try {
                
                AddNodeContentTypesFromTextures(node, newNode);
                AddNodeContentTowers(node, newNode);

                AddNodeContentFromIdentity(node, newNode);
                SetAtlasPassive(node, newNode);

            } catch (Exception e) {
                LogError($"Error getting Content for map type {node.Address.ToString("X")}: " + e.Message);
            }
            
            AddNodeBiomes(newNode);
        }
    
        // Tower tablet mods have been removed from the game, so effect scanning is disabled.
        // Effects stays empty; weight/rendering/waypoint code handle the empty case.
        newNode.RecalculateWeight();

        lock (mapCacheLock)        
            return mapCache.TryAdd(node.Coordinate, newNode) ? 1 : 0;

    }

    private int RefreshCachedMapNode(AtlasNodeDescription node, Node cachedNode)
    {
        if (!TryGetAtlasNodeArea(node, out WorldArea area))
            return 0;

        string mapId = area.Id?.Trim() ?? string.Empty;
        cachedNode.IsUnlocked = node.Element?.IsUnlocked ?? false;
        cachedNode.IsVisible = node.Element?.IsVisible ?? false;
        cachedNode.IsVisited = (node.Element?.IsVisited ?? false) || (!(node.Element?.IsUnlocked ?? true) && (node.Element?.IsVisited ?? false));
        cachedNode.IsActive = node.Element?.IsActive ?? false;
        cachedNode.Address = node.Element?.Address ?? 0;
        cachedNode.ParentAddress = node.Address;     
        cachedNode.MapNode = node;
        cachedNode.Id = mapId;
        cachedNode.Name = area.Name ?? "Unknown";
        cachedNode.MapType = ResolveMapType(node);

        if (cachedNode.IsVisited)
            return 1;

        cachedNode.Content.Clear();

        AddNodeContentTypesFromTextures(node, cachedNode);
        AddNodeContentTowers(node, cachedNode);

        AddNodeContentFromIdentity(node, cachedNode);
        AddNodeBiomes(cachedNode);
        SetAtlasPassive(node, cachedNode);

        // Tower tablet mods have been removed from the game, so effect scanning is disabled.
        cachedNode.Effects.Clear();

        if (Settings.Features.RecalculateNodeWeightsOnRefresh)
            cachedNode.RecalculateWeight();
        return 1;
    } 

    private void AddNodeBiomes(Node node)
    {
        try
        {
            node.Biomes.Clear();

            var biomes = node.MapType.Biomes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var biome in biomes)
                if (Settings.Biomes.Biomes.TryGetValue(biome, out Biome newBiome))
                    node.Biomes.TryAdd(newBiome.Name, newBiome);
        }
        catch (Exception e)
        {
            LogError($"Error getting Biomes for map type {node.Id}: " + e.Message);
        }
    }
    
    private void CacheMapConnections(Node cachedNode,
        Dictionary<Vector2i, List<Vector2i>> forwardPoints,
        Dictionary<Vector2i, List<Vector2i>> reverseNeighbors) {

        if (cachedNode == null)
            return;

        cachedNode.Neighbors ??= [];
        cachedNode.Neighbors.Clear();

        // Forward connections: this node's own neighbor coordinates.
        var hasForwardPoints = forwardPoints.TryGetValue(cachedNode.Coordinates, out var connectionPoints);
        cachedNode.NeighborCoordinates = hasForwardPoints && connectionPoints != null ? connectionPoints : [];

        lock (mapCacheLock) {
            if (hasForwardPoints && connectionPoints != null) {
                foreach (Vector2i vector in connectionPoints) {
                    if (vector == default || vector.Equals(cachedNode.Coordinates))
                        continue;

                    if (mapCache.TryGetValue(vector, out var neighborNode) && neighborNode != null && neighborNode.Coordinates != default)
                        cachedNode.Neighbors[vector] = neighborNode;
                }
            }

            // Reverse connections: other nodes that point at this node.
            if (reverseNeighbors.TryGetValue(cachedNode.Coordinates, out var sources))
                foreach (var source in sources) {
                    if (source == default || source.Equals(cachedNode.Coordinates))
                        continue;

                    if (mapCache.TryGetValue(source, out var neighborNode) && neighborNode != null && neighborNode.Coordinates != default)
                        cachedNode.Neighbors[source] = neighborNode;
                }
        }

    }
    private void AddNodeContentTypesFromTextures(AtlasNodeDescription node, Node toNode) {

        try {
            var first = node?.Element?.GetChildAtIndex(0);
            var second = first?.GetChildAtIndex(0);
            var children = second?.Children;
            if (children == null)
                return;

            if (children.Any(x => x?.TextureName?.Contains("Corrupt", StringComparison.OrdinalIgnoreCase) == true))
                AddContentToNode(toNode, "Corrupted");

            if (children.Any(x => x?.TextureName?.Contains("CorruptionNexus", StringComparison.OrdinalIgnoreCase) == true))
                AddContentToNode(toNode, "Corrupted Nexus");

            if (children.Any(x => x?.TextureName?.Contains("Sanctification", StringComparison.OrdinalIgnoreCase) == true))
                AddContentToNode(toNode, "Cleansed");

            if (children.Any(x => x?.TextureName?.Contains("UniqueMap", StringComparison.OrdinalIgnoreCase) == true))
                AddContentToNode(toNode, "Unique Map");

            if (children.Any(x => x?.TextureName?.Contains("MapBossSpecial", StringComparison.OrdinalIgnoreCase) == true))
                AddContentToNode(toNode, "Anomaly Map Boss");

            if (children.Any(x => x?.TextureName?.Contains("ContentMapBoss.dds", StringComparison.OrdinalIgnoreCase) == true))
                AddContentToNode(toNode, "Map Boss");
        }
        catch (Exception) {
            // swallow; missing children are expected sometimes
        }

    }

    // Named content used to live on AtlasPanelNode.Content; it is now exposed via
    // AtlasPanelNode.ContentIdentity, a list whose elements carry an .Id (e.g. "PowerfulMapBoss").
    // The Id is space-stripped, while ContentTypes keys/names have spaces ("Powerful Map Boss"),
    // so match by stripping spaces from the key.
    private void AddNodeContentFromIdentity(AtlasNodeDescription node, Node toNode) {
        var contentIdentity = node.Element?.ContentIdentity;
        if (contentIdentity == null)
            return;

        foreach (var content in contentIdentity) {
            var id = content?.Id;
            if (string.IsNullOrEmpty(id))
                continue;

            var contentType = ResolveContentIdentityType(id);

            if (contentType != null)
                toNode.Content.TryAdd(contentType.Name, contentType);
        }
    }

    private Content? ResolveContentIdentityType(string id)
    {
        if (TryGetContentType(id, out var direct))
            return direct;

        string normalizedId = NormalizeContentIdentity(id);
        foreach (var contentType in SnapshotContentTypes())
        {
            if (string.Equals(NormalizeContentIdentity(contentType.Key), normalizedId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeContentIdentity(contentType.Value.Name), normalizedId, StringComparison.OrdinalIgnoreCase))
                return contentType.Value;
        }

        return ResolveContentType(HumanizeContentIdentity(id));
    }

    private static string NormalizeContentIdentity(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"[\s_-]+", string.Empty);
    }

    private static string HumanizeContentIdentity(string id)
    {
        string value = (id ?? string.Empty).Replace("_", " ").Replace("-", " ").Trim();
        value = Regex.Replace(value, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
        value = Regex.Replace(value, @"([a-z0-9])([A-Z])", "$1 $2");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private void AddNodeContentTowers(AtlasNodeDescription node, Node toNode) {
        var aux = node.Element?.Height == 110; // tower height is 110
        if (aux)
            AddContentToNode(toNode, "Tower");


    }

    // A map grants an atlas passive point when its AtlasEntry.PassiveSkill.Id contains "Inside"
    // and the node hasn't been completed yet.
    private void SetAtlasPassive(AtlasNodeDescription node, Node toNode) {
        try {
            var passiveId = node.Element?.AtlasEntry?.PassiveSkill?.Id;
            bool completed = node.Element?.IsCompleted ?? false;
            bool grantsInside = passiveId?.Contains("Inside", StringComparison.OrdinalIgnoreCase) ?? false;
            toNode.GivesAtlasPoint = grantsInside && !completed;
            toNode.HasAtlasQuest = (passiveId?.Contains("AtlasQuest", StringComparison.OrdinalIgnoreCase) ?? false) && !completed;
        }
        catch { toNode.GivesAtlasPoint = false; toNode.HasAtlasQuest = false; }
    }
    #endregion

    #region Drawing Functions
    //MARK: DrawConnections
    /// <summary>
    /// Draws lines between a map node and its connected nodes on the atlas.
    /// </summary>
    /// <param name="WorldMap">The atlas panel containing the map nodes and their connections.</param>
    /// <param name="cachedNode">The map node for which connections are to be drawn.</param>
    /// 
    private void DrawConnections(Node cachedNode, RectangleF nodeCurrentPosition)
    {       
         foreach (Vector2i coordinates in cachedNode.GetNeighborCoordinates())
        {
            if (coordinates == default)
                continue;
            
            if (!TryGetCachedNode(coordinates, out Node destinationNode))
                continue;
                
            if (!Settings.Features.DrawVisitedNodeConnections && (destinationNode.IsVisited || cachedNode.IsVisited))
                continue;

            if ((!Settings.Features.DrawHiddenNodeConnections || !Settings.Features.ProcessHiddenNodes) && (!destinationNode.IsVisible || !cachedNode.IsVisible))
                continue;
            
            if (!TryGetNodeRect(destinationNode, out RectangleF destinationPos))
                continue;

            if (!IsLineVisible(nodeCurrentPosition.Center, destinationPos.Center))
                continue;

            if (Settings.Graphics.DrawGradientLines) {
                Color sourceColor = cachedNode.IsVisited ? Settings.Graphics.VisitedLineColor : cachedNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                Color destinationColor = destinationNode.IsVisited ? Settings.Graphics.VisitedLineColor : destinationNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                
                if (sourceColor == destinationColor)
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor);
                else
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor, destinationColor);

            } else {
                var color = Settings.Graphics.LockedLineColor;

                if (destinationNode.IsUnlocked || cachedNode.IsUnlocked)
                    color = Settings.Graphics.UnlockedLineColor;
                
                if (destinationNode.IsVisited && cachedNode.IsVisited)
                    color = Settings.Graphics.VisitedLineColor;

                Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, color);
            }
            
        }
    }

    /// MARK: HighlightMapContent
    /// <summary>
    /// Highlights a map node by drawing a circle around it if certain conditions are met.
    /// </summary>
    /// <param name="cachedNode">The map node to be highlighted.</param>
    /// <param name="Count">The count used to calculate the radius of the circle.</param>
    /// <param name="Content">The content string to check within the map node's elements.</param>
    /// <param name="Draw">A boolean indicating whether to draw the circle or not.</param>
    /// <param name="color">The color of the circle to be drawn.</param>
    /// <returns>Returns 1 if the circle is drawn, otherwise returns 0.</returns>
    private void DrawContentRings(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        int ringCount = 0;

        foreach (var contentName in ContentRingDrawOrder)
            if (cachedNode.Content.TryGetValue(contentName, out var orderedContent) && orderedContent != null)
                ringCount += DrawContentRing(cachedNode, nodeCurrentPosition, ringCount, orderedContent);

        foreach (Content content in cachedNode.Content.Values
                     .Where(x => x != null && !ContentRingDrawOrderLookup.Contains(x.Name))
                     .OrderBy(x => x.Name))
            ringCount += DrawContentRing(cachedNode, nodeCurrentPosition, ringCount, content);
    }

    private int DrawContentRing(Node cachedNode, RectangleF nodeCurrentPosition, int Count, Content cachedContent)
    {
        if ((cachedNode.IsVisited && !cachedNode.IsAttempted) || 
            (!Settings.MapContent.ShowRingsOnLockedNodes && !cachedNode.IsUnlocked) || 
            (!Settings.MapContent.ShowRingsOnUnlockedNodes && cachedNode.IsUnlocked) || 
            (!Settings.MapContent.ShowRingsOnHiddenNodes && !cachedNode.IsVisible) ||         
            !cachedNode.MapType.Highlight || !cachedContent.Highlight)            
            return 0;

        float radius = (Count * Settings.Graphics.RingWidth) + 1 + ((nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 2 * Settings.Graphics.RingRadius);
        if (customIconsLoaded && Settings.Graphics.UseNodeIcons) {
            // User-tunable scale (default 1.0 = original ring size).
            float d = radius * 2f * Settings.Graphics.ContentRingIconScale;
            DrawNodeSprite(nodeCurrentPosition.Center, d, d, SpriteIcon.CircleOutline, cachedContent.Color);
        }
        else
            Graphics.DrawCircle(nodeCurrentPosition.Center, radius, cachedContent.Color, Settings.Graphics.RingWidth, 32);

        return 1;
    }
    

    /// MARK: DrawWaypointLine
    /// Draws a line from the center of the screen to the specified map node on the atlas.
    /// </summary>
    /// <param name="mapNode">The atlas node to which the line will be drawn.</param>
    /// <remarks>
    /// This method checks if the feature to draw lines is enabled in the settings. If enabled, it finds the corresponding map settings
    /// for the given map node. If the map settings are found and the line drawing is enabled for that map, it proceeds to draw the line.
    /// Additionally, if the feature to draw line labels is enabled, it draws the node name and the distance to the node.
    /// </remarks>
    private void DrawWaypointLine(Node cachedNode)
    {
        
        if (cachedNode.IsVisited || cachedNode.IsAttempted || !cachedNode.MapType.DrawLine || !Settings.Features.DrawLines)
            return;

        if (!TryGetNodeRect(cachedNode, out RectangleF nodeCurrentPosition))
            return;

        var distance = Vector2.Distance(screenCenter, nodeCurrentPosition.Center);

        if (distance < 400)
            return;

        Vector2 position = Vector2.Lerp(screenCenter, nodeCurrentPosition.Center, Settings.Graphics.LabelInterpolationScale);
        // Clamp position to screen
        string label = $"{cachedNode.Name} ({distance:0})";
        Vector2 labelSize = Graphics.MeasureText(label);
        var windowRect = GameController.Window.GetWindowRectangle();
        // Clamp
        position.X = Math.Clamp(position.X, labelSize.X, windowRect.Width - labelSize.X);
        position.Y = Math.Clamp(position.Y, labelSize.Y, windowRect.Height - labelSize.Y);

        if (!IsLineVisible(position, nodeCurrentPosition.Center))
            return;

        Graphics.DrawLine(position, nodeCurrentPosition.Center, Settings.Graphics.MapLineWidth, cachedNode.MapType.NodeColor);

        if (Settings.Features.DrawLineLabels) {
            DrawCenteredTextWithBackground(label, position, cachedNode.MapType.NameColor, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
            
        
    }
    
    /// MARK: DrawMapNode
    /// Draws a highlighted circle around a map node on the atlas if the node is configured to be highlighted.
    /// </summary>
    /// <param name="mapNode">The atlas node description containing information about the map node to be drawn.</param>   
    private void DrawMapNode(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.MapTypes.HighlightMapNodes || cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;
            
        // "Special" maps have wider node art. Skip the solid circle (it would cover the map art) —
        // they get an icon drawn above the node by DrawSpecialIndicator instead.
        if (Settings.Graphics.ShowSpecialMapIndicator && nodeCurrentPosition.Width > SpecialMapWidthThreshold)
            return;

        var radius = (nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 4 * Settings.Graphics.NodeRadius;
        Color color = cachedNode.MapType.ColorNodesByWeight ? GetWeightColor(cachedNode.Weight) : cachedNode.MapType.NodeColor;

        if (customIconsLoaded && Settings.Graphics.UseNodeIcons) {
            DrawNodeSprite(nodeCurrentPosition.Center, radius * 2f, radius * 2f, cachedNode.MapType.Icon, color);
        } else {
            Graphics.DrawCircleFilled(nodeCurrentPosition.Center, radius, color, 16);
        }
    }

    // Atlas node art wider than this is a "special" map (e.g. unique/pinnacle layouts).
    private const float SpecialMapWidthThreshold = 90f;

    // Normalized UV RectangleF for a custom-atlas icon, adapting SpriteAtlas's corner-pair UVs to the
    // RectangleF (x, y, w, h) form ExileCore's Graphics.DrawImage expects.
    private static RectangleF GetSpriteUV(SpriteIcon icon)
    {
        var (uv0, uv1) = SpriteAtlas.GetUVPair(icon);
        return new RectangleF(uv0.X, uv0.Y, uv1.X - uv0.X, uv1.Y - uv0.Y);
    }

    // True once the custom sprite atlas PNG has been found and loaded.
    private bool CustomIconsAvailable => customIconsLoaded;

    /// MARK: DrawNodeSprite
    /// Draws a custom-atlas sprite centered at <paramref name="center"/>. IconFlatten vertically squashes
    /// it (shorter dest rect) so a round sprite reads as a flat disc lying on the tilted atlas plane.
    /// Uses Graphics.DrawImage so it layers like every other node draw (above lines, below labels/windows).
    private void DrawNodeSprite(Vector2 center, float width, float height, SpriteIcon icon, Color color, bool allowFlatten = true)
    {
        float h = allowFlatten ? height * (1f - Settings.Graphics.IconFlatten) : height;
        Graphics.DrawImage(CustomIconsName, new RectangleF(center.X - width / 2f, center.Y - h / 2f, width, h), GetSpriteUV(icon), color);
    }

    // Pixels above the node center to place the special-map icon (independent of node size).
    private const float SpecialIconCenterOffset = 40f;

    /// MARK: DrawSpecialIndicator
    /// Draws an icon above "special" map nodes (wider node art) instead of covering them with a fill.
    private void DrawSpecialIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowSpecialMapIndicator || !Settings.MapTypes.HighlightMapNodes ||
                cachedNode.IsVisited || cachedNode.MapType == null || !cachedNode.MapType.Highlight ||
                nodeCurrentPosition.Width <= SpecialMapWidthThreshold)
                return;

            Vector2 iconSize = new Vector2(48, 48) * Settings.Graphics.SpecialMapIconScale;

            // Position above the node by a fixed offset from its center (independent of node size).
            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(iconSize.X / 2, SpecialIconCenterOffset + iconSize.Y / 2);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            if (customIconsLoaded)
                DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, Settings.Graphics.SpecialMapIcon, Settings.Graphics.SpecialMapColor, allowFlatten: false);
            else
                Graphics.DrawImage(IconsFile, iconRect, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteHexagon), Settings.Graphics.SpecialMapColor);
        } catch (Exception e) {
            LogError("Error drawing special map indicator: " + e.Message);
        }
    }

    // Silver tint for the atlas-point marker.
    private static readonly Color AtlasPointColor = Color.FromArgb(255, 200, 200, 205);

    /// MARK: DrawAtlasPointIndicator
    /// Draws a small silver Star8 just above nodes whose map grants an atlas passive point.
    private void DrawAtlasPointIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowAtlasPointIndicator || !cachedNode.GivesAtlasPoint || cachedNode.IsVisited || !customIconsLoaded)
                return;

            float size = 20f;
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X, nodeCurrentPosition.Center.Y - nodeCurrentPosition.Height / 2f - size / 2f - 2f);
            DrawNodeSprite(center, size, size, SpriteIcon.Star8, AtlasPointColor, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing atlas point indicator: " + e.Message);
        }
    }

    // Golden tint for the atlas-quest marker.
    private static readonly Color AtlasQuestColor = Color.FromArgb(255, 255, 200, 40);

    /// MARK: DrawAtlasQuestIndicator
    /// Draws a small golden exclamation just above nodes that have atlas quest content.
    private void DrawAtlasQuestIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowAtlasQuestIndicator || !cachedNode.HasAtlasQuest || cachedNode.IsVisited || !customIconsLoaded)
                return;

            float size = 20f;
            // Offset right of the atlas-point star (which sits centered above the node) so both can show.
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X + size, nodeCurrentPosition.Center.Y - nodeCurrentPosition.Height / 2f - size / 2f - 2f);
            DrawNodeSprite(center, size, size, SpriteIcon.Exclamation, AtlasQuestColor, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing atlas quest indicator: " + e.Message);
        }
    }

    /// MARK: DrawFavoriteIndicator
    /// Draws a star icon above nodes whose map type is flagged as a favorite.
    private void DrawFavoriteIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!cachedNode.IsFavorited || cachedNode.IsVisited)
                return;

            Vector2 iconSize = new Vector2(48, 48) * Settings.Graphics.FavoriteIconScale;

            // Position above the node (mirrors the waypoint icon offset).
            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(0, nodeCurrentPosition.Height / 2 + 20);
            iconPosition -= new Vector2(iconSize.X / 2, iconSize.Y);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            if (customIconsLoaded)
                DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, SpriteIcon.Star5, Settings.Graphics.FavoriteColor, allowFlatten: false);
            else
                Graphics.DrawImage(IconsFile, iconRect, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteStar), Settings.Graphics.FavoriteColor);
        } catch (Exception e) {
            LogError("Error drawing favorite indicator: " + e.Message);
        }
    }

    //DrawMapNode
    private void DrawWeight(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.MapTypes.DrawWeightOnMap ||
            (!cachedNode.IsVisible && !Settings.MapTypes.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;  

        // Normalized weight in [0,1]
        float fraction = GetNormalizedWeight(cachedNode.Weight);

        float offsetX = Settings.MapTypes.ShowMapNames ? (Graphics.MeasureText(cachedNode.Name.ToUpper()).X / 2) + 30 : 50;
        Vector2 position = new(nodeCurrentPosition.Center.X + offsetX + Settings.Graphics.MapNameOffsetX, nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY);

        DrawCenteredTextWithBackground($"{(int)(fraction*100)}%", position, GetWeightColorFromFraction(fraction), Settings.Graphics.BackgroundColor, true, 10, 3);
    }
    /// <summary>
    /// Draws the name of the map on the atlas.
    /// </summary>
    /// <param name="cachedNode">The atlas node description containing information about the map.</param>
    private void DrawMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.MapTypes.ShowMapNames ||
            (!cachedNode.IsVisible && !Settings.MapTypes.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;

        Color fontColor = Settings.MapTypes.UseColorsForMapNames ? cachedNode.MapType.NameColor : Settings.Graphics.FontColor;
        Color backgroundColor = Settings.MapTypes.UseColorsForMapNames ? cachedNode.MapType.BackgroundColor : Settings.Graphics.BackgroundColor;

        bool isSpecial = nodeCurrentPosition.Width > SpecialMapWidthThreshold;

        if (isSpecial) {
            // Special maps get their own customizable name color (never weight-based).
            fontColor = Settings.Graphics.SpecialMapNameColor;
        } else if (Settings.MapTypes.UseWeightColorsForMapNames && cachedNode.MapType.UseWeightColorForName) {
            fontColor = GetWeightColor(cachedNode.Weight);
        }

        // Name text is always fully opaque regardless of the configured/weight color's alpha.
        fontColor = Color.FromArgb(255, fontColor.R, fontColor.G, fontColor.B);

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(Settings.Graphics.MapNameOffsetX, Settings.Graphics.MapNameOffsetY);

        // Special maps render their name 20% larger.
        using (Graphics.SetTextScale(isSpecial ? 1.2f : 1.0f))
            DrawCenteredTextWithBackground(cachedNode.Name.ToUpper(), namePosition, fontColor, backgroundColor, true, 10, 3);
    }

    private void DrawTowerMods(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if ((cachedNode.IsTower && !Settings.MapMods.ShowOnTowers) || (!cachedNode.IsTower && !Settings.MapMods.ShowOnMaps) || !cachedNode.MapType.Highlight)    
            return; 

        Dictionary<string, Color> mods = [];

        var effects = new List<Effect>();
        if (cachedNode.IsTower) {            
            if (Settings.MapMods.ShowOnTowers) {                
                effects = cachedNode.Effects.Where(x => x.Value.Sources.Contains(cachedNode.Coordinates)).Select(x => x.Value).ToList();

                if (effects.Count == 0 && cachedNode.IsVisited)
                {
                    if (Settings.Features.MissingTabletTowerContentRing)
                    {
                        float radius = (0 * Settings.Graphics.RingWidth) + 1 + ((nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 2 * Settings.Graphics.RingRadius); //DRAWING CIRCLES AROUND TOWERS TO SEE THEM IF THEY CAN BE USED
                        Color towerColor = ResolveContentType("Tower")?.Color ?? Settings.Graphics.LineColor;
                        Graphics.DrawCircle(nodeCurrentPosition.Center, radius, towerColor, Settings.Graphics.RingWidth, 32);
                    }
                }                    

            }
        } else {
            if (Settings.MapMods.ShowOnMaps && !cachedNode.IsVisited) {
                effects = cachedNode.Effects.Where(x => x.Value.Enabled).Select(x => x.Value).ToList();
            }
        }

        if (effects.Count == 0)
            return;
        
        foreach (var effect in effects) {
            if (Settings.MapMods.MapModTypes.TryGetValue(effect.ID.ToString(), out Mod mod)) {
                if (effect.Value1 >= mod.MinValueToShow) {
                    mods.TryAdd(effect.ToString(), mod.Color);
                }
            }
            
        }
        mods = mods.OrderBy(x => x.Value.ToString()).ToDictionary(x => x.Key, x => x.Value);
        DrawMapModText(mods, nodeCurrentPosition.Center);
    }
    private void DrawMapModText(Dictionary<string, Color> mods, Vector2 position)
    {      
        using (Graphics.SetTextScale(Settings.MapMods.MapModScale)) {
            string fullText = string.Join("\n", mods.Select(x => $"{x.Key}"));
            var boxSize = Graphics.MeasureText(fullText) + new Vector2(10, 10);
            var lineHeight = Graphics.MeasureText("A").Y;
            position -= new Vector2(boxSize.X / 2, boxSize.Y / 2);

            // offset the box below the node
            position += new Vector2(0, (boxSize.Y / 2) + Settings.MapMods.MapModOffset);
            
            Graphics.DrawBox(position, boxSize + position, Settings.Graphics.BackgroundColor, 5.0f);

            position += new Vector2(5, 5);

            foreach (var mod in mods)
            {
                Graphics.DrawText(mod.Key, position, mod.Value);
                position += new Vector2(0, lineHeight);
            }
        }
    }

    // private void DrawBiomes(AtlasPanelNode mapNode)
    // {
    //     var currentPosition = mapNode.GetClientRect();
    //     if (!IsOnScreen(currentPosition.Center) || !mapCache.ContainsKey(mapNode.Address))
    //         return;

    //     Node cachedNode = mapCache[mapNode.Address];
    //     string mapName = mapNode.Area.Name.Trim();
    //     if (cachedNode == null || !Settings.Biomes.ShowBiomes || cachedNode.Biomes.Count == 0)    
    //         return; 

    //     Dictionary<string, Color> biomes = new Dictionary<string, Color>();

    //     var biomeList = new List<Biome>();
    //     if (Settings.Biomes.ShowBiomes && !mapNode.IsVisited) {
    //         biomeList = cachedNode.Biomes;
    //     }

    //     foreach (var biome in biomeList) {
    //         biomes.Add(biome.ToString(), Settings.Biomes.Biomes[biome.ToString()].Color);
    //     }

    //     DrawBiomeText(biomes, currentPosition.Center);
    // }

    // private void DrawBiomeText(Dictionary<string, Color> biomes, Vector2 position)
    // {      
    //     using (Graphics.SetTextScale(Settings.Biomes.BiomeScale)) {
    //         string fullText = string.Join("\n", biomes.Select(x => $"{x.Key}"));
    //         var boxSize = Graphics.MeasureText(fullText) + new Vector2(10, 10);
    //         var lineHeight = Graphics.MeasureText("A").Y;
    //         position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

    //         // offset the box below the node
    //         position += new Vector2(0, (boxSize.Y / 2) + Settings.Biomes.BiomeOffset);
            
    //         if (!IsOnScreen(boxSize + position))
    //             return;

    //         Graphics.DrawBox(position, boxSize + position, Settings.Graphics.BackgroundColor, 5.0f);

    //         position += new Vector2(5, 5);

    //         foreach (var biome in biomes)
    //         {
    //             Graphics.DrawText(biome.Key, position, biome.Value);
    //             position += new Vector2(0, lineHeight);
    //         }
    //     }
    // }

    /// MARK: DrawTowersWithinRange
    /// <summary>
    /// Draws lines between towers and maps within range of eachother.
    /// </summary>
    /// <param name="mapNode"></param>
    private void DrawTowerRange(Node cachedNode) {
        if (!cachedNode.DrawTowers || (cachedNode.IsVisited && !cachedNode.IsTower))
            return;

        if (cachedNode.IsTower) {
            DrawNodesWithinRange(cachedNode);
        } else {
            DrawTowersWithinRange(cachedNode);
        }
    }
    /// MARK: DrawTowersWithinRange
    /// <summary>
    ///  Draws lines between the current map node and any Lost Towers within range.
    /// </summary>
    /// <param name="cachedNode"></param>
    private void DrawTowersWithinRange(Node cachedNode) {
        if (!cachedNode.DrawTowers || cachedNode.IsVisited)
            return;

        var nearbyTowers = SnapshotMapCacheValues()
            .Where(x => x.IsTower && Vector2.Distance(x.Coordinates, cachedNode.Coordinates) <= 11)
            .ToList();
        if (nearbyTowers.Count == 0)
            return;

        if (!TryGetNodeRect(cachedNode, out RectangleF nodeRect))
            return;

        Vector2 nodePos = nodeRect.Center;
        Graphics.DrawCircle(nodePos, 50, Settings.Graphics.LineColor, 5, 16);

        foreach (var tower in nearbyTowers) {
            if (!TryGetCachedNode(tower.Coordinates, out Node towerNode))
                continue;

            if (!TryGetNodeRect(towerNode, out RectangleF towerPosition))
                continue;

            var endPos = towerPosition.Center;
            var distance = Vector2.Distance(nodePos, endPos);
            var direction = (endPos - nodePos) / distance;
            var offset = direction * 50;

            Graphics.DrawCircle(towerPosition.Center, 50, Settings.Graphics.LineColor, 5, 16);      
            Graphics.DrawLine(nodePos + offset, endPos - offset, Settings.Graphics.MapLineWidth, Settings.Graphics.LineColor);     
            DrawCenteredTextWithBackground($"{nearbyTowers.Count:0} towers in range", nodePos + new Vector2(0, -50), Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
    }

    /// MARK: DrawNodesWithinRange
    /// <summary>
    /// Draws lines between maps and tower within range of eachother.
    /// </summary>
    /// <param name="cachedNode"></param>
    private void DrawNodesWithinRange(Node cachedNode) {
        if (!cachedNode.DrawTowers)
            return;

        var nearbyMaps = SnapshotMapCacheValues()
            .Where(x => x.Name != "Lost Towers" && !x.IsVisited && Vector2.Distance(x.Coordinates, cachedNode.Coordinates) <= 11)
            .ToList();
        if (nearbyMaps.Count == 0)
            return;
        if (!TryGetNodeRect(cachedNode, out RectangleF nodeRect))
            return;

        Vector2 nodePos = nodeRect.Center;
        Graphics.DrawCircle(nodePos, 50, Settings.Graphics.LineColor, 5, 16);

        foreach (var map in nearbyMaps) {
            if (!TryGetCachedNode(map.Coordinates, out Node nearbyMap))
                continue;

            if (!TryGetNodeRect(nearbyMap, out RectangleF mapPosition))
                continue;

            var endPos = mapPosition.Center;
            var distance = Vector2.Distance(nodePos, endPos);
            var direction = (endPos - nodePos) / distance;
            var offset = direction * 50;

            Graphics.DrawCircle(mapPosition.Center, 50, Settings.Graphics.LineColor, 5, 16);  
            Graphics.DrawLine(nodePos + offset, endPos - offset, Settings.Graphics.MapLineWidth, Settings.Graphics.LineColor);     
            DrawCenteredTextWithBackground($"{nearbyMaps.Count:0} maps in range", nodePos + new Vector2(0, -50), Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
    }

    #endregion

    #region Misc Drawing
    /// <summary>
    /// Draws text with a background color at the specified position.
    /// </summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="position">The position to draw the text at.</param>
    /// <param name="textColor">The color of the text.</param>
    /// <param name="backgroundColor">The color of the background.</param>
    /// Yes, I know exilecore has this built in, but I wanted padding and rounded corners.

    private void DrawCenteredTextWithBackground(string text, Vector2 position, Color color, Color backgroundColor, bool center = false, int xPadding = 0, int yPadding = 0)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text);

        boxSize += new Vector2(xPadding, yPadding);    

        if (center)
            position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

        Graphics.DrawBox(position, boxSize + position, backgroundColor, 5.0f);       

        position += new Vector2(xPadding / 2, yPadding / 2);

        Graphics.DrawText(text, position, color);
    }

    private void DrawRotatedImage(IntPtr textureId, Vector2 position, Vector2 size, float angle, Color color)
    {
        Vector2 center = position + size / 2;

        float cosTheta = (float)Math.Cos(angle);
        float sinTheta = (float)Math.Sin(angle);

        Vector2 RotatePoint(Vector2 point)
        {
            Vector2 translatedPoint = point - center;
            Vector2 rotatedPoint = new Vector2(
                translatedPoint.X * cosTheta - translatedPoint.Y * sinTheta,
                translatedPoint.X * sinTheta + translatedPoint.Y * cosTheta
            );
            return rotatedPoint + center;
        }

        Vector2 topLeft = RotatePoint(position);
        Vector2 topRight = RotatePoint(position + new Vector2(size.X, 0));
        Vector2 bottomRight = RotatePoint(position + size);
        Vector2 bottomLeft = RotatePoint(position + new Vector2(0, size.Y));


        Graphics.DrawQuad(textureId, topLeft, topRight, bottomRight, bottomLeft, color);
        }
        private void DrawGradientLine(Vector2 start, Vector2 end, Color startColor, Color endColor, float lineWidth)
    {
        // No need to draw a gradient if the colors are the same
        if (startColor == endColor)
        {
            Graphics.DrawLine(start, end, Settings.Graphics.MapLineWidth, startColor);
            return;
        }

        int segments = 10; // Number of segments to create the gradient effect
        Vector2 direction = (end - start) / segments;

        for (int i = 0; i < segments; i++)
        {
            Vector2 segmentStart = start + direction * i;
            Vector2 segmentEnd = start + direction * (i + 1);

            float t = (float)i / segments;
            Color segmentColor = ColorUtils.InterpolateColor(startColor, endColor, t);

            Graphics.DrawLine(segmentStart, segmentEnd, lineWidth, segmentColor);

        }
    }

    #endregion
    private void UpdateWaypointPaths()
    {
        foreach (var waypoint in Settings.Waypoints.Waypoints.Values)
        {
            if (TryGetCachedNode(waypoint.Coordinates, out Node waypointNode))
            {
                // Find the closest visited node to this specific waypoint
                var startNode = FindClosestVisitedNode(waypointNode);
                if (startNode == null)
                    continue;

                waypoint.PathFromStart = FindShortestPath(startNode, waypointNode);
            }
            else
            {
                waypoint.PathFromStart = null;
            }
        }
    }
    #region Helper Functions

    // Map tooltip is a WorldMap child identified by its popup texture; checked in IsOnScreen.
    // Matches any AtlasScreen "*Popup*" texture (e.g. AtlasMapNodePopup / AtlasMapNodePopupSelected).
    private const string TooltipTexturePrefix = "Art/Textures/Interface/2D/2DArt/UIImages/InGame/AtlasScreen/";
    private static bool IsTooltipTexture(string textureName) =>
        textureName != null && textureName.StartsWith(TooltipTexturePrefix) && textureName.Contains("Popup");

    // Static atlas UI textures we never want to draw the overlay over (title bar, search box bg).
    // Matched by exact texture name anywhere under the WorldMap element tree.
    private static readonly HashSet<string> ExcludeTextureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/WorldMap/WorldmapTitleBar.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/Common/AtlasSearchBg.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapPinsWindow/MapPinLegendBG.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapLegend/LegendBg.dds",
    };

    // Recursively adds the client rect of any visible descendant whose texture is in
    // ExcludeTextureNames. Depth-limited so a malformed/deep tree can't stall the frame.
    private void AddExcludeRectsByTexture(ExileCore2.PoEMemory.Element element, int depth)
    {
        if (element == null || depth > 8 || !element.IsVisible)
            return;

        if (element.TextureName != null && ExcludeTextureNames.Contains(element.TextureName))
            cachedExcludeRects.Add(element.GetClientRect());

        foreach (var child in element.Children)
            AddExcludeRectsByTexture(child, depth + 1);
    }

    // Recomputes the on-screen rect and any visible map-tooltip rects once per render frame.
    // IsOnScreen then reads these cached values instead of re-reading panel/tooltip game memory
    // on every call (it's called dozens of times per node per frame).
    private void UpdateScreenBounds()
    {
        try {
            var size = GameController.Window.GetWindowRectangleTimeCache.Size;
            float left = 0;
            float right = size.X;

            if (UI.OpenRightPanel.IsVisible)
                right -= UI.OpenRightPanel.GetClientRect().Width;

            if (UI.OpenLeftPanel.IsVisible || WaypointPanelIsOpen)
                left += Math.Max(UI.OpenLeftPanel.GetClientRect().Width, UI.SettingsPanel.GetClientRect().Width);

            cachedScreenRect = new RectangleF(left, 0, right - left, size.Y);

            // Don't render over the map tooltip. Its child index varies, so identify it
            // by its popup texture (selected/unselected) instead of a fixed position.
            cachedExcludeRects.Clear();
            foreach (var tooltip in UI.WorldMap.Children) {
                if (tooltip == null || !tooltip.IsVisible)
                    continue;
                if (!IsTooltipTexture(tooltip.TextureName))
                    continue;

                RectangleF mapTooltip = tooltip.GetClientRect();
                mapTooltip.Inflate(mapTooltip.Width * 0.1f, mapTooltip.Height * 0.1f);
                cachedExcludeRects.Add(mapTooltip);
            }

            // Don't render over the atlas title bar / search box background.
            AddExcludeRectsByTexture(UI.WorldMap, 0);

            // Don't render over the fixed HUD elements (life/mana orbs, flask panel, skill bar).
            AddExcludeRect(UI.GameUI?.LifeOrb);
            AddExcludeRect(UI.GameUI?.ManaOrb);
            AddExcludeRect(UI.GameUI?.FlaskPanel?.Parent);
            AddExcludeRect(UI.SkillBar?.Parent);
        } catch (Exception e) {
            // Keep last good bounds on a failed memory read rather than blanking the overlay.
            LogError("Error updating screen bounds: " + e.Message);
        }
    }

    // Adds a visible element's client rect to the no-draw set. Null/invisible elements are skipped.
    private void AddExcludeRect(ExileCore2.PoEMemory.Element element)
    {
        if (element == null || !element.IsVisible)
            return;
        cachedExcludeRects.Add(element.GetClientRect());
    }

    private bool IsOnScreen(Vector2 position)
    {
        foreach (var tooltip in cachedExcludeRects)
            if (tooltip.Contains(position))
                return false;

        return cachedScreenRect.Contains(position);
    }

    // A line is drawable only if both endpoints are on screen AND the segment doesn't
    // pass through a tooltip rect (a line can clear both endpoint checks yet still cross
    // the tooltip between them).
    private bool IsLineVisible(Vector2 start, Vector2 end)
    {
        if (!IsOnScreen(start) || !IsOnScreen(end))
            return false;

        foreach (var tooltip in cachedExcludeRects)
            if (SegmentIntersectsRect(start, end, tooltip))
                return false;

        return true;
    }

    // Segment vs axis-aligned rect. Endpoints are already known outside the rect (IsOnScreen
    // rejects points inside a tooltip), so test the segment against the four edges.
    private static bool SegmentIntersectsRect(Vector2 a, Vector2 b, RectangleF rect)
    {
        Vector2 tl = new(rect.Left, rect.Top);
        Vector2 tr = new(rect.Right, rect.Top);
        Vector2 br = new(rect.Right, rect.Bottom);
        Vector2 bl = new(rect.Left, rect.Bottom);

        return SegmentsIntersect(a, b, tl, tr) ||
               SegmentsIntersect(a, b, tr, br) ||
               SegmentsIntersect(a, b, br, bl) ||
               SegmentsIntersect(a, b, bl, tl);
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        float d1 = Cross(p3, p4, p1);
        float d2 = Cross(p3, p4, p2);
        float d3 = Cross(p1, p2, p3);
        float d4 = Cross(p1, p2, p4);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    // Cross product of (b - a) x (c - a); sign tells which side of line ab point c is on.
    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    public float GetDistanceToNode(Node cachedNode)
    {
        if (!TryGetNodeRect(cachedNode, out RectangleF nodeRect))
            return float.MaxValue;

        return Vector2.Distance(screenCenter, nodeRect.Center);
    }

    private float GetNormalizedWeight(float value)
    {
        // Normalize to [0,1] using the configured weight domain plus any live outliers.
        float range = maxMapWeight - minMapWeight;
        if (range <= 1e-6f)
            return 0.5f;
        float t = (value - minMapWeight) / range;
        if (float.IsNaN(t) || float.IsInfinity(t))
            return 0.5f;
        return Math.Clamp(t, 0f, 1f);
    }

    private Color GetWeightColor(float value)
    {
        float fraction = GetNormalizedWeight(value);
        return GetWeightColorFromFraction(fraction);
    }

    private Color GetWeightColorFromFraction(float fraction)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);

        Color badColor = Settings.MapTypes.BadNodeColor;
        Color goodColor = Settings.MapTypes.GoodNodeColor;
        int midpointAlpha = (badColor.A + goodColor.A) / 2;
        Color midpointColor = Color.FromArgb(midpointAlpha, 255, 220, 0);

        return fraction < 0.5f
            ? ColorUtils.InterpolateColor(badColor, midpointColor, fraction * 2f)
            : ColorUtils.InterpolateColor(midpointColor, goodColor, (fraction - 0.5f) * 2f);
    }
    
    #endregion
    #region Waypoint Panel
    // MARK: Quick Edit
    // Node currently being edited via the hover hotkey, and whether the popup is showing.
    private Node quickEditNode;
    private bool quickEditOpen;

    private static void QuickColorEdit(string id, Color color, Action<Color> set) {
        Vector4 v = new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        if (ImGui.ColorEdit4($"##{id}", ref v, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))
            set(Color.FromArgb((int)(v.W * 255), (int)(v.X * 255), (int)(v.Y * 255), (int)(v.Z * 255)));
    }

    /// MARK: DrawQuickEditPanel
    /// Floating popup (opened by the Quick Edit hotkey while hovering a node) to edit that node's
    /// map type and its content types inline. Edits the shared Map/Content instances, so changes
    /// persist to settings and apply live.
    private void DrawQuickEditPanel() {
        try {
            var map = quickEditNode?.MapType;
            if (quickEditNode == null || map == null) { quickEditOpen = false; quickEditNode = null; return; }

            Vector2 pos;
            try { pos = quickEditNode.MapNode.Element.GetClientRect().Center + new Vector2(30, 0); }
            catch { pos = screenCenter; }
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.93f);

            if (ImGui.Begin($"Quick Edit###quickedit", ref quickEditOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextDisabled($"{quickEditNode.Name}  ({map.Name})");
                ImGui.Separator();

                bool highlight = map.Highlight;
                if (ImGui.Checkbox("Highlight##qe", ref highlight)) map.Highlight = highlight;
                ImGui.SameLine();
                bool fav = map.Favorite;
                if (ImGui.Checkbox("Favorite##qe", ref fav)) map.Favorite = fav;
                ImGui.SameLine();
                bool drawLine = map.DrawLine;
                if (ImGui.Checkbox("Line##qe", ref drawLine)) map.DrawLine = drawLine;

                float weight = map.Weight;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderFloat("Weight##qe", ref weight, -25f, 50f, "%.1f")) map.Weight = weight;

                QuickColorEdit("qe_node", map.NodeColor, c => map.NodeColor = c);
                ImGui.SameLine(); ImGui.Text("Node");
                ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
                QuickColorEdit("qe_name", map.NameColor, c => map.NameColor = c);
                ImGui.SameLine(); ImGui.Text("Name");
                ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
                QuickColorEdit("qe_bg", map.BackgroundColor, c => map.BackgroundColor = c);
                ImGui.SameLine(); ImGui.Text("Text BG");

                bool cbw = map.ColorNodesByWeight;
                if (ImGui.Checkbox("Color node by weight##qe", ref cbw)) map.ColorNodesByWeight = cbw;
                bool nbw = map.UseWeightColorForName;
                if (ImGui.Checkbox("Color name by weight##qe", ref nbw)) map.UseWeightColorForName = nbw;

                ImGui.Text("Icon"); ImGui.SameLine();
                SettingsHelpers.IconPicker("qeicon", map.Icon, i => map.Icon = i);

                if (quickEditNode.Content.Count > 0) {
                    ImGui.Separator();
                    ImGui.Text("Content");
                    foreach (var (cname, content) in quickEditNode.Content) {
                        ImGui.PushID($"qe_c_{cname}");
                        QuickColorEdit("col", content.Color, c => content.Color = c);
                        ImGui.SameLine();
                        bool ring = content.Highlight;
                        if (ImGui.Checkbox("Ring##c", ref ring)) content.Highlight = ring;
                        ImGui.SameLine();
                        bool cfav = content.Favorite;
                        if (ImGui.Checkbox("Fav##c", ref cfav)) content.Favorite = cfav;
                        ImGui.SameLine();
                        float cw = content.Weight;
                        ImGui.SetNextItemWidth(110);
                        if (ImGui.SliderFloat("##cw", ref cw, -5f, 5f, "%.2f")) content.Weight = cw;
                        ImGui.SameLine();
                        ImGui.TextUnformatted(content.Name);
                        ImGui.PopID();
                    }
                }

                ImGui.Separator();
                if (ImGui.Button("Close##qe")) quickEditOpen = false;
            }
            ImGui.End();

            if (!quickEditOpen) quickEditNode = null;
        } catch (Exception e) {
            LogError("Error drawing quick edit panel: " + e.Message);
            quickEditOpen = false;
            quickEditNode = null;
        }
    }

    // MARK: Node Debug
    // Node currently shown in the debug popup, and whether it's open.
    private Node debugNode;
    private bool debugNodeOpen;

    /// MARK: DrawNodeDebugPanel
    /// Floating popup (opened by the Debug Node hotkey while hovering a node) showing the node's
    /// debug text, element flags (as a binary string + per-flag), atlas-passive presence, biome id,
    /// and the content present. All game-memory reads are guarded so a stale read can't crash the HUD.
    private void DrawNodeDebugPanel() {
        try {
            if (debugNode == null) { debugNodeOpen = false; return; }
            var node = debugNode;
            var el = node.MapNode?.Element;

            Vector2 pos;
            try { pos = node.MapNode.Element.GetClientRect().Center + new Vector2(30, 0); }
            catch { pos = screenCenter; }
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.93f);

            if (ImGui.Begin("Node Debug###nodedebug", ref debugNodeOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextUnformatted(node.DebugText(false));

                string parentAddr = $"{node.ParentAddress:X}";
                if (ImGui.SmallButton($"Copy Parent Address##nd")) ImGui.SetClipboardText(parentAddr);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Copy {parentAddr} to clipboard");

                ImGui.Separator();

                // Named element status getters, rendered true=1/false=0 in this order:
                // CanTraverse, IsActive, IsCompleted, IsSaturated, IsScrollable, IsUnlocked,
                // IsValid, IsVisible, IsVisibleLocal, IsVisited, HasShinyHighlight.
                var status = new List<bool>();
                void Add(Func<bool> get) { try { status.Add(get()); } catch { status.Add(false); } }
                Add(() => el.CanTraverse);
                Add(() => el.IsActive);
                Add(() => el.IsCompleted);
                Add(() => el.IsSaturated);
                Add(() => el.IsScrollable);
                Add(() => el.IsUnlocked);
                Add(() => el.IsValid);
                Add(() => el.IsVisible);
                Add(() => el.IsVisibleLocal);
                Add(() => el.IsVisited);
                Add(() => el.HasShinyHighlight);
                ImGui.Text($"Status: {string.Concat(status.Select(b => b ? "1" : "0"))}");

                // Element.Flags is a separate List<bool> of the node's raw flag bits.
                string bits = "";
                try { bits = string.Concat(el.Flags.Select(b => b ? "1" : "0")); } catch { }
                ImGui.Text($"Flags: {bits}");

                bool passive = false;
                try { passive = el?.AtlasEntry?.PassiveSkill != null; } catch { }
                ImGui.Text($"AtlasEntry.PassiveSkill: {(passive ? "1" : "0")}");

                string biomeId = "";
                try { biomeId = node.MapNode.Element.Biome?.Id ?? ""; } catch { }
                ImGui.Text($"Biome.Id: {biomeId}");

                ImGui.Separator();
                ImGui.Text("Content:");
                if (node.Content.Count == 0)
                    ImGui.TextDisabled("  (none)");
                else
                    foreach (var (_, c) in node.Content)
                        ImGui.TextUnformatted($"  {c.Name}");

                ImGui.Text("Biomes:");
                var biomeNames = node.Biomes.Where(x => x.Value != null).Select(x => x.Value.Name).ToList();
                if (biomeNames.Count == 0)
                    ImGui.TextDisabled("  (none)");
                else
                    foreach (var b in biomeNames)
                        ImGui.TextUnformatted($"  {b}");

                ImGui.Separator();
                if (ImGui.Button("Close##nd")) debugNodeOpen = false;
            }
            ImGui.End();

            if (!debugNodeOpen) debugNode = null;
        } catch (Exception e) {
            LogError("Error drawing node debug panel: " + e.Message);
            debugNodeOpen = false;
            debugNode = null;
        }
    }

    private void DrawWaypointPanel() {
        Vector2 panelSize = new Vector2(UI.SettingsPanel.GetClientRect().Width, UI.SettingsPanel.GetClientRect().Height);
        Vector2 panelPosition = UI.SettingsPanel.GetClientRect().TopLeft;
        ImGui.SetNextWindowPos(panelPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(panelSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.8f);

        ImGui.Begin("WaypointPanel", ref WaypointPanelIsOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove);

        // Settings table
        if (ImGui.BeginTable("waypoint_top_table", 2, ImGuiTableFlags.NoBordersInBody|ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 60);                                                               
            ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 300);                     

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            bool _show = Settings.Waypoints.ShowWaypoints;
            if(ImGui.Checkbox($"##show_waypoints", ref _show))                        
                Settings.Waypoints.ShowWaypoints = _show;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoints on Atlas");

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool _showArrows = Settings.Waypoints.ShowWaypointArrows;
            if(ImGui.Checkbox($"##show_arrows", ref _showArrows))                        
                Settings.Waypoints.ShowWaypointArrows = _showArrows;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoint Arrows on Atlas");

            ImGui.TableNextRow();
        }
        ImGui.EndTable();

        ImGui.Spacing();

        // larger font size
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 10));
        ImGui.Text("Waypoints");
        ImGui.PopStyleVar();        
        ImGui.Separator();


        #region Waypoints Table
        // Collapse
        if (ImGui.CollapsingHeader("Waypoints", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var flags = ImGuiTableFlags.BordersInnerH;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0, 0, 0, 0));
            if (ImGui.BeginTable("waypoint_list_table", 9, flags))//, new Vector2(-1, panelSize.Y/3)))
            {
                ImGui.TableSetupColumn("Enable", ImGuiTableColumnFlags.WidthFixed, 50);                                                               
                ImGui.TableSetupColumn("Waypoint Name", ImGuiTableColumnFlags.WidthFixed, 280);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 40);                    
                ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 40);     
                ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 40);     
                ImGui.TableSetupColumn("Scale", ImGuiTableColumnFlags.WidthFixed, 70);     
                ImGui.TableSetupColumn("Opt", ImGuiTableColumnFlags.WidthFixed, 80); 
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 50);
                ImGui.TableHeadersRow();                    

                foreach (var waypoint in Settings.Waypoints.Waypoints.Values) {
                    string id = waypoint.Address.ToString();
                    ImGui.PushID(id);
                    
                    ImGui.TableNextRow();

                    // Enabled
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    bool _show = waypoint.Show;
                    if (ImGui.Checkbox($"##{id}_enabled", ref _show)) {
                        waypoint.Show = _show;
                    }

                    // Name
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(300);
                    string _name = waypoint.Name;                    
                    if (ImGui.InputText($"##{id}_name", ref _name, 32)) {
                        waypoint.Name = _name;
                    }

                    // steps
                    ImGui.TableNextColumn();
                    int steps = waypoint.PathFromStart?.Count > 0 ? waypoint.PathFromStart.Count - 1 : -1;
                    if (steps >= 0)
                    {
                        // Optionally color-code based on distance
                        Color stepsColor = steps <= 3 ? Color.Green : (steps <= 7 ? Color.Yellow : Color.Red);
                        Vector4 stepsColorVector = new Vector4(stepsColor.R / 255.0f, stepsColor.G / 255.0f, stepsColor.B / 255.0f, stepsColor.A / 255.0f);
                        ImGui.PushStyleColor(ImGuiCol.Text, stepsColorVector);
                        ImGui.Text(steps.ToString());
                        ImGui.PopStyleColor();
                    }
                    else
                    {
                        ImGui.Text("-");
                    }


                    // Coordinates
                    ImGui.TableNextColumn();                    
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.X.ToString());

                    ImGui.TableNextColumn();                    
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.Y.ToString());


                    // Color
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    Color _color = waypoint.Color;
                    Vector4 _vector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{id}_nodecolor", ref _vector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        waypoint.Color = Color.FromArgb((int)(_vector.W * 255), (int)(_vector.X * 255), (int)(_vector.Y * 255), (int)(_vector.Z * 255));
                    
                    // Scale
                    ImGui.TableNextColumn();
                    float _scale = waypoint.Scale;
                    ImGui.SetNextItemWidth(70);
                    if(ImGui.SliderFloat($"##{id}_weight", ref _scale, 0.1f, 2.0f, "%.2f"))                        
                        waypoint.Scale = _scale;


                    // Buttons
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 50.0f) / 2.0f);
                    ImGui.SetNextItemWidth(50);
                    if (ImGui.Button("Del")) {
                        RemoveWaypoint(waypoint);
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                ImGui.PopStyleColor();
            }
            #endregion
            
        }
       
        ImGui.Spacing();

        #region Atlas Table
        if (ImGui.CollapsingHeader("Atlas"))
        {
            

            // Sort by Combobox
            ImGui.Text("Sort: ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            string sortBy = Settings.Waypoints.WaypointPanelSortBy;
            if (ImGui.BeginCombo("##sortByCombo", sortBy))
            {
                if (ImGui.Selectable("Name", sortBy == "Name"))
                    sortBy = "Name";
                if (ImGui.Selectable("Weight", sortBy == "Weight"))
                    sortBy = "Weight";
                if (ImGui.Selectable("Steps", sortBy == "Steps"))
                    sortBy = "Steps";

                Settings.Waypoints.WaypointPanelSortBy = sortBy;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Text("then ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            string sortBy2 = Settings.Waypoints.WaypointPanelSortBy2;
            if (ImGui.BeginCombo("##sortByCombo2", sortBy2))
            {
                if (ImGui.Selectable("None", sortBy2 == "None"))
                    sortBy2 = "None";
                if (ImGui.Selectable("Name", sortBy2 == "Name"))
                    sortBy2 = "Name";
                if (ImGui.Selectable("Weight", sortBy2 == "Weight"))
                    sortBy2 = "Weight";
                if (ImGui.Selectable("Steps", sortBy2 == "Steps"))
                    sortBy2 = "Steps";

                Settings.Waypoints.WaypointPanelSortBy2 = sortBy2;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            ImGui.Text("Max Items: ");
            ImGui.SameLine();
            int maxItems = Settings.Waypoints.WaypointPanelMaxItems;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxItems", ref maxItems))
                Settings.Waypoints.WaypointPanelMaxItems = maxItems;

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            ImGui.Text("Max Steps:");
            ImGui.SameLine();
            int maxSteps = Settings.Waypoints.WaypointPanelMaxSteps;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxSteps", ref maxSteps))
                Settings.Waypoints.WaypointPanelMaxSteps = Math.Max(0, maxSteps);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only show maps within this many steps of the explored region. 0 = unlimited.");

            ImGui.Separator();

            ImGui.Text("Search: ");
            ImGui.SameLine();
            string regex = Settings.Waypoints.WaypointPanelFilter;
            ImGui.SetNextItemWidth(250);
            if (ImGui.InputText("##search", ref regex, 32, ImGuiInputTextFlags.EnterReturnsTrue)) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemDeactivatedAfterEdit()) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip("Searches for map names and/or mod text. Press enter to search.");
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();
            bool useRegex = Settings.Waypoints.WaypointsUseRegex;
            if (ImGui.Checkbox("Regex", ref useRegex))
                Settings.Waypoints.WaypointsUseRegex = useRegex;
            
            ImGui.Separator();
        
            var tempCache = SnapshotMapCache()
                .Where(x => !x.Value.IsVisited && (!Settings.Waypoints.ShowUnlockedOnly || x.Value.IsUnlocked))
                .ToDictionary(x => x.Key, x => x.Value);
            // if search isnt blank
            if (!string.IsNullOrEmpty(Settings.Waypoints.WaypointPanelFilter)) {
                if (useRegex) {
                    tempCache = tempCache.Where(x => Regex.IsMatch(x.Value.Name, Settings.Waypoints.WaypointPanelFilter, RegexOptions.IgnoreCase) || x.Value.MatchEffect(Settings.Waypoints.WaypointPanelFilter) || x.Value.Content.Any(x => x.Value.Name == Settings.Waypoints.WaypointPanelFilter)).AsParallel().ToDictionary(x => x.Key, x => x.Value);
                } else {
                    tempCache = tempCache.Where(x => x.Value.Name.Contains(Settings.Waypoints.WaypointPanelFilter, StringComparison.CurrentCultureIgnoreCase) || x.Value.MatchEffect(Settings.Waypoints.WaypointPanelFilter) || x.Value.Content.Any(x => x.Value.Name == Settings.Waypoints.WaypointPanelFilter)).AsParallel().ToDictionary(x => x.Key, x => x.Value);
                }
            }

            // Step counts (steps from the explored region) for every reachable node, computed once.
            // Used for both the Steps sort options and the Steps column display below.
            var stepCounts = ComputeStepCounts();
            int GetSteps(Node n) => stepCounts.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;

            // Limit to maps within N steps of the explored region (0 = unlimited).
            int maxStepsFilter = Settings.Waypoints.WaypointPanelMaxSteps;
            if (maxStepsFilter > 0)
                tempCache = tempCache.Where(x => GetSteps(x.Value) <= maxStepsFilter).ToDictionary(x => x.Key, x => x.Value);

            // Two-level sort: primary, then optional secondary tiebreak. Lets the user e.g. sort by
            // Weight (desc) then Steps (asc) to surface the highest-weight map with the fewest steps.
            var ordered = sortBy switch
            {
                "Name" => tempCache.OrderBy(x => x.Value.Name),
                "Steps" => tempCache.OrderBy(x => GetSteps(x.Value)),
                _ => tempCache.OrderByDescending(x => x.Value.Weight),
            };
            ordered = sortBy2 switch
            {
                "Name" => ordered.ThenBy(x => x.Value.Name),
                "Weight" => ordered.ThenByDescending(x => x.Value.Weight),
                "Steps" => ordered.ThenBy(x => GetSteps(x.Value)),
                _ => ordered,
            };
            tempCache = ordered.Take(maxItems).ToDictionary(x => x.Key, x => x.Value);

            var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.NoSavedSettings;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2)); // Adjust the padding values as needed
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2)); // A
            if (ImGui.BeginTable("atlas_list_table", 8, flags))//, new Vector2(-1, panelSize.Y/3)))
            {                                                            
                ImGui.TableSetupColumn("Map Name", ImGuiTableColumnFlags.WidthFixed, 200);   
                ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Modifiers", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Unlocked", ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableSetupColumn("Way", ImGuiTableColumnFlags.WidthFixed, 32);
                ImGui.TableHeadersRow();                    

                Vector4 _colorVector;
                Color _color;

                if (tempCache != null) {
                    foreach (var (key, node) in tempCache) {
                        string id = node.Address.ToString();
                        ImGui.PushID(id);                        
                        ImGui.TableNextRow();

                        // Name
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(node.Name);

                        // Content — kept near full size; 0.7 was unreadable.
                        ImGui.SetWindowFontScale(0.9f);
                        ImGui.TableNextColumn();
                        foreach(var (k,content) in node.Content) {
                            _color = content.Color;
                            _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                            ImGui.TextUnformatted(content.Name);
                            ImGui.PopStyleColor();
                        }

                        // Modifiers
                        ImGui.SetWindowFontScale(0.7f);
                        ImGui.TableNextColumn();
                        foreach(var effect in node.Effects) {       
                            _color = Settings.MapMods.MapModTypes[effect.Key].Color;
                            _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                            ImGui.TextUnformatted(effect.Value.ToString());
                            ImGui.PopStyleColor();
                        }
                        // reset font size
                        ImGui.SetWindowFontScale(1.0f);
                        // Steps — graph distance from the explored region (see ComputeStepCounts),
                        // shown for every node so it stays consistent with the Steps sort options.
                        ImGui.TableNextColumn();
                        int steps = GetSteps(node);
                        if (steps >= 0 && steps != int.MaxValue)
                        {
                            // Color-code based on distance
                            Color stepsColor = steps <= 3 ? Color.Green : (steps <= 7 ? Color.Yellow : Color.Red);
                            Vector4 stepsColorVector = new Vector4(stepsColor.R / 255.0f, stepsColor.G / 255.0f, stepsColor.B / 255.0f, stepsColor.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, stepsColorVector);
                            ImGui.TextUnformatted(steps.ToString());
                            ImGui.PopStyleColor();
                        }
                        else
                        {
                            ImGui.TextUnformatted("-");
                        }
                        // Weight
                        ImGui.TableNextColumn();
                        // set color
                        _color = GetWeightColor(node.Weight);
                        _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                        ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                        ImGui.TextUnformatted(node.Weight.ToString("0.0"));
                        ImGui.PopStyleColor();

                        

                        // Unlocked
                        ImGui.TableNextColumn();
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                        bool _unlocked = node.IsUnlocked;
                        ImGui.BeginDisabled();                        
                        ImGui.Checkbox($"##{id}_enabled", ref _unlocked);
                        ImGui.EndDisabled();
    //
                        // Buttons
                        ImGui.TableNextColumn();
                        RectangleF icon = SpriteHelper.GetUV(MapIconsIndex.Waypoint);
                        
                        if (!node.IsWaypoint){
                            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TableRowBg));
                            if (ImGui.ImageButton($"$${id}_wp", iconsId, new Vector2(32,32), icon.TopLeft, icon.BottomRight)) {
                                AddWaypoint(node);
                            } else if (ImGui.IsItemHovered()) {
                                ImGui.SetTooltip("Add Waypoint");
                            }
                            ImGui.PopStyleColor();
                        }

                        ImGui.PopID();
                    }
                }
            }
            ImGui.EndTable();
            ImGui.PopStyleVar(2);
            #endregion
            
        }

        ImGui.End();
    }
    #endregion

    #region Waypoint Functions
    private void DrawWaypoint(Waypoint waypoint) {
        var mapNode = waypoint.MapNode();
        if (!Settings.Waypoints.ShowWaypoints || mapNode == null || !waypoint.Show)
            return;

        RectangleF nodeRect = ApplyAtlasScreenOffset(mapNode.Element.GetClientRect());
        if (!IsOnScreen(nodeRect.Center))
            return;

        Vector2 waypointSize = new Vector2(48, 48);
        waypointSize *= waypoint.Scale;

        Vector2 iconPosition = nodeRect.Center - new Vector2(0, nodeRect.Height / 2);

        if (mapNode.Element.GetChildAtIndex(0) != null)
            iconPosition -= new Vector2(0, mapNode.Element.GetChildAtIndex(0).GetClientRect().Height);

        iconPosition -= new Vector2(0, 20);
        Vector2 waypointTextPosition = iconPosition - new Vector2(0, 10);
        // Add step count to waypoint label if available
        string displayText = waypoint.StepCount >= 0
            ? $"{waypoint.Name} ({waypoint.StepCount} steps)"
            : waypoint.Name;

        DrawCenteredTextWithBackground(displayText, waypointTextPosition, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

        // Draw the path if enabled
        if (Settings.Graphics.ShowPaths)
            DrawWaypointPath(waypoint);

        iconPosition -= new Vector2(waypointSize.X / 2, 0);
        RectangleF iconSize = new RectangleF(iconPosition.X, iconPosition.Y, waypointSize.X, waypointSize.Y);
        Graphics.DrawImage(IconsFile, iconSize, SpriteHelper.GetUV(waypoint.Icon), waypoint.Color);


    }

    private void AddWaypoint(Node cachedNode) {
        if (Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Waypoint newWaypoint = cachedNode.ToWaypoint();
        newWaypoint.Icon = MapIconsIndex.LootFilterLargeWhiteUpsideDownHouse;
        newWaypoint.Color = GetWeightColor(cachedNode.Weight);

        Settings.Waypoints.Waypoints.Add(cachedNode.Coordinates.ToString(), newWaypoint);
        UpdateWaypointPaths();
    }

    /// MARK: SyncFavoriteWaypoints
    /// Keeps auto-created waypoints in sync with favorite map types. Adds waypoints for favorite,
    /// non-visited nodes and removes auto-created waypoints whose map is no longer a favorite (or
    /// removes all auto-created waypoints when the feature is disabled). Manual waypoints
    /// (AutoCreated == false) are never touched. Does not call UpdateWaypointPaths — the caller
    /// (RefreshMapCache) does that once after this returns.
    private void SyncFavoriteWaypoints() {
        try {
            // Snapshot favorite, non-visited nodes under the lock (background job mutates mapCache).
            Dictionary<string, Node> favorites;
            lock (mapCacheLock)
                favorites = mapCache.Values
                    .Where(x => x.IsFavorited && !x.IsVisited)
                    .GroupBy(x => x.Coordinates.ToString())
                    .ToDictionary(g => g.Key, g => g.First());

            if (!Settings.Waypoints.AutoWaypointFavorites) {
                // Feature off: clean up everything we auto-created.
                foreach (var key in Settings.Waypoints.Waypoints.Where(x => x.Value.AutoCreated).Select(x => x.Key).ToList())
                    Settings.Waypoints.Waypoints.Remove(key);
                return;
            }

            // Add waypoints for favorites that don't have one yet.
            foreach (var (key, node) in favorites) {
                if (Settings.Waypoints.Waypoints.ContainsKey(key))
                    continue;

                Waypoint newWaypoint = node.ToWaypoint();
                newWaypoint.Icon = MapIconsIndex.LootFilterLargeWhiteUpsideDownHouse;
                newWaypoint.Color = Settings.Graphics.PathLineColor;
                newWaypoint.AutoCreated = true;
                Settings.Waypoints.Waypoints.Add(key, newWaypoint);
            }

            // Remove auto-created waypoints whose map is no longer a favorite.
            foreach (var key in Settings.Waypoints.Waypoints
                .Where(x => x.Value.AutoCreated && !favorites.ContainsKey(x.Key))
                .Select(x => x.Key).ToList())
                Settings.Waypoints.Waypoints.Remove(key);
        } catch (Exception e) {
            LogError("Error syncing favorite waypoints: " + e.Message);
        }
    }

    private void RemoveWaypoint(Node cachedNode) {
        if (!Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Settings.Waypoints.Waypoints.Remove(cachedNode.Coordinates.ToString());
        UpdateWaypointPaths();
    }
    private void RemoveWaypoint(Waypoint waypoint) {
        Settings.Waypoints.Waypoints.Remove(waypoint.Coordinates.ToString());
        UpdateWaypointPaths();
    }

    private void DrawWaypointArrow(Waypoint waypoint) {
        var mapNode = waypoint.MapNode();
        if (!Settings.Waypoints.ShowWaypointArrows || mapNode == null)
            return;

        Vector2 waypointPosition = ApplyAtlasScreenOffset(mapNode.Element.GetClientRect()).Center;

        float distance = Vector2.Distance(screenCenter, waypointPosition);

        if (distance < 400)
            return;

        Vector2 arrowSize = new(64, 64);
        Vector2 arrowPosition = waypointPosition;
        arrowPosition.X = Math.Clamp(arrowPosition.X, 0, GameController.Window.GetWindowRectangleTimeCache.Size.X);
        arrowPosition.Y = Math.Clamp(arrowPosition.Y, 0, GameController.Window.GetWindowRectangleTimeCache.Size.Y);
        arrowPosition = Vector2.Lerp(screenCenter, arrowPosition, 0.80f);
        arrowPosition -= new Vector2(arrowSize.X / 2, arrowSize.Y / 2);

        Vector2 direction = waypointPosition - screenCenter;
        float phi = (float)Math.Atan2(direction.Y, direction.X) + (float)(Math.PI / 2);

        Color color = Color.FromArgb(255, waypoint.Color);
        DrawRotatedImage(arrowId, arrowPosition, arrowSize, phi, color);
         Vector2 textPosition = arrowPosition + new Vector2(arrowSize.X / 2, arrowSize.Y / 2);
        textPosition = Vector2.Lerp(textPosition, screenCenter, 0.10f);
        if (Settings.Waypoints.InverWaypointArrowsColors)
        {
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition,  Settings.Graphics.BackgroundColor, color, true, 10, 4);
        }
        else
        {
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition, color, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
    }

    #endregion



}
