namespace AppLockerOverlay;

/// <summary>Honest scope text, recovery guidance, idle re-lock options, encrypted backup import/export.</summary>
internal sealed class SecurityAndRecoveryForm : Form
{
    private readonly Form1 _hub;
    private readonly NumericUpDown _idleMinutes = new();
    private readonly CheckBox _idleRequireMaster = new();
    private readonly CheckBox _watchdogRelaunch = new();
    private readonly CheckBox _suicideReboot = new();
    private readonly NumericUpDown _suicideDelaySeconds = new();
    private readonly ToolTip _watchdogToolTip = new();
    private readonly ToolTip _suicideToolTip = new();
    private readonly ToolTip _suicideDelayToolTip = new();
    private readonly CheckBox _usbLockdownBeta = new();
    private readonly ToolTip _usbLockdownToolTip = new();
    private readonly CheckBox _closeTaskMgrWhileRules = new();
    private readonly ToolTip _taskMgrPolicyToolTip = new();
    private readonly CheckBox _blockSystemTools = new();
    private readonly ToolTip _blockSystemToolsToolTip = new();
    private readonly CheckBox _lockDialogsStayOnTop = new();
    private readonly ToolTip _lockDialogsStayOnTopToolTip = new();
    private bool _suppressSecurityFormApplyOnClose;

    private const int ContentLeft = 20;
    private const int ContentWidth = 460;

