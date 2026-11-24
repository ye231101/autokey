# AutoKey

A desktop app for **Windows, macOS (Apple Silicon and Intel) and Linux** that types your text into any window
the way a person would: an uneven rhythm, natural pauses, and typos that get noticed and corrected with Backspace.

![AutoKey](src/AutoKey/Assets/AutoKey.png)

## Install

Download the package for your computer from the **[latest release](https://github.com/ye231101/autokey/releases/latest)**,
or build the packages yourself (see [Building](#building)). Each one runs on its own; .NET doesn't have to be installed.

| System | Package | How to start it |
| --- | --- | --- |
| Windows 10/11 | `AutoKey-<version>-win-x64.exe` | Run it. Windows SmartScreen may warn about an unknown publisher: **More info › Run anyway**. |
| macOS, Apple Silicon (M1 and later) | `AutoKey-<version>-osx-arm64.dmg` | Open it and drag **AutoKey** onto the **Applications** folder next to it. See [macOS](#macos) for the first start. |
| macOS, Intel | `AutoKey-<version>-osx-x64.dmg` | Same as above. |
| Linux, x64 / ARM64 | `AutoKey-<version>-linux-x64.tar.gz` / `-linux-arm64.tar.gz` | `tar xzf AutoKey-*.tar.gz && ./AutoKey/install.sh`, then start AutoKey from the applications menu (or run `./AutoKey/AutoKey` without installing). See [Linux](#linux). |

### macOS

- **Which file.** Apple menu › **About This Mac**: "Chip: Apple M…" needs `osx-arm64`, "Processor: Intel" needs
  `osx-x64`. Each `.dmg` also comes as a `.tar.gz` with the same AutoKey.app inside, if you prefer an archive.
- **First start.** The app isn't notarized by Apple, so macOS refuses to open it at first. Open **System Settings ›
  Privacy & Security** and click **Open Anyway** (or run `xattr -dr com.apple.quarantine /Applications/AutoKey.app`).
- **Permission to type.** macOS asks you to let AutoKey control the computer. Turn on AutoKey in **System Settings ›
  Privacy & Security › Accessibility**, then quit and reopen AutoKey. Until then, a banner in AutoKey explains what
  to do. After updating AutoKey, macOS may ask again.

### Linux

- **X11 sessions** work fully. AutoKey needs the usual X11 libraries (`libX11`, `libXtst`, `libXt`, `libXrandr`,
  `libxkbcommon`, `fontconfig`), which desktop systems normally have.
- **Wayland sessions** don't let ordinary apps send keystrokes to other apps. Out of the box, AutoKey can type only
  into apps that run through XWayland. To type into every app, allow your user to create a virtual keyboard
  (uinput), then log out and back in:

  ```sh
  echo 'KERNEL=="uinput", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"' | sudo tee /etc/udev/rules.d/60-autokey-uinput.rules
  echo uinput | sudo tee /etc/modules-load.d/uinput.conf
  sudo usermod -aG input "$USER"
  sudo udevadm control --reload-rules && sudo udevadm trigger
  ```

  Members of the `input` group can read every keyboard and mouse, so only do this on a computer you trust.
  Wayland also doesn't tell apps which window has focus, so in this mode AutoKey only pauses when you switch back
  to AutoKey itself, and it can only type characters that are on your current keyboard layout.
- **Tray icon.** Closing the window only hides AutoKey if your desktop shows tray icons: KDE Plasma and most other
  desktops do, GNOME needs the AppIndicator extension. Without one, closing the window quits AutoKey as there'd be
  no way to bring it back.

## Using it

1. Type or paste the text into AutoKey.
2. Click **Start typing**, then click where the text should go. You get 3 seconds by default, and if you're still
   in AutoKey when the countdown ends, it waits until you switch windows.
   *Or* click into the target window first and press the start hotkey to begin right there.
3. Use the hotkeys from any window, or the buttons. A small floating panel shows progress while typing.

| | Windows and Linux | macOS |
| --- | --- | --- |
| Start / pause / resume | **F8** | **⌃⌥P** (Control-Option-P; F8 is the media key on Mac keyboards) |
| Stop | **Esc** | **Esc** |

While AutoKey runs, its icon sits in the system tray (the menu bar on macOS). Closing the window doesn't quit
AutoKey: it keeps running there, the hotkeys keep working, and typing that's under way carries on. Click the tray
icon, or choose **Open** from its menu, to bring the window back or to the front (on macOS, clicking AutoKey in the
Dock works too); choose **About** to see which version you have (the **ⓘ** button at the top right of the window
opens it too), and **Exit** to quit.
Windows may put a new tray icon under the **^** (Show hidden icons) arrow at first; drag it onto the taskbar to keep
it in view.

To keep text from landing in the wrong place, AutoKey:

- pauses as soon as the window being typed into loses focus, and asks you to click back in and resume;
- never types into its own windows;
- waits while you hold Ctrl, Alt, ⌘ or the Windows key, so its keystrokes don't turn into shortcuts;
- releases every key it holds (including Shift) whenever it pauses, stops or exits.

## Settings

| Setting | What it does |
| --- | --- |
| Speed | Typing speed in words per minute (10–200). |
| Rhythm | How uneven the timing between keystrokes is, from metronome-steady to erratic. |
| Typos | Chance (0–10%) that a letter is mistyped. Every typo is corrected, so the final text always matches exactly. |
| Pauses | How much the typist pauses after sentences, at line breaks, and to think. |
| Start delay | Countdown after clicking **Start typing**. |
| Pause if I switch to another window | Turn off to let typing follow focus to whichever window you click (never AutoKey's own). |
| New lines are typed with | **Shift+Enter** for chat apps where Enter sends the message. |
| Keep text exact in code editors (Windows) | In VS Code, Cursor and other editors built on VS Code, stops autocomplete, auto-indent and auto-closing brackets from changing the text. It does this with a few extra key presses, so turn it off when typing into their built-in terminal. |
| Send characters as (Windows) | **Key presses** behave like a physical keyboard (best for games, VMs and remote desktops); characters missing from your keyboard layout are sent as Unicode automatically. **Unicode** sends every character directly. |

Settings are saved per user (`%APPDATA%\AutoKey` on Windows, `~/Library/Application Support/AutoKey` on macOS,
`~/.config/AutoKey` on Linux). The text itself is never saved.

## How the typing is simulated

The text is turned into a timed plan of key presses before typing starts. That's how AutoKey can tell you how long
it will take.

- **Timing.** Intervals follow a log-normal distribution around the chosen speed and are shaped by a model of your
  keyboard layout (QWERTY, QWERTZ, AZERTY, Cyrillic, …). Alternating hands is fast, the same finger on two keys is
  slow, and the number row and Shift take longer. Common letter pairs (*th*, *er*, *in*, …) flow faster. The overall
  pace drifts slowly, and each key is held down for a realistic 50–120 ms.
- **Pauses.** Longer at sentence ends and paragraph breaks, shorter after commas, with occasional "thinking" pauses
  between words and hesitations inside long words.
- **Typos.** Slips to a neighbouring key, swapped letters (*teh*), skipped letters or spaces (*thecat*), double
  presses (*thhe*), and Shift slips (*THe*). A slip is noticed right away or a few letters later, but always before
  the word ends, so autocorrect never interferes. It is then fixed with Backspace after a short "oops" pause.
- **Key presses.** Characters on your keyboard layout are typed by pressing the real keys (with Shift where needed);
  anything else (emoji, symbols your layout lacks) is sent as text.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Works the same on Windows, macOS and Linux.

```sh
dotnet build                      # build everything
dotnet test                       # run the unit tests
dotnet run --project src/AutoKey  # start the app

dotnet run build/package.cs                           # packages for every platform, in ./artifacts
dotnet run build/package.cs -- osx-arm64 linux-x64    # only some platforms
dotnet run build/package.cs -- --version 1.0.0
```

All packages can be built on any of the three systems, including the macOS `.dmg`: on a Mac the script uses Apple's
`hdiutil` and also signs the whole .app bundle; elsewhere it writes the disk image itself (an ISO 9660 file system
with Rock Ridge extensions inside a compressed UDIF container, see `build/DiskImage`). The programs inside the app
are signed either way, which Apple Silicon requires. The GitHub Actions workflow in `.github/workflows/build.yml`
runs the tests on Windows, macOS and Linux and builds all packages on a Mac.

To distribute the macOS app without the "Open Anyway" step, sign it with an Apple Developer ID and notarize it;
the bundle identifier is set in `build/macos/Info.plist`.

**Publishing a release.** Push a version tag; GitHub Actions tests the code, builds every package on its own OS,
and publishes a release with all files attached (the download notes come from `build/release-notes.md`):

```sh
git tag v1.0.1
git push origin v1.0.1
```

| Project | Contents |
| --- | --- |
| `src/AutoKey.Core` | Platform-independent logic: the typing planner, keyboard model and the typing session (countdown, pause/resume, focus guard). |
| `src/AutoKey` | The Avalonia app (Fluent theme, follows light/dark mode) and the platform layer in `Platform/`: Win32 `SendInput` and hotkeys on Windows; [SharpHook](https://github.com/TolikPylypchuk/SharpHook) (Quartz events on macOS, XTest or uinput on Linux) plus focus detection via `NSWorkspace` and X11 elsewhere. |
| `tests/AutoKey.Core.Tests` | Every plan replays to exactly the input text, typos stay inside words, speed matches the setting, and the session behaves correctly. |
| `tests/AutoKey.Tests` | The keyboard tables and hotkey matching used on macOS and Linux. |
| `build/` | The packaging script, the macOS bundle files, the Linux menu entry and installer, and `DiskImage`, which writes `.dmg` files without a Mac. |

## Limitations

- On Windows, apps running as administrator only accept keystrokes from apps that also run as administrator.
  On macOS, password fields and Terminal's Secure Keyboard Entry block keystrokes from other apps. AutoKey detects
  both and tells you.
- On macOS and Linux, the global hotkeys use a keyboard hook, which sees key presses system-wide. AutoKey only checks
  them for its hotkeys and doesn't record anything. On Linux the hotkeys also reach the focused app.
- On Windows, AutoKey counters the automatic edits of VS Code and the editors built on it. Other editors that add
  characters on their own (auto-closing brackets, auto-indent), and all such editors on macOS and Linux, do the same
  as when you type by hand, so the result can differ from the original text. So can extensions or settings that
  reformat while you type, such as *Format On Type*.
- In code files (not plain text), VS Code can still re-align a line that starts with a closing bracket or a keyword
  like `end` if AutoKey can't safely put its indentation back: when the line is over 200 characters, is indented more
  than 40 columns, contains non-ASCII characters, or is the first line typed after starting or resuming.
