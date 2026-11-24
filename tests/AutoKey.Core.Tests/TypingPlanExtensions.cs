using System.Text;
using AutoKey.Core.Planning;

namespace AutoKey.Core.Tests;

internal static class TypingPlanExtensions
{
    /// <summary>Applies every keystroke to an empty buffer and returns the resulting text.</summary>
    public static string Replay(this TypingPlan plan)
    {
        var buffer = new StringBuilder(plan.Text.Length);
        foreach (Keystroke keystroke in plan.Keystrokes)
        {
            switch (keystroke.Kind)
            {
                case KeystrokeKind.Character:
                    buffer.Append(keystroke.Character.ToString());
                    break;
                case KeystrokeKind.Enter:
                    buffer.Append('\n');
                    break;
                case KeystrokeKind.Tab:
                    buffer.Append('\t');
                    break;
                case KeystrokeKind.Backspace when buffer.Length > 0:
                    bool surrogatePair = buffer.Length >= 2
                        && char.IsLowSurrogate(buffer[^1])
                        && char.IsHighSurrogate(buffer[^2]);
                    buffer.Length -= surrogatePair ? 2 : 1;
                    break;
            }
        }

        return buffer.ToString();
    }
}
