using System.Drawing;

namespace AppLockerOverlay;

public sealed class OverlayForm : Form
{
    private readonly Func<Form, bool>? _requireMasterBeforeUnlock;
    private readonly Func<string, bool> _onUnlock;
    private readonly Action _onCloseApp;
    private Action? _onOpenHub;
    private readonly TextBox _passwordTextBox;
    private readonly Label _errorLabel;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Button _unlockButton;
    private readonly Button _closeAppButton;
    private readonly Panel _cardPanel;

    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private List<LockHostEntry> _hosts = [];
    private string _ruleDisplayName;
    private bool _stayOnTop;
    private string? _hubOnlyWarningText;
    private readonly bool _systemToolBlacklistMode;

    /// <summary>True while the window is in the drag/resize modal loop, so background refreshes cannot fight the mouse.</summary>
    private bool _userIsMovingWindow;

    /// <summary>Lock rule root PID (key in hub); all <see cref="_hosts"/> belong to this rule.</summary>
    public int LockRuleRootPid { get; private set; }

    /// <summary>True when this dialog shows a “closed — unlock from hub” warning instead of a password field.</summary>
    public bool IsHubOnlyWarningMode { get; private set; }

    /// <summary>First host handle (legacy); prefer <see cref="GetHostHwnds"/>.</summary>
    public nint TargetHwnd => _hosts.Count > 0 ? _hosts[0].Hwnd : 0;

    /// <summary>Same as <see cref="LockRuleRootPid"/> for compatibility.</summary>
    public int LockedProcessId => LockRuleRootPid;

