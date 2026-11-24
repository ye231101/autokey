using System.Text;

namespace AutoKey.Core.Keyboard;

/// <summary>The finger that presses a key when touch typing.</summary>
public enum Finger : byte
{
    LeftPinky,
    LeftRing,
    LeftMiddle,
    LeftIndex,
    RightIndex,
    RightMiddle,
    RightRing,
    RightPinky,
    Thumb,
}

/// <summary>The hand that presses a key; the space bar belongs to the thumbs.</summary>
public enum Hand : byte
{
    Left,
    Right,
    Thumbs,
}

/// <summary>Physical location of a key on a staggered keyboard.</summary>
/// <param name="Row">0 = number row, 1 = top letter row, 2 = home row, 3 = bottom row, 4 = space bar.</param>
/// <param name="X">Horizontal position measured in key widths.</param>
/// <param name="Finger">The finger that presses the key.</param>
public readonly record struct KeyPosition(int Row, double X, Finger Finger)
{
    public Hand Hand => Finger switch
    {
        Finger.Thumb => Hand.Thumbs,
        <= Finger.LeftIndex => Hand.Left,
        _ => Hand.Right,
    };
}

/// <summary>A character together with how likely it is to be hit by mistake.</summary>
public readonly record struct WeightedRune(Rune Rune, double Weight);

/// <summary>
/// Maps characters to physical keys so the planner can choose realistic neighbouring-key typos and
/// model how finger movement affects timing.
/// </summary>
public sealed class KeyboardGeometry
{
    /// <summary>Horizontal offset of each row's first key on a standard staggered keyboard.</summary>
    private static readonly double[] RowOffsets = [0.0, 1.5, 1.75, 2.25];

    private static readonly KeyPosition SpaceKey = new(4, 6.5, Finger.Thumb);
    private static readonly KeyPosition EnterKey = new(2, 13.75, Finger.RightPinky);
    private static readonly KeyPosition TabKey = new(1, 0.25, Finger.LeftPinky);

    private readonly List<PhysicalKey> _keys = [];
    private readonly Dictionary<int, (int KeyIndex, bool Shifted)> _lookup = [];
    private readonly Dictionary<int, KeyPosition> _special = [];
    private readonly Dictionary<int, WeightedRune[]> _neighbors = [];

    private KeyboardGeometry()
    {
    }

    /// <summary>The standard US QWERTY layout.</summary>
    public static KeyboardGeometry UsQwerty { get; } = FromRows(
        ["`1234567890-=", "qwertyuiop[]\\", "asdfghjkl;'", "zxcvbnm,./"],
        ["~!@#$%^&*()_+", "QWERTYUIOP{}|", "ASDFGHJKL:\"", "ZXCVBNM<>?"]);

    /// <summary>
    /// Builds a geometry from the characters on each row of the main key block: the number row (starting with
    /// the key left of 1), the top letter row, the home row and the bottom row. A <c>'\0'</c> marks a key that
    /// produces no character.
    /// </summary>
    /// <param name="normalRows">Characters produced without Shift.</param>
    /// <param name="shiftedRows">Characters produced with Shift; derived from <paramref name="normalRows"/> when omitted.</param>
    public static KeyboardGeometry FromRows(IReadOnlyList<string> normalRows, IReadOnlyList<string>? shiftedRows = null)
    {
        ArgumentNullException.ThrowIfNull(normalRows);

        var geometry = new KeyboardGeometry();
        for (int row = 0; row < Math.Min(RowOffsets.Length, normalRows.Count); row++)
        {
            string normal = normalRows[row] ?? string.Empty;
            string? shifted = shiftedRows is not null && row < shiftedRows.Count ? shiftedRows[row] : null;
            for (int j = 0; j < normal.Length; j++)
            {
                Rune? unshiftedRune = ToRune(normal[j]);
                Rune? shiftedRune = shifted is not null && j < shifted.Length
                    ? ToRune(shifted[j])
                    : DeriveShifted(unshiftedRune);

                // The number row starts one key further left (the key left of "1").
                int column = row == 0 ? j - 1 : j;
                var position = new KeyPosition(row, RowOffsets[row] + j, FingerForColumn(column));
                geometry.AddKey(position, unshiftedRune, shiftedRune);
            }
        }

        geometry._special[' '] = SpaceKey;
        geometry._special['\n'] = EnterKey;
        geometry._special['\t'] = TabKey;
        geometry.BuildNeighbors();
        return geometry;
    }

