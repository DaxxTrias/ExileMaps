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
    
    public IngameUIElements UI;
    public AtlasPanel AtlasPanel;

    private Vector2 screenCenter;
    private List<Node> selectedNodes = [];
    private RectangleF cachedScreenRect;
    private readonly List<RectangleF> cachedTooltipRects = [];
    private Dictionary<Vector2i, Node> mapCache = [];
    public bool refreshCache = false;
    private int cacheTicks = 0;
    private bool refreshingCache = false;
    private bool clearCacheOnNextRefresh;
    private Job? refreshCacheJob;
    private int atlasWarmupTicks;
    private float maxMapWeight = 20.0f;
    private float minMapWeight = -20.0f;
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

    private bool AtlasHasBeenClosed = true;
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

        // Coalesce settings-change weight recalcs. Slider drags fire PropertyChanged many times per
        // second; instead of recomputing bounds on each, mark dirty and recompute at most ~4x/sec.
        if (weightsDirty && !refreshingCache && DateTime.Now.Subtract(lastWeightRecalc).TotalMilliseconds > 250) {
            weightsDirty = false;
            lastWeightRecalc = DateTime.Now;
            RecalculateWeights();
        }

        // Waypoint paths only change when nodes get visited (which triggers a cache refresh) or when
        // waypoints are added/removed. RefreshMapCache and Add/RemoveWaypoint already recompute them,
        // so there's no need to rerun the full per-waypoint BFS + closest-visited-node scan every tick.

        return;
    }

    public override void Render()
    {
        CheckKeybinds();

        if (WaypointPanelIsOpen) DrawWaypointPanel();

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
        RegisterHotkey(Settings.Keybinds.ToggleWaypointPanelHotkey);
        RegisterHotkey(Settings.Keybinds.AddWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.DeleteWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.ShowTowerRangeHotkey);
        RegisterHotkey(Settings.Keybinds.UpdateMapsKey);
        RegisterHotkey(Settings.Keybinds.ToggleLockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleUnlockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleVisitedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleHiddenNodesHotkey);
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

        if (Settings.Keybinds.RefreshMapCacheHotkey.PressedOnce()) {
            RequestMapCacheRefresh(clearCache: true);
        }

        if (Settings.Keybinds.DebugKey.PressedOnce())        
            DoDebugging();

        if (Settings.Keybinds.UpdateMapsKey.PressedOnce())        
            UpdateMapData();

        if (Settings.Keybinds.ToggleWaypointPanelHotkey.PressedOnce()) {  
            WaypointPanelIsOpen = !WaypointPanelIsOpen;
        }

        if (Settings.Keybinds.AddWaypointHotkey.PressedOnce())        
            AddWaypoint(GetClosestNodeToCursor());

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
                    existingMap.IDs = map.IDs;
                    existingMap.ShortestId = map.ShortestId;
                    Settings.MapTypes.Maps.TryAdd(key, existingMap);                
                } else {
                    // add new map
                    Settings.MapTypes.Maps.TryAdd(key, map);
                }
            }
        } catch (Exception e) {
            LogError("Error loading default maps: " + e.Message + "\n" + e.StackTrace);
        }
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
        using (Graphics.SetTextScale(Settings.MapMods.MapModScale))
            DrawCenteredTextWithBackground(BuildNodeDebugText(cachedNode), cachedNode.MapNode.Element.GetClientRect().Center, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

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

    private void UpdateMapData() {
        if (AtlasPanel == null)
            return;

        Settings.MapTypes.Maps ??= [];

        var mapData = SnapshotAtlasDescriptions()
            .Select(node => TryGetAtlasNodeArea(node, out WorldArea area)
                ? new { Name = area.Name?.Trim() ?? string.Empty, Id = area.Id?.Trim() ?? string.Empty }
                : null)
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Id))
            .ToList();

        // iterate through each name and find the ID for all mapes iwth that name
        foreach (var mapGroup in mapData.GroupBy(x => x!.Name)) {
            var name = mapGroup.Key;
            var mapIds = mapGroup
                .Select(x => x!.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // get shortest item from list
            var shortID = mapIds.OrderBy(x => x.Length).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(shortID))
                continue;

            if (Settings.MapTypes.Maps.TryGetValue(name.Replace(" ", ""), out Map mapType) || Settings.MapTypes.Maps.TryGetValue(shortID, out mapType)) {        
                Settings.MapTypes.Maps.Remove(name.Replace(" ", ""));                
                Settings.MapTypes.Maps.TryAdd(shortID, mapType);
                mapType.IDs = [.. mapIds];
                mapType.ShortestId = shortID;
                LogMessage($"Updated Map Data for {shortID}");
            } else {
                var newMap = new Map { 
                    Name = name, 
                    IDs = [.. mapIds],
                    ShortestId = shortID};
        
                Settings.MapTypes.Maps.TryAdd(shortID, newMap);        
                LogMessage($"Added Map Data for {shortID}");    
            }
        }

        var json = JsonSerializer.Serialize(Settings.MapTypes.Maps, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(DirectoryFullName, defaultMapsPath), json);

        LogMessage("Updated Map Data");
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
        var closestNode = AtlasPanel.Descriptions.OrderBy(x => Vector2.Distance(GameController.Game.IngameState.UIHoverElement.GetClientRect().Center, x.Element.GetClientRect().Center)).AsParallel().FirstOrDefault();

        if (closestNode == null)
            return null;

        if (TryGetCachedNode(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else
            return null;
    }

    private Node GetClosestNodeToCenterScreen() {
        var closestNode = AtlasPanel.Descriptions.OrderBy(x => Vector2.Distance(screenCenter, x.Element.GetClientRect().Center)).AsParallel().FirstOrDefault();
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

    private static bool TryGetNodeRect(Node? node, out RectangleF rect)
    {
        rect = default;
        try
        {
            if (node?.MapNode?.Element == null)
                return false;

            rect = node.MapNode.Element.GetClientRect();
            return rect.Width > 1 && rect.Height > 1;
        }
        catch
        {
            return false;
        }
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

            RecalculateWeights();
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

            // Draw path line with a different color/style to distinguish from regular connections
            Graphics.DrawLine(start, end, Settings.Graphics.WaypointLineWidth , Settings.Graphics.PathLineColor);
        }
    }

    private void RecalculateWeights() {

        // Snapshot under the lock; the background refresh job mutates mapCache concurrently.
        List<float> weights;
        lock (mapCacheLock)
            weights = mapCache.Values.Where(x => !x.IsVisited).Select(x => x.Weight).Distinct().ToList();

        if (weights.Count == 0) {
            minMapWeight = 0f;
            maxMapWeight = 1f;
            return;
        }

        // Single ascending sort, then index both ends (was two separate OrderBy passes).
        // Clip outliers for color normalization: 6th-smallest and 11th-largest when enough samples.
        weights.Sort();
        int n = weights.Count;
        minMapWeight = n > 5 ? weights[5] : weights[0];
        maxMapWeight = n > 10 ? weights[n - 11] : weights[n - 1];

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

            } catch (Exception e) {
                LogError($"Error getting Content for map type {node.Address.ToString("X")}: " + e.Message);
            }
            
            // Set Biomes
            try {
                var biomes = newNode.MapType.Biomes.Where(x => x != "").ToList();

                foreach (var biome in biomes)                     
                    if (Settings.Biomes.Biomes.TryGetValue(biome, out Biome newBiome)) 
                        newNode.Biomes.TryAdd(newBiome.Name, newBiome);

            }   catch (Exception e) {
                LogError($"Error getting Biomes for map type {mapId}: " + e.Message);
            }
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

        // Tower tablet mods have been removed from the game, so effect scanning is disabled.
        cachedNode.Effects.Clear();

        if (Settings.Features.RecalculateNodeWeightsOnRefresh)
            cachedNode.RecalculateWeight();
        return 1;
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

            if (!IsOnScreen(destinationPos.Center) || !IsOnScreen(nodeCurrentPosition.Center))
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
            
        var radius = (nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 4 * Settings.Graphics.NodeRadius;
        var fraction = GetNormalizedWeight(cachedNode.Weight);
        Color color = Settings.MapTypes.ColorNodesByWeight ? ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, fraction) : cachedNode.MapType.NodeColor;
        Graphics.DrawCircleFilled(nodeCurrentPosition.Center, radius, color, 16);
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
        Vector2 position = new(nodeCurrentPosition.Center.X + offsetX, nodeCurrentPosition.Center.Y);

        DrawCenteredTextWithBackground($"{(int)(fraction*100)}%", position, ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, fraction), Settings.Graphics.BackgroundColor, true, 10, 3);
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
        
        if (Settings.MapTypes.UseWeightColorsForMapNames) {
            float fraction = GetNormalizedWeight(cachedNode.Weight);
            fontColor = ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, fraction);
        }

        DrawCenteredTextWithBackground(cachedNode.Name.ToUpper(), nodeCurrentPosition.Center, fontColor, backgroundColor, true, 10, 3);
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

    // Map tooltip can live under WorldMap child 13 or 14; checked in IsOnScreen.
    private static readonly int[] TooltipChildIndices = { 13, 14 };

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

            // Don't render over the map tooltip. It can live under child 13 or 14;
            // child 14 doesn't always exist, so each lookup is null-guarded.
            cachedTooltipRects.Clear();
            foreach (var tooltipIndex in TooltipChildIndices) {
                var tooltip = UI.WorldMap.GetChildAtIndex(tooltipIndex);
                if (tooltip == null || !tooltip.IsVisible)
                    continue;

                RectangleF mapTooltip = tooltip.GetClientRect();
                mapTooltip.Inflate(mapTooltip.Width * 0.1f, mapTooltip.Height * 0.1f);
                cachedTooltipRects.Add(mapTooltip);
            }
        } catch (Exception e) {
            // Keep last good bounds on a failed memory read rather than blanking the overlay.
            LogError("Error updating screen bounds: " + e.Message);
        }
    }

    private bool IsOnScreen(Vector2 position)
    {
        foreach (var tooltip in cachedTooltipRects)
            if (tooltip.Contains(position))
                return false;

        return cachedScreenRect.Contains(position);
    }

    public float GetDistanceToNode(Node cachedNode)
    {
        return Vector2.Distance(screenCenter, cachedNode.MapNode.Element.GetClientRect().Center);
    }

    private float GetNormalizedWeight(float value)
    {
        // Normalize to [0,1] using current min/max, guarding against zero/NaN ranges
        float range = maxMapWeight - minMapWeight;
        if (range <= 1e-6f)
            return 0.5f;
        float t = (value - minMapWeight) / range;
        if (float.IsNaN(t) || float.IsInfinity(t))
            return 0.5f;
        return Math.Clamp(t, 0f, 1f);
    }
    
    #endregion
    #region Waypoint Panel
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
       
                Settings.Waypoints.WaypointPanelSortBy = sortBy;
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

            bool unlockedOnly = Settings.Waypoints.ShowUnlockedOnly;
            if (ImGui.Checkbox("Show Unlocked Maps Only", ref unlockedOnly))
                Settings.Waypoints.ShowUnlockedOnly = unlockedOnly;

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

            tempCache = sortBy switch
            {
                "Name" => tempCache.OrderBy(x => x.Value.Name).ToDictionary(x => x.Key, x => x.Value),
                "Weight" => tempCache.OrderByDescending(x => x.Value.Weight).ToDictionary(x => x.Key, x => x.Value),
                _ => tempCache.OrderByDescending(x => x.Value.Weight).ToDictionary(x => x.Key, x => x.Value),
            };
            tempCache = tempCache.Take(maxItems).ToDictionary(x => x.Key, x => x.Value);

            var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.NoSavedSettings;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2)); // Adjust the padding values as needed
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2)); // A
            if (ImGui.BeginTable("atlas_list_table", 8, flags))//, new Vector2(-1, panelSize.Y/3)))
            {                                                            
                ImGui.TableSetupColumn("Map Name", ImGuiTableColumnFlags.WidthFixed, 200);   
                ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, 60);     
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

                        ImGui.SetWindowFontScale(0.7f);            

                        // Content
                        ImGui.TableNextColumn();
                        foreach(var (k,content) in node.Content) {
                            _color = Settings.MapContent.ContentTypes[content.Name].Color;
                            _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                            ImGui.TextUnformatted(content.Name);
                            ImGui.PopStyleColor();
                        }
                        

                        // Modifiers
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
                        // Steps
                        ImGui.TableNextColumn();
                        string stepsText = "-";

                        // Instead of checking node.IsWaypoint, check if the node exists in the waypoints dictionary
                        string waypointKey = node.Coordinates.ToString();
                        if (Settings.Waypoints.Waypoints.TryGetValue(waypointKey, out Waypoint waypoint))
                        {
                            // We found a waypoint for this node
                            if (waypoint.PathFromStart != null && waypoint.PathFromStart.Count > 0)
                            {
                                int steps = waypoint.PathFromStart.Count - 1;
                                stepsText = steps.ToString();

                                // Optionally color-code based on distance
                                Color stepsColor = steps <= 3 ? Color.Green : (steps <= 7 ? Color.Yellow : Color.Red);
                                Vector4 stepsColorVector = new Vector4(stepsColor.R / 255.0f, stepsColor.G / 255.0f, stepsColor.B / 255.0f, stepsColor.A / 255.0f);
                                ImGui.PushStyleColor(ImGuiCol.Text, stepsColorVector);
                                ImGui.TextUnformatted(stepsText);
                                ImGui.PopStyleColor();
                            }
                            else
                            {
                                ImGui.TextUnformatted(stepsText);
                            }
                        }
                        else
                        {
                            ImGui.TextUnformatted(stepsText);
                        }
                        // Weight
                        ImGui.TableNextColumn();
                        // set color
                        float fraction = GetNormalizedWeight(node.Weight);        
                        _color = ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor,Settings.MapTypes.GoodNodeColor, fraction);
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

        RectangleF nodeRect = mapNode.Element.GetClientRect();
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

        float fraction = GetNormalizedWeight(cachedNode.Weight);
        Waypoint newWaypoint = cachedNode.ToWaypoint();
        newWaypoint.Icon = MapIconsIndex.LootFilterLargeWhiteUpsideDownHouse;
        newWaypoint.Color = ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, fraction);

        Settings.Waypoints.Waypoints.Add(cachedNode.Coordinates.ToString(), newWaypoint);
        UpdateWaypointPaths();
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

        Vector2 waypointPosition = mapNode.Element.GetClientRect().Center;

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
