using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal sealed class MultiplayerDiscoveryService
{
    private static readonly string[] SearchTerms =
    {
        "multiplayer",
        "lobby",
        "room",
        "settings",
        "track",
        "race",
        "event",
        "photon",
        "workshop",
        "map",
        "content",
        "host",
        "master",
        "chat",
        "message",
        "history",
        "window",
        "lobbyPanel",
        "lobbyFilters",
        "buttonCreateRoom",
        "createRoom",
        "quickPlay",
        "prefab"
    };

    private static readonly string[] KnownTypeNames =
    {
        "CurrentContentContainer",
        "InGameMenuMainPanel",
        "MultiplayerRaceScoreButtonPanel",
        "Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup",
        "Liftoff.Multiplayer.GameSetup.ContentSettingsPanel",
        "Liftoff.Multiplayer.GameSetup.RoomSettingsPanel",
        "Liftoff.Multiplayer.GameSetup.DroneSettingsPanel",
        "Liftoff.Multiplayer.GameSetup.GameModifiersPanel",
        "Liftoff.Multiplayer.GameContentEntry",
        "Liftoff.Multiplayer.LevelInitMultiplayer",
        "Liftoff.Multiplayer.GameSetup.LastMultiplayerSession",
        "TrackQuickInfo",
        "RaceQuickInfo",
        "ShareableContent",
        "Liftoff.Multiplayer.GameMode",
        "LiftoffDropdown",
        "Liftoff.Multiplayer.Chat.ChatHistory",
        "Liftoff.Multiplayer.Chat.ChatWindowPanel",
        "Liftoff.Multiplayer.Chat.ChatToggle",
        "Liftoff.Multiplayer.Chat.MessageEntryPanel",
        "Liftoff.Multiplayer.Chat.Messages.PlayerChatMessage",
        "Liftoff.Multiplayer.Chat.Messages.PlayerUpdateMessage",
        "Liftoff.Multiplayer.Chat.Messages.SystemChatMessage",
        // Not game types, but the chat send path resolves both by name on every
        // message. Listing them here means they come out of this one cached scan
        // instead of a GetTypes() sweep over every loaded assembly.
        "TMPro.TMP_InputField",
        "TMPro.TMP_Text"
    };

    private static readonly string[] NonGameAssemblyPrefixes =
    {
        "mscorlib",
        "netstandard",
        "System",
        "Microsoft.",
        "Mono.",
        "MonoMod",
        "0Harmony",
        "HarmonyX",
        "BepInEx"
    };

    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;
    private readonly Dictionary<string, Type> _knownTypes = new(StringComparer.Ordinal);
    private readonly List<DiscoveredTypeCandidate> _candidates = new();
    // Cache of field-name → matching types. The game's type system never changes at
    // runtime, so this full-assembly reflection scan only needs to run once per field
    // name. Without this cache, FindTypesByFieldName re-scanned every loaded type +
    // GetFields() on each, every call — and Capture() calls it 4×/sec, stalling the
    // main thread (in-game stutter).
    private readonly Dictionary<string, List<Type>> _typesByFieldNameCache = new(StringComparer.Ordinal);
    private bool _scanned;
    private bool _candidatesScored;

    public MultiplayerDiscoveryService(MultiplayerTrackControlLog log, Func<object?, string> describe)
    {
        _log = log;
        _describe = describe;
    }

    public IReadOnlyList<DiscoveredTypeCandidate> Candidates
    {
        get
        {
            EnsureCandidatesScored();
            return _candidates;
        }
    }

    /// <summary>
    /// Resolves <see cref="KnownTypeNames"/> against the loaded assemblies.
    /// Deliberately cheap: it only reads <see cref="Type.FullName"/> and never
    /// enumerates a type's members. The expensive member-level scan lives in
    /// <see cref="EnsureCandidatesScored"/>.
    /// </summary>
    public void Refresh(bool force = false)
    {
        if (_scanned && !force)
            return;

        _knownTypes.Clear();
        _candidates.Clear();
        _typesByFieldNameCache.Clear();
        _candidatesScored = false;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            foreach (var type in GetLoadableTypes(assembly))
                TryAddKnownType(type);
        }

        _scanned = true;
    }

    /// <summary>
    /// Builds the scored <see cref="Candidates"/> list on demand.
    ///
    /// Kept out of <see cref="Refresh"/> because it calls GetMethods()/GetProperties()
    /// on every type of every loaded assembly. Refresh() runs from Plugin.Awake(), which
    /// BepInEx invokes from UnityEngine.Application's static constructor — reflecting
    /// that broadly, that early, hard-crashed the game inside Mono's collector
    /// (SIGSEGV in GC_mark_from beneath RuntimeType.GetMethods). Only DumpDiscovery()
    /// ever reads these candidates, so nothing needs them at startup.
    /// </summary>
    private void EnsureCandidatesScored()
    {
        if (_candidatesScored)
            return;

        Refresh();
        _candidates.Clear();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || !IsScorableAssembly(assembly))
                continue;

            foreach (var type in GetLoadableTypes(assembly))
            {
                var candidate = ScoreType(type);
                if (candidate != null && candidate.Score > 0)
                    _candidates.Add(candidate);
            }
        }

        _candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        _candidatesScored = true;
    }

    /// <summary>
    /// The candidate scan looks for Liftoff's own multiplayer types, so the framework,
    /// BepInEx and Harmony/Cecil/MonoMod assemblies are pure noise in its results — and
    /// they are also the ones carrying the deep generic machinery that makes a blanket
    /// GetMethods() sweep risky. Skipping them makes the scan both cleaner and safer.
    /// </summary>
    private static bool IsScorableAssembly(Assembly assembly)
    {
        string? name;
        try
        {
            name = assembly.GetName().Name;
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrEmpty(name))
            return true;

        foreach (var prefix in NonGameAssemblyPrefixes)
        {
            if (name!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public Type? TryResolveKnownType(string fullName)
    {
        Refresh();
        _knownTypes.TryGetValue(fullName, out var type);
        return type;
    }

    public void DumpDiscovery(int topCount = 40)
    {
        EnsureCandidatesScored();
        _log.Info("DISCOVERY", $"Loaded assemblies: {AppDomain.CurrentDomain.GetAssemblies().Length}; candidates: {_candidates.Count}");

        foreach (var fullName in KnownTypeNames)
        {
            var type = TryResolveKnownType(fullName);
            if (type == null)
            {
                _log.Warn("DISCOVERY", $"Known type missing: {fullName}");
                continue;
            }

            _log.Info("DISCOVERY", $"Known type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            DumpInterestingMembers(type, 8);
            DumpLiveObjects(type, 2);
        }

        foreach (var candidate in _candidates.Take(topCount))
        {
            _log.Info("DISCOVERY", $"Candidate score={candidate.Score} type={candidate.Type.FullName} reasons={string.Join("|", candidate.Reasons)}");
            foreach (var method in candidate.Methods.Take(6))
                _log.Info("DISCOVERY", $"  method {ReflectionHelper.FormatMethodSignature(method)}");
            foreach (var property in candidate.Properties.Take(6))
                _log.Info("DISCOVERY", $"  property {property.PropertyType.Name} {candidate.Type.FullName}.{property.Name}");
        }
    }

    /// <summary>
    /// Scans all loaded assemblies for types containing specific field names.
    /// Useful for finding obfuscated lobby/menu controllers by their serialized Unity field names.
    /// </summary>
    public List<Type> FindTypesByFieldName(string fieldName, int maxResults = 10)
    {
        // Cached: the type system is static at runtime, so this expensive full-assembly
        // scan is memoised per field name. Key includes maxResults so differing caps
        // don't return a truncated cached list. See _typesByFieldNameCache.
        var cacheKey = maxResults == 10 ? fieldName : $"{fieldName}#{maxResults}";
        if (_typesByFieldNameCache.TryGetValue(cacheKey, out var cached))
            return cached;

        Refresh();
        var results = new List<Type>();

        foreach (var type in AppDomain.CurrentDomain
                     .GetAssemblies()
                     .Where(assembly => !assembly.IsDynamic)
                     .SelectMany(GetLoadableTypes))
        {
            try
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fields.Any(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal)))
                {
                    results.Add(type);
                    if (results.Count >= maxResults) break;
                }
            }
            catch { /* skip inaccessible types */ }
        }

        _typesByFieldNameCache[cacheKey] = results;
        return results;
    }

    /// <summary>
    /// Searches for a UI button on the main menu that navigates to the multiplayer lobby.
    /// Uses multiple strategies: field name patterns, GameObject names, label text scanning,
    /// and hierarchy path inspection.
    /// Returns the button and its parent instance if found.
    /// </summary>
    public (UnityEngine.UI.Button? button, object? parent) FindMainMenuMultiplayerButton()
    {
        Refresh();
        _log.Info("NAV_DISCOVERY", "Searching for main menu multiplayer navigation button...");

        // Strategy 1: Look for well-known field names that hint at multiplayer navigation
        var fieldPatterns = new[] { "buttonMultiplayer", "buttonOnlineMultiplayer", "buttonPlay", "multiplayerButton", "buttonOnline", "buttonMP" };
        foreach (var fieldName in fieldPatterns)
        {
            var types = FindTypesByFieldName(fieldName);
            foreach (var type in types)
            {
                var liveObjects = ReflectionHelper.GetLiveObjects(type);
                foreach (var instance in liveObjects)
                {
                    var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null) continue;
                    var button = field.GetValue(instance) as UnityEngine.UI.Button;
                    if (button != null && button.gameObject.activeInHierarchy)
                    {
                        _log.Info("NAV_DISCOVERY", $"Found navigation button via field '{fieldName}' on {type.FullName}: {ReflectionHelper.DescribeObjectIdentity(instance)}");
                        return (button, instance);
                    }
                }
            }
        }

        var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
        _log.Info("NAV_DISCOVERY", $"Field name search failed. Scanning {allButtons.Length} active buttons...");

        // Strategy 2: Check parent hierarchy names (e.g. MenuBarMultiplayer)
        foreach (var button in allButtons)
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;
            var parent = button.transform.parent;
            while (parent != null)
            {
                if (parent.name.IndexOf("Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    parent.name.IndexOf("multiplayer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _log.Info("NAV_DISCOVERY", $"Found button via parent '{parent.name}' on '{button.gameObject.name}': {ReflectionHelper.DescribeObjectIdentity(button)}");
                    return (button, null);
                }
                parent = parent.parent;
            }
        }

        // Strategy 3: Scan button GameObject names
        var nameHints = new[] { "multiplayer", "online", "multi", "lobby" };
        foreach (var hint in nameHints)
        {
            foreach (var button in allButtons)
            {
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var goName = button.gameObject.name;
                if (goName != null && goName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _log.Info("NAV_DISCOVERY", $"Found button by '{hint}' in GameObject name '{goName}': {ReflectionHelper.DescribeObjectIdentity(button)}");
                    return (button, null);
                }
            }
        }

        // Strategy 3: Check button child Text/TMP components for "multiplayer" or "online" text
        foreach (var button in allButtons)
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;
            var label = GetButtonLabel(button);
            if (label == null) continue;
            foreach (var hint in nameHints)
            {
                if (label.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _log.Info("NAV_DISCOVERY", $"Found button by '{hint}' in label text '{label}' on '{button.gameObject.name}': {ReflectionHelper.DescribeObjectIdentity(button)}");
                    return (button, null);
                }
            }
        }

        // Strategy 4: Check full hierarchy path for "multiplayer"
        foreach (var button in allButtons)
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;
            var path = GetHierarchyPath(button.transform);
            if (path.IndexOf("multiplayer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("online", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log.Info("NAV_DISCOVERY", $"Found button by hierarchy path '{path}': {ReflectionHelper.DescribeObjectIdentity(button)}");
                return (button, null);
            }
        }

        _log.Warn("NAV_DISCOVERY", $"No multiplayer navigation button found across {allButtons.Length} buttons.");
        return (null, null);
    }

    /// <summary>
    /// Logs all active buttons with their names, labels, and hierarchy paths.
    /// Useful for discovering the correct button to target for navigation.
    /// </summary>
    public void DumpAllActiveButtons()
    {
        var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
        _log.Info("BUTTON_DUMP", $"=== Active Button Dump ({allButtons.Length} buttons) ===");
        foreach (var button in allButtons)
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;
            var label = GetButtonLabel(button) ?? "(no label)";
            var path = GetHierarchyPath(button.transform);
            _log.Info("BUTTON_DUMP", $"  [{button.gameObject.name}] label=\"{label}\" path={path} id={button.GetInstanceID()}");
        }
        _log.Info("BUTTON_DUMP", "=== End Button Dump ===");
    }

    private static string? GetButtonLabel(UnityEngine.UI.Button button)
    {
        // Try Unity UI Text
        var text = button.GetComponentInChildren<UnityEngine.UI.Text>(includeInactive: false);
        if (text != null && !string.IsNullOrWhiteSpace(text.text))
            return text.text;

        // Try TextMeshPro via reflection (avoid hard dependency)
        var tmpComponents = button.GetComponentsInChildren<UnityEngine.Component>(includeInactive: false);
        foreach (var comp in tmpComponents)
        {
            if (comp == null) continue;
            var typeName = comp.GetType().Name;
            if (typeName.Contains("TextMeshPro") || typeName.Contains("TMP_Text"))
            {
                var textProp = comp.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                var val = textProp?.GetValue(comp) as string;
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }

        return null;
    }

    private static string GetHierarchyPath(UnityEngine.Transform t)
    {
        var parts = new List<string>();
        while (t != null)
        {
            parts.Add(t.name);
            t = t.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>
    /// Dumps types that have a prefabPopupMultiplayerSetup field — these are lobby host controllers
    /// that can instantiate the track-change popup even from the waiting room.
    /// </summary>
    public void DumpLobbyState()
    {
        Refresh();
        _log.Info("LOBBY_DISCOVERY", "=== Lobby State Discovery ===");

        // Find types with the popup prefab field (lobby host controller)
        var popupPrefabTypes = FindTypesByFieldName("prefabPopupMultiplayerSetup");
        _log.Info("LOBBY_DISCOVERY", $"Types with 'prefabPopupMultiplayerSetup' field: {popupPrefabTypes.Count}");
        foreach (var type in popupPrefabTypes)
        {
            _log.Info("LOBBY_DISCOVERY", $"  Type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            DumpAllMembers(type, "LOBBY_DISCOVERY", 20);
            DumpLiveObjects(type, 4);
        }

        // Find types with lobbyPanel field
        var lobbyPanelTypes = FindTypesByFieldName("lobbyPanel");
        _log.Info("LOBBY_DISCOVERY", $"Types with 'lobbyPanel' field: {lobbyPanelTypes.Count}");
        foreach (var type in lobbyPanelTypes)
        {
            _log.Info("LOBBY_DISCOVERY", $"  Type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            DumpAllMembers(type, "LOBBY_DISCOVERY", 20);
            DumpLiveObjects(type, 4);
        }

        // Find types with lobbyFilters field
        var lobbyFilterTypes = FindTypesByFieldName("lobbyFilters");
        _log.Info("LOBBY_DISCOVERY", $"Types with 'lobbyFilters' field: {lobbyFilterTypes.Count}");
        foreach (var type in lobbyFilterTypes)
        {
            _log.Info("LOBBY_DISCOVERY", $"  Type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            DumpAllMembers(type, "LOBBY_DISCOVERY", 20);
            DumpLiveObjects(type, 4);
        }

        _log.Info("LOBBY_DISCOVERY", "=== End Lobby State Discovery ===");
    }

    /// <summary>
    /// Dumps types related to game/room creation from the main menu.
    /// Looks for buttonCreateRoom and related multiplayer lobby creation UI.
    /// </summary>
    public void DumpMainMenuState()
    {
        Refresh();
        _log.Info("MENU_DISCOVERY", "=== Main Menu Discovery ===");

        // Find types with buttonCreateRoom field
        var createRoomTypes = FindTypesByFieldName("buttonCreateRoom");
        _log.Info("MENU_DISCOVERY", $"Types with 'buttonCreateRoom' field: {createRoomTypes.Count}");
        foreach (var type in createRoomTypes)
        {
            _log.Info("MENU_DISCOVERY", $"  Type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            DumpAllMembers(type, "MENU_DISCOVERY", 30);
            DumpLiveObjects(type, 4);
        }

        // Find types with buttonQuickPlay field
        var quickPlayTypes = FindTypesByFieldName("buttonQuickPlay");
        _log.Info("MENU_DISCOVERY", $"Types with 'buttonQuickPlay' field: {quickPlayTypes.Count}");
        foreach (var type in quickPlayTypes)
        {
            _log.Info("MENU_DISCOVERY", $"  Type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            DumpAllMembers(type, "MENU_DISCOVERY", 30);
            DumpLiveObjects(type, 4);
        }

        // Check LastMultiplayerSession
        var lastSessionType = TryResolveKnownType("Liftoff.Multiplayer.GameSetup.LastMultiplayerSession");
        if (lastSessionType != null)
        {
            _log.Info("MENU_DISCOVERY", $"LastMultiplayerSession type found: {lastSessionType.FullName}");
            DumpAllMembers(lastSessionType, "MENU_DISCOVERY", 20);
            DumpLiveObjects(lastSessionType, 4);
        }

        // Dump Photon connection state for context
        _log.Info("MENU_DISCOVERY", $"PhotonNetwork.IsConnected={Photon.Pun.PhotonNetwork.IsConnected}");
        _log.Info("MENU_DISCOVERY", $"PhotonNetwork.IsConnectedAndReady={Photon.Pun.PhotonNetwork.IsConnectedAndReady}");
        _log.Info("MENU_DISCOVERY", $"PhotonNetwork.InRoom={Photon.Pun.PhotonNetwork.InRoom}");
        _log.Info("MENU_DISCOVERY", $"PhotonNetwork.InLobby={Photon.Pun.PhotonNetwork.InLobby}");
        _log.Info("MENU_DISCOVERY", $"PhotonNetwork.NetworkClientState={Photon.Pun.PhotonNetwork.NetworkClientState}");

        _log.Info("MENU_DISCOVERY", "=== End Main Menu Discovery ===");
    }

    private void DumpAllMembers(Type type, string category, int limit)
    {
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var field in fields.Take(limit))
            _log.Info(category, $"  field {field.FieldType.Name} {type.Name}.{field.Name}");

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (var method in methods.Take(limit))
            _log.Info(category, $"  method {ReflectionHelper.FormatMethodSignature(method)}");

        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var property in properties.Take(limit))
            _log.Info(category, $"  property {property.PropertyType.Name} {type.Name}.{property.Name}");
    }

    private void DumpInterestingMembers(Type type, int limit)
    {
        var interestingMethods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => SearchTerms.Any(term => method.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            .Take(limit)
            .ToList();

        foreach (var method in interestingMethods)
            _log.Info("DISCOVERY", $"  method {ReflectionHelper.FormatMethodSignature(method)}");

        var interestingProperties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => SearchTerms.Any(term => property.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            .Take(limit)
            .ToList();

        foreach (var property in interestingProperties)
            _log.Info("DISCOVERY", $"  property {property.PropertyType.Name} {type.FullName}.{property.Name}");
    }

    private void DumpLiveObjects(Type type, int limit)
    {
        foreach (var liveObject in ReflectionHelper.GetLiveObjects(type).Take(limit))
        {
            _log.Info("DISCOVERY", $"  live {ReflectionHelper.DescribeObjectIdentity(liveObject)}");
            _log.Info("DISCOVERY", $"  snapshot {ReflectionHelper.SafeDescribe(_describe, liveObject)}");
        }
    }

    private void TryAddKnownType(Type type)
    {
        foreach (var fullName in KnownTypeNames)
        {
            if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
            {
                _knownTypes[fullName] = type;
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    /// <summary>
    /// Scores a single type, returning null if it cannot be reflected over. One
    /// unloadable type must not abort the whole scan.
    /// </summary>
    private static DiscoveredTypeCandidate? ScoreType(Type type)
    {
        try
        {
            return ScoreTypeCore(type);
        }
        catch
        {
            return null;
        }
    }

    private static DiscoveredTypeCandidate ScoreTypeCore(Type type)
    {
        var reasons = new List<string>();
        var score = 0;
        var fullName = type.FullName ?? type.Name;

        foreach (var term in SearchTerms)
        {
            if (fullName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 10;
                reasons.Add($"type:{term}");
            }
        }

        if (type.Namespace != null && type.Namespace.IndexOf("Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 20;
            reasons.Add("namespace:multiplayer");
        }

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var method in methods)
        {
            foreach (var term in SearchTerms)
            {
                if (method.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 3;
                    reasons.Add($"method:{term}");
                    break;
                }
            }
        }

        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var property in properties)
        {
            foreach (var term in SearchTerms)
            {
                if (property.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 2;
                    reasons.Add($"property:{term}");
                    break;
                }
            }
        }

        return new DiscoveredTypeCandidate(type, score, reasons.Distinct().ToList(), methods.ToList(), properties.ToList());
    }

    internal sealed class DiscoveredTypeCandidate
    {
        public DiscoveredTypeCandidate(Type type, int score, List<string> reasons, List<MethodInfo> methods, List<PropertyInfo> properties)
        {
            Type = type;
            Score = score;
            Reasons = reasons;
            Methods = methods;
            Properties = properties;
        }

        public Type Type { get; }
        public int Score { get; }
        public List<string> Reasons { get; }
        public List<MethodInfo> Methods { get; }
        public List<PropertyInfo> Properties { get; }
    }
}