    /// <summary>Finds the key that produces <paramref name="rune"/>.</summary>
    /// <param name="rune">The character to look up.</param>
    /// <param name="position">The key's position.</param>
    /// <param name="shifted">Whether Shift is needed to produce the character.</param>
    public bool TryGetKey(Rune rune, out KeyPosition position, out bool shifted)
    {
        if (_lookup.TryGetValue(rune.Value, out var entry))
        {
            position = _keys[entry.KeyIndex].Position;
            shifted = entry.Shifted;
            return true;
        }

        if (_special.TryGetValue(rune.Value, out position))
        {
            shifted = false;
            return true;
        }

        // Upper-case letters whose shifted form wasn't listed explicitly.
        if (Rune.IsUpper(rune))
        {
            Rune lower = Rune.ToLowerInvariant(rune);
            if (lower != rune && _lookup.TryGetValue(lower.Value, out entry) && !entry.Shifted)
            {
                position = _keys[entry.KeyIndex].Position;
                shifted = true;
                return true;
            }
        }

        position = default;
        shifted = false;
        return false;
    }

    /// <summary>
    /// Returns the characters that a slipping finger could produce instead of <paramref name="rune"/>:
    /// same-category characters (letters for letters, digits for digits) on adjacent keys, in the same Shift state.
    /// </summary>
    public IReadOnlyList<WeightedRune> GetNeighbors(Rune rune) =>
        _neighbors.TryGetValue(rune.Value, out var neighbors) ? neighbors : [];

    /// <summary>Whether <see cref="GetNeighbors"/> returns at least one character.</summary>
    public bool HasNeighbors(Rune rune) => _neighbors.ContainsKey(rune.Value);

    private void AddKey(KeyPosition position, Rune? normal, Rune? shifted)
    {
        int index = _keys.Count;
        _keys.Add(new PhysicalKey(position, normal, shifted));
        if (normal is Rune n)
        {
            _lookup.TryAdd(n.Value, (index, false));
        }

        if (shifted is Rune s)
        {
            _lookup.TryAdd(s.Value, (index, true));
        }
    }

    private void BuildNeighbors()
    {
        foreach (var (value, entry) in _lookup)
        {
            var rune = new Rune(value);
            bool isLetter = Rune.IsLetter(rune);
            if (!isLetter && !Rune.IsDigit(rune))
            {
                continue;
            }

            KeyPosition origin = _keys[entry.KeyIndex].Position;
            var neighbors = new List<WeightedRune>();
            for (int k = 0; k < _keys.Count; k++)
            {
                if (k == entry.KeyIndex)
                {
                    continue;
                }

                PhysicalKey key = _keys[k];
                int rowDistance = Math.Abs(key.Position.Row - origin.Row);
                double xDistance = Math.Abs(key.Position.X - origin.X);

                // Keys directly left/right are the most common slips; diagonal neighbours a bit less so.
                double weight = rowDistance switch
                {
                    0 when xDistance <= 1.01 => 1.0,
                    1 when xDistance <= 0.76 => 0.6,
                    _ => 0,
                };
                if (weight == 0)
                {
                    continue;
                }

                Rune? candidate = entry.Shifted ? key.Shifted : key.Normal;
                if (candidate is not Rune c || c == rune)
                {
                    continue;
                }

                // Only letters stand in for letters and digits for digits; stray punctuation can trigger
                // app features (slash commands, smart formatting) that Backspace won't undo cleanly.
                if (isLetter ? !Rune.IsLetter(c) : !Rune.IsDigit(c))
                {
                    continue;
                }

                neighbors.Add(new WeightedRune(c, weight));
            }

            if (neighbors.Count > 0)
            {
                _neighbors[value] = neighbors.ToArray();
            }
        }
    }

    private static Rune? ToRune(char c) =>
        c == '\0' || char.IsSurrogate(c) || char.IsControl(c) ? null : new Rune(c);

    private static Rune? DeriveShifted(Rune? normal)
    {
        if (normal is not Rune n || !Rune.IsLetter(n))
        {
            return null;
        }

        Rune upper = Rune.ToUpperInvariant(n);
        return upper == n ? null : upper;
    }

    private static Finger FingerForColumn(int column) => column switch
    {
        <= 0 => Finger.LeftPinky,
        1 => Finger.LeftRing,
        2 => Finger.LeftMiddle,
        3 or 4 => Finger.LeftIndex,
        5 or 6 => Finger.RightIndex,
        7 => Finger.RightMiddle,
        8 => Finger.RightRing,
        _ => Finger.RightPinky,
    };

    private readonly record struct PhysicalKey(KeyPosition Position, Rune? Normal, Rune? Shifted);
}
