using System.Runtime.InteropServices;
using System.Text;
using AutoKey.Core.Keyboard;
using AutoKey.Core.Text;

namespace AutoKey.Core.Planning;

/// <summary>
/// Turns text into keystrokes that imitate a person typing it:
/// <list type="bullet">
/// <item>an uneven rhythm shaped by which fingers and hands are involved, common letter pairs and reaching for Shift;</item>
/// <item>speed that drifts over time, with pauses at sentence and line breaks and occasional stops to think;</item>
/// <item>typos (neighbouring keys, swapped or skipped letters, double presses, Shift slips) that are noticed, either
/// immediately or a few letters later, and fixed with Backspace.</item>
/// </list>
/// Replaying the plan always produces exactly the input text: every typo is corrected before the word is finished.
/// </summary>
public sealed class HumanTypingPlanner
{
    private readonly TypingProfile _profile;
    private readonly KeyboardGeometry _keyboard;

    public HumanTypingPlanner(TypingProfile? profile = null, KeyboardGeometry? keyboard = null)
    {
        _profile = (profile ?? TypingProfile.Default).Sanitized();
        _keyboard = keyboard ?? KeyboardGeometry.UsQwerty;
    }

    /// <summary>Plans how to type <paramref name="text"/>.</summary>
    /// <param name="text">The text to type. Line endings are normalized to <c>\n</c>.</param>
    /// <param name="seed">Seed for reproducible plans; a random seed is used when omitted.</param>
    public TypingPlan CreatePlan(string text, int? seed = null)
    {
        string normalized = TextNormalizer.Normalize(text);
        Rune[] runes = TextNormalizer.ToRunes(normalized);
        var random = seed is int s ? new Random(s) : new Random();

        List<Keystroke> keystrokes = NewKeystrokeBuffer(runes.Length);
        int typos = new Builder(_profile, _keyboard, random, runes, keystrokes).Run();
        return new TypingPlan(normalized, runes.Length, keystrokes, typos);
    }

    /// <summary>
    /// Works out what typing <paramref name="text"/> involves, averaged over several simulated plans. Much cheaper
    /// than calling <see cref="CreatePlan"/> that often, because the text is prepared once and no plan is kept.
    /// </summary>
    /// <param name="text">The text to type.</param>
    /// <param name="samples">
    /// How many plans to average. Typing time is stable from plan to plan, but the number of typos in a short text
    /// varies a lot, so a single sample would be misleading.
    /// </param>
    /// <param name="seed">Seed of the first plan; each further plan uses the next number.</param>
    /// <param name="cancellationToken">Stops the work, for example because the text has changed.</param>
    public PlanEstimate Estimate(string text, int samples, int seed, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);

        Rune[] runes = TextNormalizer.ToRunes(TextNormalizer.Normalize(text));
        List<Keystroke> keystrokes = NewKeystrokeBuffer(runes.Length);
        double totalMs = 0;
        int totalTypos = 0;
        for (int i = 0; i < samples; i++)
        {
            keystrokes.Clear();
            totalTypos += new Builder(_profile, _keyboard, new Random(seed + i), runes, keystrokes).Run(cancellationToken);
            foreach (ref readonly Keystroke keystroke in CollectionsMarshal.AsSpan(keystrokes))
            {
                totalMs += keystroke.DelayBeforeMs + keystroke.HoldMs;
            }
        }

