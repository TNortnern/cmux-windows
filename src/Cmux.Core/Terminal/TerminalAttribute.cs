using System.Runtime.InteropServices;

namespace Cmux.Core.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strikethrough = 128,
}

public struct TerminalColor : IEquatable<TerminalColor>
{
    public byte R;
    public byte G;
    public byte B;
    public bool IsDefault;

    public TerminalColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
        IsDefault = false;
    }

    public static TerminalColor Default => new() { IsDefault = true };

    public static TerminalColor FromIndex(int index)
    {
        // Standard 256-color lookup
        if (index < 16)
        {
            return index switch
            {
                0 => new(0x00, 0x00, 0x00),
                1 => new(0xAA, 0x00, 0x00),
                2 => new(0x00, 0xAA, 0x00),
                3 => new(0xAA, 0x55, 0x00),
                4 => new(0x00, 0x00, 0xAA),
                5 => new(0xAA, 0x00, 0xAA),
                6 => new(0x00, 0xAA, 0xAA),
                7 => new(0xAA, 0xAA, 0xAA),
                8 => new(0x55, 0x55, 0x55),
                9 => new(0xFF, 0x55, 0x55),
                10 => new(0x55, 0xFF, 0x55),
                11 => new(0xFF, 0xFF, 0x55),
                12 => new(0x55, 0x55, 0xFF),
                13 => new(0xFF, 0x55, 0xFF),
                14 => new(0x55, 0xFF, 0xFF),
                15 => new(0xFF, 0xFF, 0xFF),
                _ => Default,
            };
        }

        if (index < 232)
        {
            // 216-color cube (6x6x6)
            int i = index - 16;
            int r = i / 36;
            int g = (i / 6) % 6;
            int b = i % 6;
            return new((byte)(r > 0 ? r * 40 + 55 : 0), (byte)(g > 0 ? g * 40 + 55 : 0), (byte)(b > 0 ? b * 40 + 55 : 0));
        }

        if (index < 256)
        {
            // 24-step grayscale
            byte v = (byte)(index - 232);
            v = (byte)(v * 10 + 8);
            return new(v, v, v);
        }

        return Default;
    }

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(r, g, b);

    public bool Equals(TerminalColor other) =>
        R == other.R && G == other.G && B == other.B && IsDefault == other.IsDefault;

    public override bool Equals(object? obj) => obj is TerminalColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(R, G, B, IsDefault);
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}

public struct TerminalAttribute
{
    public CellFlags Flags;
    public TerminalColor Foreground;
    public TerminalColor Background;

    public static TerminalAttribute Default => new()
    {
        Flags = CellFlags.None,
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
    };
}

/// <summary>
/// One grid cell, packed into 12 bytes. Layout:
///   _meta bits  0-15: character
///   _meta bits 16-23: CellFlags
///   _meta bit     24: IsDirty
///   _meta bits 25-26: Width (0-3)
///   _fg / _bg:        TerminalColor, bits 0-23 = RGB, bit 24 = IsDefault
/// default(TerminalCell) decodes to the same values as the former struct.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TerminalCell
{
    private const uint DirtyBit = 1u << 24;
    private const int WidthShift = 25;
    private const uint WidthMask = 3u << WidthShift;
    private const uint DefaultColorBit = 1u << 24;

    private uint _meta;
    private uint _fg;
    private uint _bg;

    public char Character
    {
        readonly get => (char)(_meta & 0xFFFF);
        set => _meta = (_meta & ~0xFFFFu) | value;
    }

    public TerminalAttribute Attribute
    {
        readonly get => new()
        {
            Flags = (CellFlags)((_meta >> 16) & 0xFF),
            Foreground = DecodeColor(_fg),
            Background = DecodeColor(_bg),
        };
        set
        {
            _meta = (_meta & ~(0xFFu << 16)) | (((uint)value.Flags & 0xFF) << 16);
            _fg = EncodeColor(value.Foreground);
            _bg = EncodeColor(value.Background);
        }
    }

    public bool IsDirty
    {
        readonly get => (_meta & DirtyBit) != 0;
        set => _meta = value ? _meta | DirtyBit : _meta & ~DirtyBit;
    }

    public int Width // 1 for normal, 2 for wide chars
    {
        readonly get => (int)((_meta & WidthMask) >> WidthShift);
        set => _meta = (_meta & ~WidthMask) | (((uint)value & 3u) << WidthShift);
    }

    /// <summary>True when the cell is the default blank: ' ' with default attributes.</summary>
    internal readonly bool IsBlank =>
        (_meta & 0xFFFF) == ' ' &&
        ((_meta >> 16) & 0xFF) == 0 &&
        _fg == DefaultColorBit &&
        _bg == DefaultColorBit;

    public static TerminalCell Empty => new()
    {
        Character = ' ',
        Attribute = TerminalAttribute.Default,
        IsDirty = true,
        Width = 1,
    };

    private static uint EncodeColor(TerminalColor c) =>
        c.IsDefault ? DefaultColorBit : ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static TerminalColor DecodeColor(uint v) =>
        (v & DefaultColorBit) != 0
            ? TerminalColor.Default
            : new TerminalColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