    public OverlayForm(
        string ruleDisplayName,
        int lockRuleRootPid,
        IReadOnlyList<LockHostEntry> initialHosts,
        Point dialogScreenLocation,
        Func<Form, bool>? requireMasterBeforeUnlock,
        Func<string, bool> onUnlock,
        Action onCloseApp,
        bool stayOnTop = true,
        bool systemToolBlacklistMode = false)
    {
        AppLogger.Log($"Lock group dialog created ruleRoot={lockRuleRootPid} '{ruleDisplayName}' hosts={initialHosts.Count}.");
        LockRuleRootPid = lockRuleRootPid;
        _hosts = initialHosts.ToList();
        _ruleDisplayName = ruleDisplayName;
        _requireMasterBeforeUnlock = requireMasterBeforeUnlock;
        _onUnlock = onUnlock;
        _onCloseApp = onCloseApp;
        _stayOnTop = stayOnTop;
        _systemToolBlacklistMode = systemToolBlacklistMode;

        Text = $"{FriendlyAppName(ruleDisplayName)} is locked";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        Location = dialogScreenLocation;
        ClientSize = new Size(420, 220);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = stayOnTop;
        BackColor = Color.FromArgb(28, 28, 30);
        KeyPreview = true;

        _cardPanel = new Panel
        {
            BackColor = Color.FromArgb(38, 38, 42),
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(12, 12),
            Size = new Size(396, 196)
        };
        Controls.Add(_cardPanel);

        _titleLabel = new Label
        {
            Text = $"{FriendlyAppName(ruleDisplayName)} is locked",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize = true,
            MaximumSize = new Size(368, 0),
            Location = new Point(14, 14),
            UseMnemonic = false
        };
        _cardPanel.Controls.Add(_titleLabel);

        _subtitleLabel = new Label
        {
            Text = "This app was locked by the device admin.",
            ForeColor = Color.Gainsboro,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = true,
            MaximumSize = new Size(368, 0),
            Location = new Point(14, 48),
            UseMnemonic = false
        };
        _cardPanel.Controls.Add(_subtitleLabel);

        _passwordTextBox = new TextBox
        {
            UseSystemPasswordChar = true,
            Location = new Point(16, 100),
            Width = 240
        };
        _cardPanel.Controls.Add(_passwordTextBox);

        _unlockButton = new Button
        {
            Text = systemToolBlacklistMode ? "Close" : "Unlock",
            Location = new Point(268, 98),
            Size = new Size(100, 28),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(70, 70, 70),
            FlatStyle = FlatStyle.Flat
        };
        _unlockButton.Click += (_, _) => OnPrimaryButtonClick();
        _cardPanel.Controls.Add(_unlockButton);

        _closeAppButton = new Button
        {
            Text = systemToolBlacklistMode ? "Dismiss" : "Close App",
            Location = new Point(268, 132),
            Size = new Size(100, 28),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(90, 45, 45),
            FlatStyle = FlatStyle.Flat,
            Visible = !systemToolBlacklistMode
        };
        _closeAppButton.Click += (_, _) =>
        {
            if (IsHubOnlyWarningMode)
            {
                Close();
                return;
            }

            _onCloseApp();
        };
        _cardPanel.Controls.Add(_closeAppButton);

        _errorLabel = new Label
        {
            Text = string.Empty,
            ForeColor = Color.LightCoral,
            BackColor = Color.Transparent,
            AutoSize = true,
            MaximumSize = new Size(368, 0),
            Location = new Point(16, 136),
            UseMnemonic = false
        };
        _cardPanel.Controls.Add(_errorLabel);

        RebuildCopy();

        Resize += (_, _) => LayoutCard();
        Shown += (_, _) =>
        {
            AppLogger.Log(IsHubOnlyWarningMode
                ? "Lock group warning dialog shown (hub-only)."
                : "Lock group dialog shown.");
            LayoutCard();
            BeginInvoke(new Action(() =>
            {
                ApplyStayOnTop(_stayOnTop);
                if (!_stayOnTop)
                {
                    return;
                }

                BringToFront();
                Activate();
                if (!IsHubOnlyWarningMode && _passwordTextBox.Visible)
                {
                    _passwordTextBox.Focus();
                    _passwordTextBox.SelectAll();
                }
            }));
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (IsHubOnlyWarningMode)
                {
                    Close();
                    return;
                }

                _onCloseApp();
            }
        };
        AcceptButton = _unlockButton;
    }

    public IReadOnlyList<nint> GetHostHwnds() => _hosts.ConvertAll(h => h.Hwnd);

    public IReadOnlyList<string> GetHostProcessNames() =>
        _hosts.Select(h => h.ProcessName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Turns stay-on-top on or off without recreating the dialog (used by settings and when the hub opens).</summary>
    public void ApplyStayOnTop(bool stayOnTop)
    {
        _stayOnTop = stayOnTop;
        if (TopMost == stayOnTop)
        {
            return;
        }

        TopMost = stayOnTop;
    }

    /// <summary>Updates the stored rule-root PID after a sticky retarget (does not recreate the window).</summary>
    public void RelocateRuleRoot(int newRootPid)
    {
        if (newRootPid <= 0 || newRootPid == LockRuleRootPid)
        {
            return;
        }

        LockRuleRootPid = newRootPid;
    }

    /// <summary>
    /// Switches this dialog from password-unlock UI to a warning: app/session was closed — unlock from the hub only.
    /// </summary>
    public void ApplyHubOnlyWarningMode(string warningText, Action openHub)
    {
        IsHubOnlyWarningMode = true;
        _hubOnlyWarningText = warningText;
        _onOpenHub = openHub;

        var name = FriendlyAppName(_ruleDisplayName);
        Text = $"{name} was closed";
        _titleLabel.Text = $"{name} was closed";
        _titleLabel.ForeColor = Color.FromArgb(255, 210, 120);

        _passwordTextBox.Visible = false;
        _passwordTextBox.Enabled = false;
        _errorLabel.Visible = false;
        _errorLabel.Text = string.Empty;

        _unlockButton.Text = "Open Hub";
        _unlockButton.BackColor = Color.FromArgb(50, 90, 140);

        _closeAppButton.Text = "Dismiss";
        _closeAppButton.BackColor = Color.FromArgb(70, 70, 70);

        RebuildCopy();
        LayoutCard();
        AppLogger.Log($"Lock dialog switched to hub-only warning for '{_ruleDisplayName}'.");
    }

    public void SyncHostTargets(IReadOnlyList<LockHostEntry> hosts, LockRule rule)
    {
        if (_userIsMovingWindow)
        {
            return;
        }

        if (HostsAreUnchanged(hosts) && string.Equals(_ruleDisplayName, rule.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        _hosts = hosts.ToList();
        _ruleDisplayName = rule.DisplayName;
        RebuildCopy();
    }

    private bool HostsAreUnchanged(IReadOnlyList<LockHostEntry> hosts)
    {
        if (hosts.Count != _hosts.Count)
        {
            return false;
        }

        for (var i = 0; i < hosts.Count; i++)
        {
            if (!hosts[i].Equals(_hosts[i]))
            {
                return false;
            }
        }

        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmEnterSizeMove)
        {
            _userIsMovingWindow = true;
        }
        else if (m.Msg == WmExitSizeMove)
        {
            _userIsMovingWindow = false;
        }

        base.WndProc(ref m);
    }

    private void RebuildCopy()
    {
        var name = FriendlyAppName(_ruleDisplayName);
        if (IsHubOnlyWarningMode)
        {
            Text = $"{name} was closed";
            _titleLabel.Text = $"{name} was closed";
            _subtitleLabel.Text = string.IsNullOrWhiteSpace(_hubOnlyWarningText)
                ? "This app was closed. Unlock it from the Windlock hub."
                : _hubOnlyWarningText;
        }
        else
        {
            Text = $"{name} is locked";
            _titleLabel.Text = $"{name} is locked";
            _titleLabel.ForeColor = Color.White;
            _subtitleLabel.Text = _systemToolBlacklistMode
                ? "Blocked while protection is on. Close dismisses this window; it will not stay unlocked. Turn off System tools blacklist in Security & recovery to use it freely."
                : "This app was locked by the device admin.";
        }

        LayoutCard();
    }

    private static string FriendlyAppName(string displayName)
    {
        var s = (displayName ?? string.Empty).Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }

        return string.IsNullOrWhiteSpace(s) ? "App" : s;
    }

    private void LayoutCard()
    {
        const int margin = 12;
        const int cardPad = 14;
        var footer = IsHubOnlyWarningMode ? 56 : (_systemToolBlacklistMode ? 72 : 88);
        var innerW = Math.Max(280, ClientSize.Width - 2 * margin);

        _cardPanel.Left = margin;
        _cardPanel.Top = margin;
        _cardPanel.Width = innerW;

        var innerCardW = _cardPanel.Width - cardPad * 2;
        _titleLabel.MaximumSize = new Size(innerCardW, 0);
        _subtitleLabel.MaximumSize = new Size(innerCardW, 0);
        _errorLabel.MaximumSize = new Size(innerCardW, 0);

        _titleLabel.Location = new Point(cardPad, cardPad);
        _subtitleLabel.Location = new Point(cardPad, _titleLabel.Bottom + 10);

        var contentBottom = _subtitleLabel.Bottom + 14;
        var minCardHeight = contentBottom + footer + cardPad;
        var cardFromBottom = ClientSize.Height - _cardPanel.Top - margin;
        var cardH = Math.Max(minCardHeight, Math.Max(160, cardFromBottom));

        var desiredClientH = _cardPanel.Top + cardH + margin;
        if (desiredClientH != ClientSize.Height)
        {
            var screenCap = (Screen.PrimaryScreen?.WorkingArea.Height ?? 900) - 24;
            ClientSize = new Size(ClientSize.Width, Math.Min(Math.Max(desiredClientH, 200), screenCap));
            cardFromBottom = ClientSize.Height - _cardPanel.Top - margin;
            cardH = Math.Max(minCardHeight, Math.Max(160, cardFromBottom));
        }

        _cardPanel.Height = cardH;
        var buttonTop = contentBottom;
        var rightX = _cardPanel.Width - 116;

        if (IsHubOnlyWarningMode)
        {
            _passwordTextBox.Visible = false;
            _errorLabel.Visible = false;
            _unlockButton.Location = new Point(cardPad, buttonTop);
            _unlockButton.Size = new Size(120, 30);
            _closeAppButton.Location = new Point(cardPad + 128, buttonTop);
            _closeAppButton.Size = new Size(100, 30);
            return;
        }

        _passwordTextBox.Visible = true;
        _errorLabel.Visible = true;
        _passwordTextBox.Location = new Point(cardPad, buttonTop);
        _passwordTextBox.Width = Math.Max(140, rightX - cardPad - 8);
        _unlockButton.Location = new Point(rightX, buttonTop - 2);
        _unlockButton.Size = new Size(100, 28);
        _closeAppButton.Location = new Point(rightX, buttonTop + 32);
        _closeAppButton.Size = new Size(100, 28);
        _errorLabel.Location = new Point(cardPad, buttonTop + 34);
    }

    private void OnPrimaryButtonClick()
    {
        if (IsHubOnlyWarningMode)
        {
            try
            {
                _onOpenHub?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Open Hub from warning dialog failed.", ex);
            }

            return;
        }

        TryUnlock();
    }

    private void TryUnlock()
    {
        if (IsHubOnlyWarningMode)
        {
            return;
        }

        _errorLabel.Text = string.Empty;

        if (_requireMasterBeforeUnlock is not null && !_requireMasterBeforeUnlock(this))
        {
            return;
        }

        var password = _passwordTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            if (!_onUnlock(string.Empty))
            {
                _errorLabel.Text = "Wrong password.";
                LayoutCard();
                _passwordTextBox.Focus();
                return;
            }

            AppLogger.Log("Lock group dialog unlock accepted (master-only path).");
            Close();
            return;
        }

        if (_onUnlock(password))
        {
            AppLogger.Log("Lock group dialog unlock accepted.");
            Close();
            return;
        }

        AppLogger.Log("Lock group dialog unlock rejected.");
        _errorLabel.Text = "Wrong password.";
        LayoutCard();
        _passwordTextBox.Clear();
        _passwordTextBox.Focus();
    }
}