        return new PlanEstimate(
            runes.Length,
            TimeSpan.FromMilliseconds(totalMs / samples),
            (int)Math.Round((double)totalTypos / samples));
    }

    /// <summary>Room for the text plus a typical share of typos and corrections.</summary>
    private static List<Keystroke> NewKeystrokeBuffer(int characters) => new(characters + (characters / 6));

    private sealed class Builder
    {
        private const double MinGapMs = 6;
        private const double MinHoldMs = 25;

        /// <summary>A typo types at most two characters, plus up to four more before it's noticed.</summary>
        private const int MaxTypoLength = 6;

        /// <summary>A running plan checks for cancellation every 4096 characters.</summary>
        private const int CancellationCheckMask = 0xFFF;

        private static readonly HashSet<int> CommonBigrams = BuildBigrams(
            "th he in er an re on at en nd ti es or te of ed is it al ar st to nt ng se ha as ou io le ve co me de hi ri ro ic ne ea ra ce");

        private readonly TypingProfile _profile;
        private readonly KeyboardGeometry _keyboard;
        private readonly Random _random;
        private readonly Rune[] _text;
        private readonly List<Keystroke> _keystrokes;

        private readonly double _baseMs;     // average time between key presses at the configured speed
        private readonly double _rhythmCv;    // spread of the rhythm
        private readonly double _holdMs;      // average time a key is held down
        private readonly double _shiftMs;     // extra time needed to bring in Shift

        private double _flow = 1.0;           // slowly drifting speed multiplier ("in the zone" vs. sluggish)
        private Rune? _previous;              // the last character typed (null after Backspace)
        private bool _previousShifted;
        private double _pendingMs;            // extra pause to add before the next keystroke
        private int _careful;                 // keystrokes left in "careful" mode after fixing a typo
        private int _lastTypoAt = -1;
        private int _pauseAppliedAt = -1;
        private int _hesitateAt = -1;
        private double _hesitateMs;
        private int _typoCount;

        public Builder(TypingProfile profile, KeyboardGeometry keyboard, Random random, Rune[] text, List<Keystroke> keystrokes)
        {
            _profile = profile;
            _keyboard = keyboard;
            _random = random;
            _text = text;
            _keystrokes = keystrokes;

            _baseMs = 12_000.0 / profile.WordsPerMinute;
            _rhythmCv = 0.08 + (0.42 * profile.Variability);
            _holdMs = Math.Clamp(_baseMs * 0.42, 50, 115);
            _shiftMs = Math.Clamp(_baseMs * 0.3, 35, 110);
        }

        private enum TypoKind
        {
            Substitution,  // a neighbouring key instead of the right one: "tge"
            Transposition, // two letters swapped: "teh"
            Omission,      // a letter (or a space) skipped: "te", "thecat"
            Insertion,     // an extra key pressed: "thre", "thhe"
            WrongCase,     // Shift released late or pressed late: "THe", "the"
        }

        /// <summary>Appends the keystrokes for the whole text and returns how many typos they include.</summary>
        public int Run(CancellationToken cancellationToken = default)
        {
            int i = 0;
            while (i < _text.Length)
            {
                if ((i & CancellationCheckMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (_pauseAppliedAt != i)
                {
                    _pendingMs += PauseBefore(i);
                    _pauseAppliedAt = i;
                }

                if (TryTypo(i))
                {
                    continue; // the typo has been typed and deleted again; now type position i properly
                }

                Type(_text[i], isTypo: false, progress: i + 1);
                i++;
            }

            return _typoCount;
        }

        private void Type(Rune rune, bool isTypo, int progress)
        {
            bool known = _keyboard.TryGetKey(rune, out KeyPosition key, out bool shifted);
            double interval = Interval(rune, known, key, shifted);
            KeystrokeKind kind = rune.Value switch
            {
                '\n' => KeystrokeKind.Enter,
                '\t' => KeystrokeKind.Tab,
                _ => KeystrokeKind.Character,
            };

            Emit(kind, kind == KeystrokeKind.Character ? rune : default, interval, SampleHold(1.0), isTypo, progress);
            _previous = rune;
            _previousShifted = known && shifted;
            if (_careful > 0)
            {
                _careful--;
            }
        }

        private void Backspace(bool first, int progress)
        {
            double interval = first
                ? Math.Clamp(_random.NextLogNormal(380, 0.4), 160, 1500)  // the moment of "oops"
                : Math.Clamp(_random.NextLogNormal(Math.Clamp(_baseMs * 0.55, 65, 170), 0.22), 45, 400);

            Emit(KeystrokeKind.Backspace, default, interval, SampleHold(0.8), isTypo: false, progress);
            _previous = null;
            _previousShifted = false;
        }

        /// <summary>Time from the previous key press to this one.</summary>
        private double Interval(Rune rune, bool known, KeyPosition key, bool shifted)
        {
            double factor = 1.0;
            if (_previous is Rune previous)
            {
                if (known && _keyboard.TryGetKey(previous, out KeyPosition previousKey, out _))
                {
                    if (previousKey.Hand == Hand.Thumbs || key.Hand == Hand.Thumbs)
                    {
                        factor *= 0.95;
                    }
                    else if (previousKey.Finger == key.Finger)
                    {
                        // Repeating a key is quick; moving one finger between two keys is slow.
                        factor *= previousKey == key ? 0.95 : 1.3;
                    }
                    else if (previousKey.Hand != key.Hand)
                    {
                        factor *= 0.88; // alternating hands is the fastest
                    }
                    else
                    {
                        factor *= 1.03;
                    }

                    if (Math.Abs(previousKey.Row - key.Row) >= 2)
                    {
                        factor *= 1.12; // jumping over a row
                    }
                }

                if (IsCommonBigram(previous, rune))
                {
                    factor *= 0.85; // practised letter pairs flow faster
                }

                if (IsWordChar(rune) && !IsWordChar(previous))
                {
                    factor *= 1.12; // starting a new word
                }
            }

            if (!known)
            {
                factor *= 1.6; // hunting for an unusual character
            }
            else if (key.Row == 0)
            {
                factor *= 1.15; // reaching up to the number row
            }

            if (_careful > 0)
            {
                factor *= 1.15;
            }

            double mean = _baseMs * factor * _flow;
            if (known && shifted && !_previousShifted)
            {
                mean += _shiftMs;
            }

            double sample = _random.NextLogNormal(mean, _rhythmCv);
            return Math.Clamp(sample, Math.Min(30, mean * 0.5), mean * 4);
        }

        private double SampleHold(double scale) =>
            Math.Clamp(_random.NextLogNormal(_holdMs * scale, 0.18 + (0.2 * _profile.Variability)), MinHoldMs, 250);

        private void Emit(KeystrokeKind kind, Rune rune, double intervalMs, double holdMs, bool isTypo, int progress)
        {
            double total = intervalMs + _pendingMs;
            _pendingMs = 0;

            double delay = 0;
            if (_keystrokes.Count > 0)
            {
                Keystroke last = _keystrokes[^1];
                delay = total - last.HoldMs;
                if (delay < MinGapMs)
                {
                    // This key goes down before the previous one would come up: shorten the previous hold.
                    double hold = Math.Max(MinHoldMs, total - MinGapMs);
                    if (hold < last.HoldMs)
                    {
                        _keystrokes[^1] = last with { HoldMs = hold };
                    }

                    delay = Math.Max(MinGapMs, total - _keystrokes[^1].HoldMs);
                }
            }

            _keystrokes.Add(new Keystroke(kind, rune, delay, holdMs, isTypo, progress));
        }

        /// <summary>Natural pause before typing position <paramref name="i"/>.</summary>
        private double PauseBefore(int i)
        {
            if (i == 0)
            {
                return 0;
            }

            Rune current = _text[i];
            Rune previous = _text[i - 1];
            bool wordStart = IsWordStart(i);
            if (wordStart)
            {
                StartWord(i);
            }

            double scale = _profile.PauseScale;
            if (scale <= 0)
            {
                return 0;
            }

            double pause = 0;
            if (previous.Value == '\n')
            {
                pause = current.Value == '\n'
                    ? _random.NextLogNormal(250, 0.4)                                                     // blank line
                    : _random.NextLogNormal(i >= 2 && _text[i - 2].Value == '\n' ? 1500 : 650, 0.5);   // new paragraph / line
            }
            else if (wordStart)
            {
                int before = LastMeaningfulBefore(i - 1);
                if (before is '.' or '!' or '?' or '…')
                {
                    pause = _random.NextLogNormal(600, 0.55);  // new sentence
                }
                else if (before is ',' or ';' or ':' or '—' or '–')
                {
                    pause = _random.NextLogNormal(230, 0.55);  // next clause
                }
                else if (before != '\n' && _random.Chance(0.025))
                {
                    pause = _random.NextLogNormal(1100, 0.55); // stopping to think
                }
            }

            if (i == _hesitateAt)
            {
                pause += _hesitateMs;
            }

            return pause * scale;
        }

        private void StartWord(int i)
        {
            double drift = _random.NextGaussian() * (0.02 + (0.08 * _profile.Variability));
            _flow = Math.Clamp(1 + ((_flow - 1) * 0.75) + drift, 0.7, 1.4);

            // People sometimes falter halfway through long words.
            int length = 0;
            while (i + length < _text.Length && IsWordChar(_text[i + length]))
            {
                length++;
            }

            if (length >= 8 && _random.Chance(0.3))
            {
                _hesitateAt = i + _random.Next(3, length - 1);
                _hesitateMs = _random.NextLogNormal(260, 0.5);
            }
        }

        private bool TryTypo(int i)
        {
            if (_profile.TypoRate <= 0 || i == _lastTypoAt)
            {
                return false;
            }

            Rune current = _text[i];
            bool isSpace = current.Value == ' ';
            if (!isSpace && !CanMistype(current))
            {
                return false;
            }

            double chance = _profile.TypoRate * (_careful > 0 ? 0.3 : 1.0) * (isSpace ? 0.4 : 1.0);
            if (!_random.Chance(chance))
            {
                return false;
            }

            Rune? next = i + 1 < _text.Length ? _text[i + 1] : null;
            Rune? previous = i > 0 ? _text[i - 1] : null;
            bool nextUsable = next is Rune n && CanMistype(n) && n != current;
            bool canDouble = previous is Rune p && CanMistype(p) && p != current;
            bool hasNeighbors = _keyboard.HasNeighbors(current);
            Rune? wrongCase = WrongCase(i);

            Span<double> weights = stackalloc double[5];
            if (isSpace)
            {
                if (previous is Rune before && IsWordChar(before) && nextUsable)
                {
                    weights[(int)TypoKind.Omission] = 1;
                }
            }
            else
            {
                weights[(int)TypoKind.Substitution] = hasNeighbors ? 0.5 : 0;
                weights[(int)TypoKind.Transposition] = nextUsable ? 0.18 : 0;
                weights[(int)TypoKind.Omission] = nextUsable ? 0.10 : 0;
                weights[(int)TypoKind.Insertion] = hasNeighbors || canDouble ? 0.12 : 0;
                weights[(int)TypoKind.WrongCase] = wrongCase is not null ? 0.06 : 0;
            }

            int kindIndex = PickIndex(weights);
            if (kindIndex < 0)
            {
                return false;
            }

            // What actually gets typed, and where the typist's intention continues afterwards.
            Span<Rune> typed = stackalloc Rune[MaxTypoLength];
            int length = 0;
            int resumeAt;
            switch ((TypoKind)kindIndex)
            {
                case TypoKind.Substitution:
                    typed[length++] = PickNeighbor(current);
                    resumeAt = i + 1;
                    break;
                case TypoKind.Transposition:
                    typed[length++] = next!.Value;
                    typed[length++] = current;
                    resumeAt = i + 2;
                    break;
                case TypoKind.Omission:
                    typed[length++] = next!.Value;
                    resumeAt = i + 2;
                    break;
                case TypoKind.Insertion:
                    bool useNeighbor = hasNeighbors && (!canDouble || _random.Chance(0.6));
                    typed[length++] = useNeighbor ? PickNeighbor(current) : previous!.Value;
                    resumeAt = i;
                    break;
                default:
                    typed[length++] = wrongCase!.Value;
                    resumeAt = i + 1;
                    break;
            }

            // Often the slip goes unnoticed for a few more letters, but always within the same word, so that
            // autocorrect (which kicks in at the end of a word) never interferes with the correction.
            int extra = _random.Chance(_profile.ImmediateNoticeChance) ? 0 : SampleNoticeDelay();
            for (int j = 0; j < extra && resumeAt + j < _text.Length && CanMistype(_text[resumeAt + j]); j++)
            {
                typed[length++] = _text[resumeAt + j];
            }

            foreach (Rune rune in typed[..length])
            {
                Type(rune, isTypo: true, progress: i);
            }

            for (int d = 0; d < length; d++)
            {
                Backspace(first: d == 0, progress: i);
            }

            _pendingMs += Math.Clamp(_random.NextLogNormal(170, 0.4), 60, 600); // regaining composure
            _careful = 2 + _random.Next(3);
            _lastTypoAt = i;
            _typoCount++;
            return true;
        }

        private Rune? WrongCase(int i)
        {
            Rune current = _text[i];
            if (!Rune.IsLetter(current))
            {
                return null;
            }

            Rune swapped = Rune.IsUpper(current) ? Rune.ToLowerInvariant(current) : Rune.ToUpperInvariant(current);
            if (swapped == current)
            {
                return null;
            }

            if (Rune.IsLower(current) && i > 0 && Rune.IsUpper(_text[i - 1]))
            {
                return swapped; // Shift released too late: "THe"
            }

            if (Rune.IsUpper(current) && IsWordStart(i))
            {
                return swapped; // Shift pressed too late: "the"
            }

            return null;
        }

        private int SampleNoticeDelay()
        {
            double u = _random.NextDouble();
            return u < 0.45 ? 1 : u < 0.75 ? 2 : u < 0.9 ? 3 : 4;
        }

        private Rune PickNeighbor(Rune rune)
        {
            IReadOnlyList<WeightedRune> neighbors = _keyboard.GetNeighbors(rune);
            double total = 0;
            foreach (WeightedRune neighbor in neighbors)
            {
                total += neighbor.Weight;
            }

            double pick = _random.NextDouble() * total;
            foreach (WeightedRune neighbor in neighbors)
            {
                pick -= neighbor.Weight;
                if (pick <= 0)
                {
                    return neighbor.Rune;
                }
            }

            return neighbors[^1].Rune;
        }

        private int PickIndex(ReadOnlySpan<double> weights)
        {
            double total = 0;
            foreach (double weight in weights)
            {
                total += weight;
            }

            if (total <= 0)
            {
                return -1;
            }

            double pick = _random.NextDouble() * total;
            int chosen = -1;
            for (int k = 0; k < weights.Length; k++)
            {
                if (weights[k] > 0)
                {
                    chosen = k;
                    pick -= weights[k];
                    if (pick <= 0)
                    {
                        break;
                    }
                }
            }

            return chosen;
        }

        private bool IsWordStart(int i)
        {
            if (!IsWordChar(_text[i]))
            {
                return false;
            }

            if (i == 0)
            {
                return true;
            }

            Rune previous = _text[i - 1];
            if (IsWordChar(previous))
            {
                return false;
            }

            // An apostrophe inside a word ("don't") doesn't start a new one.
            return !(previous.Value is '\'' or '’' && i >= 2 && IsWordChar(_text[i - 2]));
        }

        /// <summary>The last character before <paramref name="index"/>, skipping spaces, closing quotes and brackets.</summary>
        private int LastMeaningfulBefore(int index)
        {
            for (int k = index; k >= 0; k--)
            {
                int value = _text[k].Value;
                if (value is ' ' or '\t' or '"' or '\'' or ')' or ']' or '}' or '»' or '’' or '”')
                {
                    continue;
                }

                return value;
            }

            return '\n';
        }

        /// <summary>
        /// Letters and digits can be mistyped. Characters from scripts usually entered through an input method
        /// editor (Chinese, Japanese, …) are left alone unless they are on the keyboard layout.
        /// </summary>
        private bool CanMistype(Rune rune) =>
            rune.IsBmp && Rune.IsLetterOrDigit(rune) && (rune.Value < 0x0530 || _keyboard.TryGetKey(rune, out _, out _));

        private static bool IsWordChar(Rune rune) => Rune.IsLetterOrDigit(rune);

        private static bool IsCommonBigram(Rune first, Rune second) =>
            first.IsAscii && second.IsAscii &&
            CommonBigrams.Contains(BigramKey(char.ToLowerInvariant((char)first.Value), char.ToLowerInvariant((char)second.Value)));

        private static int BigramKey(char first, char second) => (first << 16) | second;

        private static HashSet<int> BuildBigrams(string pairs)
        {
            var set = new HashSet<int>();
            foreach (string pair in pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                set.Add(BigramKey(pair[0], pair[1]));
            }

            return set;
        }
    }
}
