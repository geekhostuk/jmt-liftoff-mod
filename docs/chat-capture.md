# Chat capture

The plugin emits a [`chat_message`](../contracts/chat_message.json) event for every
chat message a player sends. Getting that to mean *"a message was sent"* rather than
*"a message was drawn"* takes a little care, because Liftoff has no public receive
callback the plugin can subscribe to.

## Why the obvious hook is wrong

The three fields the event needs — user id, nick, text — only appear together,
already unpacked, in a rendering method:

```csharp
ChatWindowPanel.GenerateUserMessage(string userId, string userName, string message, Color ledColor)
```

The `Color ledColor` parameter is the tell: this is presentation. It is called once
per message *drawn*, and it is reached two ways:

```
OnChatMessageReceived(msg)  ──► GenerateUserMessage(player, msg) ──► GenerateUserMessage(4-arg)
OnEnable()                  ──► GenerateChatFromHistory()        ──► GenerateUserMessage(4-arg)   ← once per retained message
```

`ChatWindowPanel.OnDisable()` calls `ClearChat()` and `OnEnable()` rebuilds the
window from `messageHistory`. The race scene reload disables and re-enables the
panel, so **the whole retained backlog is redrawn at the start of every race**.

Hooking the render method alone therefore re-emits every past message as a fresh
event, once per race. That was the behaviour up to and including 1.0.0.

## Why that was harmful, not just noisy

Nothing in the emitted payload distinguished a replay from a real message:
`timestamp_utc` is stamped at emit time so the replay looked *newer* than the
original, `event_ordinal` restarts each race so it could not order messages across
one, and `actor` came back `null` because the historical sender was no longer in the
actor table.

A server that treats chat as **input** — track votes, `/next`, `/extend` — could not
filter them, so it acted on them. In a live session a single `3` typed to vote for a
track was counted again in each of the three following races, steering the rotation
with a ballot nobody had typed. The bot also reacted to its own past announcements,
which came back through the same path as player chat.

## What the plugin does now

Emission is gated on being inside the receive call. The render hook still supplies
the strings, but it only publishes when the plugin knows a message was actually
received:

| Mode | Gate | When it is used |
|---|---|---|
| `Receive` | Emit only while inside `OnChatMessageReceived`. | Normal. A redraw emits nothing, because nothing was received. |
| `SuppressHistory` | Emit unless inside `GenerateChatFromHistory`. | `OnChatMessageReceived` could not be found. Weaker: it skips the *known* redraw path rather than requiring a known receive. |
| `Legacy` | Emit every render. | Neither gate found. The backlog **will** be replayed; servers must dedupe. |
| `None` | — | The render method itself was not found; no chat is captured. |

Both gates are reference-counted and released from Harmony **finalizers**, not
postfixes, so a throw inside the game's own code cannot leave the gate stuck open or
stuck shut.

The active mode is logged at startup (`[Chat] Chat capture installed. Mode=…`) and
reported to the server as `chat_capture_mode` in
[`session_started`](../contracts/session_started.json), so a server can tell whether
it is getting the de-duplicated guarantee or has to defend itself.

## Two fields worth keying on

Independently of the gating, every `chat_message` now carries:

- **`chat_id`** — monotonic per message within the session and never reset.
  `(session_id, chat_id)` is a stable dedupe key, and unlike `event_ordinal` it
  orders messages across race boundaries.
- **`self`** — true when the bot sent the message itself, so a server acting on chat
  can ignore its own announcements instead of reacting to them.

## If it regresses

The failure is quiet — duplicates look like real messages. To check:

1. Join the bot's room and send one chat message.
2. Force a track change so a new race starts.
3. Watch `BepInEx/plugins/JmtLiftoffMod/photon-race-*.jsonl`. The message should
   appear **once**. If it appears again with a new `timestamp_utc` and a restarted
   `event_ordinal`, the gate is not in force — check the startup log for the mode.

A game update that renames `OnChatMessageReceived` degrades to `SuppressHistory`
rather than breaking, and one that renames both degrades to `Legacy`. Neither is
silent: the mode is in the log and in `session_started`.
