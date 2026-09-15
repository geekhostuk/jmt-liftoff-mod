using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine.SceneManagement;

namespace JmtLiftoffMod.Features.Diagnostics;

/// <summary>
/// Notices frames that take far longer than the ones around them, and says how much of each
/// was this mod's own work.
///
/// A stall of a few tens of milliseconds is what a pilot feels as the drone clipping
/// something it should have missed, and it leaves no other trace: the watchdog only speaks
/// up after five seconds. So every entry point the game calls this mod through is timed,
/// and a frame well over the usual is written to the BepInEx log with this mod's share of
/// it, and whether a garbage collection ran. Every five minutes in a room a summary follows,
/// so a quiet log reads as "measured and fine" rather than "not measured".
///
/// Main thread only. It costs two Stopwatch reads per entry point and allocates nothing
/// unless it writes a line.
/// </summary>
internal static class FrameProbe
{
    public enum Part
    {
        Update,
        Gui,
        PhotonEvent,
        PlayerProperties,
        RoomProperties,
        RoomMembers,
        Command,
    }

    private static readonly string[] Names =
    {
        "update", "debug windows", "Photon events", "player properties", "room properties",
        "players joining or leaving", "panel commands",
    };

    private static readonly int PartCount = Names.Length;

    /// <summary>A frame is slow at this many times the usual one...</summary>
    private const double SlowFactor = 3;

    /// <summary>...and never under this: a few milliseconds over at 240 fps is not felt.</summary>
    private const double SlowFloorMs = 25;

    /// <summary>Longer than this is a load, a menu or an alt-tab, not a stutter.</summary>
    private const double PauseMs = 3000;

    private const double QuietAfterLoadSeconds = 5;
    private const double LineGapSeconds = 2;
    private const double SummarySeconds = 300;

    private static readonly long[] Spent = new long[PartCount];
    private static readonly long[] SpentInWindow = new long[PartCount];
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private static Action<string>? _log;
    private static int _depth;
    private static Part _openPart;
    private static long _openedAt;

    private static long _lastTick;
    private static double _usualMs;
    private static int _collections;
    private static long _quietUntil;
    private static long _nextLineAt;
    private static int _unlogged;

    private static long _windowStart;
    private static int _frames;
    private static int _slow;
    private static int _slowWithCollection;
    private static double _worstMs;
    private static double _oursMs;
    private static double _oursWorstMs;
    private static int _oursWorstPart;

    public static void Install(Action<string> log)
    {
        _log = log;
        SceneManager.sceneLoaded += (_, _) => _quietUntil = Stopwatch.GetTimestamp() + Ticks(QuietAfterLoadSeconds * 1000);
    }

    /// <summary>Times one call into this mod. Nested calls count once, as the outermost part.</summary>
    public static Scope Measure(Part part) => new(part);

    public readonly struct Scope : IDisposable
    {
        private readonly bool _outer;

        public Scope(Part part)
        {
            _outer = _depth++ == 0;
            if (_outer)
            {
                _openPart = part;
                _openedAt = Stopwatch.GetTimestamp();
            }
        }

        public void Dispose()
        {
            _depth--;
            if (_outer)
                Spent[(int)_openPart] += Stopwatch.GetTimestamp() - _openedAt;
        }
    }

    /// <summary>
    /// Called first thing in Update, once a frame: closes the frame before, which runs from
    /// the last call to this one. <paramref name="counting"/> when a stutter would matter,
    /// which for this mod is being in a room.
    /// </summary>
    public static void Tick(bool counting)
    {
        var now = Stopwatch.GetTimestamp();
        _depth = 0;
        var collections = GC.CollectionCount(0);
        var collected = collections != _collections;
        _collections = collections;

        if (_lastTick == 0)
        {
            _lastTick = _windowStart = now;
            return;
        }
        var frameMs = (now - _lastTick) / TicksPerMs;
        _lastTick = now;

        if (counting && now >= _quietUntil && frameMs < PauseMs)
            Count(now, frameMs, collected);
        Array.Clear(Spent, 0, PartCount);

        if ((now - _windowStart) / TicksPerMs >= SummarySeconds * 1000)
            Summarise(now);
    }

    private static void Count(long now, double frameMs, bool collected)
    {
        long ours = 0;
        var top = 0;
        for (var i = 0; i < PartCount; i++)
        {
            ours += Spent[i];
            SpentInWindow[i] += Spent[i];
            if (Spent[i] > Spent[top])
                top = i;
        }
        var oursMs = ours / TicksPerMs;
        _frames++;
        _oursMs += oursMs;
        if (oursMs > _oursWorstMs)
        {
            _oursWorstMs = oursMs;
            _oursWorstPart = top;
        }

        var usual = _usualMs > 0 ? _usualMs : frameMs;
        if (frameMs < Math.Max(SlowFloorMs, SlowFactor * usual))
        {
            // Slow frames stay out of "usual", or one bad patch would hide the next.
            _usualMs = _usualMs > 0 ? _usualMs + (frameMs - _usualMs) * 0.02 : frameMs;
            return;
        }

        _slow++;
        if (collected)
            _slowWithCollection++;
        _worstMs = Math.Max(_worstMs, frameMs);
        if (now < _nextLineAt)
        {
            _unlogged++;
            return;
        }
        _nextLineAt = now + Ticks(LineGapSeconds * 1000);
        var line = $"[Perf] Slow frame: {frameMs:0} ms, where frames usually take {usual:0.0} ms. " +
                   $"This mod's code ran {oursMs:0.00} ms of it{Breakdown(Spent)}." +
                   (collected ? " A garbage collection ran during it." : "") +
                   (_unlogged > 0 ? $" ({_unlogged} more slow frame(s) since the last line.)" : "");
        _unlogged = 0;
        _log?.Invoke(line);
    }

    private static void Summarise(long now)
    {
        if (_frames > 0)
        {
            var minutes = (now - _windowStart) / TicksPerMs / 60000;
            _log?.Invoke(
                $"[Perf] Last {minutes:0} min in a room: {_frames} frames, usually {_usualMs:0.0} ms; " +
                $"{_slow} slow" + (_slow > 0 ? $" (worst {_worstMs:0} ms, {_slowWithCollection} with a garbage collection)" : "") +
                $". This mod's code: {_oursMs / _frames:0.000} ms a frame on average, " +
                $"{_oursWorstMs:0.00} ms at most ({Names[_oursWorstPart]}){Breakdown(SpentInWindow, "in total: ")}.");
        }
        _windowStart = now;
        _frames = _slow = _slowWithCollection = 0;
        _worstMs = _oursMs = _oursWorstMs = 0;
        _oursWorstPart = 0;
        Array.Clear(SpentInWindow, 0, PartCount);
    }

    /// <summary>" (photon events 0.21 ms, update 0.05 ms)", biggest first, or nothing when it rounds to nothing.</summary>
    private static string Breakdown(long[] spent, string lead = "")
    {
        var parts = new List<(double Ms, string Name)>();
        for (var i = 0; i < PartCount; i++)
        {
            var ms = spent[i] / TicksPerMs;
            if (ms >= 0.01)
                parts.Add((ms, Names[i]));
        }
        if (parts.Count == 0)
            return "";
        return " (" + lead + string.Join(", ", parts.OrderByDescending(p => p.Ms).Take(4).Select(p => $"{p.Name} {p.Ms:0.00} ms")) + ")";
    }

    private static long Ticks(double ms) => (long)(ms * TicksPerMs);
}
