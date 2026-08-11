using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppLockerOverlay;

public partial class Form1 : Form
{
    private string _configPath;
    private LockerConfig _config = new();

    /// <summary>Plaintext master for this process; used to encrypt the whole settings file on save.</summary>
    private string? _masterPasswordForEncryptionSession;

    /// <summary>One standalone unlock dialog per lock rule (RootPid), covering all host HWNDs for that rule.</summary>
    private readonly Dictionary<int, OverlayForm> _lockGroupDialogs = new();
    private readonly Dictionary<int, LockRule> _lockRules = new();
    private Dictionary<int, ProcessSnapshotItem> _processSnapshot = new();
    private DateTime _lastSnapshotRefreshUtc = DateTime.MinValue;
    private readonly HashSet<nint> _unlockedWindowHandles = [];
    private readonly Dictionary<nint, string> _unlockedWindowTitles = new();

    /// <summary>Windows we hid for locking — still tracked after SW_HIDE so the unlock dialog stays alive.</summary>
    private readonly HashSet<nint> _hiddenForLockHwnds = [];

    /// <summary>Hosts that already received a media pause/stop for this lock session (avoid repeating every watcher tick).</summary>
    private readonly HashSet<nint> _mediaPausedForLockHwnds = [];

    /// <summary>Window size/max/normal state captured before hide, restored on unlock (avoids stuck maximize).</summary>
    private readonly Dictionary<nint, WINDOWPLACEMENT> _savedWindowPlacements = new();

    private readonly System.Windows.Forms.Timer _watcherTimer = new() { Interval = 220 };

    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _trayStatusItem;
    private ToolStripMenuItem? _trayToggleItem;
    private bool _isShuttingDown;

    private Label? _statusLabel;
    private TreeView? _processTree;
    private ComboBox? _lockScopeCombo;
    private TextBox? _searchProcessBox;
    private ListBox? _lockedRulesList;
    private ListBox? _tamperAlertsList;
    private Label? _alertsDetailLabel;
    private Button? _dismissTamperAlertButton;
    private Label? _tamperAlertsLabel;
    private Button? _alertsBellButton;
    private Panel? _alertsPopupPanel;
    private readonly List<TamperAlertItem> _tamperAlerts = new();
    private Button? _toggleButton;
    private Label? _lockScopeHelpLabel;
    private Label? _lockScopeLabel;
    private Label? _criticalSelectionWarningLabel;
    private Button? _changeMasterButton;
    private Button? _hideToTrayButton;
    private LinkLabel? _securityRecoveryLink;
    private LinkLabel? _uninstallAppLink;
    private LinkLabel? _usbLockdownOffLink;
    private ToolStripMenuItem? _trayUsbLockdownOffItem;
    private readonly System.Windows.Forms.Timer _hubIdleTimer = new() { Interval = 15_000 };
    private readonly System.Windows.Forms.Timer _usbLockdownTimer = new() { Interval = 1600 };
    private bool _usbLockdownPromptActive;
    private readonly HashSet<int> _usbLockdownAllowedMmcPids = new();
    private bool _hubOpenRequiresMasterVerify;
    private int _hubClientWidth = 960;
    private bool _hasHydratedLockRulesFromDisk;

    /// <summary>Locks were restored from the enforcement manifest; the settings file is still encrypted and must not be written.</summary>
    private bool _isEnforcementOnlyMode;

    /// <summary>False until the master password is accepted, so an auto-restarted hub stays in the tray.</summary>
    private bool _allowHubWindowToBeShown = true;

    /// <summary>
    /// While true, lock overlays must not BringToFront/Activate — that was stealing focus from the master-password box
    /// every watcher tick after a force-kill recovery.
    /// </summary>
    private int _focusSensitiveUiDepth;

    private DateTime _lastGlobalMediaStopUtc = DateTime.MinValue;

    private readonly List<string> _pendingSessionEndedNotices = new();
    private bool _sessionEndedNoticeScheduled;
    private bool _lockedSessionTrackerLoaded;

    private int _watchdogProcessId;
    private DateTime _lastWatchdogCheckUtc = DateTime.MinValue;
    private DateTime _watchdogNextSpawnUtc = DateTime.MinValue;
    private int _watchdogImmediateCrashStreak;
    private readonly Dictionary<nint, LockCoverForm> _lockCovers = new();

