using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;
using Application = System.Windows.Forms.Application;
using Control = System.Windows.Forms.Control;
using Timer = System.Windows.Forms.Timer;

namespace Goalkeeper;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        _singleInstance = new Mutex(true, @"Local\GoalAutoResume-2f236151-6644-4fe3-bd12-6b82e3ad9b05", out bool firstInstance);
        if (!firstInstance)
        {
            _singleInstance.Dispose();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        _singleInstance.Dispose();
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "GoalAutoResume";
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _monitorItem;
    private readonly ToolStripMenuItem _retryItem;
    private readonly ToolStripMenuItem _dismissItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Timer _timer;
    private readonly AppSettings _settings;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDismissAttempt = DateTimeOffset.MinValue;
    private string _lastStatus = "";
    private bool _checking;
    private bool _exiting;

    public TrayContext()
    {
        _settings = AppSettings.Load();
        _settings.Save();
        _tray = new NotifyIcon();
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _monitorItem = new ToolStripMenuItem("Monitor Goals") { Checked = _settings.Enabled, CheckOnClick = true };
        _monitorItem.CheckedChanged += (_, _) =>
        {
            _settings.Enabled = _monitorItem.Checked;
            _settings.Save();
            Icon? oldIcon = _tray.Icon;
            _tray.Icon = MakeIcon(AnyMonitoringEnabled);
            oldIcon?.Dispose();
            SetStatus(MonitoringStatus());
            if (_settings.Enabled) _ = CheckAsync(true);
        };

        _retryItem = new ToolStripMenuItem("Auto-click model Retry")
        {
            Checked = _settings.AutoRetryEnabled,
            CheckOnClick = true
        };
        _retryItem.CheckedChanged += (_, _) =>
        {
            _settings.AutoRetryEnabled = _retryItem.Checked;
            _settings.Save();
            Icon? oldIcon = _tray.Icon;
            _tray.Icon = MakeIcon(AnyMonitoringEnabled);
            oldIcon?.Dispose();
            SetStatus(MonitoringStatus());
            if (_settings.AutoRetryEnabled) _ = CheckAsync(false);
        };

        _dismissItem = new ToolStripMenuItem("Auto-close notices")
        {
            Checked = _settings.AutoDismissContentNotices,
            CheckOnClick = true
        };
        _dismissItem.CheckedChanged += (_, _) =>
        {
            _settings.AutoDismissContentNotices = _dismissItem.Checked;
            _settings.Save();
            Icon? oldIcon = _tray.Icon;
            _tray.Icon = MakeIcon(AnyMonitoringEnabled);
            oldIcon?.Dispose();
            SetStatus(MonitoringStatus());
            if (_settings.AutoDismissContentNotices) _ = CheckAsync(false);
        };

        _startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        _startupItem.Click += (_, _) =>
        {
            bool enable = !_startupItem.Checked;
            try
            {
                SetStartup(enable);
                _startupItem.Checked = enable;
                EventLogFile.Write(enable ? "Windows startup enabled" : "Windows startup disabled");
            }
            catch (Exception ex)
            {
                EventLogFile.Write("Could not change Windows startup: " + ex.Message);
            }
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem("Goalkeeper") { Enabled = false, Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) });
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_monitorItem);
        _menu.Items.Add(_retryItem);
        _menu.Items.Add(_dismissItem);
        _menu.Items.Add(new ToolStripMenuItem("Check now", null, async (_, _) => await CheckAsync(true)));
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        _tray.Icon = MakeIcon(AnyMonitoringEnabled);
        _tray.Text = "Goalkeeper";
        _tray.ContextMenuStrip = _menu;
        _tray.Visible = true;
        _tray.DoubleClick += async (_, _) => await CheckAsync(true);

        _timer = new Timer { Interval = 2500, Enabled = true };
        _timer.Tick += async (_, _) => await CheckAsync(false);
        SetStatus(MonitoringStatus());
        EventLogFile.Write("App started; " + MonitoringStatus());
        if (AnyMonitoringEnabled) _ = CheckAsync(false);
    }

    private bool AnyMonitoringEnabled =>
        _settings.Enabled || _settings.AutoRetryEnabled || _settings.AutoDismissContentNotices;

    private string MonitoringStatus()
    {
        var features = new List<string>();
        if (_settings.Enabled) features.Add("Goals");
        if (_settings.AutoRetryEnabled) features.Add("Retry");
        if (_settings.AutoDismissContentNotices) features.Add("notices");
        return features.Count == 0 ? "Monitoring off" : "Monitoring " + string.Join(" + ", features);
    }

    private async Task CheckAsync(bool force)
    {
        if (_checking || _exiting || !AnyMonitoringEnabled) return;
        _checking = true;
        try
        {
            bool watchGoals = _settings.Enabled;
            bool watchRetry = _settings.AutoRetryEnabled;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool mayResume = force || now >= _nextAttempt;
            bool mayRetry = now >= _settings.NextRetryAtUtc;
            bool dismissNotices = _settings.AutoDismissContentNotices && now >= _nextDismissAttempt;
            var result = await Task.Run(() =>
            {
                ContentNoticeResult notice = dismissNotices ? ContentNoticeWatcher.Scan() : default;
                RetryScanResult retry = watchRetry ? RetryWatcher.Scan(mayRetry) : default;
                ScanResult goal = watchGoals && !retry.CandidateVisible
                    ? GoalWatcher.Scan(mayResume) : default;
                return (notice, retry, goal);
            });
            if (_exiting) return;

            if (result.notice.Attempted)
            {
                _nextDismissAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
                EventLogFile.Write(result.notice.Status);
            }
            if (result.retry.Attempted)
            {
                _settings.NextRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(RetryWatcher.CooldownSeconds);
                _settings.Save();
            }
            if (result.goal.Attempted)
                _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(20);

            string status = result.retry.CandidateVisible || !watchGoals
                ? result.retry.Status : result.goal.Status;
            SetStatus(string.IsNullOrEmpty(status) ? MonitoringStatus() : status);
        }
        catch (Exception ex)
        {
            SetStatus("Check failed; see log");
            EventLogFile.Write("Check failed: " + ex);
        }
        finally
        {
            _checking = false;
        }
    }

    private void SetStatus(string status)
    {
        _statusItem.Text = status;
        _tray.Text = ("Goalkeeper: " + status)[..Math.Min(63, ("Goalkeeper: " + status).Length)];
        if (status != _lastStatus)
        {
            _lastStatus = status;
            EventLogFile.Write(status);
        }
    }

    private static Icon MakeIcon(bool enabled)
    {
        using var image = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var circle = new SolidBrush(enabled ? Color.FromArgb(21, 139, 88) : Color.Gray);
            graphics.FillEllipse(circle, 2, 2, 28, 28);
            using var mark = new SolidBrush(Color.White);
            graphics.FillRectangle(mark, 11, 9, 3, 14);
            graphics.FillRectangle(mark, 18, 9, 3, 14);
        }
        nint handle = image.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void OpenLog()
    {
        EventLogFile.EnsureExists();
        Process.Start(new ProcessStartInfo(EventLogFile.Path) { UseShellExecute = true });
    }

    private static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(RunValue) as string, '"' + Application.ExecutablePath + '"', StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartup(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(RunValue, '"' + Application.ExecutablePath + '"', RegistryValueKind.String);
        else key.DeleteValue(RunValue, false);
    }

    private void Exit()
    {
        _exiting = true;
        _timer.Stop();
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        _timer.Dispose();
        EventLogFile.Write("App exited");
        ExitThread();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}

