using System.Collections.Frozen;
using System.Text.Json;
using Nexus.Core.Protocol;

namespace Nexus.Core.Input;

public enum MouseButton { Left, Right, Middle }

public enum ButtonAction { Click, DoubleClick, Down, Up }

public sealed record InputMovePayload(int Dx, int Dy);

/// <summary>Absolute position on the primary monitor, 0..65535 on both axes (the remote screen picture).</summary>
public sealed record InputMoveToPayload(int X, int Y);

public sealed record InputButtonPayload(MouseButton Button, ButtonAction Action);

/// <summary>Wheel units: 120 = one notch. Positive dy scrolls up (away from the user), positive dx right.</summary>
public sealed record InputScrollPayload(int Dx, int Dy);

public sealed record InputTextPayload(string Text);

public sealed record InputKeyPayload(string Key, IReadOnlyList<string> Modifiers);

public sealed record InputMediaPayload(string Action);

/// <summary>One Win32 INPUT record, platform-neutral so the planning can be tested anywhere.</summary>
public readonly record struct InputStep(bool IsMouse, int Dx, int Dy, int MouseData, uint Flags, ushort VirtualKey, ushort Scan)
{
    public static InputStep Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) => new(true, dx, dy, data, flags, 0, 0);

    public static InputStep Key(ushort vk, uint flags) => new(false, 0, 0, 0, flags, vk, 0);

    public static InputStep Unicode(char c, bool up) => new(false, 0, 0, 0, InputFlags.KeyUnicode | (up ? InputFlags.KeyUp : 0), 0, c);
}

/// <summary>Win32 constants (winuser.h).</summary>
public static class InputFlags
{
    public const uint MouseMove = 0x0001;
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
    public const uint RightDown = 0x0008;
    public const uint RightUp = 0x0010;
    public const uint MiddleDown = 0x0020;
    public const uint MiddleUp = 0x0040;
    public const uint Wheel = 0x0800;
    public const uint HWheel = 0x1000;
    public const uint Absolute = 0x8000;

    public const uint KeyExtended = 0x0001;
    public const uint KeyUp = 0x0002;
    public const uint KeyUnicode = 0x0004;
}

/// <summary>
/// The fixed set of keys a phone may press. Typing goes through <see cref="InputPlanner.Text"/>;
/// these are for navigation and shortcuts (Ctrl+C, Alt+Tab, Win+D…).
/// </summary>
public static class InputKeys
{
    public const int MaxTextLength = 256;
    public const int MaxMove = 2000;
    public const int MaxScroll = 2400;

    public static readonly FrozenDictionary<string, (ushort Vk, bool Extended)> Keys = BuildKeys();