    /// <summary>Set when this build cannot spawn a helper at all (for example under <c>dotnet run</c>), so we stop retrying.</summary>
    private bool _watchdogSpawnUnavailable;
    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "taskmgr",
        "dwm",
        "startmenuexperiencehost",
        "searchhost",
        "searchapp",
        "shellexperiencehost",
        "applicationframehost",
        "textinputhost",
        "lockapp",
        "systemsettings",
        "snippingtool",
        "screenclippinghost",
        "ctfmon",
        "widgets",
        "msedgewebview2",
        "runtimebroker",
        "dllhost",
        "smartscreen",
        "securityhealthsystray"
    };

    private static readonly HashSet<string> IgnoredClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    };

    public Form1()
    {
        _configPath = ConfigPathResolver.ResolveConfigFilePath();
        EnsureLockedSessionTrackerLoaded();
        var configDir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(configDir))
        {
            Directory.CreateDirectory(configDir);
        }

        AppLogger.Log("Form1 constructor start.", _configPath);

        var ranWizard = false;
        if (!File.Exists(_configPath))
        {
            using var wizard = new FirstRunWizardForm(ConfigPathResolver.SuggestedStorageDirectory);
            if (wizard.ShowDialog() != DialogResult.OK)
            {
                AppLogger.Log("First-run setup cancelled.");
                _isShuttingDown = true;
                BeginInvoke(new Action(() => Application.ExitThread()));
                return;
            }

            _configPath = wizard.ResultConfigFilePath;
            _masterPasswordForEncryptionSession = wizard.CommittedMasterPassword;
            ranWizard = true;
        }

        InitializeComponent();
        BuildControlUi();

        if (ranWizard)
        {
            if (!LoadConfig())
            {
                MessageBox.Show(
                    null,
                    "Windlock could not read the new settings file. You can delete it and restart to run setup again.",
                    "Windlock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _isShuttingDown = true;
                BeginInvoke(new Action(() => Application.ExitThread()));
                return;
            }
        }
        else if (File.Exists(_configPath))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(_configPath);
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not read settings file.", ex);
                _isShuttingDown = true;
                BeginInvoke(new Action(() => Application.ExitThread()));
                return;
            }

            if (ConfigCrypto.IsEncryptedPayload(bytes))
            {
                // Restoring from the manifest keeps locks running after a force-kill without anyone typing the master password.
                if (!TryStartInEnforcementOnlyMode() && !UnlockEncryptedStorageAtStartup())
                {
                    _isShuttingDown = true;
                    BeginInvoke(new Action(() => Application.ExitThread()));
                    return;
                }
            }
            else
            {
                if (!LoadConfig())
                {
                    _isShuttingDown = true;
                    BeginInvoke(new Action(() => Application.ExitThread()));
                    return;
                }

                if (!PromptAndVerifyMasterPassword(
                        "Unlock Windlock Hub",
                        "Enter master password to open Windlock"))
                {
                    AppLogger.Log("Startup master password failed or cancelled.");
                    _isShuttingDown = true;
                    BeginInvoke(new Action(() => Application.ExitThread()));
                    return;
                }
            }
        }
        else
        {
            if (!LoadConfig())
            {
                _isShuttingDown = true;
                BeginInvoke(new Action(() => Application.ExitThread()));
                return;
            }
        }

        BuildTrayIcon();
        _hubIdleTimer.Tick += HubIdleTimerOnTick;
        _hubIdleTimer.Start();
        VisibleChanged += (_, _) => RefreshLockDialogsStayOnTopFromSettings();

        RefreshProcessSnapshotAndTree();
        ApplyProtectionFromRulesNow();

        _watcherTimer.Tick += WatcherTimerOnTick;
        _watcherTimer.Start();
        UpdateUi();

        if (!_isEnforcementOnlyMode && !_config.SecurityAndRecoveryIntroShown && TryOpenSecurityAndRecoveryDialog(isIntroMode: true))
        {
            _config.SecurityAndRecoveryIntroShown = true;
            SaveConfig();
        }

        if (_isEnforcementOnlyMode)
        {
            NotifyEnforcementOnlyStartup();
        }

        TrySpawnWatchdogProcess(killExistingHelpers: true);

        if (_config.UsbLockdownBetaEnabled)
        {
            TryStartUsbLockdownMonitor();
        }

        AppLogger.Log("Form1 initialized successfully.");
    }

    /// <summary>
    /// Rebuilds just enough state from the DPAPI manifest to keep locks enforced. Used when the settings file is
    /// encrypted and nobody has entered the master password yet, which is what happens after the hub is force-killed.
    /// </summary>
    private bool TryStartInEnforcementOnlyMode()
    {
        var manifest = EnforcementManifestStore.TryLoad(_configPath);
        if (manifest is null || !manifest.HasEnforceableRules || string.IsNullOrWhiteSpace(manifest.MasterPasswordHash))
        {
            return false;
        }

        _config = new LockerConfig
        {
            Enabled = manifest.ProtectionEnabled,
            MasterPasswordHash = manifest.MasterPasswordHash,
            LockRules = manifest.LockRules,
            CloseTaskManagerWhileLockRulesActive = manifest.CloseTaskManagerWhileLockRulesActive,
            BlockSystemToolsWhileProtected = manifest.BlockSystemToolsWhileProtected,
            LockDialogsStayOnTop = manifest.LockDialogsStayOnTop,
            LockDialogsStayOnTopPromptShown = true,
            SecurityAndRecoveryIntroShown = true,
            // Keep auto-restart armed after a force-kill recover so the next End task is covered too.
            WatchdogRelaunchOnForceKill = true
        };
        _config.Normalize();

        _isEnforcementOnlyMode = true;
        _allowHubWindowToBeShown = false;
        _hubOpenRequiresMasterVerify = true;
        AppLogger.Log($"Enforcement-only start: restored {_config.LockRules.Count} rule(s) from the manifest without the master password.");
        return true;
    }

    private void NotifyEnforcementOnlyStartup()
    {
        var reason = WatchdogRelaunch.StartedAfterAbruptExit
            ? "Windlock was closed unexpectedly and restarted itself."
            : "Windlock is running in the tray.";
        try
        {
            _trayIcon?.ShowBalloonTip(
                7000,
                "Windlock",
                $"{reason} Protection is active for {_lockRules.Count} app(s). Open the hub from the tray icon to manage it.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not show the enforcement-only balloon tip.", ex);
        }
    }

    /// <summary>Once the master password is known, decrypt the real settings file so the hub works normally again.</summary>
    private void TryLeaveEnforcementOnlyMode()
    {
        if (!_isEnforcementOnlyMode)
        {
            return;
        }

        if (!LoadConfig())
        {
            AppLogger.Log("Enforcement-only mode: the settings file still could not be decrypted; staying read-only.");
            return;
        }

        _isEnforcementOnlyMode = false;
        SaveConfig();
        AppLogger.Log("Enforcement-only mode ended; the settings file is unlocked for this session.");
        UpdateUi();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowHubWindowToBeShown)
        {
            value = false;
            if (!IsHandleCreated)
            {
                CreateHandle();
            }
        }

        base.SetVisibleCore(value);
    }

    /// <summary>If the hub already has the master password for this session, do not ask again before overlay unlock (still asks when session has no master).</summary>
    private bool AuthorizeMasterBeforeOverlayUnlock(Form overlay)
    {
        if (!string.IsNullOrWhiteSpace(_masterPasswordForEncryptionSession))
        {
            return true;
        }

        return PromptAndVerifyMasterPassword(
            "Master password",
            "Enter your Windlock master password to authorize unlocking this locked group.",
            overlay);
    }

    /// <summary>System-tool blacklist always asks for the master password (session unlock must not bypass Explorer/shells).</summary>
    private bool AuthorizeMasterForSystemToolOverlay(Form overlay) =>
        PromptAndVerifyMasterPassword(
            "Master password",
            "Enter your Windlock master password to close this blocked system tool.\n\n"
            + "It will not stay unlocked. To use File Explorer or shells freely, turn off System tools blacklist in Security & recovery.",
            overlay);

    private void RevokeUnlockedHandlesForSystemToolBlacklist(Dictionary<int, LockRule> pidRuleMap)
    {
        if (!_config.Enabled || !_config.BlockSystemToolsWhileProtected || pidRuleMap.Count == 0)
        {
            return;
        }

        foreach (var hwnd in _unlockedWindowHandles.ToList())
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                _unlockedWindowHandles.Remove(hwnd);
                _unlockedWindowTitles.Remove(hwnd);
                continue;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pidU);
            if (pidRuleMap.TryGetValue((int)pidU, out var rule) && rule.IsSystemToolBlacklist)
            {
                _unlockedWindowHandles.Remove(hwnd);
                _unlockedWindowTitles.Remove(hwnd);
            }
        }
    }

    private static void CloseSystemToolHostWindows(IEnumerable<nint> hwnds)
    {
        foreach (var hwnd in hwnds)
        {
            try
            {
                if (NativeMethods.IsWindow(hwnd))
                {
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, nint.Zero, nint.Zero);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static bool WatchdogCommandLineReferencesParentPid(string? commandLine, int parentPid)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return false;
        }

        const string prefix = "--watchdog-parent";
        var idx = commandLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        idx += prefix.Length;
        while (idx < commandLine.Length && char.IsWhiteSpace(commandLine[idx]))
        {
            idx++;
        }

        var end = idx;
        while (end < commandLine.Length && char.IsDigit(commandLine[end]))
        {
            end++;
        }

        if (end == idx)
        {
            return false;
        }

        return int.TryParse(commandLine.AsSpan(idx, end - idx), out var parsed) && parsed == parentPid;
    }

    private void TryKillWatchdogHelperProcessesForCurrentHub()
    {
        var myPid = Process.GetCurrentProcess().Id;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
            foreach (var obj in searcher.Get())
            {
                if (obj is not ManagementObject mo)
                {
                    continue;
                }

                var pid = Convert.ToInt32(mo["ProcessId"] ?? 0);
                if (pid <= 0 || pid == myPid)
                {
                    continue;
                }

                var cmd = mo["CommandLine"]?.ToString();
                if (!WatchdogCommandLineReferencesParentPid(cmd, myPid))
                {
                    continue;
                }

                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: false);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not enumerate/kill watchdog helper processes.", ex);
        }
    }

    private void TrySpawnWatchdogProcess(bool killExistingHelpers)
    {
        if (killExistingHelpers)
        {
            TryKillWatchdogHelperProcessesForCurrentHub();
        }

        if (!_config.WatchdogRelaunchOnForceKill && !_config.SuicideRebootOnAbruptTermination)
        {
            return;
        }

        var pp = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(pp))
        {
            _watchdogSpawnUnavailable = true;
            return;
        }

        var pid = Process.GetCurrentProcess().Id;
        string fileName;
        string arguments;
        string? hubExeForRelaunch = null;

        if (pp.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!_config.SuicideRebootOnAbruptTermination)
            {
                // Under "dotnet run" there is no .exe to relaunch, so a relaunch-only helper would do nothing.
                _watchdogSpawnUnavailable = true;
                return;
            }

            // Avoid Assembly.Location: empty when published as a single-file bundle; BaseDirectory + name works for dotnet exec.
            var asmName = typeof(Program).Assembly.GetName().Name;
            if (string.IsNullOrEmpty(asmName))
            {
                _watchdogSpawnUnavailable = true;
                return;
            }

            var dll = Path.Combine(AppContext.BaseDirectory, asmName + ".dll");
            if (!File.Exists(dll))
            {
                _watchdogSpawnUnavailable = true;
                return;
            }

            fileName = pp;
            arguments =
                $"exec \"{dll}\" --watchdog-parent {pid} --suicide-reboot --suicide-delay-sec {_config.SuicideRebootDelaySeconds}";
        }
        else
        {
            hubExeForRelaunch = pp;
            // Random process name (rotated each spawn) so End task on Windlock does not kill the watcher.
            var helperExe = WatchdogRelaunch.EnsureHelperExecutable(pp, rotateName: killExistingHelpers);
            if (string.IsNullOrEmpty(helperExe))
            {
                AppLogger.Log("Watchdog: could not create helper executable; falling back to same-exe helper (weaker against Task Manager).");
                fileName = pp;
            }
            else
            {
                fileName = helperExe;
            }

            arguments = WatchdogRelaunch.BuildHelperArguments(
                pid,
                hubExeForRelaunch,
                _config.SuicideRebootOnAbruptTermination,
                _config.SuicideRebootDelaySeconds);
        }

        try
        {
            // Working directory must be the helper's folder so a LocalAppData bundle finds its DLLs.
            // (The apphost also probes next to the exe; both must be consistent.)
            var workDir = Path.GetDirectoryName(fileName);
            if (string.IsNullOrWhiteSpace(workDir))
            {
                workDir = Path.GetDirectoryName(hubExeForRelaunch) ?? Environment.CurrentDirectory;
            }

            var startedPid = 0;
            // Detach from this process tree. A normal child Process.Start is killed with the hub by Task Manager.
            if (!fileName.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
                NativeMethods.TryStartDetachedProcess(fileName, arguments, workDir, out var detachedPid))
            {
                startedPid = detachedPid;
                AppLogger.Log($"Watchdog helper process started detached (PID {detachedPid}, exe={fileName}).");
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = workDir
                };
                var child = Process.Start(psi);
                if (child is not null)
                {
                    startedPid = child.Id;
                    child.Dispose();
                    AppLogger.Log($"Watchdog helper process started (PID {startedPid}, exe={fileName}).");
                }
            }

            if (startedPid <= 0)
            {
                return;
            }

            _watchdogProcessId = startedPid;
            Thread.Sleep(400);
            if (!IsProcessAlive(startedPid))
            {
                _watchdogImmediateCrashStreak++;
                var backoffSec = Math.Min(60, 5 * _watchdogImmediateCrashStreak);
                _watchdogNextSpawnUtc = DateTime.UtcNow.AddSeconds(backoffSec);
                AppLogger.Log(
                    $"Watchdog helper PID {startedPid} exited immediately (missing DLLs or startup crash). " +
                    $"Backing off {backoffSec}s (streak={_watchdogImmediateCrashStreak}).");
                _watchdogProcessId = 0;
                return;
            }

            _watchdogImmediateCrashStreak = 0;
            _watchdogNextSpawnUtc = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watchdog spawn failed.", ex);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restarts the helper if it was killed, so hub and helper keep each other alive.</summary>
    private void EnsureWatchdogHelperRunning()
    {
        if (_isShuttingDown || _watchdogSpawnUnavailable)
        {
            return;
        }

        if (!_config.WatchdogRelaunchOnForceKill && !_config.SuicideRebootOnAbruptTermination)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastWatchdogCheckUtc).TotalSeconds < 2)
        {
            return;
        }

        _lastWatchdogCheckUtc = DateTime.UtcNow;

        if (IsWatchdogHelperAlive())
        {
            _watchdogImmediateCrashStreak = 0;
            return;
        }

        if (DateTime.UtcNow < _watchdogNextSpawnUtc)
        {
            return;
        }

        AppLogger.Log("Watchdog helper is gone; starting a replacement with a new random name.");
        // Rotate the helper file name whenever the previous watcher exits.
        TrySpawnWatchdogProcess(killExistingHelpers: true);
    }

    private bool IsWatchdogHelperAlive()
    {
        if (_watchdogProcessId > 0)
        {
            try
            {
                using var p = Process.GetProcessById(_watchdogProcessId);
                if (!p.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                _watchdogProcessId = 0;
            }
            catch
            {
                // fall through to command-line scan
            }
        }

        // Detached helpers may not keep a stable tracked PID after a hub restart; scan by command line.
        var myPid = Process.GetCurrentProcess().Id;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
            foreach (var obj in searcher.Get())
            {
                if (obj is not ManagementObject mo)
                {
                    continue;
                }

                var pid = Convert.ToInt32(mo["ProcessId"] ?? 0);
                if (pid <= 0 || pid == myPid)
                {
                    continue;
                }

                var cmd = mo["CommandLine"]?.ToString();
                if (!WatchdogCommandLineReferencesParentPid(cmd, myPid))
                {
                    continue;
                }

                _watchdogProcessId = pid;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not scan for watchdog helper processes.", ex);
        }

        return false;
    }

    internal void ApplyWatchdogRelaunchSetting(bool enabled)
    {
        if (_config.WatchdogRelaunchOnForceKill == enabled)
        {
            return;
        }

        _config.WatchdogRelaunchOnForceKill = enabled;
        SaveConfig();
    }

    internal void ApplySuicideRebootSetting(bool enabled)
    {
        if (_config.SuicideRebootOnAbruptTermination == enabled)
        {
            return;
        }

        _config.SuicideRebootOnAbruptTermination = enabled;
        SaveConfig();
    }

    internal void ApplySuicideRebootDelaySeconds(int seconds)
    {
        seconds = Math.Clamp(seconds, 0, 86400);
        if (_config.SuicideRebootDelaySeconds == seconds)
        {
            return;
        }

        _config.SuicideRebootDelaySeconds = seconds;
        SaveConfig();
    }

    internal void ApplyCloseTaskManagerWhileRulesSetting(bool enabled)
    {
        if (_config.CloseTaskManagerWhileLockRulesActive == enabled)
        {
            return;
        }

        _config.CloseTaskManagerWhileLockRulesActive = enabled;
        SaveConfig();
    }

    internal void ApplyBlockSystemToolsWhileProtectedSetting(bool enabled)
    {
        if (_config.BlockSystemToolsWhileProtected == enabled)
        {
            return;
        }

        _config.BlockSystemToolsWhileProtected = enabled;
        if (enabled)
        {
            RemoveSystemToolProcessesFromUserLockRules();
        }

        SaveConfig();
        ApplyProtectionFromRulesNow();
        UpdateLockedRulesList();
        UpdateUi();
    }

    /// <summary>Active locks are for apps only — drop Explorer/shell rules that the system-tools blacklist already covers.</summary>
    private void RemoveSystemToolProcessesFromUserLockRules()
    {
        var remove = _lockRules.Values
            .Where(r =>
                SystemToolBlacklist.IsExplorerProcessName(r.ProcessName) ||
                SystemToolBlacklist.IsExplorerProcessName(r.DisplayName) ||
                SystemToolBlacklist.IsShellProcessName(r.ProcessName) ||
                SystemToolBlacklist.IsShellProcessName(r.DisplayName))
            .Select(r => r.RootPid)
            .ToList();

        foreach (var rootPid in remove)
        {
            _lockRules.Remove(rootPid);
            RemoveLockGroup(rootPid, reEnableWindows: false);
        }

        if (remove.Count > 0)
        {
            AppLogger.Log($"Removed {remove.Count} Explorer/shell rule(s) from Active locks (system-tools blacklist covers them).");
        }
    }

    /// <summary>Re-reads flags and delay from <see cref="_config"/> and replaces any helper process.</summary>
    internal void RefreshWatchdogHelperProcess()
    {
        _watchdogSpawnUnavailable = false;
        TrySpawnWatchdogProcess(killExistingHelpers: true);
    }

    private void TryStartUsbLockdownMonitor()
    {
        _usbLockdownTimer.Tick -= UsbLockdownTimerOnTick;
        _usbLockdownTimer.Tick += UsbLockdownTimerOnTick;
        if (!_usbLockdownTimer.Enabled)
        {
            _usbLockdownTimer.Start();
        }
    }

    private void TryStopUsbLockdownMonitor()
    {
        _usbLockdownTimer.Stop();
        _usbLockdownTimer.Tick -= UsbLockdownTimerOnTick;
        _usbLockdownAllowedMmcPids.Clear();
    }

    private void UsbLockdownTimerOnTick(object? sender, EventArgs e)
    {
        if (!_config.UsbLockdownBetaEnabled || _usbLockdownPromptActive || _isShuttingDown)
        {
            return;
        }

        foreach (var pid in _usbLockdownAllowedMmcPids.ToList())
        {
            try
            {
                _ = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                _usbLockdownAllowedMmcPids.Remove(pid);
            }
            catch
            {
                _usbLockdownAllowedMmcPids.Remove(pid);
            }
        }

        Process[] mmc;
        try
        {
            mmc = Process.GetProcessesByName("mmc");
        }
        catch
        {
            return;
        }

        foreach (var proc in mmc)
        {
            string title;
            try
            {
                proc.Refresh();
                title = proc.MainWindowTitle ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (!UsbInputLockdownBeta.LooksLikeDeviceManagerWindow(title))
            {
                continue;
            }

            if (_usbLockdownAllowedMmcPids.Contains(proc.Id))
            {
                continue;
            }

            nint mmcHwnd;
            try
            {
                mmcHwnd = proc.MainWindowHandle;
            }
            catch
            {
                continue;
            }

            var disabledDeviceManagerUi = false;
            if (mmcHwnd != nint.Zero && NativeMethods.IsWindow(mmcHwnd))
            {
                _ = NativeMethods.EnableWindow(mmcHwnd, false);
                disabledDeviceManagerUi = true;
            }

            _usbLockdownPromptActive = true;
            try
            {
                using var prompt = new PasswordPromptForm(
                    "Device Manager blocked (beta)",
                    "USB lockdown is on. Enter the Windlock master password to use this Device Manager window.",
                    "Device Manager is a separate program — input there is turned off until you enter the correct password or it closes.");
                prompt.TopMost = true;
                var allowDeviceManager =
                    prompt.ShowDialog(this) == DialogResult.OK && VerifyMasterPassword(prompt.Password);
                if (!allowDeviceManager)
                {
                    UsbInputLockdownBeta.TryCloseProcess(proc);
                }
                else
                {
                    _usbLockdownAllowedMmcPids.Add(proc.Id);
                }
            }
            finally
            {
                if (disabledDeviceManagerUi && mmcHwnd != nint.Zero && NativeMethods.IsWindow(mmcHwnd))
                {
                    _ = NativeMethods.EnableWindow(mmcHwnd, true);
                }

                _usbLockdownPromptActive = false;
            }

            break;
        }
    }

    /// <summary>Enables or disables USB lockdown beta (master required). Updates registry when elevated.</summary>
    internal bool TrySetUsbLockdownBetaEnabled(bool enabled)
    {
        if (enabled == _config.UsbLockdownBetaEnabled)
        {
            return true;
        }

        var verb = enabled ? "enable" : "turn off";
        if (!PromptAndVerifyMasterPassword(
                "USB lockdown (beta)",
                $"Enter your master password to {verb} experimental USB / Device Manager lockdown.",
                this))
        {
            return false;
        }

        if (enabled)
        {
            UsbInputLockdownBeta.TryEnableUsbMassStorage(_config, out var elev, out var applied, out var detail);
            if (!string.IsNullOrEmpty(detail))
            {
                AppLogger.Log("USB lockdown beta: " + detail);
            }

            if (!applied && !elev)
            {
                MessageBox.Show(
                    this,
                    "USB mass storage was not changed because Windlock is not running as Administrator.\n\n"
                    + "Device Manager will still require the master password while this mode is on.\n\n"
                    + "To apply USB storage blocking, run Windlock as Administrator once, then enable again from Security & recovery.",
                    "USB lockdown (beta)",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else if (!applied && elev)
            {
                MessageBox.Show(
                    this,
                    "Could not change USB storage policy: " + (detail ?? "unknown"),
                    "USB lockdown (beta)",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            _config.UsbLockdownBetaEnabled = true;
            SaveConfig();
            TryStartUsbLockdownMonitor();
        }
        else
        {
            UsbInputLockdownBeta.TryDisableUsbMassStorage(_config, out var detail);
            if (!string.IsNullOrEmpty(detail))
            {
                AppLogger.Log("USB lockdown beta (disable): " + detail);
            }

            _config.UsbLockdownBetaEnabled = false;
            SaveConfig();
            TryStopUsbLockdownMonitor();
        }

        UpdateUi();
        ArrangeLeftScopeColumnAndFooter();
        return true;
    }

    internal string ActiveConfigPathForDisplay => _configPath;

    internal LockerConfig HubConfigForSecurityUi => _config;

    internal void ApplyHubIdleRelockSettings(int minutes, bool requireMasterOnOpenAfterIdleHide)
    {
        minutes = Math.Clamp(minutes, 0, 24 * 60);
        if (_config.HubIdleRelockMinutes == minutes &&
            _config.HubIdleRelockRequireMasterOnOpen == requireMasterOnOpenAfterIdleHide)
        {
            return;
        }

        _config.HubIdleRelockMinutes = minutes;
        _config.HubIdleRelockRequireMasterOnOpen = requireMasterOnOpenAfterIdleHide;
        SaveConfig();
    }

    internal void RunEncryptedBackupExportFromSecurityUi()
    {
        if (string.IsNullOrWhiteSpace(_masterPasswordForEncryptionSession))
        {
            MessageBox.Show(
                this,
                "Export needs your master password in this session. Restart Windlock, unlock with your master password, then try again.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export encrypted backup",
            Filter = "Windlock backup (*.alb)|*.alb|All files|*.*",
            DefaultExt = "alb",
            FileName = "windlock-backup.alb"
        };

        if (sfd.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SyncLockRulesFromLiveToConfig();
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(sfd.FileName, ConfigCrypto.EncryptUtf8(json, _masterPasswordForEncryptionSession));
            MessageBox.Show(this, "Backup saved.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Backup export failed.", ex);
            MessageBox.Show(this, "Export failed: " + ex.Message, "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal bool RunEncryptedBackupRestoreFromSecurityUi()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Choose encrypted backup",
            Filter = "Windlock backup (*.alb)|*.alb|All files|*.*"
        };

        if (ofd.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(ofd.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not read the file: " + ex.Message, "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        if (!ConfigCrypto.IsEncryptedPayload(bytes))
        {
            MessageBox.Show(
                this,
                "That file does not look like an Windlock encrypted backup.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (!PromptMasterPasswordOnce(
                "Restore backup",
                "Enter the master password that was used when this backup was exported.",
                this,
                out var master))
        {
            return false;
        }

        LockerConfig restored;
        try
        {
            var json = ConfigCrypto.DecryptToUtf8(bytes, master);
            restored = JsonSerializer.Deserialize<LockerConfig>(json) ?? new LockerConfig();
            restored.Normalize();
        }
        catch (CryptographicException)
        {
            MessageBox.Show(
                this,
                "Wrong master password or the file is damaged.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        catch (JsonException)
        {
            MessageBox.Show(
                this,
                "The backup could not be read as valid settings.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        if (MessageBox.Show(
                this,
                "This will replace your current hub settings (including lock rules) with this backup. "
                + "Export a backup of your current settings first if you might need it.\n\nContinue?",
                "Restore backup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return false;
        }

        try
        {
            _config = restored;
            _masterPasswordForEncryptionSession = master;
            _lockRules.Clear();
            foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
            {
                RemoveLockGroup(rootPid, reEnableWindows: true);
            }

            RefreshProcessSnapshotAndTree();
            if (_config.LockRules is { Count: > 0 })
            {
                HydrateLockRulesFromPersistedConfig();
            }
            else
            {
                SaveConfig();
            }

            ApplyProtectionFromRulesNow();
            UpdateLockedRulesList();
            UpdateUi();
            MessageBox.Show(this, "Settings were restored from the backup.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Restore backup failed.", ex);
            MessageBox.Show(this, "Restore failed: " + ex.Message, "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void ShowSecurityAndRecoveryFromHub()
    {
        TryOpenSecurityAndRecoveryDialog(isIntroMode: false);
    }

    /// <summary>Opens Security & recovery after master check. Intro uses a lighter check if the session already has the master.</summary>
    private bool TryOpenSecurityAndRecoveryDialog(bool isIntroMode)
    {
        if (isIntroMode)
        {
            if (string.IsNullOrWhiteSpace(_masterPasswordForEncryptionSession) &&
                !PromptAndVerifyMasterPassword(
                    "Setup information",
                    "Enter your master password to continue.",
                    this))
            {
                return false;
            }
        }
        else
        {
            if (!PromptAndVerifyMasterPassword(
                    "Security & recovery",
                    "Enter your master password to open this screen.",
                    this))
            {
                return false;
            }
        }

        using var f = new SecurityAndRecoveryForm(this, isIntroMode);
        f.ShowDialog(this);
        return true;
    }

    private void OpenHubFromTrayOrShortcut()
    {
        using var focusGuard = BeginFocusSensitiveUi();
        // Drop TopMost before any prompt so the master-password dialog and hub are not buried under lock screens.
        ApplyStayOnTopToAllLockDialogs(false);

        if (_hubOpenRequiresMasterVerify)
        {
            if (!PromptAndVerifyMasterPassword(
                    "Unlock Windlock Hub",
                    "Enter your master password to open the hub.",
                    null))
            {
                RefreshLockDialogsStayOnTopFromSettings();
                return;
            }

            _hubOpenRequiresMasterVerify = false;
        }

        _allowHubWindowToBeShown = true;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        RefreshLockDialogsStayOnTopFromSettings();
    }

    private FocusSensitiveUiScope BeginFocusSensitiveUi() => new(this);

    private void EnterFocusSensitiveUi() => _focusSensitiveUiDepth++;

    private void LeaveFocusSensitiveUi()
    {
        if (_focusSensitiveUiDepth > 0)
        {
            _focusSensitiveUiDepth--;
        }
    }

    private bool IsFocusSensitiveUiActive => _focusSensitiveUiDepth > 0;

    private sealed class FocusSensitiveUiScope : IDisposable
    {
        private readonly Form1 _hub;
        private bool _disposed;

        public FocusSensitiveUiScope(Form1 hub)
        {
            _hub = hub;
            _hub.EnterFocusSensitiveUi();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hub.LeaveFocusSensitiveUi();
        }
    }

    /// <summary>True only when the setting is on and the hub is not on screen (otherwise locks bury the hub).</summary>
    private bool ComputeLockDialogStayOnTop() =>
        _config.LockDialogsStayOnTop && !Visible;

    private void ApplyStayOnTopToAllLockDialogs(bool stayOnTop)
    {
        foreach (var overlay in _lockGroupDialogs.Values.ToList())
        {
            try
            {
                if (!overlay.IsDisposed)
                {
                    overlay.ApplyStayOnTop(stayOnTop);
                }
            }
            catch
            {
                // ignored
            }
        }

        foreach (var cover in _lockCovers.Values.ToList())
        {
            try
            {
                if (!cover.IsDisposed)
                {
                    cover.ApplyStayOnTop(stayOnTop);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private void RefreshLockDialogsStayOnTopFromSettings()
    {
        ApplyStayOnTopToAllLockDialogs(ComputeLockDialogStayOnTop());
    }

    internal void ApplyLockDialogsStayOnTopSetting(bool enabled)
    {
        if (_config.LockDialogsStayOnTop == enabled && _config.LockDialogsStayOnTopPromptShown)
        {
            return;
        }

        _config.LockDialogsStayOnTop = enabled;
        _config.LockDialogsStayOnTopPromptShown = true;
        SaveConfig();
        RefreshLockDialogsStayOnTopFromSettings();
    }

    /// <summary>Asks once whether lock screens may stay on top; answer is saved and can be changed in Security & recovery.</summary>
    private void EnsureStayOnTopPermissionAsked()
    {
        if (_config.LockDialogsStayOnTopPromptShown)
        {
            return;
        }

        var allow = MessageBox.Show(
            this,
            "Allow Windlock lock screens to stay on top of other windows?\n\n"
            + "This keeps unlock prompts visible so locked apps cannot cover them.\n"
            + "If you say No, lock screens behave like normal windows (the hub will not get stuck behind them).\n\n"
            + "You can change this anytime in Security, backup & recovery.",
            "Show lock screens on top",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1) == DialogResult.Yes;

        _config.LockDialogsStayOnTop = allow;
        _config.LockDialogsStayOnTopPromptShown = true;
        SaveConfig();
        RefreshLockDialogsStayOnTopFromSettings();
        AppLogger.Log($"Show-on-top permission: {(allow ? "allowed" : "denied")}.");
    }

    private void HubIdleTimerOnTick(object? sender, EventArgs e)
    {
        if (_isShuttingDown || _config.HubIdleRelockMinutes <= 0)
        {
            return;
        }

        if (!Visible || WindowState != FormWindowState.Normal)
        {
            return;
        }

        var idleSec = NativeMethods.GetLastInputIdleSeconds();
        if (idleSec < _config.HubIdleRelockMinutes * 60)
        {
            return;
        }

        if (_config.HubIdleRelockRequireMasterOnOpen)
        {
            _hubOpenRequiresMasterVerify = true;
        }

        Hide();
        AppLogger.Log("Hub hidden automatically due to idle timeout.");
    }

    /// <summary>Re-apply overlays from current rules and process snapshot (used on startup and when turning protection back on).</summary>
    private void ApplyProtectionFromRulesNow()
    {
        if (_isShuttingDown || !_config.Enabled)
        {
            return;
        }

        try
        {
            var pidRuleMap = BuildLockedPidRuleMap();
            SyncStandaloneLockOverlays(pidRuleMap);
            TryCloseTaskManagerForActiveLockPolicy();
            TryCloseBlockedSystemToolWindows();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("ApplyProtectionFromRulesNow failed.", ex);
        }
    }

    private void BuildControlUi()
    {
        Text = "Windlock Hub";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        _hubClientWidth = 960;
        var hubWidth = _hubClientWidth;
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = "Windlock",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Location = new Point(18, 14),
            AutoSize = true
        };
        Controls.Add(title);

        _statusLabel = new Label
        {
            Location = new Point(20, 52),
            MaximumSize = new Size(hubWidth - 100, 0),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(60, 60, 60)
        };
        Controls.Add(_statusLabel);

        _alertsBellButton = new Button
        {
            Text = "🔔",
            Location = new Point(hubWidth - 56, 14),
            Size = new Size(40, 34),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Emoji", 12f),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        _alertsBellButton.FlatAppearance.BorderSize = 0;
        _alertsBellButton.Click += (_, _) => ToggleAlertsPopup();
        var alertsTip = new ToolTip { InitialDelay = 200, AutoPopDelay = 8000 };
        alertsTip.SetToolTip(_alertsBellButton, "Security alerts: locked apps that were ended (e.g. Task Manager)");
        Controls.Add(_alertsBellButton);

        _alertsPopupPanel = new Panel
        {
            Visible = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Size = new Size(420, 260),
            Location = new Point(hubWidth - 440, 52),
            AutoScroll = true
        };
        _tamperAlertsLabel = new Label
        {
            Text = "Security alerts",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(12, 10),
            AutoSize = true
        };
        _alertsPopupPanel.Controls.Add(_tamperAlertsLabel);

        _tamperAlertsList = new ListBox
        {
            Location = new Point(12, 34),
            Size = new Size(394, 72),
            IntegralHeight = false,
            HorizontalScrollbar = true
        };
        _tamperAlertsList.SelectedIndexChanged += (_, _) => UpdateAlertsDetailPanel();
        _alertsPopupPanel.Controls.Add(_tamperAlertsList);

        _alertsDetailLabel = new Label
        {
            Location = new Point(12, 114),
            MaximumSize = new Size(394, 0),
            AutoSize = true,
            UseMnemonic = false,
            ForeColor = Color.FromArgb(45, 45, 45),
            Font = new Font("Segoe UI", 9f)
        };
        _alertsPopupPanel.Controls.Add(_alertsDetailLabel);

        _dismissTamperAlertButton = new Button
        {
            Text = "Dismiss (master password)",
            Location = new Point(12, 220),
            Size = new Size(394, 30)
        };
        _dismissTamperAlertButton.Click += (_, _) => DismissTamperAlertWithMasterPassword();
        _alertsPopupPanel.Controls.Add(_dismissTamperAlertButton);
        Controls.Add(_alertsPopupPanel);
        _alertsPopupPanel.BringToFront();

        MouseDown += (_, e) =>
        {
            if (_alertsPopupPanel is { Visible: true } &&
                !_alertsPopupPanel.Bounds.Contains(e.Location) &&
                _alertsBellButton is not null &&
                !_alertsBellButton.Bounds.Contains(e.Location))
            {
                _alertsPopupPanel.Visible = false;
            }
        };

        var searchLabel = new Label
        {
            Text = "Search apps",
            Location = new Point(20, 84),
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80)
        };
        Controls.Add(searchLabel);

        _searchProcessBox = new TextBox
        {
            Location = new Point(20, searchLabel.Bottom + 6),
            Size = new Size(500, 24),
            PlaceholderText = "Name or PID"
        };
        _searchProcessBox.TextChanged += (_, _) => PopulateProcessTree();
        Controls.Add(_searchProcessBox);

        var refreshButton = new Button
        {
            Text = "↻",
            Location = new Point(_searchProcessBox.Right + 8, _searchProcessBox.Top - 1),
            Size = new Size(34, 28),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            FlatStyle = FlatStyle.System
        };
        refreshButton.Click += (_, _) => RefreshProcessSnapshotAndTree();
        Controls.Add(refreshButton);

        var processTreeTop = _searchProcessBox.Bottom + 10;
        _processTree = new TreeView
        {
            Location = new Point(20, processTreeTop),
            Size = new Size(540, 300),
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle
        };
        _processTree.AfterSelect += (_, _) =>
        {
            UpdateCriticalSelectionWarning();
            ArrangeLeftScopeColumnAndFooter();
        };
        Controls.Add(_processTree);

        _criticalSelectionWarningLabel = new Label
        {
            Visible = false,
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.Firebrick,
            UseMnemonic = false
        };
        Controls.Add(_criticalSelectionWarningLabel);

        _lockScopeLabel = new Label
        {
            Text = "Scope",
            AutoSize = true,
            UseMnemonic = false
        };
        Controls.Add(_lockScopeLabel);

        _lockScopeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Size = new Size(420, 26)
        };
        _lockScopeCombo.Items.AddRange(
        [
            "Single process only",
            "Same app name (all copies)",
            "App + helpers (recommended)"
        ]);
        _lockScopeCombo.SelectedIndex = RecommendedLockScopeIndex;
        _lockScopeCombo.SelectedIndexChanged += LockScopeComboOnSelectedIndexChanged;
        Controls.Add(_lockScopeCombo);

        _lockScopeHelpLabel = new Label
        {
            MaximumSize = new Size(540, 0),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray,
            UseMnemonic = false
        };
        Controls.Add(_lockScopeHelpLabel);

        const int lockColLeft = 580;
        const int lockBtnW = 178;
        const int lockColGap = 10;
        var lockColRight = lockColLeft + lockBtnW + lockColGap;
        var lockRowTop = processTreeTop;
        const int lockRowGap = 40;

        // Beside the process list: selected actions. Far right: all actions.
        var lockSelectedButton = new Button
        {
            Text = "🔒  Lock selected",
            Location = new Point(lockColLeft, lockRowTop),
            Size = new Size(lockBtnW, 34),
            FlatStyle = FlatStyle.System
        };
        lockSelectedButton.Click += (_, _) => LockSelectedProcess();
        Controls.Add(lockSelectedButton);

        var lockAllButton = new Button
        {
            Text = "🔒  Lock all",
            Location = new Point(lockColRight, lockRowTop),
            Size = new Size(lockBtnW, 34),
            FlatStyle = FlatStyle.System
        };
        lockAllButton.Click += (_, _) => LockAllVisibleProcesses();
        Controls.Add(lockAllButton);

        var unlockSelectedButton = new Button
        {
            Text = "🔓  Unlock selected",
            Location = new Point(lockColLeft, lockRowTop + lockRowGap),
            Size = new Size(lockBtnW, 34),
            FlatStyle = FlatStyle.System
        };
        unlockSelectedButton.Click += (_, _) => UnlockSelectedRule();
        Controls.Add(unlockSelectedButton);

        var unlockAllButton = new Button
        {
            Text = "🔓  Unlock all",
            Location = new Point(lockColRight, lockRowTop + lockRowGap),
            Size = new Size(lockBtnW, 34),
            FlatStyle = FlatStyle.System
        };
        unlockAllButton.Click += (_, _) => UnlockAllRules();
        Controls.Add(unlockAllButton);

        var lockedSectionTop = unlockSelectedButton.Bottom + 16;
        var lockedLabel = new Label
        {
            Text = "🔐  Active locks",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(lockColLeft, lockedSectionTop),
            AutoSize = true
        };
        Controls.Add(lockedLabel);

        var sessionTip = new Label
        {
            Text = "⚠  Lock apps that can browse files (editors, browsers). Explorer block alone is not enough.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.Firebrick,
            Location = new Point(lockColLeft, lockedLabel.Bottom + 4),
            MaximumSize = new Size(hubWidth - lockColLeft - 20, 0),
            AutoSize = true,
            UseMnemonic = false
        };
        Controls.Add(sessionTip);

        var lockedListTop = sessionTip.Bottom + 6;
        var lockedListHeight = 178;
        _lockedRulesList = new ListBox
        {
            Location = new Point(lockColLeft, lockedListTop),
            Size = new Size(hubWidth - lockColLeft - 20, lockedListHeight),
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_lockedRulesList);

        _toggleButton = new Button
        {
            Text = "⏸  Turn off",
            Location = new Point(lockColLeft, 400),
            Size = new Size(120, 34),
            FlatStyle = FlatStyle.System
        };
        _toggleButton.Click += (_, _) => SetProtection(!_config.Enabled);
        Controls.Add(_toggleButton);

        _changeMasterButton = new Button
        {
            Text = "Change Master Password",
            Location = new Point(_toggleButton.Right + 10, 400),
            Size = new Size(196, 34),
            FlatStyle = FlatStyle.System
        };
        _changeMasterButton.Click += (_, _) => ChangeMasterPassword();
        Controls.Add(_changeMasterButton);

        _hideToTrayButton = new Button
        {
            Text = "Hide to Tray",
            Location = new Point(lockColLeft, 444),
            Size = new Size(lockBtnW * 2 + lockColGap, 32),
            FlatStyle = FlatStyle.System
        };
        _hideToTrayButton.Click += (_, _) => Hide();
        Controls.Add(_hideToTrayButton);

        _securityRecoveryLink = new LinkLabel
        {
            Text = "🛡  Security & recovery",
            AutoSize = true,
            UseMnemonic = false
        };
        _securityRecoveryLink.Click += (_, _) => ShowSecurityAndRecoveryFromHub();
        Controls.Add(_securityRecoveryLink);

        _uninstallAppLink = new LinkLabel
        {
            Text = "🗑  Uninstall",
            AutoSize = true,
            UseMnemonic = false,
            LinkColor = Color.Firebrick,
            ActiveLinkColor = Color.DarkRed
        };
        _uninstallAppLink.Click += (_, _) => TryUninstallAppLockerFromHub();
        Controls.Add(_uninstallAppLink);

        _usbLockdownOffLink = new LinkLabel
        {
            Text = "USB lockdown off…",
            AutoSize = true,
            UseMnemonic = false,
            Visible = _config.UsbLockdownBetaEnabled
        };
        _usbLockdownOffLink.Click += (_, _) => TrySetUsbLockdownBetaEnabled(false);
        Controls.Add(_usbLockdownOffLink);

        LockScopeComboOnSelectedIndexChanged(null, EventArgs.Empty);
        UpdateCriticalSelectionWarning();
        RefreshTamperAlertsList();
        ArrangeLeftScopeColumnAndFooter();
    }

    private void ToggleAlertsPopup()
    {
        if (_alertsPopupPanel is null || _alertsBellButton is null)
        {
            return;
        }

        if (!_alertsPopupPanel.Visible)
        {
            _alertsPopupPanel.Location = new Point(
                Math.Max(8, _alertsBellButton.Right - _alertsPopupPanel.Width),
                _alertsBellButton.Bottom + 4);
            _alertsPopupPanel.Visible = true;
            _alertsPopupPanel.BringToFront();
            RefreshTamperAlertsList();
        }
        else
        {
            _alertsPopupPanel.Visible = false;
        }
    }

    private void ArrangeLeftScopeColumnAndFooter()
    {
        if (_processTree is null || _lockScopeLabel is null || _lockScopeCombo is null || _lockScopeHelpLabel is null)
        {
            return;
        }

        const int left = 20;
        var y = _processTree.Bottom + 8;
        if (_criticalSelectionWarningLabel is { Visible: true })
        {
            _criticalSelectionWarningLabel.Location = new Point(left, y);
            y = _criticalSelectionWarningLabel.Bottom + 8;
        }
        else if (_criticalSelectionWarningLabel is not null)
        {
            _criticalSelectionWarningLabel.Location = new Point(left, y);
        }

        _lockScopeLabel.Location = new Point(left, y + 2);
        _lockScopeCombo.Location = new Point(left + 56, y - 2);
        _lockScopeHelpLabel.Location = new Point(left, _lockScopeCombo.Bottom + 8);
        if (_lockedRulesList is not null)
        {
            ReflowFooterFromScopeLayout();
        }
    }

    private void ReflowFooterFromScopeLayout()
    {
        if (_lockScopeHelpLabel is null || _lockedRulesList is null || _toggleButton is null || _changeMasterButton is null || _hideToTrayButton is null)
        {
            return;
        }

        const int lockColLeft = 580;
        var footerTop = Math.Max(_lockScopeHelpLabel.Bottom, _lockedRulesList.Bottom) + 18;
        _toggleButton.Location = new Point(lockColLeft, footerTop);
        _changeMasterButton.Location = new Point(_toggleButton.Right + 10, footerTop);
        _hideToTrayButton.Location = new Point(lockColLeft, footerTop + 44);
        if (_securityRecoveryLink is not null)
        {
            _securityRecoveryLink.Location = new Point(20, footerTop + 6);
        }

        if (_uninstallAppLink is not null)
        {
            var uninstallY = _securityRecoveryLink is not null ? _securityRecoveryLink.Bottom + 6 : footerTop + 6;
            _uninstallAppLink.Location = new Point(20, uninstallY);
        }

        var leftColBottom = _uninstallAppLink?.Bottom
            ?? _securityRecoveryLink?.Bottom
            ?? footerTop + 6;
        if (_usbLockdownOffLink is not null)
        {
            _usbLockdownOffLink.Location = new Point(20, leftColBottom + 6);
            if (_usbLockdownOffLink.Visible)
            {
                leftColBottom = _usbLockdownOffLink.Bottom;
            }
        }

        var footerBottom = Math.Max(_hideToTrayButton.Bottom, leftColBottom) + 16;
        ClientSize = new Size(_hubClientWidth, Math.Max(640, footerBottom));
    }

    private void UpdateCriticalSelectionWarning()
    {
        if (_criticalSelectionWarningLabel is null)
        {
            return;
        }

        if (_processTree?.SelectedNode?.Tag is ProcessSnapshotItem p && IsCriticalSystemProcess(p))
        {
            _criticalSelectionWarningLabel.Text =
                $"⚠  {p.Name} is a critical Windows process — locking it can freeze the PC. Skip it.";
            _criticalSelectionWarningLabel.Visible = true;
        }
        else
        {
            _criticalSelectionWarningLabel.Visible = false;
            _criticalSelectionWarningLabel.Text = string.Empty;
        }
    }

    private static string NormalizedExeBaseName(string name)
    {
        var s = name.Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return s[..^4];
        }

        return s;
    }

    private static readonly HashSet<string> CriticalSystemProcessBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "winlogon",
        "fontdrvhost",
        "dwm",
        "explorer",
        "Memory Compression",
        "Secure System",
        "sihost"
    };

    private static bool IsCriticalSystemProcess(ProcessSnapshotItem p)
    {
        if (p.ProcessId == 4)
        {
            return true;
        }

        var n = NormalizedExeBaseName(p.Name);
        return CriticalSystemProcessBaseNames.Contains(n);
    }

    private void BuildTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Visible = true,
            Text = "Windlock Hub"
        };

        var menu = new ContextMenuStrip();
        _trayStatusItem = new ToolStripMenuItem { Enabled = false };
        _trayToggleItem = new ToolStripMenuItem();
        _trayToggleItem.Click += (_, _) => SetProtection(!_config.Enabled);

        var showControl = new ToolStripMenuItem("Open Hub");
        showControl.Click += (_, _) => OpenHubFromTrayOrShortcut();

        var securityItem = new ToolStripMenuItem("Security & recovery…");
        securityItem.Click += (_, _) => TryOpenSecurityAndRecoveryDialog(isIntroMode: false);

        var backupItem = new ToolStripMenuItem("Export encrypted backup…");
        backupItem.Click += (_, _) => RunEncryptedBackupExportFromSecurityUi();

        var restoreItem = new ToolStripMenuItem("Restore from backup…");
        restoreItem.Click += (_, _) => RunEncryptedBackupRestoreFromSecurityUi();

        _trayUsbLockdownOffItem = new ToolStripMenuItem("Turn off USB lockdown (beta)…");
        _trayUsbLockdownOffItem.Visible = _config.UsbLockdownBetaEnabled;
        _trayUsbLockdownOffItem.Click += (_, _) => TrySetUsbLockdownBetaEnabled(false);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => StartShutdown(requireMaster: true);

        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(showControl);
        menu.Items.Add(securityItem);
        menu.Items.Add(backupItem);
        menu.Items.Add(restoreItem);
        menu.Items.Add(_trayUsbLockdownOffItem);
        menu.Items.Add("-");
        menu.Items.Add(exit);

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => OpenHubFromTrayOrShortcut();
    }

    private bool LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            _config = new LockerConfig();
            return true;
        }

        try
        {
            var bytes = File.ReadAllBytes(_configPath);
            if (ConfigCrypto.IsEncryptedPayload(bytes))
            {
                if (string.IsNullOrWhiteSpace(_masterPasswordForEncryptionSession))
                {
                    AppLogger.Log("Encrypted settings require a master password in session.");
                    return false;
                }

                string json;
                try
                {
                    json = ConfigCrypto.DecryptToUtf8(bytes, _masterPasswordForEncryptionSession);
                }
                catch (CryptographicException ex)
                {
                    AppLogger.LogException("Config decrypt failed.", ex);
                    return false;
                }

                _config = JsonSerializer.Deserialize<LockerConfig>(json) ?? new LockerConfig();
            }
            else
            {
                var text = Encoding.UTF8.GetString(bytes);
                _config = JsonSerializer.Deserialize<LockerConfig>(text) ?? new LockerConfig();
            }

            _config.Normalize();
            return true;
        }
        catch (JsonException ex)
        {
            AppLogger.LogException("Config JSON invalid.", ex);
            MessageBox.Show(
                null,
                $"Windlock could not read your settings file:\n{_configPath}\n\nYou can delete this file and restart to run setup again.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Config load failed.", ex);
            MessageBox.Show(
                null,
                $"Windlock could not read your settings file:\n{_configPath}\n\nYou can delete this file and restart to run setup again.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private void SyncLockRulesFromLiveToConfig()
    {
        _config.LockRules = _lockRules.Values
            .Select(r => new PersistedLockRule
            {
                RootPid = r.RootPid,
                DisplayName = r.DisplayName,
                ProcessName = r.ProcessName,
                Scope = r.Scope,
                PasswordHash = r.PasswordHash
            })
            .ToList();
    }

    private void SaveConfig()
    {
        SyncLockRulesFromLiveToConfig();

        // In enforcement-only mode the settings file is encrypted with a password we do not have; writing it now would
        // either replace it with plaintext or with the partial state rebuilt from the manifest.
        if (_isEnforcementOnlyMode)
        {
            EnforcementManifestStore.TrySave(_configPath, _config);
            return;
        }

        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            if (!string.IsNullOrWhiteSpace(_masterPasswordForEncryptionSession))
            {
                var payload = ConfigCrypto.EncryptUtf8(json, _masterPasswordForEncryptionSession);
                File.WriteAllBytes(_configPath, payload);
            }
            else
            {
                File.WriteAllText(_configPath, json);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Config save failed.", ex);
            MessageBox.Show(this, "Could not save settings: " + ex.Message, "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        EnforcementManifestStore.TrySave(_configPath, _config);
    }

    private bool UnlockEncryptedStorageAtStartup()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!PromptMasterPasswordOnce(
                    "Unlock Windlock Hub",
                    "Enter master password to open Windlock",
                    null,
                    out var password))
            {
                AppLogger.Log("Startup master password cancelled.");
                return false;
            }

            _masterPasswordForEncryptionSession = password;
            if (LoadConfig())
            {
                return true;
            }

            MessageBox.Show(
                this,
                "Wrong master password or the settings file could not be decrypted.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _masterPasswordForEncryptionSession = null;
        }

        AppLogger.Log("Startup master password exhausted attempts.");
        return false;
    }

    private void HydrateLockRulesFromPersistedConfig()
    {
        _config.LockRules ??= new List<PersistedLockRule>();
        if (_config.LockRules.Count == 0)
        {
            return;
        }

        var persistedEntryCount = _config.LockRules.Count;
        _lockRules.Clear();
        foreach (var pr in _config.LockRules)
        {
            if (string.IsNullOrWhiteSpace(pr.PasswordHash))
            {
                continue;
            }

            if (!TryResolvePersistedLockRule(pr, out var rule))
            {
                AppLogger.Log(
                    $"Persisted lock skipped (no matching live process): display='{pr.DisplayName}' rootPid={pr.RootPid} process='{pr.ProcessName}' scope={pr.Scope}.");
                continue;
            }

            _lockRules[rule.RootPid] = rule;
        }

        if (_config.BlockSystemToolsWhileProtected)
        {
            RemoveSystemToolProcessesFromUserLockRules();
        }

        SaveConfig();
        if (persistedEntryCount > 0)
        {
            AppLogger.Log($"Hydrate finished: {_lockRules.Count} active rule(s) from {persistedEntryCount} persisted entr(y/ies).");
        }
    }

    private bool TryResolvePersistedLockRule(PersistedLockRule pr, out LockRule rule)
    {
        rule = null!;
        if (_processSnapshot.TryGetValue(pr.RootPid, out var liveAtRoot))
        {
            if (!ProcessNamesLikelySame(liveAtRoot.Name, pr.ProcessName))
            {
                return TryResolvePersistedLockByProcessName(pr, out rule);
            }

            rule = new LockRule(
                pr.RootPid,
                EffectiveDisplayName(pr.DisplayName, liveAtRoot.Name),
                NormalizedExeBaseName(pr.ProcessName),
                pr.Scope,
                pr.PasswordHash);
            return true;
        }

        // PID is gone (Task Manager End task, crash, etc.). Keep the lock armed by name so a reopen stays locked.
        if (pr.Scope == LockScope.SinglePidOnly)
        {
            if (string.IsNullOrWhiteSpace(pr.ProcessName) && string.IsNullOrWhiteSpace(pr.DisplayName))
            {
                return false;
            }

            var sticky = new PersistedLockRule
            {
                RootPid = pr.RootPid,
                DisplayName = pr.DisplayName,
                ProcessName = string.IsNullOrWhiteSpace(pr.ProcessName) ? pr.DisplayName : pr.ProcessName,
                Scope = LockScope.ProcessNameAllInstances,
                PasswordHash = pr.PasswordHash
            };
            return TryResolvePersistedLockByProcessName(sticky, out rule);
        }

        return TryResolvePersistedLockByProcessName(pr, out rule);
    }

    private bool TryResolvePersistedLockByProcessName(PersistedLockRule pr, out LockRule rule)
    {
        rule = null!;
        var processKey = NormalizedExeBaseName(
            string.IsNullOrWhiteSpace(pr.ProcessName) ? pr.DisplayName : pr.ProcessName);
        var live = _processSnapshot.Values
            .Where(x => ProcessNamesLikelySame(x.Name, processKey))
            .OrderBy(x => x.ProcessId)
            .FirstOrDefault();

        if (live is null)
        {
            // Keep name-based rules armed while the app happens to be closed. Dropping them here used to delete the rule
            // on the next save, so closing an app was enough to make it permanently unlocked after a hub restart.
            if (string.IsNullOrWhiteSpace(processKey))
            {
                rule = null!;
                return false;
            }

            rule = new LockRule(
                pr.RootPid,
                EffectiveDisplayName(pr.DisplayName, processKey),
                processKey,
                pr.Scope,
                pr.PasswordHash);
            return true;
        }

        rule = new LockRule(
            live.ProcessId,
            EffectiveDisplayName(pr.DisplayName, live.Name),
            processKey,
            pr.Scope,
            pr.PasswordHash);
        return true;
    }

    private static string EffectiveDisplayName(string savedDisplay, string liveProcessName) =>
        string.IsNullOrWhiteSpace(savedDisplay) ? liveProcessName : savedDisplay;

    /// <summary>Treats brave / brave.exe as the same app name.</summary>
    private static bool ProcessNamesLikelySame(string snapshotName, string ruleProcessName) =>
        string.Equals(
            NormalizedExeBaseName(snapshotName),
            NormalizedExeBaseName(ruleProcessName),
            StringComparison.OrdinalIgnoreCase);

    private void WatcherTimerOnTick(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        EnsureWatchdogHelperRunning();

        if (!_config.Enabled)
        {
            return;
        }

        try
        {
            RefreshProcessSnapshotForWatcher();
            MaintainStickyLockRules();
            var pidRuleMap = BuildLockedPidRuleMap();
            DetectLockedSessionEndings(pidRuleMap);
            SyncStandaloneLockOverlays(pidRuleMap);
            TryCloseTaskManagerForActiveLockPolicy();
            TryCloseBlockedSystemToolWindows();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Watcher tick exception", ex);
        }
    }

    private void EnsureLockedSessionTrackerLoaded()
    {
        if (_lockedSessionTrackerLoaded)
        {
            return;
        }

        LockedSessionTracker.Load();
        _lockedSessionTrackerLoaded = true;
    }

    private static string LockRuleProcessKey(LockRule rule) =>
        NormalizedExeBaseName(
            string.IsNullOrWhiteSpace(rule.ProcessName) ? rule.DisplayName : rule.ProcessName);

    /// <summary>
    /// When a locked app is ended (e.g. Task Manager), open the hub and tell the user to unlock from there —
    /// no password unlock dialog for that session. State is persisted next to the randomized helper name file.
    /// </summary>
    private void DetectLockedSessionEndings(Dictionary<int, LockRule> pidRuleMap)
    {
        EnsureLockedSessionTrackerLoaded();
        if (_lockRules.Count == 0)
        {
            return;
        }

        var lockablePids = GetProcessesWithLockableTopLevelWindows()
            .Select(p => p.ProcessId)
            .ToHashSet();
        var dirty = false;

        foreach (var rule in _lockRules.Values.ToList())
        {
            var key = LockRuleProcessKey(rule);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var matchingPids = _processSnapshot.Values
                .Where(p => ProcessNamesLikelySame(p.Name, key))
                .Select(p => p.ProcessId)
                .ToHashSet();
            var hasAnyProcess = matchingPids.Count > 0;
            var hasLockableWindow = matchingPids.Any(lockablePids.Contains);
            var hasMappedLivePid = pidRuleMap.Keys.Any(matchingPids.Contains);

            var hasLiveOverlayHost = false;
            foreach (var (rootPid, dlg) in _lockGroupDialogs)
            {
                if (dlg.IsDisposed)
                {
                    continue;
                }

                var related =
                    rootPid == rule.RootPid ||
                    dlg.GetHostProcessNames().Any(n => ProcessNamesLikelySame(n, key));
                if (!related)
                {
                    continue;
                }

                foreach (var hwnd in dlg.GetHostHwnds())
                {
                    if (!NativeMethods.IsWindow(hwnd))
                    {
                        continue;
                    }

                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pidU);
                    if (pidU != 0 && _processSnapshot.ContainsKey((int)pidU))
                    {
                        hasLiveOverlayHost = true;
                        break;
                    }
                }

                if (hasLiveOverlayHost)
                {
                    break;
                }
            }

            // Live = real window or a still-valid overlay host. Headless helper PIDs alone must NOT keep password UI.
            _ = hasMappedLivePid;
            var sessionLive = hasLockableWindow || hasLiveOverlayHost;
            if (sessionLive)
            {
                if (LockedSessionTracker.NoteLiveSession(key, rule.RootPid))
                {
                    dirty = true;
                }

                continue;
            }

            // No live UI session. If we previously saw one, this is an End-task / full close.
            if (LockedSessionTracker.TryMarkSessionEnded(key, out var restartCount, out var generation))
            {
                dirty = true;
                AppLogger.Log(
                    $"Locked session ended for '{key}' (restartCount={restartCount}, generation={generation}, anyProcessLeft={hasAnyProcess}).");
                QueueLockedSessionEndedNotice(
                    string.IsNullOrWhiteSpace(rule.DisplayName) ? key : rule.DisplayName,
                    key,
                    restartCount);
                continue;
            }

            // Already hub-only: no floating dialog — unlock from hub; alerts live in the hub list.
            if (LockedSessionTracker.IsHubOnly(key))
            {
                CloseLockOverlaysForProcessKey(key);
            }
        }

        if (dirty)
        {
            LockedSessionTracker.Save();
        }
    }

    private void CloseLockOverlaysForProcessKey(string processKey)
    {
        foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
        {
            var matches = false;
            if (_lockRules.TryGetValue(rootPid, out var rule) &&
                ProcessNamesLikelySame(rule.ProcessName, processKey))
            {
                matches = true;
            }
            else if (_lockGroupDialogs.TryGetValue(rootPid, out var dlg) &&
                     !dlg.IsDisposed &&
                     (dlg.IsHubOnlyWarningMode ||
                      dlg.GetHostProcessNames().Any(n => ProcessNamesLikelySame(n, processKey))))
            {
                matches = true;
            }

            if (matches)
            {
                RemoveLockGroup(rootPid, reEnableWindows: false);
            }
        }
    }

    private void QueueLockedSessionEndedNotice(string displayName, string processKey, int restartCount)
    {
        CloseLockOverlaysForProcessKey(processKey);

        var label = string.IsNullOrWhiteSpace(displayName) ? processKey : displayName.Trim();
        if (label.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            label = label[..^4];
        }

        if (restartCount > 1)
        {
            label = $"{label} (session #{restartCount})";
        }

        if (!_pendingSessionEndedNotices.Exists(n =>
                string.Equals(n, label, StringComparison.OrdinalIgnoreCase)))
        {
            _pendingSessionEndedNotices.Add(label);
        }

        if (_sessionEndedNoticeScheduled || _isShuttingDown)
        {
            return;
        }

        _sessionEndedNoticeScheduled = true;
        try
        {
            BeginInvoke(new Action(FlushLockedSessionEndedNotices));
        }
        catch
        {
            _sessionEndedNoticeScheduled = false;
            FlushLockedSessionEndedNotices();
        }
    }

    private void FlushLockedSessionEndedNotices()
    {
        _sessionEndedNoticeScheduled = false;
        if (_isShuttingDown || _pendingSessionEndedNotices.Count == 0)
        {
            _pendingSessionEndedNotices.Clear();
            return;
        }

        var apps = _pendingSessionEndedNotices.ToList();
        _pendingSessionEndedNotices.Clear();

        // Close any leftover “was closed” floating dialogs (they were recreating every tick before).
        foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
        {
            if (_lockGroupDialogs.TryGetValue(rootPid, out var dlg) &&
                !dlg.IsDisposed &&
                dlg.IsHubOnlyWarningMode)
            {
                RemoveLockGroup(rootPid, reEnableWindows: false);
            }
        }

        _tamperAlerts.Insert(0, new TamperAlertItem(DateTime.Now, apps));
        while (_tamperAlerts.Count > 40)
        {
            _tamperAlerts.RemoveAt(_tamperAlerts.Count - 1);
        }

        OpenHubAfterLockedSessionEnded();
        RefreshTamperAlertsList();
        if (_alertsPopupPanel is not null && _alertsBellButton is not null)
        {
            _alertsPopupPanel.Location = new Point(
                Math.Max(8, _alertsBellButton.Right - _alertsPopupPanel.Width),
                _alertsBellButton.Bottom + 4);
            _alertsPopupPanel.Visible = true;
            _alertsPopupPanel.BringToFront();
        }

        UpdateLockedRulesList();
        UpdateUi();

        try
        {
            _trayIcon?.ShowBalloonTip(
                6000,
                "Windlock",
                $"Locked app ended (likely Task Manager): {string.Join(", ", apps)}",
                ToolTipIcon.Warning);
        }
        catch
        {
            // ignored
        }

        AppLogger.Log($"Security alert: locked app(s) ended — {string.Join(", ", apps)}");
    }

    private void RefreshTamperAlertsList()
    {
        if (_tamperAlertsList is not null)
        {
            var keep = _tamperAlertsList.SelectedItem as TamperAlertItem;
            _tamperAlertsList.BeginUpdate();
            _tamperAlertsList.Items.Clear();
            foreach (var alert in _tamperAlerts)
            {
                _tamperAlertsList.Items.Add(alert);
            }

            _tamperAlertsList.EndUpdate();
            if (_tamperAlerts.Count > 0)
            {
                var idx = keep is null ? 0 : _tamperAlerts.IndexOf(keep);
                _tamperAlertsList.SelectedIndex = idx >= 0 ? idx : 0;
            }
        }

        if (_tamperAlertsLabel is not null)
        {
            _tamperAlertsLabel.Text = _tamperAlerts.Count == 0
                ? "No new alerts"
                : $"Alerts ({_tamperAlerts.Count})";
            _tamperAlertsLabel.ForeColor = _tamperAlerts.Count == 0
                ? Color.Gray
                : Color.FromArgb(180, 90, 0);
        }

        if (_alertsBellButton is not null)
        {
            _alertsBellButton.Text = _tamperAlerts.Count == 0 ? "🔔" : $"🔔{_tamperAlerts.Count}";
            _alertsBellButton.ForeColor = _tamperAlerts.Count == 0
                ? Color.FromArgb(70, 70, 70)
                : Color.FromArgb(200, 90, 0);
            _alertsBellButton.Font = new Font(
                "Segoe UI Emoji",
                _tamperAlerts.Count == 0 ? 12f : 10f,
                _tamperAlerts.Count == 0 ? FontStyle.Regular : FontStyle.Bold);
        }

        if (_dismissTamperAlertButton is not null)
        {
            _dismissTamperAlertButton.Enabled = _tamperAlerts.Count > 0;
        }

        UpdateAlertsDetailPanel();
    }

    private void UpdateAlertsDetailPanel()
    {
        if (_alertsDetailLabel is null || _alertsPopupPanel is null || _dismissTamperAlertButton is null)
        {
            return;
        }

        if (_tamperAlertsList?.SelectedItem is TamperAlertItem alert)
        {
            _alertsDetailLabel.Text = alert.DetailText;
        }
        else if (_tamperAlerts.Count == 0)
        {
            _alertsDetailLabel.Text = "No alerts right now.";
        }
        else
        {
            _alertsDetailLabel.Text = "Select an alert to read the full message.";
        }

        // Expand the popup so the full message fits; scroll if it would run off-screen.
        const int pad = 12;
        const int contentW = 394;
        _alertsDetailLabel.MaximumSize = new Size(contentW, 0);
        _alertsDetailLabel.Location = new Point(pad, (_tamperAlertsList?.Bottom ?? 106) + 8);

        _dismissTamperAlertButton.Location = new Point(pad, _alertsDetailLabel.Bottom + 10);
        _dismissTamperAlertButton.Size = new Size(contentW, 30);

        var neededH = _dismissTamperAlertButton.Bottom + pad;
        var maxH = Math.Max(240, ClientSize.Height - (_alertsBellButton?.Bottom ?? 48) - 24);
        _alertsPopupPanel.Size = new Size(pad * 2 + contentW, Math.Min(neededH, maxH));
        if (_alertsBellButton is not null)
        {
            _alertsPopupPanel.Location = new Point(
                Math.Max(8, _alertsBellButton.Right - _alertsPopupPanel.Width),
                _alertsBellButton.Bottom + 4);
        }
    }

    private void DismissTamperAlertWithMasterPassword()
    {
        if (_tamperAlerts.Count == 0)
        {
            MessageBox.Show(this, "There are no security alerts to dismiss.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!PromptAndVerifyMasterPassword(
                "Dismiss security alert",
                "Enter the master password to dismiss this alert.\n\n" +
                "This confirms an admin saw that a locked app was ended (for example from Task Manager).",
                this))
        {
            return;
        }

        if (_tamperAlertsList?.SelectedItem is TamperAlertItem selected)
        {
            _tamperAlerts.Remove(selected);
        }
        else
        {
            _tamperAlerts.RemoveAt(0);
        }

        RefreshTamperAlertsList();
        if (_tamperAlerts.Count == 0 && _alertsPopupPanel is not null)
        {
            _alertsPopupPanel.Visible = false;
        }

        AppLogger.Log("Security alert dismissed with master password.");
    }

    /// <summary>Opens the hub without a master-password prompt after a locked app session was ended.</summary>
    private void OpenHubAfterLockedSessionEnded()
    {
        ApplyStayOnTopToAllLockDialogs(false);
        _allowHubWindowToBeShown = true;
        // This path is intentional: the user already had an active hub session; avoid stacking unlock prompts.
        _hubOpenRequiresMasterVerify = false;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        RefreshLockDialogsStayOnTopFromSettings();
        AppLogger.Log("Hub opened after a locked app session ended (unlock from hub only).");
    }

    private bool IsHubOnlyUnlockRule(LockRule rule)
    {
        EnsureLockedSessionTrackerLoaded();
        var key = LockRuleProcessKey(rule);
        return !string.IsNullOrWhiteSpace(key) && LockedSessionTracker.IsHubOnly(key);
    }

    private void ClearHubOnlyUnlockForRule(LockRule rule)
    {
        EnsureLockedSessionTrackerLoaded();
        var key = LockRuleProcessKey(rule);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        LockedSessionTracker.ClearHubOnly(key);
        LockedSessionTracker.Save();
    }

    /// <summary>
    /// When a locked app is killed (e.g. from Task Manager), keep the lock armed by name and retarget new instances.
    /// Single-PID rules are upgraded automatically so reopening the app does not bypass the lock.
    /// </summary>
    private void MaintainStickyLockRules()
    {
        var changed = false;
        HashSet<int>? lockablePids = null;
        foreach (var old in _lockRules.Values.ToList())
        {
            var processName = string.IsNullOrWhiteSpace(old.ProcessName) ? old.DisplayName : old.ProcessName;
            if (string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            var rootAlive = _processSnapshot.ContainsKey(old.RootPid);
            // Only retarget when the old PID is gone, and only onto a process that still has a real window.
            // Headless browser helpers (same .exe name) used to steal the root PID every tick → SaveConfig/UpdateUi
            // loop that closed the tray menu while the user was clicking it.
            ProcessSnapshotItem? liveMatch = null;
            if (!rootAlive)
            {
                lockablePids ??= GetProcessesWithLockableTopLevelWindows()
                    .Select(p => p.ProcessId)
                    .ToHashSet();
                liveMatch = _processSnapshot.Values
                    .Where(x =>
                        ProcessNamesLikelySame(x.Name, processName) &&
                        lockablePids.Contains(x.ProcessId))
                    .OrderBy(x => x.ProcessId)
                    .FirstOrDefault();
            }

            var scope = old.Scope;
            if (!rootAlive && scope == LockScope.SinglePidOnly)
            {
                // PID is gone — stick to the app name so a new browser/session is locked again.
                scope = LockScope.ProcessNameAllInstances;
                changed = true;
                AppLogger.Log(
                    $"Lock rule for '{processName}' upgraded from Single PID to all instances after the process exited.");
            }

            if (!rootAlive && liveMatch is not null)
            {
                _lockRules.Remove(old.RootPid);
                _lockRules[liveMatch.ProcessId] = new LockRule(
                    liveMatch.ProcessId,
                    EffectiveDisplayName(old.DisplayName, liveMatch.Name),
                    NormalizedExeBaseName(processName),
                    scope,
                    old.PasswordHash);
                RekeyLockGroupDialog(old.RootPid, liveMatch.ProcessId);
                changed = true;
                continue;
            }

            if (scope != old.Scope ||
                !string.Equals(old.ProcessName, NormalizedExeBaseName(processName), StringComparison.OrdinalIgnoreCase))
            {
                _lockRules[old.RootPid] = new LockRule(
                    old.RootPid,
                    old.DisplayName,
                    NormalizedExeBaseName(processName),
                    scope,
                    old.PasswordHash);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        // Persist quietly. Refreshing hub/tray chrome here closes an open tray context menu mid-click.
        SaveConfig();
        if (Visible && !IsTrayContextMenuOpen() && !IsFocusSensitiveUiActive)
        {
            UpdateLockedRulesList();
            UpdateUi();
        }
    }

    private bool IsTrayContextMenuOpen() =>
        _trayIcon?.ContextMenuStrip is { IsDisposed: false, Visible: true };

    /// <summary>True while tray menu or a master-password dialog needs an undisturbed UI thread.</summary>
    private bool ShouldDeferLockDialogChrome() =>
        IsFocusSensitiveUiActive || IsTrayContextMenuOpen();

    /// <summary>Keeps the same unlock dialog when a rule's root PID is retargeted after the process restarts.</summary>
    private void RekeyLockGroupDialog(int oldRootPid, int newRootPid)
    {
        if (oldRootPid == newRootPid || !_lockGroupDialogs.Remove(oldRootPid, out var overlay))
        {
            return;
        }

        if (_lockGroupDialogs.ContainsKey(newRootPid))
        {
            try
            {
                overlay.Close();
                overlay.Dispose();
            }
            catch
            {
                // ignored
            }

            return;
        }

        try
        {
            overlay.RelocateRuleRoot(newRootPid);
        }
        catch
        {
            // ignored
        }

        _lockGroupDialogs[newRootPid] = overlay;
    }

    /// <summary>Best-effort: closes built-in Task Manager while protection is on (optional setting).</summary>
    private void TryCloseTaskManagerForActiveLockPolicy()
    {
        if (_isShuttingDown || !_config.Enabled || !_config.CloseTaskManagerWhileLockRulesActive)
        {
            return;
        }

        Process[] list;
        try
        {
            list = Process.GetProcessesByName("taskmgr");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Task Manager policy: enumerate taskmgr failed.", ex);
            return;
        }

        if (list.Length == 0)
        {
            return;
        }

        foreach (var p in list)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException($"Task Manager policy: could not terminate taskmgr pid {p.Id}.", ex);
            }
            finally
            {
                try
                {
                    p.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private Dictionary<nint, WindowTarget> EnumerateTargetWindows()
    {
        var results = new Dictionary<nint, WindowTarget>();
        var currentPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!IsCandidateWindow(hWnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0 || pid == currentPid)
            {
                return true;
            }

            string processName;
            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch (Exception ex)
            {
                AppLogger.LogException($"Failed reading process for hwnd {hWnd} pid {pid}.", ex);
                return true;
            }

            if (IgnoredProcessNames.Contains(processName))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hWnd, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 120 || height < 80)
            {
                return true;
            }

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            var windowBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var clientBounds = GetClientAreaBounds(hWnd, windowBounds);
            var overlayBounds = ComputeContentOnlyOverlayBounds(hWnd, windowBounds, clientBounds);
            if (overlayBounds.Width < 120 || overlayBounds.Height < 80)
            {
                return true;
            }

            results[hWnd] = new WindowTarget
            {
                ProcessId = (int)pid,
                ProcessName = processName,
                Bounds = overlayBounds,
                Title = title
            };
            return true;
        }, nint.Zero);

        return results;
    }

    /// <summary>
    /// Processes that currently own at least one normal visible top-level window we would overlay.
    /// Used for "Lock All" so we never create rules for background services (e.g. every svchost),
    /// which combined with "all instances" scope could map almost every PID and freeze the session.
    /// </summary>
    private List<ProcessSnapshotItem> GetProcessesWithLockableTopLevelWindows()
    {
        var lockablePids = EnumerateTargetWindows()
            .Values
            .Select(t => t.ProcessId)
            .Distinct()
            .ToHashSet();

        return lockablePids
            .Select(pid => _processSnapshot.TryGetValue(pid, out var item) ? item : null)
            .Where(x => x is not null)
            .Cast<ProcessSnapshotItem>()
            .DistinctBy(x => x.ProcessId)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Dictionary<int, LockRule> BuildLockedPidRuleMap()
    {
        var map = new Dictionary<int, LockRule>();

        if (_lockRules.Count > 0)
        {
            var childrenMap = _processSnapshot
                .Values
                .GroupBy(x => x.ParentProcessId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ProcessId).ToList());

            foreach (var rule in _lockRules.Values.ToList())
            {
                var rootCandidates = new HashSet<int>();
                if (rule.Scope == LockScope.SinglePidOnly)
                {
                    if (_processSnapshot.ContainsKey(rule.RootPid))
                    {
                        rootCandidates.Add(rule.RootPid);
                    }
                }
                else
                {
                    // Match brave.exe and brave (and any new PID after Task Manager End task).
                    rootCandidates = _processSnapshot.Values
                        .Where(x => ProcessNamesLikelySame(x.Name, rule.ProcessName))
                        .Select(x => x.ProcessId)
                        .ToHashSet();

                    if (_processSnapshot.ContainsKey(rule.RootPid))
                    {
                        rootCandidates.Add(rule.RootPid);
                    }
                }

                if (rootCandidates.Count == 0)
                {
                    continue;
                }

                foreach (var rootPid in rootCandidates)
                {
                    map[rootPid] = rule;
                }

                if (rule.Scope != LockScope.ProcessTreeByName)
                {
                    continue;
                }

                var queue = new Queue<int>();
                var seen = new HashSet<int>();
                foreach (var rootPid in rootCandidates)
                {
                    queue.Enqueue(rootPid);
                    seen.Add(rootPid);
                }

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!childrenMap.TryGetValue(current, out var children))
                    {
                        continue;
                    }

                    foreach (var child in children)
                    {
                        if (!seen.Add(child))
                        {
                            continue;
                        }

                        map[child] = rule;
                        queue.Enqueue(child);
                    }
                }
            }
        }

        // System tools are closed silently (like Task Manager) — never overlay-lock them (that broke editors such as Cursor).
        return map;
    }

    /// <summary>
    /// While protection is on, silently close Explorer folder windows, Run (Win+R), Explorer-hosted browse dialogs,
    /// and command shells. Does not touch other apps (Cursor/editors stay usable); never kills explorer.exe itself.
    /// </summary>
    private void TryCloseBlockedSystemToolWindows()
    {
        if (_isShuttingDown || !_config.Enabled || !_config.BlockSystemToolsWhileProtected)
        {
            return;
        }

        var currentPid = Environment.ProcessId;
        var shellPidsToKill = new HashSet<int>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == nint.Zero || !NativeMethods.IsWindow(hWnd))
            {
                return true;
            }

            if (hWnd == NativeMethods.GetShellWindow())
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pidU);
            var pid = (int)pidU;
            if (pid == 0 || pid == currentPid)
            {
                return true;
            }

            var classBuilder = new StringBuilder(256);
            _ = NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
            var className = classBuilder.ToString();
            if (IgnoredClassNames.Contains(className))
            {
                return true;
            }

            string processName;
            try
            {
                processName = Process.GetProcessById(pid).ProcessName;
            }
            catch
            {
                return true;
            }

            if (IgnoredProcessNames.Contains(processName))
            {
                return true;
            }

            var title = GetWindowTitle(hWnd);
            var closeWindow = false;

            if (SystemToolBlacklist.IsExplorerProcessName(processName))
            {
                // Real folder windows + Run + Explorer-hosted Open/Browse dialogs only.
                // Never kill explorer.exe (desktop/taskbar live there).
                if (SystemToolBlacklist.IsExplorerFolderWindowClass(className) ||
                    SystemToolBlacklist.IsRunDialog(className, title) ||
                    SystemToolBlacklist.IsExplorerBrowseDialog(className, title))
                {
                    closeWindow = true;
                }
            }
            else if (SystemToolBlacklist.IsShellProcessName(processName))
            {
                // Console / Terminal apps: end the process (same idea as Task Manager policy).
                if (NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd) || _hiddenForLockHwnds.Contains(hWnd))
                {
                    shellPidsToKill.Add(pid);
                }
            }

            if (closeWindow)
            {
                try
                {
                    NativeMethods.PostMessage(hWnd, NativeMethods.WM_CLOSE, nint.Zero, nint.Zero);
                }
                catch
                {
                    // ignored
                }
            }

            return true;
        }, nint.Zero);

        foreach (var pid in shellPidsToKill)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: false);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    /// Top-level windows that belong to locked PIDs (visible or minimized). Used to drive standalone lock dialogs.
    /// </summary>
    private Dictionary<nint, LockTargetInfo> EnumerateLockTargets(Dictionary<int, LockRule> pidRuleMap)
    {
        var results = new Dictionary<nint, LockTargetInfo>();
        if (pidRuleMap.Count == 0)
        {
            return results;
        }

        var currentPid = Environment.ProcessId;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == nint.Zero || !NativeMethods.IsWindow(hWnd))
            {
                return true;
            }

            if (hWnd == NativeMethods.GetShellWindow())
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pidU);
            var pid = (int)pidU;
            if (pid == 0 || pid == currentPid || !pidRuleMap.TryGetValue(pid, out var ruleForWindow))
            {
                return true;
            }

            var classBuilder = new StringBuilder(256);
            _ = NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
            var className = classBuilder.ToString();
            if (IgnoredClassNames.Contains(className))
            {
                return true;
            }

            var title = GetWindowTitle(hWnd);
            var isRunDialog = SystemToolBlacklist.IsRunDialog(className, title);

            // Folder-only Explorer blacklist: never touch desktop/tray (already ignored); only folder views + Run.
            if (ruleForWindow.ExplorerFolderWindowsOnly)
            {
                if (!SystemToolBlacklist.IsExplorerFolderWindowClass(className) && !isRunDialog)
                {
                    return true;
                }
            }

            // Run (Win+R) is often owned by the shell; still lock it. Other owned windows stay skipped.
            if (!isRunDialog && NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != nint.Zero)
            {
                return true;
            }

            if (!isRunDialog)
            {
                var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                {
                    return true;
                }
            }

            // Include windows we intentionally hid for locking (SW_HIDE is neither visible nor iconic).
            var intentionallyHidden = _hiddenForLockHwnds.Contains(hWnd);
            if (!intentionallyHidden && !NativeMethods.IsWindowVisible(hWnd) && !NativeMethods.IsIconic(hWnd))
            {
                return true;
            }

            string processName;
            try
            {
                processName = Process.GetProcessById(pid).ProcessName;
            }
            catch (Exception ex)
            {
                AppLogger.LogException($"Lock scan: failed reading process for hwnd {hWnd} pid {pid}.", ex);
                return true;
            }

            if (IgnoredProcessNames.Contains(processName))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = "(no title)";
            }

            results[hWnd] = new LockTargetInfo(pid, processName, title);
            return true;
        }, nint.Zero);

        return results;
    }

    private void EnforceLockedWindowSuppressed(nint hWnd)
    {
        if (!NativeMethods.IsWindow(hWnd))
        {
            _hiddenForLockHwnds.Remove(hWnd);
            _mediaPausedForLockHwnds.Remove(hWnd);
            _savedWindowPlacements.Remove(hWnd);
            return;
        }

        try
        {
            // Capture maximize/normal size once before the first hide — SW_RESTORE on unlock was freezing that state.
            if (!_savedWindowPlacements.ContainsKey(hWnd) &&
                NativeMethods.TryGetWindowPlacement(hWnd, out var placement))
            {
                _savedWindowPlacements[hWnd] = placement;
            }

            // Hardware video (browsers, players) paints above normal overlays — hide the window entirely.
            _ = NativeMethods.EnableWindow(hWnd, false);
            // Always re-assert hide: Chromium can show itself again after a prior SW_HIDE.
            _ = NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);

            _hiddenForLockHwnds.Add(hWnd);

            string? processName = null;
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                if (pid != 0)
                {
                    processName = Process.GetProcessById((int)pid).ProcessName;
                }
            }
            catch
            {
                // ignored
            }

            TryPauseMediaForLockedWindow(hWnd, processName);
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Best-effort pause/stop so video/audio does not keep playing while the window is locked.</summary>
    private void TryPauseMediaForLockedWindow(nint hWnd, string? processName)
    {
        var firstForHwnd = _mediaPausedForLockHwnds.Add(hWnd);
        var mediaHost = LooksLikeMediaHostProcess(processName);
        var dueGlobalPulse = mediaHost && (DateTime.UtcNow - _lastGlobalMediaStopUtc).TotalSeconds >= 1.5;

        if (!firstForHwnd && !dueGlobalPulse)
        {
            return;
        }

        try
        {
            if (firstForHwnd)
            {
                NativeMethods.SendMediaAppCommand(hWnd, NativeMethods.AppCommandMediaPause);
                NativeMethods.SendMediaAppCommand(hWnd, NativeMethods.AppCommandMediaStop);
                NativeMethods.SendMediaAppCommand(hWnd, NativeMethods.AppCommandMediaPlayPause);
            }

            // Browsers often ignore WM_APPCOMMAND once the HWND is hidden. Pulse Stop only —
            // Play/Pause toggles and would restart audio every tick.
            if (dueGlobalPulse)
            {
                _lastGlobalMediaStopUtc = DateTime.UtcNow;
                NativeMethods.TapMediaKey(NativeMethods.VkMediaStop);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Media pause failed for hwnd 0x{hWnd:X}.", ex);
        }
    }

    private static bool LooksLikeMediaHostProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var n = NormalizedExeBaseName(processName);
        return n.Contains("chrome", StringComparison.OrdinalIgnoreCase)
               || n.Contains("brave", StringComparison.OrdinalIgnoreCase)
               || n.Contains("msedge", StringComparison.OrdinalIgnoreCase)
               || n.Contains("firefox", StringComparison.OrdinalIgnoreCase)
               || n.Contains("opera", StringComparison.OrdinalIgnoreCase)
               || n.Contains("vivaldi", StringComparison.OrdinalIgnoreCase)
               || n.Contains("vlc", StringComparison.OrdinalIgnoreCase)
               || n.Contains("spotify", StringComparison.OrdinalIgnoreCase)
               || n.Contains("wmplayer", StringComparison.OrdinalIgnoreCase)
               || n.Contains("microsoft.media", StringComparison.OrdinalIgnoreCase)
               || n.Contains("video", StringComparison.OrdinalIgnoreCase)
               || n.Contains("mpv", StringComparison.OrdinalIgnoreCase)
               || n.Contains("potplayer", StringComparison.OrdinalIgnoreCase)
               || n.Contains("discord", StringComparison.OrdinalIgnoreCase)
               || n.Contains("itunes", StringComparison.OrdinalIgnoreCase)
               || n.Contains("applemusic", StringComparison.OrdinalIgnoreCase);
    }

    private void RevealLockedWindow(nint hWnd)
    {
        _hiddenForLockHwnds.Remove(hWnd);
        _mediaPausedForLockHwnds.Remove(hWnd);
        RemoveLockCover(hWnd);
        _savedWindowPlacements.TryGetValue(hWnd, out var savedPlacement);
        _savedWindowPlacements.Remove(hWnd);

        if (!NativeMethods.IsWindow(hWnd))
        {
            return;
        }

        try
        {
            _ = NativeMethods.EnableWindow(hWnd, true);

            if (savedPlacement.length > 0)
            {
                // Restores normal vs maximized exactly as before the lock (do not call SW_RESTORE).
                NativeMethods.TryRestoreWindowPlacement(hWnd, savedPlacement);
            }
            else
            {
                _ = NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);
            }

            // Re-enable the title-bar maximize/minimize buttons after EnableWindow(false).
            _ = NativeMethods.SetWindowPos(
                hWnd,
                nint.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_FRAMECHANGED |
                NativeMethods.SWP_SHOWWINDOW);
        }
        catch
        {
            // ignored
        }
    }

    private static Rectangle? TryGetCoverBounds(nint hWnd)
    {
        if (!NativeMethods.IsWindow(hWnd) || !NativeMethods.GetWindowRect(hWnd, out var rect))
        {
            return null;
        }

        var windowBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (windowBounds.Width < 80 || windowBounds.Height < 60)
        {
            return null;
        }

        var clientBounds = GetClientAreaBounds(hWnd, windowBounds);
        return ComputeContentOnlyOverlayBounds(hWnd, windowBounds, clientBounds);
    }

    /// <summary>
    /// Legacy black cover overlays are not used anymore (locked apps are hidden instead).
    /// Any leftover cover windows are closed so they cannot sit on the desktop as a draggable black panel.
    /// </summary>
    private void SyncLockCovers(IReadOnlyList<(nint Hwnd, LockTargetInfo Info)> stillLocked)
    {
        _ = stillLocked;
        RemoveAllLockCovers();
    }

    private void RemoveLockCover(nint hwnd)
    {
        if (!_lockCovers.Remove(hwnd, out var cover))
        {
            return;
        }

        try
        {
            cover.Close();
            cover.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    private void RemoveAllLockCovers()
    {
        foreach (var hwnd in _lockCovers.Keys.ToList())
        {
            RemoveLockCover(hwnd);
        }
    }

    private static Point ComputeStandaloneLockDialogLocation(nint hWnd, int openOverlayCount)
    {
        const int dialogW = 440;
        const int dialogH = 340;
        var digest = Math.Abs(hWnd.GetHashCode() % 401);
        var spreadX = (digest % 6) * 28 + (openOverlayCount % 5) * 22;
        var spreadY = ((digest / 6) % 5) * 24 + (openOverlayCount % 4) * 16;

        // Prefer centering on the locked window so the unlock dialog sits over the covered content.
        if (NativeMethods.IsWindow(hWnd) && NativeMethods.GetWindowRect(hWnd, out var rect))
        {
            var host = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (host.Width >= 120 && host.Height >= 80)
            {
                var x = host.Left + (host.Width - dialogW) / 2 + spreadX - 48;
                var y = host.Top + (host.Height - dialogH) / 2 + spreadY - 36;
                var screen = Screen.FromHandle(hWnd) ?? Screen.PrimaryScreen;
                if (screen is not null)
                {
                    var wa = screen.WorkingArea;
                    x = Math.Clamp(x, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - dialogW - 8));
                    y = Math.Clamp(y, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - dialogH - 8));
                }

                return new Point(x, y);
            }
        }

        Screen? fallback = null;
        try
        {
            fallback = Screen.FromHandle(hWnd);
        }
        catch
        {
            // ignored
        }

        fallback ??= Screen.PrimaryScreen;
        if (fallback is null)
        {
            return new Point(120, 120);
        }

        var area = fallback.WorkingArea;
        var fx = area.Left + (area.Width - dialogW) / 2 + spreadX - 48;
        var fy = area.Top + (area.Height - dialogH) / 2 + spreadY - 36;
        fx = Math.Clamp(fx, area.Left + 8, Math.Max(area.Left + 8, area.Right - dialogW - 8));
        fy = Math.Clamp(fy, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - dialogH - 8));
        return new Point(fx, fy);
    }

    /// <summary>
    /// Keeps locked windows disabled and covered (content hidden), with one unlock dialog per lock rule.
    /// </summary>
    private void SyncStandaloneLockOverlays(Dictionary<int, LockRule> pidRuleMap)
    {
        var lockTargets = EnumerateLockTargets(pidRuleMap);

        _unlockedWindowHandles.RemoveWhere(h => !lockTargets.ContainsKey(h));
        foreach (var stale in _unlockedWindowTitles.Keys.Where(h => !lockTargets.ContainsKey(h)).ToList())
        {
            _unlockedWindowTitles.Remove(stale);
        }

        // Drop any leftover system-tool overlay dialogs from older builds (system tools are silent-close now).
        foreach (var syntheticRoot in new[] { SystemToolBlacklist.ExplorerRuleRootPid, SystemToolBlacklist.ShellsRuleRootPid })
        {
            if (_lockGroupDialogs.ContainsKey(syntheticRoot))
            {
                RemoveLockGroup(syntheticRoot, reEnableWindows: false);
            }
        }

        var byRuleRoot = new Dictionary<int, List<(nint Hwnd, LockTargetInfo Info)>>();
        foreach (var (hwnd, info) in lockTargets)
        {
            if (!pidRuleMap.TryGetValue(info.ProcessId, out var ruleForGroup))
            {
                continue;
            }

            var rootPid = ruleForGroup.RootPid;
            if (!byRuleRoot.TryGetValue(rootPid, out var groupList))
            {
                groupList = new List<(nint, LockTargetInfo)>();
                byRuleRoot[rootPid] = groupList;
            }

            groupList.Add((hwnd, info));
        }

        var deferDialogChrome = ShouldDeferLockDialogChrome();
        var activeRuleRoots = byRuleRoot.Keys.ToHashSet();
        if (!deferDialogChrome)
        {
            foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
            {
                if (activeRuleRoots.Contains(rootPid))
                {
                    continue;
                }

                // Rule root may have been retargeted; migrate the dialog instead of unlocking the app.
                var migrated = false;
                if (_lockGroupDialogs.TryGetValue(rootPid, out var orphanDialog) && !orphanDialog.IsDisposed)
                {
                    var hostNames = orphanDialog.GetHostProcessNames();
                    var newRoot = byRuleRoot.Keys.FirstOrDefault(candidate =>
                        _lockRules.TryGetValue(candidate, out var live) &&
                        hostNames.Any(n => ProcessNamesLikelySame(n, live.ProcessName)));
                    if (newRoot != 0 && newRoot != rootPid)
                    {
                        RekeyLockGroupDialog(rootPid, newRoot);
                        migrated = _lockGroupDialogs.ContainsKey(newRoot);
                    }
                }

                if (!migrated)
                {
                    // Never re-enable here — briefly revealing locked apps caused audio to keep playing after TM kills.
                    RemoveLockGroup(rootPid, reEnableWindows: false);
                }
            }
        }

        var allStillLocked = new List<(nint Hwnd, LockTargetInfo Info)>();
        foreach (var (ruleRootPid, rawGroup) in byRuleRoot)
        {
            if (!pidRuleMap.TryGetValue(rawGroup[0].Info.ProcessId, out var rule))
            {
                continue;
            }

            var stillLocked = new List<(nint Hwnd, LockTargetInfo Info)>();
            foreach (var pair in rawGroup)
            {
                var (hwnd, info) = pair;
                var temporarilyUnlocked = false;
                if (_unlockedWindowHandles.Contains(hwnd))
                {
                    var oldTitle = _unlockedWindowTitles.TryGetValue(hwnd, out var v) ? v : string.Empty;
                    if (string.Equals(oldTitle, info.Title, StringComparison.Ordinal))
                    {
                        temporarilyUnlocked = true;
                    }
                    else
                    {
                        _unlockedWindowHandles.Remove(hwnd);
                        _unlockedWindowTitles[hwnd] = info.Title;
                    }
                }

                if (temporarilyUnlocked)
                {
                    RevealLockedWindow(hwnd);
                    continue;
                }

                stillLocked.Add(pair);
            }

            if (stillLocked.Count == 0)
            {
                if (deferDialogChrome)
                {
                    continue;
                }

                // Host windows are gone (typical Task Manager End task) → hub security alert, no floating dialog.
                // System-tool blacklist is ambient (not a user lock rule); do not raise "app ended" alerts for it.
                if (!rule.IsSystemToolBlacklist &&
                    _lockGroupDialogs.TryGetValue(ruleRootPid, out var endingDlg) &&
                    !endingDlg.IsDisposed &&
                    !endingDlg.IsHubOnlyWarningMode)
                {
                    var key = LockRuleProcessKey(rule);
                    var restartCount = LockedSessionTracker.ForceHubOnlyUnlock(key);
                    LockedSessionTracker.Save();
                    AppLogger.Log($"Lock hosts gone for '{key}'; posting hub security alert.");
                    QueueLockedSessionEndedNotice(
                        string.IsNullOrWhiteSpace(rule.DisplayName) ? key : rule.DisplayName,
                        key,
                        restartCount);
                    continue;
                }

                if (IsHubOnlyUnlockRule(rule))
                {
                    RemoveLockGroup(ruleRootPid, reEnableWindows: false);
                    continue;
                }

                RemoveLockGroup(ruleRootPid, reEnableWindows: false);
                continue;
            }

            allStillLocked.AddRange(stillLocked);

            stillLocked.Sort((a, b) =>
            {
                var aRoot = a.Info.ProcessId == rule.RootPid ? 0 : 1;
                var bRoot = b.Info.ProcessId == rule.RootPid ? 0 : 1;
                var c = aRoot.CompareTo(bRoot);
                return c != 0 ? c : string.Compare(a.Info.ProcessName, b.Info.ProcessName, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var (hwnd, _) in stillLocked)
            {
                EnforceLockedWindowSuppressed(hwnd);
            }

            // After End task: keep windows locked, but no unlock dialog — admin clears the rule from the hub.
            if (IsHubOnlyUnlockRule(rule))
            {
                if (!deferDialogChrome)
                {
                    RemoveLockGroup(ruleRootPid, reEnableWindows: false);
                }

                continue;
            }

            // Closing/creating lock dialogs while the tray menu is open cancels the menu (looks like a glitch loop).
            if (deferDialogChrome)
            {
                continue;
            }

            var hostEntries = stillLocked
                .Select(p => new LockHostEntry(p.Hwnd, p.Info.ProcessId, p.Info.ProcessName, p.Info.Title))
                .ToList();

            if (_lockGroupDialogs.TryGetValue(ruleRootPid, out var existingDialog))
            {
                if (existingDialog.IsHubOnlyWarningMode)
                {
                    RemoveLockGroup(ruleRootPid, reEnableWindows: false);
                }
                else
                {
                    existingDialog.SyncHostTargets(hostEntries, rule);
                    continue;
                }
            }

            try
            {
                var location = ComputeStandaloneLockDialogLocation(stillLocked[0].Hwnd, _lockGroupDialogs.Count);
                AppLogger.Log($"Creating lock group dialog ruleRoot={ruleRootPid} '{rule.DisplayName}' hosts={hostEntries.Count}.");

                var overlaySlot = new OverlayForm?[1];
                var rootPidCapture = ruleRootPid;
                var systemToolRule = rule.IsSystemToolBlacklist;
                overlaySlot[0] = new OverlayForm(
                    rule.DisplayName,
                    ruleRootPid,
                    hostEntries,
                    location,
                    requireMasterBeforeUnlock: systemToolRule
                        ? AuthorizeMasterForSystemToolOverlay
                        : AuthorizeMasterBeforeOverlayUnlock,
                    onUnlock: password =>
                    {
                        if (!string.IsNullOrWhiteSpace(password) && !ValidateRulePassword(password, rule))
                        {
                            return false;
                        }

                        var hosts = overlaySlot[0]!.GetHostHwnds();

                        // Blacklisted system tools: never permanently unlock. Close the windows instead.
                        // Free use requires turning the option off in Security & recovery.
                        if (systemToolRule)
                        {
                            CloseSystemToolHostWindows(hosts);
                            RemoveLockGroup(rootPidCapture, reEnableWindows: false);
                            return true;
                        }

                        foreach (var hWnd in hosts)
                        {
                            _unlockedWindowHandles.Add(hWnd);
                            var unlockedTitle = GetWindowTitle(hWnd);
                            if (string.IsNullOrWhiteSpace(unlockedTitle))
                            {
                                unlockedTitle = "(no title)";
                            }

                            _unlockedWindowTitles[hWnd] = unlockedTitle;
                            RevealLockedWindow(hWnd);
                        }

                        RemoveLockGroup(rootPidCapture, reEnableWindows: false);
                        if (Visible)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                BringToFront();
                                Activate();
                            }));
                        }

                        return true;
                    },
                    onCloseApp: () =>
                    {
                        if (systemToolRule)
                        {
                            CloseSystemToolHostWindows(overlaySlot[0]!.GetHostHwnds());
                            RemoveLockGroup(rootPidCapture, reEnableWindows: false);
                            return;
                        }

                        CloseProcessById(rule.RootPid);
                    },
                    stayOnTop: ComputeLockDialogStayOnTop(),
                    systemToolBlacklistMode: systemToolRule);

                var newOverlay = overlaySlot[0]!;
                newOverlay.FormClosed += (_, _) => { _lockGroupDialogs.Remove(rootPidCapture); };

                newOverlay.Show();
                // Never steal focus while the user is typing a master password (common after Task Manager force-kill).
                if (!IsFocusSensitiveUiActive && !Visible && ComputeLockDialogStayOnTop())
                {
                    newOverlay.BringToFront();
                    newOverlay.Activate();
                }

                _lockGroupDialogs[ruleRootPid] = newOverlay;
                if (LockedSessionTracker.NoteLiveSession(LockRuleProcessKey(rule), ruleRootPid))
                {
                    LockedSessionTracker.Save();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException($"Failed creating lock group dialog for ruleRoot={ruleRootPid}.", ex);
                foreach (var (hwnd, _) in stillLocked)
                {
                    try
                    {
                        if (NativeMethods.IsWindow(hwnd))
                        {
                            _ = NativeMethods.EnableWindow(hwnd, true);
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        SyncLockCovers(allStillLocked);

        // Do not BringToFront every tick — that fought the master-password dialog after recovery.
        // Overlays already use TopMost when enabled; only nudge Z-order when nothing focus-sensitive is open.
    }

    private void RemoveLockGroup(int ruleRootPid, bool reEnableWindows)
    {
        if (!_lockGroupDialogs.Remove(ruleRootPid, out var overlay))
        {
            return;
        }

        var hwnds = overlay.GetHostHwnds();
        try
        {
            overlay.Close();
            overlay.Dispose();
        }
        catch
        {
            // ignored
        }

        if (!reEnableWindows)
        {
            // Still drop covers; leave windows hidden until unlock path reveals them (temporary unlock set).
            foreach (var hwnd in hwnds)
            {
                RemoveLockCover(hwnd);
            }

            return;
        }

        foreach (var hwnd in hwnds)
        {
            RevealLockedWindow(hwnd);
        }
    }

    private void RefreshProcessSnapshotAndTree()
    {
        var selectedPid = GetSelectedProcessPid();
        _processSnapshot = CaptureProcessSnapshot();
        _lastSnapshotRefreshUtc = DateTime.UtcNow;
        if (!_hasHydratedLockRulesFromDisk)
        {
            HydrateLockRulesFromPersistedConfig();
            _hasHydratedLockRulesFromDisk = true;
        }

        PopulateProcessTree();
        if (selectedPid.HasValue)
        {
            SelectProcessNodeByPid(selectedPid.Value);
        }
        UpdateLockedRulesList();
        UpdateUi();
    }

    private void RefreshProcessSnapshotForWatcher()
    {
        // Poll faster when a lock is armed but its app is not running (waiting for Task Manager kill → reopen).
        var waitingForRelaunch = _lockRules.Values.Any(r =>
            r.Scope != LockScope.SinglePidOnly
                ? !_processSnapshot.Values.Any(p => ProcessNamesLikelySame(p.Name, r.ProcessName))
                : !_processSnapshot.ContainsKey(r.RootPid));
        var minSeconds = waitingForRelaunch ? 0.35 : 2.0;
        if ((DateTime.UtcNow - _lastSnapshotRefreshUtc).TotalSeconds < minSeconds)
        {
            return;
        }

        _processSnapshot = CaptureProcessSnapshot();
        _lastSnapshotRefreshUtc = DateTime.UtcNow;
    }

    private Dictionary<int, ProcessSnapshotItem> CaptureProcessSnapshot()
    {
        var result = new Dictionary<int, ProcessSnapshotItem>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");
            foreach (var obj in searcher.Get())
            {
                if (obj is not ManagementObject mo)
                {
                    continue;
                }

                var pid = Convert.ToInt32(mo["ProcessId"] ?? 0);
                var ppid = Convert.ToInt32(mo["ParentProcessId"] ?? 0);
                var name = (mo["Name"]?.ToString() ?? "unknown").Trim();
                if (pid <= 0)
                {
                    continue;
                }

                result[pid] = new ProcessSnapshotItem(pid, ppid, name);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("WMI process snapshot failed; using fallback list.", ex);
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    result[proc.Id] = new ProcessSnapshotItem(proc.Id, 0, proc.ProcessName);
                }
                catch
                {
                    // ignore stale process
                }
            }
        }

        return result;
    }

    private void PopulateProcessTree()
    {
        if (_processTree is null)
        {
            return;
        }

        _processTree.BeginUpdate();
        _processTree.Nodes.Clear();
        var filter = _searchProcessBox?.Text?.Trim() ?? string.Empty;
        var hasFilter = filter.Length > 0;

        // Display parent: shell hosts like explorer.exe launch most apps, which made browsers look "under Explorer".
        // For the tree UI only, treat those as roots so Brave/Chrome/etc. show as their own apps.
        var byParent = _processSnapshot.Values
            .GroupBy(GetDisplayParentProcessId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var roots = _processSnapshot.Values
            .Where(x => GetDisplayParentProcessId(x) == 0)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in roots)
        {
            TreeNode? rootNode = hasFilter
                ? CreateFilteredProcessNode(root, byParent, new HashSet<int> { root.ProcessId }, filter)
                : CreateProcessNode(root);

            if (rootNode is null)
            {
                continue;
            }

            if (!hasFilter)
            {
                AddChildNodesRecursive(rootNode, byParent, new HashSet<int> { root.ProcessId });
            }
            _processTree.Nodes.Add(rootNode);
        }

        _processTree.EndUpdate();
        UpdateCriticalSelectionWarning();
        ArrangeLeftScopeColumnAndFooter();
    }

    /// <summary>
    /// Parent PID used only for the hub process tree. Real WMI parent is unchanged for lock-scope process trees.
    /// </summary>
    private int GetDisplayParentProcessId(ProcessSnapshotItem item)
    {
        if (item.ParentProcessId <= 0 || !_processSnapshot.TryGetValue(item.ParentProcessId, out var parent))
        {
            return 0;
        }

        if (IsShellLauncherProcess(parent))
        {
            return 0;
        }

        return item.ParentProcessId;
    }

    private static bool IsShellLauncherProcess(ProcessSnapshotItem item)
    {
        var name = NormalizedExeBaseName(item.Name);
        return name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("sihost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
    }

    private void AddChildNodesRecursive(TreeNode parentNode, Dictionary<int, List<ProcessSnapshotItem>> byParent, HashSet<int> visited)
    {
        if (parentNode.Tag is not ProcessSnapshotItem parentItem)
        {
            return;
        }

        if (!byParent.TryGetValue(parentItem.ProcessId, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            if (!visited.Add(child.ProcessId))
            {
                continue;
            }

            var childNode = CreateProcessNode(child);
            parentNode.Nodes.Add(childNode);
            AddChildNodesRecursive(childNode, byParent, visited);
        }
    }

    private static TreeNode CreateProcessNode(ProcessSnapshotItem item)
    {
        var node = new TreeNode($"{item.Name} (PID {item.ProcessId})")
        {
            Tag = item
        };
        if (IsCriticalSystemProcess(item))
        {
            node.ForeColor = Color.DarkRed;
        }

        return node;
    }

    private static TreeNode? CreateFilteredProcessNode(
        ProcessSnapshotItem item,
        Dictionary<int, List<ProcessSnapshotItem>> byParent,
        HashSet<int> visited,
        string filter)
    {
        var selfMatch = item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        item.ProcessId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);

        var node = CreateProcessNode(item);
        var hasMatchedDescendant = false;

        if (byParent.TryGetValue(item.ProcessId, out var children))
        {
            foreach (var child in children)
            {
                if (!visited.Add(child.ProcessId))
                {
                    continue;
                }

                var childNode = CreateFilteredProcessNode(child, byParent, visited, filter);
                if (childNode is null)
                {
                    continue;
                }

                node.Nodes.Add(childNode);
                hasMatchedDescendant = true;
            }
        }

        if (!selfMatch && !hasMatchedDescendant)
        {
            return null;
        }

        return node;
    }

    private void LockSelectedProcess()
    {
        if (_processTree?.SelectedNode?.Tag is not ProcessSnapshotItem selected)
        {
            MessageBox.Show(this, "Select a process first.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        EnsureStayOnTopPermissionAsked();

        if (IsCriticalSystemProcess(selected))
        {
            var confirm = MessageBox.Show(
                this,
                $"{selected.Name} (PID {selected.ProcessId}) is a critical Windows process.\n\n" +
                "Locking it can freeze the desktop, break the taskbar or sign-in, or force you to restart the PC from the power button.\n\n" +
                "We strongly recommend No. Only choose Yes if you fully understand the risk.",
                "Critical system process",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                return;
            }
        }

        if (SystemToolBlacklist.IsExplorerProcessName(selected.Name) ||
            SystemToolBlacklist.IsShellProcessName(selected.Name))
        {
            if (_config.BlockSystemToolsWhileProtected)
            {
                MessageBox.Show(
                    this,
                    "File Explorer and command shells are already covered by System tools blacklist while protection is on.\n\n"
                    + "They do not appear under Active locks. Manage that option in Security & recovery.",
                    "Windlock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                    this,
                    "Locking File Explorer or shells this way adds an Active lock rule.\n\n"
                    + "Prefer System tools blacklist in Security & recovery if you want them blocked whenever protection is on "
                    + "(with a warning — some apps that open Explorer may misbehave).\n\n"
                    + "Continue with a normal Active lock anyway?",
                    "Lock Explorer / shell",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
        }

        var scope = GetSelectedScope();
        var intro = FormatProcessesToLockHeading(selected);
        var lockPrompt = new PasswordPromptForm(
            "Set Lock Password",
            intro + Environment.NewLine + Environment.NewLine +
            "Set a lock password for that process. Leave empty to use your master password.",
            GetLockScopeAdvisoryForPasswordDialogs(scope));

        if (lockPrompt.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var subPasswordHash = string.IsNullOrEmpty(lockPrompt.Password)
            ? _config.MasterPasswordHash
            : ComputeSha256(lockPrompt.Password);

        var processKey = NormalizedExeBaseName(selected.Name);
        _lockRules[selected.ProcessId] = new LockRule(
            selected.ProcessId,
            selected.Name,
            processKey,
            scope,
            subPasswordHash);
        AppLogger.Log($"Locked process rule added: {selected.Name} pid={selected.ProcessId} scope={scope}.");
        SaveConfig();
        UpdateLockedRulesList();
        UpdateUi();
        ApplyProtectionFromRulesNow();
    }

    private void LockAllVisibleProcesses()
    {
        EnsureStayOnTopPermissionAsked();

        _processSnapshot = CaptureProcessSnapshot();
        _lastSnapshotRefreshUtc = DateTime.UtcNow;

        var scope = GetSelectedScope();
        var candidates = GetProcessesWithLockableTopLevelWindows();

        if (_searchProcessBox is { } search && !string.IsNullOrWhiteSpace(search.Text))
        {
            var filter = search.Text.Trim();
            candidates = candidates
                .Where(x =>
                    x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    x.ProcessId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Explorer / shells are handled by the system-tools blacklist (when enabled), not as Active lock rules.
        candidates = candidates
            .Where(x =>
                !SystemToolBlacklist.IsExplorerProcessName(x.Name) &&
                !SystemToolBlacklist.IsShellProcessName(x.Name))
            .ToList();

        if (candidates.Count == 0)
        {
            MessageBox.Show(
                this,
                "No apps match right now.\n\n" +
                "Lock All only includes programs that have a normal visible, non-minimized window (not background-only processes). " +
                "File Explorer and command shells are not added here — use System tools blacklist in Security & recovery if you want those blocked while protection is on. " +
                "If you just unlocked apps, they may still be minimized — click Refresh (↻) or restore the window, then try again. " +
                "Clear the search box if a filter is active.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        candidates = candidates
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ProcessId)
            .ToList();

        var bulkAdvisory =
            "LOCK ALL: Rules are only created for programs that currently have a normal visible window — not every background service. " +
            "File Explorer and shells are not included (optional system-tools blacklist covers those while protection is on). " +
            "Your lock scope still applies to how wide each rule reaches." +
            Environment.NewLine +
            GetLockScopeAdvisoryForPasswordDialogs(scope);

        var intro = FormatProcessesToLockHeading(candidates);
        var lockPrompt = new PasswordPromptForm(
            "Set Lock Password",
            intro + Environment.NewLine + Environment.NewLine +
            "Set one shared lock password for every app listed above. Leave empty to use your master password.",
            bulkAdvisory);
        if (lockPrompt.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var subPasswordHash = string.IsNullOrEmpty(lockPrompt.Password)
            ? _config.MasterPasswordHash
            : ComputeSha256(lockPrompt.Password);

        foreach (var item in candidates)
        {
            _lockRules[item.ProcessId] = new LockRule(
                item.ProcessId,
                item.Name,
                NormalizedExeBaseName(item.Name),
                scope,
                subPasswordHash);
        }

        AppLogger.Log($"Bulk lock (visible-window owners only) added for {candidates.Count} processes, scope={scope}.");
        SaveConfig();
        UpdateLockedRulesList();
        UpdateUi();
        ApplyProtectionFromRulesNow();
    }

    private void UnlockSelectedRule()
    {
        if (_lockedRulesList?.SelectedItem is not LockRuleListItem item || !_lockRules.TryGetValue(item.RootPid, out var rule))
        {
            MessageBox.Show(this, "Select a lock rule first.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!PromptAndVerifyMasterPassword(
                "Master password",
                $"Enter your Windlock master password to remove the lock rule for {rule.DisplayName}.",
                this))
        {
            return;
        }

        RemoveLockRule(rule.RootPid);
    }

    private void RemoveLockRule(int rootPid)
    {
        if (_lockRules.TryGetValue(rootPid, out var removed))
        {
            ClearHubOnlyUnlockForRule(removed);
        }

        _lockRules.Remove(rootPid);
        AppLogger.Log($"Lock rule removed for root pid={rootPid}.");
        RemoveOverlaysForRoot(rootPid);
        SaveConfig();
        UpdateLockedRulesList();
        UpdateUi();
    }

    private void UnlockAllRules()
    {
        if (_lockRules.Count == 0)
        {
            MessageBox.Show(this, "There are no lock rules to remove.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Remove all {_lockRules.Count} lock rule(s)? Locked windows will be enabled again.",
            "Unlock All",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        if (!PromptAndVerifyMasterPassword(
                "Master password",
                "Enter your Windlock master password to confirm removing all lock rules.",
                this))
        {
            return;
        }

        _lockRules.Clear();
        EnsureLockedSessionTrackerLoaded();
        LockedSessionTracker.ClearAll();
        foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
        {
            RemoveLockGroup(rootPid, reEnableWindows: true);
        }
        AppLogger.Log("All lock rules removed.");
        SaveConfig();
        UpdateLockedRulesList();
        UpdateUi();
    }

    private void RemoveOverlaysForRoot(int rootPid)
    {
        RemoveLockGroup(rootPid, reEnableWindows: true);
    }

    private void UpdateLockedRulesList()
    {
        if (_lockedRulesList is null)
        {
            return;
        }

        _lockedRulesList.BeginUpdate();
        _lockedRulesList.Items.Clear();
        foreach (var rule in _lockRules.Values.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            // System tools are ambient while protection is on — never list them as Active locks.
            if (SystemToolBlacklist.IsExplorerProcessName(rule.ProcessName) ||
                SystemToolBlacklist.IsExplorerProcessName(rule.DisplayName) ||
                SystemToolBlacklist.IsShellProcessName(rule.ProcessName) ||
                SystemToolBlacklist.IsShellProcessName(rule.DisplayName))
            {
                continue;
            }

            _lockedRulesList.Items.Add(new LockRuleListItem(rule, hubOnlyUnlock: IsHubOnlyUnlockRule(rule)));
        }
        _lockedRulesList.EndUpdate();
    }

    private void CloseProcessById(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            proc.Kill(entireProcessTree: false);
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"CloseProcessById failed for pid {processId}.", ex);
        }
        finally
        {
            foreach (var kv in _lockGroupDialogs.ToList())
            {
                if (kv.Value.GetHostHwnds().Any(h => GetPid(h) == processId))
                {
                    RemoveLockGroup(kv.Key, reEnableWindows: true);
                }
            }
        }
    }

    /// <summary>Accepts the per-rule lock password or the hub master password (so you can recover a locked app).</summary>
    private bool ValidateRulePassword(string password, LockRule rule)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var hash = ComputeSha256(password);
        if (string.Equals(hash, rule.PasswordHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(hash, _config.MasterPasswordHash, StringComparison.OrdinalIgnoreCase);
    }

    private bool VerifyMasterPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        return string.Equals(ComputeSha256(password), _config.MasterPasswordHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// TopMost lock overlays steal Z-order from owned modals on some Windows builds; suspend TopMost while showing a dialog on top of that owner.
    /// </summary>
    private static void WithSuspendedOwnerTopMost(IWin32Window? owner, Action action)
    {
        if (owner is not Form om || !om.TopMost)
        {
            action();
            return;
        }

        om.TopMost = false;
        try
        {
            action();
        }
        finally
        {
            om.TopMost = true;
        }
    }

    private bool PromptMasterPasswordOnce(string title, string prompt, IWin32Window? owner, out string password)
    {
        var captured = string.Empty;
        var ownerWindow = owner ?? (IWin32Window)this;
        using var focusGuard = BeginFocusSensitiveUi();
        ApplyStayOnTopToAllLockDialogs(false);
        WithSuspendedOwnerTopMost(ownerWindow, () =>
        {
            using var dialog = new PasswordPromptForm(title, prompt);
            if (dialog.ShowDialog(ownerWindow) != DialogResult.OK)
            {
                return;
            }

            captured = dialog.Password ?? string.Empty;
        });

        password = captured;
        return !string.IsNullOrWhiteSpace(captured);
    }

    private bool PromptAndVerifyMasterPassword(string title, string prompt, IWin32Window? owner = null)
    {
        var ownerWindow = owner ?? (IWin32Window)this;
        using var focusGuard = BeginFocusSensitiveUi();
        ApplyStayOnTopToAllLockDialogs(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!PromptMasterPasswordOnce(title, prompt, owner, out var password))
            {
                return false;
            }

            if (VerifyMasterPassword(password))
            {
                _masterPasswordForEncryptionSession = password;
                TryLeaveEnforcementOnlyMode();
                return true;
            }

            WithSuspendedOwnerTopMost(ownerWindow, () =>
                MessageBox.Show(ownerWindow, "Wrong master password.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }

        return false;
    }

    private void SetProtection(bool enabled)
    {
        if (!enabled &&
            !PromptAndVerifyMasterPassword("Turn Protection OFF", "Enter master password to disable protection:"))
        {
            return;
        }

        _config.Enabled = enabled;
        if (!enabled)
        {
            // Turning protection off clears every app lock, not only the overlay chrome.
            _lockRules.Clear();
            EnsureLockedSessionTrackerLoaded();
            LockedSessionTracker.ClearAll();
            _unlockedWindowHandles.Clear();
            _unlockedWindowTitles.Clear();
            foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
            {
                RemoveLockGroup(rootPid, reEnableWindows: true);
            }

            AppLogger.Log("Protection OFF: all lock rules cleared.");
        }

        SaveConfig();

        if (enabled)
        {
            ApplyProtectionFromRulesNow();
        }

        UpdateLockedRulesList();
        UpdateUi();
    }

    private void TryUninstallAppLockerFromHub()
    {
        if (_isShuttingDown)
        {
            return;
        }

        const string intro =
            "Completely uninstalls Windlock from this PC: saved settings, locks, and helper files next to the app.\n\n"
            + "Exported backups are not removed. After a reinstall you can import a backup with the same master password.\n\n"
            + "This cannot be undone. Continue?";
        if (MessageBox.Show(
                this,
                intro,
                "Uninstall Windlock",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "Uninstall Windlock now?",
                "Uninstall Windlock",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Stop,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        if (!PromptAndVerifyMasterPassword(
                "Uninstall Windlock",
                "Enter your master password to confirm uninstall.",
                this))
        {
            return;
        }

        AppLogger.Log("Uninstall: removing configured data.");

        if (_config.UsbLockdownBetaEnabled)
        {
            TryStopUsbLockdownMonitor();
            UsbInputLockdownBeta.TryDisableUsbMassStorage(_config, out _);
        }

        if (_config.WatchdogRelaunchOnForceKill || _config.SuicideRebootOnAbruptTermination)
        {
            WatchdogRelaunch.WriteCleanShutdownMarker(Process.GetCurrentProcess().Id);
        }

        AppUninstallCleanup.TryUnregisterRestartMetadata();

        var configPath = _configPath;
        var postExitTargets = AppUninstallCleanup.EnumeratePostExitDeletionTargets();
        EnforcementManifestStore.TryDelete(configPath);
        var deleteErrors = AppUninstallCleanup.TryDeleteConfiguredData(configPath);

        if (!string.IsNullOrEmpty(deleteErrors))
        {
            AppLogger.Log("Uninstall: partial delete errors:\n" + deleteErrors);
        }

        if (File.Exists(configPath))
        {
            MessageBox.Show(
                this,
                "The main settings file could not be removed, so uninstall was stopped.\n\nDetails:\n"
                + (string.IsNullOrEmpty(deleteErrors) ? "(Unknown error.)" : deleteErrors),
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        AppUninstallCleanup.TrySchedulePostExitFileDeletion(postExitTargets);

        if (!string.IsNullOrEmpty(deleteErrors))
        {
            MessageBox.Show(
                this,
                "Core data was removed, but some files could not be deleted (often another program has them open):\n\n"
                + deleteErrors
                + "\n\nYou can delete those paths manually. The app will now close.",
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        _isShuttingDown = true;
        _hubIdleTimer.Stop();
        _watcherTimer.Stop();

        foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
        {
            RemoveLockGroup(rootPid, reEnableWindows: true);
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        Application.ExitThread();
    }

    private void ChangeMasterPassword()
    {
        if (!PromptAndVerifyMasterPassword("Verify Master Password", "Enter current master password:"))
        {
            return;
        }

        using var prompt = new PasswordPromptForm("Change Master Password", "Enter new master password:");
        if (prompt.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(prompt.Password))
        {
            _config.MasterPasswordHash = ComputeSha256(prompt.Password);
            _masterPasswordForEncryptionSession = prompt.Password;
            SaveConfig();
            MessageBox.Show(this, "Master password updated.", "Windlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void UpdateUi()
    {
        var on = _config.Enabled;
        var status = on ? $"🟢  Protection on  ·  {_lockRules.Count} lock(s)" : $"⚪  Protection off  ·  {_lockRules.Count} lock(s)";

        if (_statusLabel is not null) _statusLabel.Text = status;
        if (_toggleButton is not null) _toggleButton.Text = on ? "⏸  Turn off" : "▶  Turn on";
        if (_usbLockdownOffLink is not null)
        {
            _usbLockdownOffLink.Visible = _config.UsbLockdownBetaEnabled;
        }

        // Updating tray menu item text/visibility while the menu is open dismisses it instantly.
        if (IsTrayContextMenuOpen())
        {
            return;
        }

        if (_trayStatusItem is not null) _trayStatusItem.Text = on ? $"🟢 On · {_lockRules.Count} locks" : $"⚪ Off · {_lockRules.Count} locks";
        if (_trayToggleItem is not null) _trayToggleItem.Text = on ? "Turn protection off" : "Turn protection on";
        if (_trayUsbLockdownOffItem is not null)
        {
            _trayUsbLockdownOffItem.Visible = _config.UsbLockdownBetaEnabled;
        }
    }

    private void StartShutdown(bool requireMaster)
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (requireMaster && !PromptAndVerifyMasterPassword("Exit Windlock", "Enter master password to exit:"))
        {
            return;
        }

        AppLogger.Log("Shutdown requested.");
        _isShuttingDown = true;
        _watchdogProcessId = 0;
        RemoveAllLockCovers();
        TryStopUsbLockdownMonitor();
        if (_config.WatchdogRelaunchOnForceKill || _config.SuicideRebootOnAbruptTermination)
        {
            WatchdogRelaunch.WriteCleanShutdownMarker(Process.GetCurrentProcess().Id);
        }

        TryKillWatchdogHelperProcessesForCurrentHub();
        WatchdogRelaunch.TryCleanupHelperArtifacts(Environment.ProcessPath);

        _hubIdleTimer.Stop();
        _watcherTimer.Stop();

        foreach (var rootPid in _lockGroupDialogs.Keys.ToList())
        {
            RemoveLockGroup(rootPid, reEnableWindows: true);
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        Application.ExitThread();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isShuttingDown)
        {
            base.OnFormClosing(e);
            return;
        }

        // Keep the hub (and watcher) alive: hide to tray instead of closing. Task Manager "End task" often sends this path first;
        // a hard TerminateProcess cannot be blocked in user mode — rules are persisted so overlays restore on next launch.
        e.Cancel = true;
        if (e.CloseReason == CloseReason.TaskManagerClosing)
        {
            AppLogger.Log("Close from Task Manager intercepted; hub minimized to tray. Lock rules are on disk and will restore when you open the hub again.");
        }

        Hide();
    }

    private static string ComputeSha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsCandidateWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }
        if (!NativeMethods.IsWindowVisible(hWnd))
        {
            return false;
        }
        if (NativeMethods.IsIconic(hWnd))
        {
            return false;
        }
        if (hWnd == NativeMethods.GetShellWindow())
        {
            return false;
        }
        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != nint.Zero)
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        var classBuilder = new StringBuilder(256);
        _ = NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
        var className = classBuilder.ToString();
        if (IgnoredClassNames.Contains(className))
        {
            return false;
        }

        return true;
    }

    private static int GetPid(nint hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        return (int)pid;
    }

    private void LockScopeComboOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_lockScopeHelpLabel is null || _lockScopeCombo is null)
        {
            return;
        }

        var idx = Math.Max(0, Math.Min(2, _lockScopeCombo.SelectedIndex));
        var isRecommended = idx == RecommendedLockScopeIndex;
        _lockScopeHelpLabel.Text = GetLockScopeInlineHelp(idx);
        _lockScopeHelpLabel.ForeColor = isRecommended ? Color.FromArgb(21, 108, 63) : Color.DimGray;
        _lockScopeHelpLabel.Font = new Font("Segoe UI", 8.5f, isRecommended ? FontStyle.Bold : FontStyle.Regular);
        ArrangeLeftScopeColumnAndFooter();
    }

    /// <summary>Combo index that survives app restarts best, so it is flagged as recommended in the UI.</summary>
    private const int RecommendedLockScopeIndex = 2;

    /// <summary>Short always-visible hint under the combo (plain language).</summary>
    private static string GetLockScopeInlineHelp(int index) => index switch
    {
        0 => "One running process only. A restart may need a new lock.",
        1 => "Every copy of this app name, including new ones.",
        2 => "✓ Recommended: this app and its helper processes.",
        _ => string.Empty
    };

    /// <summary>Extra warning block appended in password dialogs when setting a lock.</summary>
    private static string GetLockScopeAdvisoryForPasswordDialogs(LockScope scope) => scope switch
    {
        LockScope.SinglePidOnly =>
            "SCOPE WARNING: Only this one PID is locked. The next time the app runs it might use a different PID and will not be protected until you lock again.",
        LockScope.ProcessNameAllInstances =>
            "SCOPE WARNING: This name can match many processes. Lock All only adds rules for apps with a visible window right now — but this scope still means every matching name (now and later) follows the rule. Double-check the program name is not too generic.",
        LockScope.ProcessTreeByName =>
            "SCOPE WARNING: Parent and child processes are locked together with one shared unlock dialog. This is intentional so you are not asked for the password separately for each helper. If you did not want helpers included, cancel and choose “Single PID only”.",
        _ => string.Empty
    };

    private static string FormatProcessesToLockHeading(ProcessSnapshotItem single) =>
        "What you are locking:" + Environment.NewLine +
        $"• {single.Name} (PID {single.ProcessId})";

    private static string FormatProcessesToLockHeading(IReadOnlyList<ProcessSnapshotItem> items, int maxLines = 28)
    {
        var sb = new StringBuilder();
        sb.Append("What you are locking (").Append(items.Count).Append("):");
        sb.AppendLine();
        var n = Math.Min(maxLines, items.Count);
        for (var i = 0; i < n; i++)
        {
            var p = items[i];
            sb.Append("• ").Append(p.Name).Append(" (PID ").Append(p.ProcessId).Append(')').AppendLine();
        }

        if (items.Count > maxLines)
        {
            sb.Append("… ").Append(items.Count - maxLines).AppendLine(" more (same password and scope for all of them).");
        }

        return sb.ToString().TrimEnd();
    }

    private LockScope GetSelectedScope()
    {
        return _lockScopeCombo?.SelectedIndex switch
        {
            0 => LockScope.SinglePidOnly,
            2 => LockScope.ProcessTreeByName,
            _ => LockScope.ProcessNameAllInstances
        };
    }

    private int? GetSelectedProcessPid()
    {
        if (_processTree?.SelectedNode?.Tag is ProcessSnapshotItem selected)
        {
            return selected.ProcessId;
        }

        return null;
    }

    private void SelectProcessNodeByPid(int pid)
    {
        if (_processTree is null)
        {
            return;
        }

        foreach (TreeNode node in _processTree.Nodes)
        {
            var found = FindNodeByPid(node, pid);
            if (found is null)
            {
                continue;
            }

            _processTree.SelectedNode = found;
            found.EnsureVisible();
            break;
        }
    }

    private static TreeNode? FindNodeByPid(TreeNode node, int pid)
    {
        if (node.Tag is ProcessSnapshotItem item && item.ProcessId == pid)
        {
            return node;
        }

        foreach (TreeNode child in node.Nodes)
        {
            var found = FindNodeByPid(child, pid);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static Rectangle GetClientAreaBounds(nint hWnd, Rectangle fallbackWindowRect)
    {
        if (!NativeMethods.GetClientRect(hWnd, out var clientRect))
        {
            return fallbackWindowRect;
        }

        var topLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new POINT { X = clientRect.Right, Y = clientRect.Bottom };
        if (!NativeMethods.ClientToScreen(hWnd, ref topLeft) || !NativeMethods.ClientToScreen(hWnd, ref bottomRight))
        {
            return fallbackWindowRect;
        }

        var width = Math.Max(1, bottomRight.X - topLeft.X);
        var height = Math.Max(1, bottomRight.Y - topLeft.Y);
        if (width < 120 || height < 80)
        {
            return fallbackWindowRect;
        }

        return new Rectangle(topLeft.X, topLeft.Y, width, height);
    }

    /// <summary>
    /// Screen rect for the lock overlay: full client for classic framed windows (caption is outside the client),
    /// or client minus a top band for borderless / custom-chrome apps so minimize / drag / tabs stay usable.
    /// </summary>
    private static Rectangle ComputeContentOnlyOverlayBounds(nint hWnd, Rectangle windowBounds, Rectangle clientBounds)
    {
        var nonClientTop = Math.Max(0, clientBounds.Top - windowBounds.Top);
        var dpi = NativeMethods.GetDpiForWindow(hWnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        int topInsetInClient;
        if (nonClientTop >= 12)
        {
            // System-drawn caption + frame sit above the client — locking the full client leaves the real title bar free.
            topInsetInClient = 0;
        }
        else
        {
            // Client starts at the window top (Chrome, Electron, etc.): leave a DPI-scaled band for window controls + tabs.
            var cap = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYCAPTION);
            var frame = Math.Min(NativeMethods.GetSystemMetrics(NativeMethods.SM_CYFRAME), 20);
            var bandAt96 = Math.Max(48, cap + frame + 10);
            var band = (int)Math.Round(bandAt96 * (dpi / 96.0));
            topInsetInClient = Math.Min(band, Math.Max(0, clientBounds.Height - 88));
        }

        var contentHeight = clientBounds.Height - topInsetInClient;
        if (contentHeight < 80)
        {
            topInsetInClient = Math.Max(0, clientBounds.Height - 80);
        }

        return Rectangle.FromLTRB(
            clientBounds.Left,
            clientBounds.Top + topInsetInClient,
            clientBounds.Right,
            clientBounds.Bottom);
    }

    private static string GetWindowTitle(nint hWnd)
    {
        var len = NativeMethods.GetWindowTextLength(hWnd);
        if (len <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(len + 1);
        _ = NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

public sealed class LockerConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>SHA-256 hex of UTF-8 master password. Empty only before first-run wizard completes.</summary>
    public string MasterPasswordHash { get; set; } = string.Empty;

    /// <summary>Saved lock rules so overlays can be restored after Windlock or target apps restart.</summary>
    public List<PersistedLockRule> LockRules { get; set; } = new();

    /// <summary>0 = disabled. While the hub window is visible, hide to tray after this many minutes without keyboard/mouse input.</summary>
    public int HubIdleRelockMinutes { get; set; }

    /// <summary>When true, opening the hub from the tray after an idle hide requires the master password again.</summary>
    public bool HubIdleRelockRequireMasterOnOpen { get; set; } = true;

    /// <summary>After the security & recovery dialog has been shown once, we do not auto-show it again.</summary>
    public bool SecurityAndRecoveryIntroShown { get; set; }

    /// <summary>When true, a small helper process may restart the hub after an abrupt termination (not a Windows reboot). On by default.</summary>
    public bool WatchdogRelaunchOnForceKill { get; set; } = true;

    /// <summary>When true, the helper schedules a Windows restart if the hub is killed without a clean exit (dangerous; opt-in).</summary>
    public bool SuicideRebootOnAbruptTermination { get; set; }

    /// <summary>Seconds passed to <c>shutdown /t</c> when suicide fires (0 = immediate). Bumped to 60 for legacy configs that had suicide on but no stored delay.</summary>
    public int SuicideRebootDelaySeconds { get; set; } = WatchdogRelaunch.DefaultSuicideDelaySeconds;

    /// <summary>Bumps when new persisted fields are added so older JSON can be migrated once.</summary>
    public int ConfigSchemaVersion { get; set; }

    /// <summary>Beta: optional USB mass storage policy (HKLM) + Device Manager (mmc) master gate from the hub.</summary>
    public bool UsbLockdownBetaEnabled { get; set; }

    /// <summary>USBSTOR Start value before lockdown; -1 if not captured.</summary>
    public int UsbStorStartBeforeLockdown { get; set; } = -1;

    /// <summary>
    /// When true with protection ON, the hub terminates Task Manager (taskmgr.exe) on each watcher tick if it is running.
    /// Reduces bypass via Ctrl+Shift+Esc; does not block other admin tools.
    /// </summary>
    public bool CloseTaskManagerWhileLockRulesActive { get; set; }

    /// <summary>
    /// When true with protection ON, silently close File Explorer folder windows, Run (Win+R), Explorer browse dialogs,
    /// and command shells (no unlock dialog — same style as Task Manager close). Never kills explorer.exe. Off by default.
    /// </summary>
    public bool BlockSystemToolsWhileProtected { get; set; }

    /// <summary>When true, unlock dialogs stay above other windows. Off keeps the hub and other apps reachable without fighting TopMost locks.</summary>
    public bool LockDialogsStayOnTop { get; set; } = true;

    /// <summary>Once the first-time "show on top" permission prompt has been answered, we do not ask again.</summary>
    public bool LockDialogsStayOnTopPromptShown { get; set; }

    public void Normalize()
    {
        if (!string.IsNullOrWhiteSpace(MasterPasswordHash))
        {
            MasterPasswordHash = MasterPasswordHash.Trim();
        }

        LockRules ??= new List<PersistedLockRule>();
        HubIdleRelockMinutes = Math.Clamp(HubIdleRelockMinutes, 0, 24 * 60);
        SuicideRebootDelaySeconds = Math.Clamp(SuicideRebootDelaySeconds, 0, 86400);

        if (ConfigSchemaVersion < 2)
        {
            if (SuicideRebootOnAbruptTermination && SuicideRebootDelaySeconds == 0)
            {
                SuicideRebootDelaySeconds = WatchdogRelaunch.DefaultSuicideDelaySeconds;
            }

            ConfigSchemaVersion = 2;
        }

        if (ConfigSchemaVersion < 3)
        {
            // Auto-restart used to be opt-in, which left protection off for good once the hub was force-killed.
            WatchdogRelaunchOnForceKill = true;
            ConfigSchemaVersion = 3;
        }

        if (ConfigSchemaVersion < 4)
        {
            // Existing installs already used TopMost locks; keep that until they answer the permission prompt.
            LockDialogsStayOnTop = true;
            LockDialogsStayOnTopPromptShown = false;
            ConfigSchemaVersion = 4;
        }

        if (ConfigSchemaVersion < 5)
        {
            // Older builds defaulted this on; keep existing true/false from JSON. New installs default off.
            ConfigSchemaVersion = 5;
        }

        if (ConfigSchemaVersion < 6)
        {
            // Explorer/shell blacklist stays opt-in (can break apps that open Explorer).
            ConfigSchemaVersion = 6;
        }
    }
}

public sealed class PersistedLockRule
{
    public int RootPid { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public LockScope Scope { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
}

internal sealed record LockTargetInfo(int ProcessId, string ProcessName, string Title);

public readonly record struct LockHostEntry(nint Hwnd, int ProcessId, string ProcessName, string Title);

public sealed class WindowTarget
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public Rectangle Bounds { get; set; }
    public string Title { get; set; } = string.Empty;
}

public sealed record ProcessSnapshotItem(int ProcessId, int ParentProcessId, string Name);

public enum LockScope
{
    SinglePidOnly = 0,
    ProcessNameAllInstances = 1,
    ProcessTreeByName = 2
}

public sealed record LockRule(
    int RootPid,
    string DisplayName,
    string ProcessName,
    LockScope Scope,
    string PasswordHash,
    bool IsSystemToolBlacklist = false,
    bool ExplorerFolderWindowsOnly = false);

public sealed class LockRuleListItem
{
    public int RootPid { get; }
    private readonly string _display;

    public LockRuleListItem(LockRule rule, bool hubOnlyUnlock = false)
    {
        RootPid = rule.RootPid;
        var scopeText = rule.Scope switch
        {
            LockScope.SinglePidOnly => "Single process",
            LockScope.ProcessTreeByName => "App + helpers",
            _ => "Same app name"
        };
        var suffix = hubOnlyUnlock ? "  ·  closed (unlock from hub)" : string.Empty;
        _display = $"{rule.DisplayName} (PID {rule.RootPid})  ·  {scopeText}{suffix}";
    }

    public override string ToString() => _display;
}

public enum TamperAlertKind
{
    LockedAppEnded = 0
}

/// <summary>In-hub notice that a locked app session was ended (e.g. Task Manager), dismissible with master password.</summary>
public sealed class TamperAlertItem
{
    public DateTime TimeLocal { get; }
    public TamperAlertKind Kind { get; }
    public IReadOnlyList<string> AppNames { get; }

    public TamperAlertItem(DateTime timeLocal, IReadOnlyList<string> appNames, TamperAlertKind kind = TamperAlertKind.LockedAppEnded)
    {
        TimeLocal = timeLocal;
        Kind = kind;
        AppNames = appNames.ToList();
    }

    public string KindTitle => Kind switch
    {
        TamperAlertKind.LockedAppEnded => "Locked app ended",
        _ => "Alert"
    };

    /// <summary>Short line for the alerts list.</summary>
    public string SummaryText
    {
        get
        {
            var apps = string.Join(", ", AppNames);
            if (apps.Length > 42)
            {
                apps = apps[..39] + "…";
            }

            return $"{TimeLocal:HH:mm}  {KindTitle}  ·  {apps}";
        }
    }

    /// <summary>Full readable text shown in the expandable detail area.</summary>
    public string DetailText => Kind switch
    {
        TamperAlertKind.LockedAppEnded =>
            $"{TimeLocal:HH:mm:ss}  ·  {KindTitle}\n\n"
            + "A locked app was ended (for example from Task Manager).\n"
            + "New windows stay locked until you unlock from the hub.\n\n"
            + "Apps:\n"
            + string.Join("\n", AppNames.Select(a => "•  " + a)),
        _ => SummaryText
    };

    public override string ToString() => SummaryText;
}
