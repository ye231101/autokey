using System.Windows.Input;
using Avalonia.Threading;
using AutoKey.Core.Execution;
using AutoKey.Core.Keyboard;
using AutoKey.Core.Planning;
using AutoKey.Core.Text;
using AutoKey.Platform;
using AutoKey.Services;

namespace AutoKey.ViewModels;

internal enum StatusKind
{
    Idle,
    Active,
    Waiting,
    Success,
    Error,
}

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int EstimateSeed = 2026;

    private readonly AppSettings _settings;
    private readonly IPlatform _platform;
    private readonly Func<Task<string?>> _readClipboard;
    private readonly DispatcherTimer _estimateTimer;
    private readonly DispatcherTimer _overlayTimer;
    private readonly RelayCommand[] _commands;

    private KeyboardGeometry _keyboard;
    private TypingSession? _session;
    private IDisposable? _typingScope;
    private SessionStatus? _sessionStatus;
    private SessionProgress? _sessionProgress;
    private CancellationTokenSource? _estimateCancellation;
    private bool _toggleHotkeyAvailable = true;
    private bool _showingInputError;

    private string _text = string.Empty;
    private string _textSummary = string.Empty;
    private string _statusTitle = string.Empty;
    private string _statusDetail = string.Empty;
    private StatusKind _statusKind;
    private double _progress;
    private bool _isProgressVisible;
    private bool _isSessionActive;
    private bool _isPaused;
    private bool _isOverlayVisible;
    private PlatformNotice? _notice;

    public MainViewModel(AppSettings settings, IPlatform platform, Func<Task<string?>> readClipboard)
    {
        _settings = settings;
        _platform = platform;
        _readClipboard = readClipboard;
        _keyboard = platform.GetKeyboardGeometry();

        _estimateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _estimateTimer.Tick += (_, _) => UpdateEstimate();

        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _overlayTimer.Tick += (_, _) =>
        {
            _overlayTimer.Stop();
            IsOverlayVisible = false;
        };

        var start = new RelayCommand(() => Start(fromHotkey: false), () => IsIdleWithText);
        var pauseResume = new RelayCommand(TogglePause, () => IsSessionActive);
        var stop = new RelayCommand(Stop, () => IsSessionActive);
        var paste = new RelayCommand(Paste, () => !IsSessionActive);
        var clear = new RelayCommand(() => Text = string.Empty, () => IsIdleWithText);
        var reset = new RelayCommand(ResetSettings, () => !IsSessionActive);
        var noticeAction = new RelayCommand(() => _notice?.Action?.Invoke(), () => _notice?.Action is not null);
        (StartCommand, PauseResumeCommand, StopCommand, PasteCommand, ClearCommand, ResetSettingsCommand, NoticeActionCommand) =
            (start, pauseResume, stop, paste, clear, reset, noticeAction);
        _commands = [start, pauseResume, stop, paste, clear, reset, noticeAction];

        RefreshNotice();
        ShowReadyStatus();
        UpdateEstimate();
    }

    public ICommand StartCommand { get; }

    public ICommand PauseResumeCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand PasteCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand ResetSettingsCommand { get; }

    public ICommand NoticeActionCommand { get; }

    // ----- Text -----------------------------------------------------------------------------------

    public string Text
    {
        get => _text;
        set
        {
            bool hadText = _text.Length > 0;
            if (!SetProperty(ref _text, value ?? string.Empty))
            {
                return;
            }

            ScheduleEstimate();
            if (hadText != (_text.Length > 0))
            {
                RaiseCommandStates(); // only Start and Clear depend on the text, and only on whether there is any
            }

            if (_showingInputError)
            {
                ShowReadyStatus();
            }
        }
    }

    public string TextSummary
    {
        get => _textSummary;
        private set => SetProperty(ref _textSummary, value);
    }

    // ----- Settings -------------------------------------------------------------------------------

    public double WordsPerMinute
    {
        get => _settings.WordsPerMinute;
        set => UpdateSetting(Math.Round(Math.Clamp(value, 10, 200)), _settings.WordsPerMinute, v => _settings.WordsPerMinute = v,
            nameof(WordsPerMinute), nameof(SpeedLabel), nameof(SpeedHint));
    }

    public string SpeedLabel => $"{WordsPerMinute:0} WPM";

    public string SpeedHint => WordsPerMinute switch
    {
        < 25 => "Hunt-and-peck",
        < 45 => "Average typist",
        < 70 => "Skilled typist",
        < 95 => "Fast typist",
        < 130 => "Very fast typist",
        _ => "Competition speed",
    };

    public double VariationPercent
    {
        get => _settings.VariationPercent;
        set => UpdateSetting(Math.Round(Math.Clamp(value, 0, 100)), _settings.VariationPercent, v => _settings.VariationPercent = v,
            nameof(VariationPercent), nameof(VariationLabel));
    }

    public string VariationLabel => VariationPercent switch
    {
        < 20 => "Steady",
        < 45 => "Even",
        < 70 => "Natural",
        _ => "Erratic",
    };

    public double TypoPercent
    {
        get => _settings.TypoPercent;
        set => UpdateSetting(Math.Round(Math.Clamp(value, 0, 10) * 2) / 2, _settings.TypoPercent, v => _settings.TypoPercent = v,
            nameof(TypoPercent), nameof(TypoLabel), nameof(TypoHint));
    }

    public string TypoLabel => TypoPercent <= 0 ? "Off" : $"{TypoPercent:0.#}%";

    public string TypoHint => TypoPercent switch
    {
        <= 0 => "Never makes a mistake",
        < 2 => "Careful: the occasional slip",
        < 5 => "Everyday slips, fixed along the way",
        _ => "Sloppy: lots of backspacing",
    };

    public double PausePercent
    {
        get => _settings.PausePercent;
        set => UpdateSetting(Math.Round(Math.Clamp(value, 0, 200) / 5) * 5, _settings.PausePercent, v => _settings.PausePercent = v,
            nameof(PausePercent), nameof(PauseLabel));
    }

    public string PauseLabel => PausePercent switch
    {
        <= 0 => "Off",
        < 60 => "Few",
        <= 140 => "Natural",
        _ => "Many",
    };

    public double StartDelaySeconds
    {
        get => _settings.StartDelaySeconds;
        set => UpdateSetting(Math.Round(Math.Clamp(value, 0, 10)), _settings.StartDelaySeconds, v => _settings.StartDelaySeconds = v,
            nameof(StartDelaySeconds), nameof(StartDelayLabel), estimate: false);
    }

    public string StartDelayLabel => StartDelaySeconds <= 0 ? "None" : $"{StartDelaySeconds:0} s";

    public bool PauseOnFocusLoss
    {
        get => _settings.PauseOnFocusLoss;
        set
        {
            _settings.PauseOnFocusLoss = value;
            OnPropertyChanged();
        }
    }

    public bool ShowOverlay
    {
        get => _settings.ShowOverlay;
        set
        {
            _settings.ShowOverlay = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0 = Enter, 1 = Shift+Enter.</summary>
    public int NewLineKeyIndex
    {
        get => _settings.ShiftEnterForNewLines ? 1 : 0;
        set
        {
            _settings.ShiftEnterForNewLines = value == 1;
            OnPropertyChanged();
        }
    }

    public bool KeepCodeEditorTextExact
    {
        get => _settings.KeepCodeEditorTextExact;
        set
        {
            _settings.KeepCodeEditorTextExact = value;
            OnPropertyChanged();
        }
    }

    public bool ShowCodeEditorOption => _platform.DetectsCodeEditors;

    /// <summary>Index into <see cref="TypingMethod"/>.</summary>
    public int TypingMethodIndex
    {
        get => (int)_settings.TypingMethod;
        set
        {
            _settings.TypingMethod = Enum.IsDefined((TypingMethod)value) ? (TypingMethod)value : TypingMethod.Keyboard;
            OnPropertyChanged();
        }
    }

    public bool ShowTypingMethod => _platform.SupportsTypingMethods;

    public bool CanEditSettings => !IsSessionActive;

    // ----- Platform notice ------------------------------------------------------------------------

    public bool HasNotice => _notice is not null;

    public string NoticeTitle => _notice?.Title ?? string.Empty;

    public string NoticeMessage => _notice?.Message ?? string.Empty;

    public string NoticeActionLabel => _notice?.ActionLabel ?? string.Empty;

    public bool HasNoticeAction => _notice?.Action is not null;

    /// <summary>Re-checks platform limitations, for example after the user granted a permission.</summary>
    public void RefreshNotice()
    {
        PlatformNotice? notice;
        try
        {
            notice = _platform.GetNotice();
        }
        catch (Exception ex)
        {
            notice = new PlatformNotice("Something is wrong with this system", ex.Message);
        }

        if (Equals(notice, _notice))
        {
            return;
        }

        _notice = notice;
        OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(NoticeTitle));
        OnPropertyChanged(nameof(NoticeMessage));
        OnPropertyChanged(nameof(NoticeActionLabel));
        OnPropertyChanged(nameof(HasNoticeAction));
        RaiseCommandStates();
    }

    // ----- Session --------------------------------------------------------------------------------

    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set
        {
            if (SetProperty(ref _isSessionActive, value))
            {
                OnPropertyChanged(nameof(CanEditSettings));
                RaiseCommandStates();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
                OnPropertyChanged(nameof(OverlayHint));
            }
        }
    }

    private bool IsIdleWithText => !IsSessionActive && Text.Length > 0;

    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set
        {
            if (SetProperty(ref _statusKind, value))
            {
                OnPropertyChanged(nameof(IsStatusActive));
                OnPropertyChanged(nameof(IsStatusWaiting));
                OnPropertyChanged(nameof(IsStatusSuccess));
                OnPropertyChanged(nameof(IsStatusError));
            }
        }
    }

    public bool IsStatusActive => StatusKind == StatusKind.Active;

    public bool IsStatusWaiting => StatusKind == StatusKind.Waiting;

    public bool IsStatusSuccess => StatusKind == StatusKind.Success;

    public bool IsStatusError => StatusKind == StatusKind.Error;

    /// <summary>Progress from 0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        private set => SetProperty(ref _isProgressVisible, value);
    }

    public bool IsOverlayVisible
    {
        get => _isOverlayVisible;
        private set => SetProperty(ref _isOverlayVisible, value);
    }

    private string ToggleKey => _platform.Hotkeys.Label(HotkeyAction.ToggleTyping);

    private string StopKey => _platform.Hotkeys.Label(HotkeyAction.Stop);

    public string HotkeyHint => _toggleHotkeyAvailable
        ? $"Hotkeys work from any window:  {ToggleKey} start · pause · resume   |   {StopKey} stop"
        : $"The {ToggleKey} hotkey isn't available (another app uses it, or a permission is missing), so use the buttons.";

    public string OverlayHint => (_toggleHotkeyAvailable ? $"{ToggleKey} {(IsPaused ? "resume" : "pause")}  ·  " : string.Empty) + $"{StopKey} stop";

    private string ResumeHint => _toggleHotkeyAvailable ? $"press {ToggleKey} or click Resume" : "click Resume";

    /// <summary>Called by the window once it knows whether the start/pause hotkey works.</summary>
    public void SetToggleHotkeyAvailable(bool available)
    {
        if (_toggleHotkeyAvailable == available)
        {
            return;
        }

        _toggleHotkeyAvailable = available;
        OnPropertyChanged(nameof(HotkeyHint));
        OnPropertyChanged(nameof(OverlayHint));
        if (!IsSessionActive && _sessionStatus is null)
        {
            ShowReadyStatus();
        }
    }

    /// <summary>The start/pause hotkey: start typing into the focused window, or pause/resume.</summary>
    public void OnToggleHotkey()
    {
        if (IsSessionActive)
        {
            TogglePause();
        }
        else
        {
            Start(fromHotkey: true);
        }
    }

    /// <summary>The stop hotkey (registered only while a session is active).</summary>
    public void OnStopHotkey() => Stop();

    public void Start(bool fromHotkey)
    {
        if (IsSessionActive)
        {
            return;
        }

        if (Text.Length == 0)
        {
            ShowInputError("Nothing to type yet", "Type or paste some text first.");
            return;
        }

        TypingSession session;
        try
        {
            _keyboard = _platform.GetKeyboardGeometry();
            TypingPlan plan = new HumanTypingPlanner(CreateProfile(), _keyboard).CreatePlan(Text);
            if (plan.Count == 0)
            {
                ShowInputError("Nothing to type", "The text only contains characters that can't be typed.");
                return;
            }

            IReadOnlyCollection<string> untypeable = _platform.FindUntypeableCharacters(plan.Text);
            if (untypeable.Count > 0)
            {
                string list = string.Join("  ", untypeable.Take(12)) + (untypeable.Count > 12 ? "  …" : string.Empty);
                ShowInputError("Some characters can't be typed here", $"Your keyboard layout can't produce: {list}");
                return;
            }

            var options = new SessionOptions
            {
                // The hotkey is pressed in the target window itself, so there's no need to count down.
                CountdownSeconds = fromHotkey ? 0 : _settings.StartDelaySeconds,
                PauseOnFocusLoss = _settings.PauseOnFocusLoss,
                KeepCodeEditorTextExact = _settings.KeepCodeEditorTextExact,
            };
            IKeyboardDevice keyboard = _platform.CreateKeyboard(_settings.TypingMethod, _settings.ShiftEnterForNewLines);
            session = new TypingSession(plan, keyboard, _platform.Desktop, options);
        }
        catch (Exception ex)
        {
            ShowInputError("Can't start typing", ex.Message);
            return;
        }

        session.StatusChanged += status => Dispatcher.UIThread.Post(() => OnStatusChanged(session, status));
        session.ProgressChanged += progress => Dispatcher.UIThread.Post(() => OnProgressChanged(session, progress));

        _session = session;
        _sessionStatus = null;
        _sessionProgress = null;
        _showingInputError = false;
        _typingScope = _platform.BeginTyping();
        IsSessionActive = true;
        IsPaused = false;
        Progress = 0;
        IsProgressVisible = true;
        _overlayTimer.Stop();
        IsOverlayVisible = _settings.ShowOverlay;
        SetStatus("Getting ready…", string.Empty, StatusKind.Waiting);

        session.Start();
    }

    private void TogglePause() => _session?.TogglePause();

    private void Stop() => _session?.Stop();

    private void OnStatusChanged(TypingSession session, SessionStatus status)
    {
        if (session != _session)
        {
            return;
        }

        _sessionStatus = status;
        IsPaused = status.State == SessionState.Paused;
        RenderStatus();
        if (status.IsFinished)
        {
            EndSession();
        }
    }

    private void OnProgressChanged(TypingSession session, SessionProgress progress)
    {
        if (session != _session)
        {
            return;
        }

        _sessionProgress = progress;
        Progress = progress.Fraction;
        if (_sessionStatus?.State == SessionState.Typing)
        {
            StatusDetail = ProgressDetail();
        }
    }

    private void RenderStatus()
    {
        if (_sessionStatus is not SessionStatus status || _session is null)
        {
            return;
        }

        string? target = string.IsNullOrWhiteSpace(status.TargetTitle) ? null : Formatting.Shorten(status.TargetTitle, 48);
        TypingPlan plan = _session.Plan;
        switch (status.State)
        {
            case SessionState.Countdown:
                SetStatus($"Starting in {status.CountdownSeconds}…", "Click where you want the text to go.", StatusKind.Waiting);
                break;

            case SessionState.WaitingForTarget:
                SetStatus("Waiting for you to switch windows", "Click into the window you want to type in.", StatusKind.Waiting);
                break;

            case SessionState.Typing:
                SetStatus(target is null ? "Typing…" : $"Typing into “{target}”", ProgressDetail(), StatusKind.Active);
                break;

            case SessionState.Paused when status.PauseReason == PauseReason.FocusLost:
                SetStatus("Paused: you switched windows", $"Click back where the text goes, then {ResumeHint}.", StatusKind.Waiting);
                break;

            case SessionState.Paused when status.PauseReason == PauseReason.TargetBlocked:
                SetStatus("Can't type into this window", _platform.DescribeBlockedTarget(target, ResumeHint), StatusKind.Error);
                break;

            case SessionState.Paused:
                SetStatus("Paused", $"To carry on, {ResumeHint}.", StatusKind.Waiting);
                break;

            case SessionState.Completed:
                Progress = 1;
                string typos = plan.TypoCount switch
                {
                    0 => string.Empty,
                    1 => ", fixing 1 typo along the way",
                    _ => $", fixing {plan.TypoCount} typos along the way",
                };
                string elapsed = Formatting.Duration(_sessionProgress?.Elapsed ?? plan.EstimatedDuration);
                SetStatus("Done", $"Typed {plan.CharacterCount:N0} characters in {elapsed}{typos}.", StatusKind.Success);
                break;

            case SessionState.Stopped:
                int typed = _sessionProgress?.CharactersTyped ?? 0;
                SetStatus("Stopped", $"Typed {typed:N0} of {plan.CharacterCount:N0} characters.", StatusKind.Idle);
                break;

            case SessionState.Faulted:
                SetStatus("Something went wrong", status.Error ?? "Typing stopped unexpectedly.", StatusKind.Error);
                break;
        }
    }

    private string ProgressDetail() => _sessionProgress is SessionProgress p
        ? $"{p.Fraction:P0}  ·  {p.CharactersTyped:N0} of {p.CharactersTotal:N0} characters  ·  {Formatting.Duration(p.Remaining)} left"
        : "Getting started…";

    private void EndSession()
    {
        _typingScope?.Dispose();
        _typingScope = null;
        IsSessionActive = false;
        IsPaused = false;
        if (IsOverlayVisible)
        {
            _overlayTimer.Stop();
            _overlayTimer.Start(); // leave the result on screen for a moment
        }
    }

    private void ShowReadyStatus()
    {
        _showingInputError = false;
        SetStatus(
            "Ready",
            _toggleHotkeyAvailable
                ? $"Click Start typing, or press {ToggleKey} while you're in the window you want to type into."
                : "Click Start typing, then click into the window you want to type into.",
            StatusKind.Idle);
    }

    private void ShowInputError(string title, string detail)
    {
        SetStatus(title, detail, StatusKind.Error);
        _showingInputError = true;
    }

    private void SetStatus(string title, string detail, StatusKind kind)
    {
        StatusTitle = title;
        StatusDetail = detail;
        StatusKind = kind;
    }

    private void RaiseCommandStates()
    {
        foreach (RelayCommand command in _commands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private async void Paste()
    {
        try
        {
            if (await _readClipboard() is { Length: > 0 } text)
            {
                Text = text;
            }
        }
        catch (Exception)
        {
            // The clipboard can be busy or hold something other than text; the user can simply try again.
        }
    }

    public void SaveSettings() => _settings.Save();

    private void ResetSettings()
    {
        var defaults = new AppSettings();
        WordsPerMinute = defaults.WordsPerMinute;
        VariationPercent = defaults.VariationPercent;
        TypoPercent = defaults.TypoPercent;
        PausePercent = defaults.PausePercent;
        StartDelaySeconds = defaults.StartDelaySeconds;
        PauseOnFocusLoss = defaults.PauseOnFocusLoss;
        ShowOverlay = defaults.ShowOverlay;
        NewLineKeyIndex = defaults.ShiftEnterForNewLines ? 1 : 0;
        KeepCodeEditorTextExact = defaults.KeepCodeEditorTextExact;
        TypingMethodIndex = (int)defaults.TypingMethod;
    }

    private void UpdateSetting(double value, double current, Action<double> apply, string property, string label, string? hint = null, bool estimate = true)
    {
        if (value == current)
        {
            return;
        }

        apply(value);
        OnPropertyChanged(property);
        OnPropertyChanged(label);
        if (hint is not null)
        {
            OnPropertyChanged(hint);
        }

        if (estimate)
        {
            ScheduleEstimate();
        }
    }

    private TypingProfile CreateProfile() => new()
    {
        WordsPerMinute = _settings.WordsPerMinute,
        Variability = _settings.VariationPercent / 100,
        TypoRate = _settings.TypoPercent / 100,
        PauseScale = _settings.PausePercent / 100,
    };

    private void ScheduleEstimate()
    {
        _estimateTimer.Stop();
        _estimateTimer.Start();
    }

    /// <summary>Plans the text in the background to show how long typing it will take.</summary>
    private async void UpdateEstimate()
    {
        _estimateTimer.Stop();
        CancelEstimate();
        string text = Text;
        if (text.Length == 0)
        {
            TextSummary = "AutoKey types this into whichever window you choose.";
            return;
        }

        _estimateCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _estimateCancellation.Token;
        try
        {
            var planner = new HumanTypingPlanner(CreateProfile(), _keyboard);
            int samples = text.Length > 100_000 ? 1 : 5;
            var (estimate, words) = await Task.Run(
                () => (planner.Estimate(text, samples, EstimateSeed, cancellation), TextNormalizer.CountWords(text)),
                cancellation);
            if (cancellation.IsCancellationRequested)
            {
                return; // the text or settings changed in the meantime
            }

            string typos = estimate.TypoCount > 0 ? $"  ·  ~{estimate.TypoCount:N0} typo{(estimate.TypoCount == 1 ? "" : "s")}" : string.Empty;
            TextSummary = $"{estimate.CharacterCount:N0} characters  ·  {words:N0} words  ·  " +
                          $"about {Formatting.Duration(estimate.Duration)}{typos}";
        }
        catch (OperationCanceledException)
        {
            // A newer estimate has taken over.
        }
        catch (Exception ex)
        {
            TextSummary = $"Couldn't estimate the typing time: {ex.Message}";
        }
    }

    private void CancelEstimate()
    {
        _estimateCancellation?.Cancel();
        _estimateCancellation?.Dispose();
        _estimateCancellation = null;
    }

    public void Dispose()
    {
        _estimateTimer.Stop();
        CancelEstimate();
        _overlayTimer.Stop();
        if (_session is { } session && IsSessionActive)
        {
            session.Stop();
            session.WaitForExit(TimeSpan.FromSeconds(2)); // the typing thread releases any held keys
        }

        _typingScope?.Dispose();
        SaveSettings();
    }
}