    public static readonly FrozenDictionary<string, (ushort Vk, bool Extended)> Modifiers = new Dictionary<string, (ushort, bool)>
    {
        ["ctrl"] = (0xA2, false),
        ["alt"] = (0xA4, false),
        ["shift"] = (0xA0, false),
        ["win"] = (0x5B, true),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static readonly FrozenDictionary<string, ushort> Media = new Dictionary<string, ushort>
    {
        ["playPause"] = 0xB3,
        ["next"] = 0xB0,
        ["previous"] = 0xB1,
        ["mute"] = 0xAD,
        ["volumeDown"] = 0xAE,
        ["volumeUp"] = 0xAF,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenDictionary<string, (ushort, bool)> BuildKeys()
    {
        var keys = new Dictionary<string, (ushort, bool)>(StringComparer.Ordinal)
        {
            ["enter"] = (0x0D, false),
            ["tab"] = (0x09, false),
            ["escape"] = (0x1B, false),
            ["backspace"] = (0x08, false),
            ["space"] = (0x20, false),
            ["delete"] = (0x2E, true),
            ["insert"] = (0x2D, true),
            ["left"] = (0x25, true),
            ["up"] = (0x26, true),
            ["right"] = (0x27, true),
            ["down"] = (0x28, true),
            ["home"] = (0x24, true),
            ["end"] = (0x23, true),
            ["pageUp"] = (0x21, true),
            ["pageDown"] = (0x22, true),
            ["printScreen"] = (0x2C, true),
            ["win"] = (0x5B, true),
        };
        for (var c = 'a'; c <= 'z'; c++)
        {
            keys[c.ToString()] = ((ushort)char.ToUpperInvariant(c), false);
        }
        for (var c = '0'; c <= '9'; c++)
        {
            keys[c.ToString()] = (c, false);
        }
        for (var i = 1; i <= 12; i++)
        {
            keys["f" + i] = ((ushort)(0x6F + i), false);
        }
        return keys.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

/// <summary>Turns validated input commands into Win32 INPUT records.</summary>
public static class InputPlanner
{
    public static IReadOnlyList<InputStep> Move(InputMovePayload p) => [InputStep.Mouse(InputFlags.MouseMove, p.Dx, p.Dy)];

    public static IReadOnlyList<InputStep> MoveTo(InputMoveToPayload p) => [InputStep.Mouse(InputFlags.MouseMove | InputFlags.Absolute, p.X, p.Y)];

    public static IReadOnlyList<InputStep> Button(InputButtonPayload p)
    {
        var (down, up) = p.Button switch
        {
            MouseButton.Right => (InputFlags.RightDown, InputFlags.RightUp),
            MouseButton.Middle => (InputFlags.MiddleDown, InputFlags.MiddleUp),
            _ => (InputFlags.LeftDown, InputFlags.LeftUp),
        };
        return p.Action switch
        {
            ButtonAction.Down => [InputStep.Mouse(down)],
            ButtonAction.Up => [InputStep.Mouse(up)],
            ButtonAction.DoubleClick => [InputStep.Mouse(down), InputStep.Mouse(up), InputStep.Mouse(down), InputStep.Mouse(up)],
            _ => [InputStep.Mouse(down), InputStep.Mouse(up)],
        };
    }

    public static IReadOnlyList<InputStep> Scroll(InputScrollPayload p)
    {
        var steps = new List<InputStep>(2);
        if (p.Dy != 0)
        {
            steps.Add(InputStep.Mouse(InputFlags.Wheel, data: p.Dy));
        }
        if (p.Dx != 0)
        {
            steps.Add(InputStep.Mouse(InputFlags.HWheel, data: p.Dx));
        }
        return steps;
    }

    public static IReadOnlyList<InputStep> Text(InputTextPayload p)
    {
        var steps = new List<InputStep>(p.Text.Length * 2);
        foreach (var c in p.Text)
        {
            switch (c)
            {
                case '\r':
                    break;
                case '\n':
                    steps.Add(InputStep.Key(0x0D, 0));
                    steps.Add(InputStep.Key(0x0D, InputFlags.KeyUp));
                    break;
                case '\t':
                    steps.Add(InputStep.Key(0x09, 0));
                    steps.Add(InputStep.Key(0x09, InputFlags.KeyUp));
                    break;
                default:
                    // UTF-16 code units; a surrogate pair is sent as two units, which Windows recombines.
                    steps.Add(InputStep.Unicode(c, up: false));
                    steps.Add(InputStep.Unicode(c, up: true));
                    break;
            }
        }
        return steps;
    }

    /// <summary>Modifiers down, key down, key up, modifiers up in reverse — all in one batch, so nothing stays pressed.</summary>
    public static IReadOnlyList<InputStep> Key(InputKeyPayload p)
    {
        var steps = new List<InputStep>();
        var mods = p.Modifiers.Select(m => InputKeys.Modifiers[m]).ToList();
        foreach (var (vk, ext) in mods)
        {
            steps.Add(InputStep.Key(vk, ext ? InputFlags.KeyExtended : 0));
        }
        var (keyVk, keyExt) = InputKeys.Keys[p.Key];
        var keyFlags = keyExt ? InputFlags.KeyExtended : 0;
        steps.Add(InputStep.Key(keyVk, keyFlags));
        steps.Add(InputStep.Key(keyVk, keyFlags | InputFlags.KeyUp));
        for (var i = mods.Count - 1; i >= 0; i--)
        {
            steps.Add(InputStep.Key(mods[i].Vk, (mods[i].Extended ? InputFlags.KeyExtended : 0) | InputFlags.KeyUp));
        }
        return steps;
    }

    public static IReadOnlyList<InputStep> Media(InputMediaPayload p)
    {
        var vk = InputKeys.Media[p.Action];
        return [InputStep.Key(vk, InputFlags.KeyExtended), InputStep.Key(vk, InputFlags.KeyExtended | InputFlags.KeyUp)];
    }

    public static IReadOnlyList<InputStep> For(object payload) => payload switch
    {
        InputMovePayload m => Move(m),
        InputMoveToPayload t => MoveTo(t),
        InputButtonPayload b => Button(b),
        InputScrollPayload s => Scroll(s),
        InputTextPayload t => Text(t),
        InputKeyPayload k => Key(k),
        InputMediaPayload m => Media(m),
        _ => throw new ArgumentException("Not an input payload.", nameof(payload)),
    };
}

/// <summary>
/// Tracks mouse buttons a phone pressed (drag) so they are released if the phone goes quiet —
/// a lost connection must not leave a button held down on the PC.
/// </summary>
public sealed class HeldButtons
{
    public static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(5);

    private readonly HashSet<MouseButton> _held = [];
    private readonly Lock _lock = new();
    private DateTimeOffset _lastActivity;

    public void Observe(object payload, DateTimeOffset now)
    {
        lock (_lock)
        {
            _lastActivity = now;
            if (payload is InputButtonPayload b)
            {
                if (b.Action == ButtonAction.Down)
                {
                    _held.Add(b.Button);
                }
                else
                {
                    _held.Remove(b.Button);
                }
            }
        }
    }

    /// <summary>Steps that release buttons held longer than <see cref="IdleRelease"/> without any input.</summary>
    public IReadOnlyList<InputStep> ReleaseIfIdle(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_held.Count == 0 || now - _lastActivity < IdleRelease)
            {
                return [];
            }
            var steps = _held.SelectMany(b => InputPlanner.Button(new InputButtonPayload(b, ButtonAction.Up))).ToList();
            _held.Clear();
            return steps;
        }
    }
}

public static class InputParsers
{
    private static readonly FrozenDictionary<string, MouseButton> Buttons = new Dictionary<string, MouseButton>
    {
        ["left"] = MouseButton.Left,
        ["right"] = MouseButton.Right,
        ["middle"] = MouseButton.Middle,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, ButtonAction> Actions = new Dictionary<string, ButtonAction>
    {
        ["click"] = ButtonAction.Click,
        ["double"] = ButtonAction.DoubleClick,
        ["down"] = ButtonAction.Down,
        ["up"] = ButtonAction.Up,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static PayloadResult Move(JsonElement p)
    {
        if (!Only(p, "dx", "dy") || !Int(p, "dx", InputKeys.MaxMove, true, out var dx) || !Int(p, "dy", InputKeys.MaxMove, true, out var dy))
        {
            return PayloadResult.Invalid($"'dx' and 'dy' must be integers within ±{InputKeys.MaxMove}.");
        }
        return PayloadResult.Ok(new InputMovePayload(dx, dy));
    }

    public static PayloadResult MoveTo(JsonElement p)
    {
        if (!Only(p, "x", "y") || !p.TryGetProperty("x", out var xe) || !xe.TryGetInt32(out var x) || x is < 0 or > 65535
            || !p.TryGetProperty("y", out var ye) || !ye.TryGetInt32(out var y) || y is < 0 or > 65535)
        {
            return PayloadResult.Invalid("'x' and 'y' must be 0..65535.");
        }
        return PayloadResult.Ok(new InputMoveToPayload(x, y));
    }

    public static PayloadResult Button(JsonElement p)
    {
        if (!Only(p, "button", "action") || !Str(p, "button", out var b) || !Buttons.TryGetValue(b, out var button)
            || !Str(p, "action", out var a) || !Actions.TryGetValue(a, out var action))
        {
            return PayloadResult.Invalid("'button' must be left|right|middle and 'action' click|double|down|up.");
        }
        return PayloadResult.Ok(new InputButtonPayload(button, action));
    }

    public static PayloadResult Scroll(JsonElement p)
    {
        if (!Only(p, "dx", "dy") || !Int(p, "dx", InputKeys.MaxScroll, false, out var dx) || !Int(p, "dy", InputKeys.MaxScroll, false, out var dy)
            || (dx == 0 && dy == 0))
        {
            return PayloadResult.Invalid($"'dx'/'dy' must be integers within ±{InputKeys.MaxScroll}, at least one non-zero.");
        }
        return PayloadResult.Ok(new InputScrollPayload(dx, dy));
    }

    public static PayloadResult Text(JsonElement p)
    {
        if (!Only(p, "text") || !Str(p, "text", out var text) || text.Length is 0 or > InputKeys.MaxTextLength
            || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r')))
        {
            return PayloadResult.Invalid($"'text' must be 1..{InputKeys.MaxTextLength} characters without control characters.");
        }
        return PayloadResult.Ok(new InputTextPayload(text));
    }

    public static PayloadResult Key(JsonElement p)
    {
        if (!Only(p, "key", "modifiers") || !Str(p, "key", out var key) || !InputKeys.Keys.ContainsKey(key))
        {
            return PayloadResult.Invalid("'key' must be one of the supported key names.");
        }
        var modifiers = new List<string>();
        if (p.TryGetProperty("modifiers", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 4)
            {
                return PayloadResult.Invalid("'modifiers' must be a list of ctrl|alt|shift|win.");
            }
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } m || !InputKeys.Modifiers.ContainsKey(m) || modifiers.Contains(m))
                {
                    return PayloadResult.Invalid("'modifiers' must be a list of ctrl|alt|shift|win without repeats.");
                }
                modifiers.Add(m);
            }
        }
        return PayloadResult.Ok(new InputKeyPayload(key, modifiers));
    }

    public static PayloadResult Media(JsonElement p)
    {
        if (!Only(p, "action") || !Str(p, "action", out var action) || !InputKeys.Media.ContainsKey(action))
        {
            return PayloadResult.Invalid("'action' must be playPause|next|previous|mute|volumeDown|volumeUp.");
        }
        return PayloadResult.Ok(new InputMediaPayload(action));
    }

    private static bool Only(JsonElement p, params string[] allowed) =>
        p.ValueKind == JsonValueKind.Object && p.EnumerateObject().All(x => allowed.Contains(x.Name, StringComparer.Ordinal));

    private static bool Str(JsonElement p, string name, out string value)
    {
        value = "";
        if (!p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = v.GetString()!;
        return true;
    }

    private static bool Int(JsonElement p, string name, int max, bool required, out int value)
    {
        value = 0;
        if (!p.TryGetProperty(name, out var v))
        {
            return !required;
        }
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value) && value >= -max && value <= max;
    }
}