    public SecurityAndRecoveryForm(Form1 hub, bool isIntroMode)
    {
        _hub = hub;
        Text = isIntroMode ? "Windlock — please read once" : "Security & recovery";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(248, 248, 248);
        ClientSize = new Size(520, 560);

        var y = 16;

        AddHeading("🛡  What it is", ref y);
        AddParagraph(
            "Locks the apps you choose and keeps your Windlock settings private. "
            + "It is not antivirus, and it does not replace signing out of Windows, disk encryption, or a strong login password.",
            ref y);
        AddSectionGap(ref y);

        AddHeading("💾  Backup & recovery", ref y);
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pointerName = ConfigPathResolver.NewPointerFileName;
        AddParagraph(
            "Your settings live in one encrypted file. Next to the app you may also see a small pointer file "
            + $"(for example \"{pointerName}\") that points to it.",
            ref y);
        AddParagraph(
            "Export a backup below and store it somewhere safe. Restoring needs the same master password used for that backup. "
            + "A Windows restart can fix a stuck app, but it cannot recover a forgotten master password.",
            ref y);
        AddMutedLine("App folder", exeDir, ref y);
        AddMutedLine("Settings file", _hub.ActiveConfigPathForDisplay, ref y);

        var exportButton = new Button
        {
            Text = "Export encrypted backup…",
            AutoSize = true,
            Location = new Point(ContentLeft, y)
        };
        exportButton.Click += (_, _) => _hub.RunEncryptedBackupExportFromSecurityUi();
        Controls.Add(exportButton);
        y += 36;

        var restoreButton = new Button
        {
            Text = "Restore from encrypted backup…",
            AutoSize = true,
            Location = new Point(ContentLeft, y)
        };
        restoreButton.Click += (_, _) =>
        {
            if (_hub.RunEncryptedBackupRestoreFromSecurityUi())
            {
                _suppressSecurityFormApplyOnClose = true;
                Close();
            }
        };
        Controls.Add(restoreButton);
        y += 40;
        AddSectionGap(ref y);

        AddHeading("🪟  Lock screens", ref y);
        AddParagraph(
            "Keep unlock prompts visible above other windows. Turn this off if lock screens bury the Windlock hub "
            + "or stay stuck in front after a restart.",
            ref y);

        _lockDialogsStayOnTop.Text = "Show lock screens on top of other windows";
        _lockDialogsStayOnTop.AutoSize = true;
        _lockDialogsStayOnTop.Location = new Point(ContentLeft, y);
        _lockDialogsStayOnTop.Checked = _hub.HubConfigForSecurityUi.LockDialogsStayOnTop;
        Controls.Add(_lockDialogsStayOnTop);
        y += 34;

        _lockDialogsStayOnTopToolTip.InitialDelay = 300;
        _lockDialogsStayOnTopToolTip.AutoPopDelay = 28000;
        _lockDialogsStayOnTopToolTip.SetToolTip(
            _lockDialogsStayOnTop,
            "Recommended while protecting apps. Windlock turns this off automatically while the hub window is open, so the hub is not stuck behind locks.");
        AddSectionGap(ref y);

        AddHeading("⏱  Idle hide (hub)", ref y);
        AddParagraph(
            "If the hub stays open with no input for the minutes below, it hides to the tray. "
            + "Optionally ask for the master password again when you reopen it from the tray.",
            ref y);

        Controls.Add(new Label
        {
            Text = "Minutes of no input before hiding (0 = off)",
            Location = new Point(ContentLeft, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(55, 55, 55)
        });
        y += 22;

        _idleMinutes.Minimum = 0;
        _idleMinutes.Maximum = 24 * 60;
        _idleMinutes.Location = new Point(ContentLeft, y);
        _idleMinutes.Size = new Size(80, 24);
        _idleMinutes.Value = Math.Clamp(_hub.HubConfigForSecurityUi.HubIdleRelockMinutes, 0, 24 * 60);
        Controls.Add(_idleMinutes);
        y += 34;

        _idleRequireMaster.Text = "After idle hide, ask for master password when opening from tray";
        _idleRequireMaster.AutoSize = true;
        _idleRequireMaster.Location = new Point(ContentLeft, y);
        _idleRequireMaster.Checked = _hub.HubConfigForSecurityUi.HubIdleRelockRequireMasterOnOpen;
        Controls.Add(_idleRequireMaster);
        y += 36;
        AddSectionGap(ref y);

        AddHeading("🛠  Extra protection", ref y);
        AddParagraph(
            "These apply while protection is on. They do not appear under Active locks.",
            ref y);

        AddWarning(
            "⚠  Session protection tip: unlockable apps (browsers, editors, games, etc.) can still open or save files "
            + "through their own Open/Save dialogs. Blocking File Explorer alone is not enough. "
            + "Lock those apps under Active locks if you want a stronger session.",
            ref y);

        _closeTaskMgrWhileRules.Text = "Close Task Manager while protection is on (recommended)";
        _closeTaskMgrWhileRules.AutoSize = true;
        _closeTaskMgrWhileRules.Location = new Point(ContentLeft, y);
        _closeTaskMgrWhileRules.Checked = _hub.HubConfigForSecurityUi.CloseTaskManagerWhileLockRulesActive;
        Controls.Add(_closeTaskMgrWhileRules);
        y += 34;

        _taskMgrPolicyToolTip.InitialDelay = 300;
        _taskMgrPolicyToolTip.AutoPopDelay = 28000;
        _taskMgrPolicyToolTip.SetToolTip(
            _closeTaskMgrWhileRules,
            "Turn off from here if you need Task Manager while Windlock protection is on.");

        AddParagraph(
            "Optional: silently close File Explorer folders, Run (Win+R), and command shells "
            + "(same idea as Task Manager — no unlock dialog). Desktop and taskbar stay usable.",
            ref y);

        _blockSystemTools.Text = "Silently block File Explorer folders, shells, and Run while protection is on";
        _blockSystemTools.AutoSize = true;
        _blockSystemTools.Location = new Point(ContentLeft, y);
        _blockSystemTools.Checked = _hub.HubConfigForSecurityUi.BlockSystemToolsWhileProtected;
        Controls.Add(_blockSystemTools);
        y += 34;

        AddWarning(
            "⚠  Do not rely on this alone. Unlocked apps can still browse files. Also, some apps that open Explorer may misbehave.",
            ref y);

        _blockSystemToolsToolTip.InitialDelay = 300;
        _blockSystemToolsToolTip.AutoPopDelay = 28000;
        _blockSystemToolsToolTip.SetToolTip(
            _blockSystemTools,
            "Closes standalone Explorer folders/shells only. Unlocked apps can still use their own file dialogs. "
            + "Lock those apps if you need stronger session protection.");
        AddSectionGap(ref y);

        AddHeading("🔁  If the hub is killed", ref y);
        AddParagraph(
            "A small helper watches Windlock (and Windlock watches the helper). The helper uses a random process name that changes when either exits, "
            + "so ending Windlock in Task Manager does not also end the watcher.",
            ref y);
        AddParagraph(
            "If Windlock is force-closed, the helper starts it again and restores your locks without asking for the master password. "
            + "Exiting from the tray is a normal exit and does not restart anything.",
            ref y);
        AddParagraph(
            "Auto-restart is on by default. Turn it off only if you want a force-close to end protection until you start Windlock yourself. "
            + "Suicide (testing) schedules a full PC restart after a sudden kill. If both are on, Suicide runs instead of auto-restart.",
            ref y);

        _watchdogRelaunch.Text = "Auto-restart Windlock after a sudden stop (recommended)";
        _watchdogRelaunch.AutoSize = true;
        _watchdogRelaunch.Location = new Point(ContentLeft, y);
        _watchdogRelaunch.Checked = _hub.HubConfigForSecurityUi.WatchdogRelaunchOnForceKill;
        Controls.Add(_watchdogRelaunch);
        y += 34;

        _watchdogToolTip.InitialDelay = 250;
        _watchdogToolTip.AutoPopDelay = 24000;
        _watchdogToolTip.ReshowDelay = 120;
        _watchdogToolTip.ShowAlways = true;
        _watchdogToolTip.SetToolTip(
            _watchdogRelaunch,
            "Starts a randomly named helper (not Windlock.exe) so Task Manager End task on Windlock does not kill the watcher. "
            + "The name rotates when Windlock or the helper exits. The helper restarts Windlock and re-applies locks. "
            + "Not available under dotnet run. Tray Exit counts as normal.");

        _suicideReboot.Text = "Suicide (testing): restart the whole PC after a sudden stop";
        _suicideReboot.AutoSize = true;
        _suicideReboot.Location = new Point(ContentLeft, y);
        _suicideReboot.Checked = _hub.HubConfigForSecurityUi.SuicideRebootOnAbruptTermination;
        Controls.Add(_suicideReboot);
        y += 30;

        Controls.Add(new Label
        {
            Text = "Seconds before restart (0 = as soon as possible)",
            Location = new Point(ContentLeft, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(55, 55, 55)
        });
        y += 22;

        _suicideDelaySeconds.Minimum = 0;
        _suicideDelaySeconds.Maximum = 86400;
        _suicideDelaySeconds.Location = new Point(ContentLeft, y);
        _suicideDelaySeconds.Size = new Size(88, 24);
        _suicideDelaySeconds.Value = Math.Clamp(_hub.HubConfigForSecurityUi.SuicideRebootDelaySeconds, 0, 86400);
        Controls.Add(_suicideDelaySeconds);
        y += 34;

        _suicideToolTip.InitialDelay = 250;
        _suicideToolTip.AutoPopDelay = 32000;
        _suicideToolTip.ReshowDelay = 120;
        _suicideToolTip.ShowAlways = true;
        _suicideToolTip.SetToolTip(
            _suicideReboot,
            "Experimental: should schedule a Windows restart if the hub is killed without a normal exit. "
            + "May not work everywhere. Try shutdown /a in Command Prompt to cancel before the timer ends. Affects every open app.");

        _suicideDelayToolTip.InitialDelay = 250;
        _suicideDelayToolTip.AutoPopDelay = 20000;
        _suicideDelayToolTip.SetToolTip(
            _suicideDelaySeconds,
            "Passed to Windows shutdown /t. Zero is immediate (still not instant like pulling power). "
            + "Saving applies on the next helper start; if the helper is already running, restart Windlock once to refresh it.");
        AddSectionGap(ref y);

        AddHeading("🔌  USB lockdown (beta)", ref y);
        AddParagraph(
            "Best-effort only. When on, opening Device Manager (English title) while this hub is running asks for your master password. "
            + "Until then, that Device Manager window cannot be used. Cancel or a wrong password closes it.",
            ref y);
        AddParagraph(
            "USB mass storage (thumb drives) can be turned off through a Windows service setting (USBSTOR) only if Windlock runs as Administrator. "
            + "USB keyboards and mice stay enabled so you are not locked out.",
            ref y);

        _usbLockdownBeta.Text = "Enable USB lockdown (beta)";
        _usbLockdownBeta.AutoSize = true;
        _usbLockdownBeta.Location = new Point(ContentLeft, y);
        _usbLockdownBeta.Checked = _hub.HubConfigForSecurityUi.UsbLockdownBetaEnabled;
        Controls.Add(_usbLockdownBeta);
        y += 40;

        _usbLockdownToolTip.InitialDelay = 300;
        _usbLockdownToolTip.AutoPopDelay = 32000;
        _usbLockdownToolTip.SetToolTip(
            _usbLockdownBeta,
            "Turn off from the hub or tray with the master password. Not a security guarantee against malware or admin users.");

        var close = new Button
        {
            Text = "Save settings & close",
            Location = new Point(ContentLeft + ContentWidth - 240, y),
            Size = new Size(240, 32)
        };
        close.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(close);
        AcceptButton = close;

        y += 40;
        ClientSize = new Size(520, Math.Min(740, y + 28));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdogToolTip.Dispose();
            _suicideToolTip.Dispose();
            _suicideDelayToolTip.Dispose();
            _usbLockdownToolTip.Dispose();
            _taskMgrPolicyToolTip.Dispose();
            _blockSystemToolsToolTip.Dispose();
            _lockDialogsStayOnTopToolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_suppressSecurityFormApplyOnClose)
        {
            if (_usbLockdownBeta.Checked && !_hub.HubConfigForSecurityUi.UsbLockdownBetaEnabled)
            {
                if (MessageBox.Show(
                        _hub,
                        "USB lockdown (beta) can change USB mass storage settings (when running as Administrator) and will prompt "
                        + "for your master password when Device Manager opens.\n\n"
                        + "You can turn it off from the hub or tray with the master password.\n\n"
                        + "Enable this experimental feature?",
                        "USB lockdown (beta)",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    _usbLockdownBeta.Checked = false;
                }
            }

            if (_suicideReboot.Checked && !_hub.HubConfigForSecurityUi.SuicideRebootOnAbruptTermination)
            {
                var d = (int)_suicideDelaySeconds.Value;
                var when = d == 0 ? "as soon as Windows schedules it" : $"after about {d} seconds";
                var confirm =
                    "Suicide mode restarts the whole PC "
                    + when
                    + " if the hub is killed without a normal tray exit. Other apps may lose unsaved work. Continue?";
                if (MessageBox.Show(
                        _hub,
                        confirm,
                        "Suicide reboot mode",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    _suicideReboot.Checked = false;
                }
            }

            if (_blockSystemTools.Checked && !_hub.HubConfigForSecurityUi.BlockSystemToolsWhileProtected)
            {
                if (MessageBox.Show(
                        _hub,
                        "This silently closes File Explorer folder windows, Run (Win+R), Explorer browse dialogs, and command shells "
                        + "while protection is on (no unlock prompt).\n\n"
                        + "Other apps stay usable, but Open Folder / file pickers that use Explorer may not open, and some apps can misbehave or crash.\n\n"
                        + "Desktop and taskbar stay usable. You can turn this off again anytime.\n\n"
                        + "Enable silent File Explorer / shell blocking?",
                        "Block File Explorer & shells",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    _blockSystemTools.Checked = false;
                }
            }

            if (_closeTaskMgrWhileRules.Checked && !_hub.HubConfigForSecurityUi.CloseTaskManagerWhileLockRulesActive)
            {
                if (MessageBox.Show(
                        _hub,
                        "When this is on, Task Manager will be closed whenever it is running while protection is ON.\n\n"
                        + "You can turn it off again here. Continue?",
                        "Close Task Manager",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    _closeTaskMgrWhileRules.Checked = false;
                }
            }

            _hub.ApplyHubIdleRelockSettings((int)_idleMinutes.Value, _idleRequireMaster.Checked);
            _hub.ApplyLockDialogsStayOnTopSetting(_lockDialogsStayOnTop.Checked);
            _hub.ApplyWatchdogRelaunchSetting(_watchdogRelaunch.Checked);
            _hub.ApplySuicideRebootSetting(_suicideReboot.Checked);
            _hub.ApplySuicideRebootDelaySeconds((int)_suicideDelaySeconds.Value);
            _hub.ApplyCloseTaskManagerWhileRulesSetting(_closeTaskMgrWhileRules.Checked);
            _hub.ApplyBlockSystemToolsWhileProtectedSetting(_blockSystemTools.Checked);
            if (_usbLockdownBeta.Checked != _hub.HubConfigForSecurityUi.UsbLockdownBetaEnabled)
            {
                if (!_hub.TrySetUsbLockdownBetaEnabled(_usbLockdownBeta.Checked))
                {
                    _usbLockdownBeta.Checked = _hub.HubConfigForSecurityUi.UsbLockdownBetaEnabled;
                }
            }

            _hub.RefreshWatchdogHelperProcess();
        }

        base.OnFormClosing(e);
    }

    private void AddHeading(string text, ref int y)
    {
        var l = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Location = new Point(ContentLeft, y),
            MaximumSize = new Size(ContentWidth, 0),
            AutoSize = true,
            UseMnemonic = false,
            ForeColor = Color.FromArgb(28, 28, 28)
        };
        Controls.Add(l);
        y = l.Bottom + 8;
    }

    private void AddParagraph(string text, ref int y)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(ContentLeft, y),
            MaximumSize = new Size(ContentWidth, 0),
            AutoSize = true,
            UseMnemonic = false,
            ForeColor = Color.FromArgb(55, 55, 55)
        };
        Controls.Add(l);
        y = l.Bottom + 10;
    }

    private void AddWarning(string text, ref int y)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(ContentLeft, y),
            MaximumSize = new Size(ContentWidth, 0),
            AutoSize = true,
            UseMnemonic = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.Firebrick
        };
        Controls.Add(l);
        y = l.Bottom + 12;
    }

    private void AddMutedLine(string title, string value, ref int y)
    {
        var heading = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Location = new Point(ContentLeft, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 90, 90),
            UseMnemonic = false
        };
        Controls.Add(heading);
        y = heading.Bottom + 2;

        var path = new Label
        {
            Text = value,
            Location = new Point(ContentLeft, y),
            MaximumSize = new Size(ContentWidth, 0),
            AutoSize = true,
            ForeColor = Color.FromArgb(70, 70, 70),
            UseMnemonic = false
        };
        Controls.Add(path);
        y = path.Bottom + 12;
    }

    private void AddSectionGap(ref int y)
    {
        y += 6;
        var line = new Panel
        {
            Location = new Point(ContentLeft, y),
            Size = new Size(ContentWidth, 1),
            BackColor = Color.FromArgb(210, 210, 210)
        };
        Controls.Add(line);
        y += 16;
    }
}
