using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Photon.Pun;
using UnityEngine;
using UnityEngine.UI;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal sealed class MultiplayerChatService
{
    private static readonly string[] ChatTypeNames =
    {
        "Liftoff.Multiplayer.Chat.ChatHistory",
        "Liftoff.Multiplayer.Chat.ChatWindowPanel",
        "Liftoff.Multiplayer.Chat.MessageEntryPanel",
        "Liftoff.Multiplayer.Chat.ChatToggle"
    };

    private static readonly string[] SendMethodNames =
    {
        "SendUserMessage",
        "OnSubmit",
        "OnInputFieldEditEnded",
        "OnChatValueChange",
        "SendMessage",
        "SubmitMessage",
        "OnSend",
        "PostMessage",
        "Send",
        "Submit"
    };

    private static readonly string[] ChatToggleTypeNames =
    {
        "Liftoff.Multiplayer.Chat.ChatToggle"
    };

    private static readonly string[] ShowChatMethodNames =
    {
        "ShowChat",
        "OnShowChat",
        "Show"
    };

    private static readonly string[] CandidateSubmitButtonNames =
    {
        "send",
        "submit",
        "chat",
        "post"
    };

    private static readonly string[] PreferredInputFieldNames =
    {
        "input",
        "message",
        "chat"
    };

    private readonly MultiplayerTrackControlConfig _config;
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerHostStateDetector _hostDetector;
    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;

    // ── Send hot path ─────────────────────────────────────────────────────
    //
    // A chat send runs on Unity's main thread, so every lookup it repeats is a
    // frame the game does not render -- felt in the cockpit as a stutter. What
    // used to be redone for EVERY message:
    //
    //  - three Resources.FindObjectsOfTypeAll sweeps (the host snapshot, the
    //    chat toggle, the chat panel). Each walks every loaded UnityEngine
    //    object, loaded assets included.
    //  - up to five full AppDomain type sweeps, because "TMPro.TMP_InputField"
    //    and "TMPro.TMP_Text" are not names the discovery service knows, so
    //    ResolveType fell through to GetTypes() on every loaded assembly.
    //  - three GetComponentsInChildren walks over the panel subtree.
    //  - one GetMethods sweep per send-method candidate, plus an unconditional
    //    reflection dump of all of them purely to write a log line.
    //
    // All of it is stable for the lifetime of a scene, so it is resolved once
    // and revalidated through Unity's destroyed-object null: a scene reload
    // nulls the cached panel, which drops every per-scene cache with it. The
    // reflection caches below are keyed by type and outlive a scene change.
    private readonly Dictionary<string, Type?> _resolvedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MethodInfo>> _chatMethods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MethodInfo?> _zeroArgMethods = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dumpedSendCandidates = new(StringComparer.Ordinal);
    private Component? _cachedPanel;
    private Component[]? _cachedToggles;
    private bool _toggleUnavailable;
    private PanelHandles? _handles;

    /// <summary>
    /// The child components a send touches, resolved once per panel rather than
    /// once per message.
    /// </summary>
    private sealed class PanelHandles
    {
        public Component Owner = null!;
        public InputField[] InputFields = Array.Empty<InputField>();
        public Component[] TmpInputs = Array.Empty<Component>();
        public Component[] TmpTexts = Array.Empty<Component>();
    }

    public MultiplayerChatService(
        MultiplayerTrackControlConfig config,
        MultiplayerDiscoveryService discovery,
        MultiplayerHostStateDetector hostDetector,
        MultiplayerTrackControlLog log,
        Func<object?, string> describe)
    {
        _config = config;
        _discovery = discovery;
        _hostDetector = hostDetector;
        _log = log;
        _describe = describe;
    }

    public void DumpChatState()
    {
        foreach (var typeName in ChatTypeNames)
        {
            var type = ResolveType(typeName);
            if (type == null)
            {
                _log.Warn("CHAT", $"Known chat type missing: {typeName}");
                continue;
            }

            _log.Info("CHAT", $"Known chat type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            foreach (var liveObject in ReflectionHelper.GetLiveObjects(type).Take(3))
            {
                _log.Info("CHAT", $"  live {ReflectionHelper.DescribeObjectIdentity(liveObject)}");
                _log.Info("CHAT", $"  snapshot {ReflectionHelper.SafeDescribe(_describe, liveObject)}");
                DumpInterestingMembers(liveObject);
                DumpMessageCollection(liveObject);
            }
        }
    }

    public bool SendConfiguredMessage()
    {
        var text = _config.PendingChatMessage.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Warn("CHAT", "PendingChatMessage is empty. Nothing to send.");
            return false;
        }

        return TrySendMessage(text, source: "config");
    }

    public bool SendRaw(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return TrySendMessage(message.Trim(), source: "server");
    }

    public bool SendAnnouncement(string environmentName, string trackName, string raceName, string workshopId)
    {
        var template = _config.ChatMessageTemplate.Value ?? "Switching to {environment} / {race}";
        var message = template
            .Replace("{environment}", environmentName ?? string.Empty)
            .Replace("{track}", trackName ?? string.Empty)
            .Replace("{race}", raceName ?? string.Empty)
            .Replace("{workshop}", workshopId ?? string.Empty);

        if (string.IsNullOrWhiteSpace(message))
            return false;

        return TrySendMessage(message.Trim(), source: "announcement");
    }

    private bool TrySendMessage(string message, string source)
    {
        // The chat panel survives in the scene after a Photon disconnect but its internals
        // (chat history, message routing) are wired to the destroyed room. Invoking
        // OnChatValueChange/SendUserMessage on it spams TargetInvocationException once per
        // call. Skip cleanly when we're not actually in a room.
        //
        // PhotonNetwork.InRoom is a field read and is what Capture() itself falls back to,
        // while the full capture costs a FindObjectsOfTypeAll -- so only pay for the
        // capture when the cheap check says we are not in a room.
        if (!PhotonNetwork.InRoom)
        {
            var snapshot = _hostDetector.Capture();
            if (!snapshot.IsInMultiplayer)
            {
                _log.Info("CHAT", $"Skipping chat send via {source}: not in a multiplayer room ({snapshot.InMultiplayerReason}). Message=\"{message}\"");
                return false;
            }
        }

        _discovery.Refresh();
        _log.Info("CHAT", () => $"Attempting to send chat message via {source}: \"{message}\"");

        // A scene reload destroys the whole chat UI at once, so one dead handle means
        // every cache below it is stale. The (object) cast reads past Unity's
        // destroyed-object null: it asks "did we cache anything?", where IsAlive asks
        // "is what we cached still usable?".
        if ((object?)_cachedPanel != null && !IsAlive(_cachedPanel))
            InvalidateSceneCaches();

        EnsureChatWindowVisible();

        if (IsAlive(_cachedPanel))
        {
            if (TrySendViaPanel(_cachedPanel!, message))
                return true;

            // The route that worked before has stopped working; rediscover below.
            _log.Warn("CHAT", "The cached chat panel no longer accepts a send; rediscovering.");
            InvalidateSceneCaches();
            EnsureChatWindowVisible();
        }

        foreach (var panel in GetLiveChatObjects("Liftoff.Multiplayer.Chat.ChatWindowPanel"))
        {
            if (TrySendViaPanel(panel, message))
            {
                _cachedPanel = panel as Component;
                return true;
            }
        }

        foreach (var panel in GetLiveChatObjects("Liftoff.Multiplayer.Chat.MessageEntryPanel"))
        {
            if (TrySendViaPanel(panel, message))
            {
                _cachedPanel = panel as Component;
                return true;
            }
        }

        _log.Warn("CHAT", "No working chat send path was found. TODO: validate chat sender methods at runtime.");
        return false;
    }

    /// <summary>
    /// Drops every handle that belongs to the current scene. Type resolution goes
    /// with them because a negative result is cached too, and a scene load is
    /// exactly when a type that was missing (an assembly not loaded yet) may
    /// start resolving.
    /// </summary>
    private void InvalidateSceneCaches()
    {
        _cachedPanel = null;
        _cachedToggles = null;
        _toggleUnavailable = false;
        _handles = null;
        _resolvedTypes.Clear();
    }

    /// <summary>
    /// Unity reports a destroyed object as equal to null, so this doubles as the
    /// "has the scene been reloaded under us?" check.
    /// </summary>
    private static bool IsAlive(Component? component) => component != null;

    private static bool AllAlive(IEnumerable<Component> components)
    {
        // The loop variable is typed as Component on purpose: that is what makes
        // the comparison below use Unity's destroyed-object equality.
        foreach (Component component in components)
        {
            if (component == null)
                return false;
        }

        return true;
    }

    private PanelHandles GetHandles(Component panel)
    {
        if (_handles != null
            && ReferenceEquals(_handles.Owner, panel)
            && IsAlive(panel)
            && AllAlive(_handles.InputFields)
            && AllAlive(_handles.TmpInputs)
            && AllAlive(_handles.TmpTexts))
            return _handles;

        var tmpInputType = ResolveType("TMPro.TMP_InputField");
        var tmpTextType = ResolveType("TMPro.TMP_Text");

        _handles = new PanelHandles
        {
            Owner = panel,
            InputFields = panel.GetComponentsInChildren<InputField>(true)
                .OrderByDescending(input => ScoreInputField(input.name))
                .ToArray(),
            TmpInputs = tmpInputType == null
                ? Array.Empty<Component>()
                : panel.GetComponentsInChildren(tmpInputType, true),
            // The name filter is part of the lookup rather than the loop: which
            // TMP_Text objects are chat targets is a property of the hierarchy,
            // and the hierarchy is what this cache is keyed on.
            TmpTexts = tmpTextType == null
                ? Array.Empty<Component>()
                : panel.GetComponentsInChildren(tmpTextType, true)
                    .Where(tmpText => LooksLikeChatTextTargetName(tmpText.name))
                    .ToArray(),
        };

        return _handles;
    }

    private bool TrySendViaPanel(object panel, string message)
    {
        _log.Info("CHAT", () => $"Trying chat sender on {ReflectionHelper.DescribeObjectIdentity(panel)}");
        var populatedLiveInput = TryPopulateLiveInputFields(panel, message);
        if (!populatedLiveInput)
            TryPopulateKnownTextFields(panel, message);
        DumpDeclaredSendCandidates(panel);

        if (TryInvokeKnownChatWindowFlow(panel, message))
            return true;

        foreach (var methodName in SendMethodNames)
        {
            if (TryInvokeChatMethod(panel, methodName, message))
                return true;
        }

        var submittedViaInput = TrySubmitInputFieldEvents(panel, message);
        var submittedViaButton = TryClickChildSubmitButton(panel);
        if (submittedViaInput && submittedViaButton)
        {
            _log.Info("CHAT", "Completed chat submit via input end-edit pipeline and send button.");
            return true;
        }

        if (submittedViaButton)
        {
            _log.Info("CHAT", "Completed chat submit via send button after populating live input.");
            return true;
        }

        if (submittedViaInput)
        {
            _log.Warn("CHAT", "Invoked the live chat input end-edit pipeline, but no real send button was triggered afterward.");
            return true;
        }

        return false;
    }

    private bool TryInvokeKnownChatWindowFlow(object panel, string message)
    {
        var panelType = panel.GetType();
        if (!string.Equals(panelType.FullName, "Liftoff.Multiplayer.Chat.ChatWindowPanel", StringComparison.Ordinal))
            return false;

        var invoked = false;

        if (TryInvokeZeroArgMethod(panel, "GiveFocus"))
            invoked = true;

        if (TryInvokeChatMethod(panel, "OnChatValueChange", message))
            invoked = true;

        if (TryInvokeChatMethod(panel, "OnInputFieldEditEnded", message))
            invoked = true;

        if (TryInvokeChatMethod(panel, "SendUserMessage", message))
        {
            _log.Info("CHAT", "Completed chat submit via ChatWindowPanel.SendUserMessage().");
            return true;
        }

        return invoked;
    }

    private bool TryPopulateLiveInputFields(object panel, string message)
    {
        if (panel is not Component component)
            return false;

        var populated = false;
        foreach (var inputField in GetHandles(component).InputFields)
        {
            inputField.text = message;
            inputField.onValueChanged?.Invoke(message);
            _log.Info("CHAT", () => $"Populated child InputField on {ReflectionHelper.DescribeObjectIdentity(inputField)} name=\"{inputField.name}\".");
            populated = true;
        }

        TryPopulateTextMeshProChildren(component, message);
        return populated;
    }

    private bool TrySubmitInputFieldEvents(object panel, string message)
    {
        if (panel is not Component component)
            return false;

        var handles = GetHandles(component);
        var submitted = false;
        foreach (var inputField in handles.InputFields)
        {
            try
            {
                inputField.ActivateInputField();
                inputField.MoveTextEnd(false);
                inputField.onEndEdit?.Invoke(message);
                inputField.DeactivateInputField();
                _log.Info("CHAT", () => $"Invoked InputField end-edit pipeline on {ReflectionHelper.DescribeObjectIdentity(inputField)} name=\"{inputField.name}\".");
                submitted = true;
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"Failed to submit InputField on {inputField.name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var tmpInput in handles.TmpInputs)
        {
            try
            {
                TryInvokeZeroArgMethod(tmpInput, "ActivateInputField");
                TryInvokeZeroArgMethod(tmpInput, "MoveTextEnd");

                var onEndEdit = ReflectionHelper.GetMemberValue(tmpInput, "onEndEdit");
                var invoke = onEndEdit == null ? null : ReflectionHelper.FindMethod(onEndEdit.GetType(), "Invoke", 1);
                if (invoke != null)
                {
                    invoke.Invoke(onEndEdit, new object[] { message });
                    _log.Info("CHAT", () => $"Invoked TMP_InputField end-edit pipeline on {ReflectionHelper.DescribeObjectIdentity(tmpInput)}.");
                    submitted = true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"Failed to submit TMP_InputField on {ReflectionHelper.DescribeObjectIdentity(tmpInput)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return submitted;
    }

    private void TryPopulateKnownTextFields(object panel, string message)
    {
        foreach (var memberName in new[] { "message", "Message", "text", "Text", "currentMessage", "CurrentMessage" })
            TrySetTextMember(panel, memberName, message);

        foreach (var holderName in new[] { "inputField", "messageInput", "input", "chatInput", "txtMessage" })
        {
            var holder = ReflectionHelper.GetMemberValue(panel, holderName);
            if (holder == null)
                continue;

            if (holder is InputField inputField)
            {
                inputField.text = message;
                _log.Info("CHAT", $"Populated UnityEngine.UI.InputField via {holderName}.");
                continue;
            }

            TrySetTextMember(holder, "text", message);
            TrySetTextMember(holder, "Text", message);
        }

        if (panel is Component component)
        {
            foreach (var inputField in GetHandles(component).InputFields)
            {
                inputField.text = message;
                _log.Info("CHAT", () => $"Populated child InputField on {ReflectionHelper.DescribeObjectIdentity(inputField)}.");
            }

            foreach (var textComponent in component.GetComponentsInChildren<Text>(true))
            {
                if (!LooksLikeChatTextTarget(textComponent))
                    continue;

                textComponent.text = message;
                _log.Info("CHAT", () => $"Populated child Text on {ReflectionHelper.DescribeObjectIdentity(textComponent)} name=\"{textComponent.name}\".");
            }

            TryPopulateTextMeshProChildren(component, message);
        }
    }

    private void TrySetTextMember(object instance, string memberName, string text)
    {
        try
        {
            if (ReflectionHelper.SetMemberValue(instance, memberName, text))
                _log.Info("CHAT", () => $"Set {instance.GetType().FullName}.{memberName} for chat send.");
        }
        catch (Exception ex)
        {
            _log.Warn("CHAT", $"Failed to set {instance.GetType().FullName}.{memberName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryInvokeChatMethod(object panel, string methodName, string message)
    {
        foreach (var method in GetChatMethods(panel.GetType(), methodName))
        {
            try
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    method.Invoke(panel, Array.Empty<object>());
                    _log.Info("CHAT", () => $"Invoked {ReflectionHelper.FormatMethodSignature(method)}");
                    return true;
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    method.Invoke(panel, new object[] { message });
                    _log.Info("CHAT", () => $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with message text.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"{methodName} invocation failed on {panel.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// Declared methods of one name on one type, memoised. The sweep behind this
    /// ran once per candidate name per message -- up to thirteen GetMethods calls
    /// to send a single line.
    /// </summary>
    private List<MethodInfo> GetChatMethods(Type panelType, string methodName)
    {
        var key = $"{panelType.FullName}|{methodName}";
        if (_chatMethods.TryGetValue(key, out var cached))
            return cached;

        var methods = panelType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName)
            .Where(IsSupportedChatMethod)
            .OrderBy(method => method.GetParameters().Length)
            .ToList();

        _chatMethods[key] = methods;
        return methods;
    }

    private MethodInfo? FindZeroArgMethod(Type type, string methodName)
    {
        var key = $"{type.FullName}|{methodName}";
        if (_zeroArgMethods.TryGetValue(key, out var cached))
            return cached;

        var method = ReflectionHelper.FindDeclaredMethod(type, methodName, 0);
        _zeroArgMethods[key] = method;
        return method;
    }

    private void EnsureChatWindowVisible()
    {
        if (_toggleUnavailable)
            return;

        if (_cachedToggles == null || !AllAlive(_cachedToggles))
        {
            _cachedToggles = ChatToggleTypeNames
                .SelectMany(GetLiveChatObjects)
                .OfType<Component>()
                .ToArray();

            if (_cachedToggles.Length == 0)
            {
                // Nothing to reveal. Latch it so the object sweep above does not
                // run again on every message; InvalidateSceneCaches clears it.
                _toggleUnavailable = true;
                return;
            }
        }

        foreach (var toggle in _cachedToggles)
        {
            try
            {
                var isHidden = ReflectionHelper.GetMemberValue(toggle, "IsChatWindowHidden");
                if (!ReflectionHelper.IsTruthy(isHidden))
                    return;

                foreach (var methodName in ShowChatMethodNames)
                {
                    var method = FindZeroArgMethod(toggle.GetType(), methodName);
                    if (method == null || !IsSupportedChatMethod(method))
                        continue;

                    method.Invoke(toggle, Array.Empty<object>());
                    _log.Info("CHAT", () => $"Invoked {ReflectionHelper.FormatMethodSignature(method)} to reveal the chat window before sending.");
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"Failed to reveal chat window via {ReflectionHelper.DescribeObjectIdentity(toggle)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void DumpDeclaredSendCandidates(object panel)
    {
        // Diagnostics only, and the answer is a property of the type rather than
        // of the message: worth one reflection sweep per panel type, not one per
        // announcement.
        var panelType = panel.GetType();
        if (!_dumpedSendCandidates.Add(panelType.FullName ?? panelType.Name))
            return;

        var methods = panelType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => SendMethodNames.Contains(method.Name, StringComparer.Ordinal))
            .Where(IsSupportedChatMethod)
            .OrderBy(method => method.Name)
            .ThenBy(method => method.GetParameters().Length)
            .Select(ReflectionHelper.FormatMethodSignature)
            .Distinct()
            .ToList();

        if (methods.Count == 0)
        {
            _log.Warn("CHAT", $"No declared chat send candidates were found on {panelType.FullName}.");
            return;
        }

        foreach (var method in methods)
            _log.Info("CHAT", $"Declared send candidate: {method}");
    }

    private static bool IsSupportedChatMethod(MethodInfo method)
    {
        var declaringType = method.DeclaringType;
        if (declaringType == null)
            return false;

        if (declaringType.Namespace != null &&
            declaringType.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
            return false;

        return true;
    }

    private void DumpInterestingMembers(object liveObject)
    {
        var members = liveObject.GetType()
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member.Name.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             member.Name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             member.Name.IndexOf("history", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             member.Name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(12)
            .ToList();

        foreach (var member in members)
            _log.Info("CHAT", $"  member {liveObject.GetType().FullName}.{member.Name}");

        if (liveObject is Component component)
            DumpInterestingChildComponents(component);
    }

    private void DumpMessageCollection(object liveObject)
    {
        foreach (var memberName in new[] { "messages", "Messages", "history", "History", "entries", "Entries" })
        {
            var value = ReflectionHelper.GetMemberValue(liveObject, memberName);
            var items = ReflectionHelper.EnumerateAsObjects(value).Take(8).ToList();
            if (items.Count == 0)
                continue;

            _log.Info("CHAT", $"  {memberName} count~{items.Count}");
            for (var index = 0; index < items.Count; index++)
                _log.Info("CHAT", $"    [{index}] {ReflectionHelper.SafeDescribe(_describe, items[index])}");
        }
    }

    private IEnumerable<object> GetLiveChatObjects(string fullTypeName)
    {
        var type = ResolveType(fullTypeName);
        return ReflectionHelper.GetLiveObjects(type);
    }

    private bool TryClickChildSubmitButton(object panel)
    {
        if (panel is not Component component)
            return false;

        var buttons = component.GetComponentsInChildren<Button>(true)
            .Where(button => CandidateSubmitButtonNames.Any(token =>
                button.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                button.gameObject.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        foreach (var button in buttons)
        {
            try
            {
                button.onClick?.Invoke();
                _log.Info("CHAT", () => $"Invoked Button.onClick on {ReflectionHelper.DescribeObjectIdentity(button)} name=\"{button.name}\".");
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"Failed to invoke Button.onClick on {button.name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private void TryPopulateTextMeshProChildren(Component component, string message)
    {
        var handles = GetHandles(component);

        foreach (var tmpInput in handles.TmpInputs)
        {
            if (ReflectionHelper.SetMemberValue(tmpInput, "text", message))
                _log.Info("CHAT", () => $"Populated TMP_InputField text on {ReflectionHelper.DescribeObjectIdentity(tmpInput)}.");

            TryInvokeZeroArgMethod(tmpInput, "ActivateInputField");
            TryInvokeZeroArgMethod(tmpInput, "MoveTextEnd");
        }

        foreach (var tmpText in handles.TmpTexts)
        {
            if (ReflectionHelper.SetMemberValue(tmpText, "text", message))
                _log.Info("CHAT", () => $"Populated TMP_Text on {ReflectionHelper.DescribeObjectIdentity(tmpText)} name=\"{tmpText.name}\".");
        }
    }

    private bool TryInvokeZeroArgMethod(object instance, string methodName)
    {
        try
        {
            var method = FindZeroArgMethod(instance.GetType(), methodName);
            if (method == null)
                return false;

            method.Invoke(instance, Array.Empty<object>());
            _log.Info("CHAT", () => $"Invoked {ReflectionHelper.FormatMethodSignature(method)}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("CHAT", $"Failed to invoke {instance.GetType().FullName}.{methodName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void DumpInterestingChildComponents(Component component)
    {
        var children = component.GetComponentsInChildren<Component>(true)
            .Where(child =>
            {
                var typeName = child.GetType().FullName ?? child.GetType().Name;
                return typeName.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       typeName.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       typeName.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child.name.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child.name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child.name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child is InputField ||
                       child is Button ||
                       child is Text;
            })
            .Take(20)
            .ToList();

        foreach (var child in children)
            _log.Info("CHAT", $"  child {ReflectionHelper.DescribeObjectIdentity(child)} name=\"{child.name}\" type={child.GetType().FullName}");
    }

    private static bool LooksLikeChatTextTarget(Text textComponent)
    {
        return LooksLikeChatTextTargetName(textComponent.name);
    }

    private static bool LooksLikeChatTextTargetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.IndexOf("prefab", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("missed", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("header", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        return name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("chatinput", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("messageinput", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ScoreInputField(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;

        var score = 0;
        foreach (var token in PreferredInputFieldNames)
        {
            if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 10;
        }

        if (name.IndexOf("send", StringComparison.OrdinalIgnoreCase) >= 0)
            score -= 5;

        return score;
    }

    private Type? ResolveType(string fullTypeName)
    {
        if (_resolvedTypes.TryGetValue(fullTypeName, out var cached))
            return cached;

        var type = ResolveTypeUncached(fullTypeName);
        _resolvedTypes[fullTypeName] = type;
        return type;
    }

    /// <summary>
    /// Falls back to sweeping GetTypes() over every loaded assembly when the
    /// discovery service does not know the name -- which is why ResolveType
    /// memoises, misses included. Before that, the two TMPro lookups on the send
    /// path paid this sweep several times for every chat line.
    /// </summary>
    private Type? ResolveTypeUncached(string fullTypeName)
    {
        var type = _discovery.TryResolveKnownType(fullTypeName);
        if (type != null)
            return type;

        var simpleName = fullTypeName.Split('.').Last();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            Type[] loadableTypes;
            try
            {
                loadableTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                loadableTypes = ex.Types.Where(candidate => candidate != null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            type = loadableTypes.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, fullTypeName, StringComparison.Ordinal) ||
                string.Equals(candidate.Name, simpleName, StringComparison.Ordinal));
            if (type != null)
                return type;
        }

        return null;
    }
}