internal readonly record struct ScanResult(string Status, bool Attempted = false, bool Resumed = false);

internal static class GoalWatcher
{
    private static readonly Condition ResumeCondition = ButtonNamed("Resume goal");
    private static readonly Condition PauseCondition = ButtonNamed("Pause goal");

    public static ScanResult Scan(bool mayInvoke)
    {
        bool foundWindow = false;
        bool foundPursuing = false;
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement window in windows)
        {
            if (!IsChatGptWindow(window)) continue;
            foundWindow = true;

            AutomationElement? resume = FindGoalButton(window, ResumeCondition);
            if (resume is not null)
            {
                if (!mayInvoke) return new ScanResult("Goal Play control found; retry pending");

                // The second lookup prevents a transition during a UI redraw from toggling the Goal.
                Thread.Sleep(500);
                AutomationElement? confirmed = FindGoalButton(window, ResumeCondition);
                if (confirmed is null) return new ScanResult("Goal changed during check");

                try
                {
                    if (!confirmed.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                        return new ScanResult("Resume control cannot be invoked");
                    ((InvokePattern)pattern).Invoke();
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
                {
                    return new ScanResult("Resume attempt failed: " + ex.GetType().Name, Attempted: true);
                }

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    Thread.Sleep(400);
                    if (FindGoalButton(window, PauseCondition) is not null)
                        return new ScanResult("Goal resumed", Attempted: true, Resumed: true);
                }
                return new ScanResult("Resume pressed; state not confirmed", Attempted: true);
            }

            if (FindGoalButton(window, PauseCondition) is not null)
                foundPursuing = true;
        }
        if (foundPursuing) return new ScanResult("Goal is pursuing");
        return new ScanResult(foundWindow ? "No Goal control in current chat" : "ChatGPT is not running");
    }

    internal static bool IsChatGptWindow(AutomationElement window)
    {
        try
        {
            int pid = window.Current.ProcessId;
            if (pid <= 0) return false;
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ElementNotAvailableException)
        {
            return false;
        }
    }

    private static AutomationElement? FindGoalButton(AutomationElement window, Condition buttonCondition)
    {
        try
        {
            var matches = window.FindAll(TreeScope.Descendants, buttonCondition);
            foreach (AutomationElement button in matches)
            {
                if (!button.Current.IsEnabled) continue;
                if (button.Current.IsOffscreen && !IsIconic(window.Current.NativeWindowHandle)) continue;
                if (!IsInGoalToolbar(button)) continue;
                return button;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            EventLogFile.Write("Accessibility lookup failed: " + ex.GetType().Name);
        }
        return null;
    }

    internal static bool HasGoalControl(AutomationElement window) =>
        FindGoalButton(window, ResumeCondition) is not null ||
        FindGoalButton(window, PauseCondition) is not null;

    private static bool IsInGoalToolbar(AutomationElement button)
    {
        AutomationElement? ancestor = button;
        // Clear/Edit are siblings in the compact Goal toolbar. A separate resume
        // confirmation dialog can have the same button name, but not these siblings.
        for (int depth = 0; depth < 3 && ancestor is not null; depth++)
        {
            ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
            if (ancestor is null) break;
            if (ancestor.Current.ControlType != ControlType.Group) continue;

            var descendants = ancestor.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            bool clear = false, edit = false;
            foreach (AutomationElement item in descendants)
            {
                string name = item.Current.Name;
                if (name == "Clear goal") clear = true;
                else if (name == "Edit goal") edit = true;
            }
            if (clear && edit) return true;
        }
        return false;
    }

    private static Condition ButtonNamed(string name) => new AndCondition(
        new PropertyCondition(AutomationElement.NameProperty, name),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);
}

internal readonly record struct RetryScanResult(string Status, bool CandidateVisible = false, bool Attempted = false);

internal static class RetryWatcher
{
    internal const int CooldownSeconds = 5;
    private const string CapacityMessage = "Selected model is at capacity. Please try a different model.";
    private static readonly Regex RetryLabel = new(@"^Retry(?: in [0-9]+s)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Condition ButtonCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
    private static readonly Condition CapacityTextCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
        new PropertyCondition(AutomationElement.NameProperty, CapacityMessage));

    public static RetryScanResult Scan(bool mayInvoke)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement window in windows)
        {
            if (!GoalWatcher.IsChatGptWindow(window) || !GoalWatcher.HasGoalControl(window)) continue;

            AutomationElement? button = FindCapacityRetry(window);
            if (button is null) continue;

            try
            {
                if (!button.Current.IsEnabled)
                    return new RetryScanResult("Capacity Retry waiting for app", CandidateVisible: true);
                // ChatGPT already retries when its countdown expires. Only the bare
                // Retry label needs an additional manual click.
                if (button.Current.Name != "Retry")
                    return new RetryScanResult("Capacity Retry counting down", CandidateVisible: true);
                if (!mayInvoke)
                    return new RetryScanResult("Capacity Retry cooling down", CandidateVisible: true);

                Thread.Sleep(500);
                AutomationElement? confirmed = FindCapacityRetry(window);
                if (confirmed is null || !confirmed.Current.IsEnabled || confirmed.Current.Name != "Retry")
                    return new RetryScanResult("Capacity Retry changed during check", CandidateVisible: true);
                if (!confirmed.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                    return new RetryScanResult("Capacity Retry cannot be invoked", CandidateVisible: true);

                ((InvokePattern)pattern).Invoke();
                return new RetryScanResult("Capacity Retry clicked", CandidateVisible: true, Attempted: true);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                return new RetryScanResult("Capacity Retry attempt failed: " + ex.GetType().Name,
                    CandidateVisible: true, Attempted: true);
            }
        }

        return new RetryScanResult("No capacity Retry in current Goal");
    }

    private static AutomationElement? FindCapacityRetry(AutomationElement window)
    {
        try
        {
            var buttons = window.FindAll(TreeScope.Descendants, ButtonCondition);
            AutomationElement? latest = null;
            double lowest = double.NegativeInfinity;
            foreach (AutomationElement button in buttons)
            {
                if (!RetryLabel.IsMatch(button.Current.Name)) continue;
                if (button.Current.IsOffscreen && !IsIconic(window.Current.NativeWindowHandle)) continue;

                AutomationElement? card = TreeWalker.RawViewWalker.GetParent(button);
                if (card?.Current.ControlType != ControlType.Group ||
                    card.FindFirst(TreeScope.Children, CapacityTextCondition) is null) continue;

                double top = button.Current.BoundingRectangle.Top;
                if (latest is null || top > lowest)
                {
                    latest = button;
                    lowest = top;
                }
            }
            return latest;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            EventLogFile.Write("Capacity Retry lookup failed: " + ex.GetType().Name);
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);
}

internal readonly record struct ContentNoticeResult(string Status, bool Attempted = false);

internal static class ContentNoticeWatcher
{
    private const string FullAccessDismissName = "Dismiss Full access warning for this session";
    private static readonly Condition DismissCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "Dismiss safety banner"),
            new PropertyCondition(AutomationElement.NameProperty, FullAccessDismissName),
            new PropertyCondition(AutomationElement.NameProperty, "Dismiss")));
    private static readonly Condition TitleCondition = TextNamed("This content can’t be shown");
    private static readonly Condition FullAccessTitleCondition = TextNamed("Full access is on");
    private const string FullAccessBody =
        "ChatGPT can edit any file and run commands with internet access without your approval. This increases the risk of data loss, exposed information, and unexpected changes.";
    private static readonly Condition ExtraThoughtTitleCondition = new PropertyCondition(
        AutomationElement.NameProperty, "Giving this request a little extra thought");
    private const string ExtraThoughtBody =
        "If you'd rather not wait, retry with a faster model. It may be less capable of handling complex requests.";
    private static readonly Condition BodyCondition = TextNamed(
        "Daybreak isn’t available for Astra. Some cybersecurity requests may still be limited.");
    private const string DaybreakBlueBody =
        "This content was flagged for possible cybersecurity risk. You’re approved for Daybreak Blue. To use it on your individual account, enroll in Advanced Account Security and use a compatible FIDO2 hardware security key. Software or synced passkeys don’t satisfy the hardware-key requirement.";
    private const string DaybreakBlueTail =
        "under Your eligibility. Daybreak Blue reduces refusals for authorized defensive work on supported models. Other safeguards still apply.";

    public static ContentNoticeResult Scan()
    {
        try { return ScanCore(); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // A transient notice lookup failure must not skip Goal or Retry checks.
            return new ContentNoticeResult("Content notice check failed: " + ex.GetType().Name,
                Attempted: true);
        }
    }

    private static ContentNoticeResult ScanCore()
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement window in windows)
        {
            if (!GoalWatcher.IsChatGptWindow(window)) continue;
            try
            {
                AutomationElement? button = FindDismissButton(window);
                if (button is null) continue;

                int[] runtimeId = button.GetRuntimeId();
                Thread.Sleep(200);
                AutomationElement? confirmed = FindDismissButton(window);
                if (confirmed is null || !runtimeId.SequenceEqual(confirmed.GetRuntimeId())) continue;
                if (confirmed.Current.Name == "Dismiss")
                    return DismissExtraThought(window, confirmed, runtimeId);
                string noticeName = confirmed.Current.Name == FullAccessDismissName
                    ? "Full access notice" : "Content notice";
                if (!confirmed.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                    return new ContentNoticeResult(noticeName + " cannot be dismissed", Attempted: true);

                ((InvokePattern)pattern).Invoke();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Thread.Sleep(200);
                    if (!DismissButtonStillPresent(window, runtimeId))
                        return new ContentNoticeResult(noticeName + " dismissed", Attempted: true);
                }
                return new ContentNoticeResult(noticeName + " close pressed; state not confirmed", Attempted: true);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                return new ContentNoticeResult("Content notice check failed: " + ex.GetType().Name,
                    Attempted: true);
            }
        }
        return default;
    }

    private static AutomationElement? FindDismissButton(AutomationElement window)
    {
        var buttons = window.FindAll(TreeScope.Descendants, DismissCondition);
        foreach (AutomationElement button in buttons)
        {
            if (!button.Current.IsEnabled || button.Current.IsOffscreen) continue;
            if (button.Current.Name == "Dismiss")
            {
                // Opening a menu is limited to the foreground app so this
                // notice cannot interrupt work in another window.
                if (IsForeground(window) && IsExtraThoughtNotice(button)) return button;
                continue;
            }
            if (button.Current.Name == FullAccessDismissName)
            {
                if (IsFullAccessNotice(button)) return button;
                continue;
            }
            AutomationElement? notice = TreeWalker.RawViewWalker.GetParent(button);
            // Match the title and a known message in the button's own notice.
            // The Blue message is split by its security-setup link; only the
            // named dismiss button is invoked.
            if (notice is not null &&
                notice.FindFirst(TreeScope.Children, TitleCondition) is not null &&
                HasKnownBody(notice))
                return button;
        }
        return null;
    }

    private static bool IsExtraThoughtNotice(AutomationElement button)
    {
        AutomationElement? notice = TreeWalker.RawViewWalker.GetParent(button);
        if (notice is null ||
            notice.FindFirst(TreeScope.Children, ExtraThoughtTitleCondition) is null) return false;
        var texts = notice.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        foreach (AutomationElement text in texts)
            if (text.Current.Name.Trim() == ExtraThoughtBody) return true;
        return false;
    }

    private static ContentNoticeResult DismissExtraThought(
        AutomationElement window, AutomationElement button, int[] runtimeId)
    {
        // Leave an already-open user menu alone. Radix portals the Dismiss
        // menu outside the banner, so match its label and exact two actions.
        if (!IsForeground(window) || HasVisibleMenu(window)) return default;
        ExpandCollapsePattern? expand = button.TryGetCurrentPattern(
            ExpandCollapsePattern.Pattern, out object? expandObject)
            ? (ExpandCollapsePattern)expandObject : null;
        InvokePattern? invoke = button.TryGetCurrentPattern(
            InvokePattern.Pattern, out object? invokeObject)
            ? (InvokePattern)invokeObject : null;
        if (expand is null && invoke is null)
            return new ContentNoticeResult("Extra-thought notice menu cannot be opened", Attempted: true);
        if (expand is not null && expand.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            return default;
        if (!IsForeground(window) || !IsExtraThoughtNotice(button)) return default;

        try
        {
            if (expand is not null) expand.Expand();
            else invoke!.Invoke();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Thread.Sleep(100);
                if (!IsForeground(window) || !IsExtraThoughtNotice(button)) break;
                AutomationElement? menu = FindExtraThoughtMenu(window, button);
                if (menu is null) continue;
                AutomationElement? item = menu.FindFirst(TreeScope.Descendants, MenuItemNamed("This time"));
                if (item is null || !item.Current.IsEnabled || item.Current.IsOffscreen ||
                    !item.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern)) break;
                if (!IsForeground(window)) break;
                ((InvokePattern)pattern).Invoke();
                for (int check = 0; check < 3; check++)
                {
                    Thread.Sleep(200);
                    if (!DismissButtonStillPresent(window, runtimeId))
                        return new ContentNoticeResult("Extra-thought notice dismissed for this turn", Attempted: true);
                }
                return new ContentNoticeResult("Extra-thought notice dismissal pressed; state not confirmed", Attempted: true);
            }
            return new ContentNoticeResult("Extra-thought notice menu action not available", Attempted: true);
        }
        finally
        {
            // Close only the menu opened for this exact banner if an action
            // failed. No cursor movement, keystrokes, or window activation.
            try
            {
                if (IsForeground(window) && FindExtraThoughtMenu(window, button) is not null)
                {
                    if (expand is not null && expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                        expand.Collapse();
                    else invoke?.Invoke();
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                EventLogFile.Write("Extra-thought menu cleanup failed: " + ex.GetType().Name);
            }
        }
    }

    private static bool HasVisibleMenu(AutomationElement window)
    {
        var menus = window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
        foreach (AutomationElement menu in menus)
            if (!menu.Current.IsOffscreen) return true;
        return false;
    }

    private static AutomationElement? FindExtraThoughtMenu(AutomationElement window, AutomationElement button)
    {
        var menus = window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
        foreach (AutomationElement menu in menus)
        {
            if (menu.Current.IsOffscreen || menu.Current.Name != "Dismiss") continue;
            AutomationElement? label = menu.Current.LabeledBy;
            if (label is not null && !label.GetRuntimeId().SequenceEqual(button.GetRuntimeId())) continue;
            var items = menu.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            if (items.Count != 2 ||
                menu.FindFirst(TreeScope.Descendants, MenuItemNamed("This time")) is null ||
                menu.FindFirst(TreeScope.Descendants, MenuItemNamed("Don’t show again")) is null) continue;
            return menu;
        }
        return null;
    }

    private static Condition MenuItemNamed(string name) => new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new PropertyCondition(AutomationElement.NameProperty, name));

    private static bool IsForeground(AutomationElement window) =>
        GetForegroundWindow() == window.Current.NativeWindowHandle;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private static bool IsFullAccessNotice(AutomationElement button)
    {
        // The session X is inside the notice's actions group. UIA may flatten
        // that group; inspect only the nearest ancestors, never the whole chat.
        AutomationElement? notice = button;
        for (int depth = 0; depth < 3; depth++)
        {
            notice = TreeWalker.RawViewWalker.GetParent(notice);
            if (notice is null || notice.Current.ControlType == ControlType.Window ||
                notice.Current.ControlType == ControlType.Document) return false;
            if (notice.FindFirst(TreeScope.Descendants, FullAccessTitleCondition) is null) continue;
            var texts = notice.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement text in texts)
            {
                // The body ends with a separate Learn more link and text node.
                if (text.Current.Name.Trim() == FullAccessBody) return true;
            }
        }
        return false;
    }

    private static bool DismissButtonStillPresent(AutomationElement window, int[] runtimeId)
    {
        // A second supported notice can remain after this one is dismissed.
        var buttons = window.FindAll(TreeScope.Descendants, DismissCondition);
        foreach (AutomationElement button in buttons)
            if (runtimeId.SequenceEqual(button.GetRuntimeId())) return true;
        return false;
    }

    private static bool HasKnownBody(AutomationElement notice)
    {
        if (notice.FindFirst(TreeScope.Children, BodyCondition) is not null) return true;
        var texts = notice.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        bool blueBody = false, blueTail = false;
        foreach (AutomationElement text in texts)
        {
            // Inline links leave separator whitespace at the edges of text nodes.
            string value = text.Current.Name.Trim();
            blueBody |= value == DaybreakBlueBody;
            blueTail |= value == DaybreakBlueTail;
        }
        return blueBody && blueTail;
    }

    private static Condition TextNamed(string name) => new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
        new PropertyCondition(AutomationElement.NameProperty, name));
}

internal sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoRetryEnabled { get; set; }
    public bool AutoDismissContentNotices { get; set; } = true;
    public DateTimeOffset NextRetryAtUtc { get; set; } = DateTimeOffset.MinValue;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                DateTimeOffset latestRetry = DateTimeOffset.UtcNow.AddSeconds(RetryWatcher.CooldownSeconds);
                if (settings.NextRetryAtUtc > latestRetry)
                {
                    settings.NextRetryAtUtc = latestRetry;
                    settings.Save();
                }
                return settings;
            }
        }
        catch (Exception ex) { EventLogFile.Write("Settings could not be read: " + ex.Message); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) { EventLogFile.Write("Settings could not be saved: " + ex.Message); }
    }

    private static string SettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoalAutoResume", "settings.json");
}

internal static class EventLogFile
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoalAutoResume", "events.log");

    public static void EnsureExists()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (!File.Exists(Path)) File.WriteAllText(Path, "");
    }

    public static void Write(string message)
    {
        try
        {
            EnsureExists();
            File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch { /* Logging must never stop monitoring. */ }
    }
}
