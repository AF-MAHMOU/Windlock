using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppLockerOverlay;

/// <summary>First launch: requirements, storage + master password, then optional system-tool protection.</summary>
public sealed class FirstRunWizardForm : Form
{
    // Keep in sync with Windlock.csproj TargetFramework (net10.0-windows → user installs .NET 10 Desktop Runtime for framework-dependent builds).
    private const string DotNetMajorVersion = "10";
    private static readonly Uri DotNetRuntimeDownloadPage = new($"https://dotnet.microsoft.com/download/dotnet/{DotNetMajorVersion}");
    private static readonly Uri DotNetInstallDocs = new("https://learn.microsoft.com/dotnet/core/install/windows");

    private const int ContentLeft = 20;
    private const int ContentWidth = 480;
    private const int CurrentSchemaVersion = 6;

    private readonly Panel _requirementsPanel;
    private readonly Panel _setupPanel;
    private readonly Panel _protectionPanel;
    private readonly TextBox _folderBox;
    private readonly TextBox _passwordBox;
    private readonly TextBox _confirmBox;
    private readonly CheckBox _blockSystemToolsBox;
    private readonly CheckBox _closeTaskManagerBox;
    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private readonly Button _btnFinish;
    private readonly Button _btnCancel;
    private int _step;

    public string ResultConfigFilePath { get; private set; } = string.Empty;

    /// <summary>Plaintext master for this session only; used to encrypt on disk and to unlock without a second prompt.</summary>
    public string CommittedMasterPassword { get; private set; } = string.Empty;

    public FirstRunWizardForm(string suggestedConfigDirectory)
    {
        Text = "Windlock — First-time setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 500);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(248, 248, 248);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(248, 248, 248)
        };

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(16, 10, 16, 10),
            BackColor = Color.FromArgb(242, 242, 242)
        };

        _btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(84, 30)
        };
        bottom.Controls.Add(_btnCancel);
        CancelButton = _btnCancel;

        _btnFinish = new Button
        {
            Text = "Finish",
            Size = new Size(90, 30),
            Visible = false
        };
        _btnFinish.Click += (_, _) => TryFinish();
        bottom.Controls.Add(_btnFinish);

        _btnNext = new Button
        {
            Text = "Next",
            Size = new Size(84, 30)
        };
        _btnNext.Click += (_, _) => GoNext();
        bottom.Controls.Add(_btnNext);

        _btnBack = new Button
        {
            Text = "Back",
            Size = new Size(84, 30),
            Visible = false
        };
        _btnBack.Click += (_, _) =>
        {
            _step = Math.Max(0, _step - 1);
            ApplyStep();
        };
        bottom.Controls.Add(_btnBack);

        bottom.Resize += (_, _) => LayoutBottomBar(bottom);
        Controls.Add(content);
        Controls.Add(bottom);

        _requirementsPanel = BuildRequirementsPanel();
        var setup = BuildSetupPanel(suggestedConfigDirectory);
        _setupPanel = setup.Panel;
        _folderBox = setup.FolderBox;
        _passwordBox = setup.PasswordBox;
        _confirmBox = setup.ConfirmBox;

        var protection = BuildProtectionPanel();
        _protectionPanel = protection.Panel;
        _blockSystemToolsBox = protection.BlockSystemTools;
        _closeTaskManagerBox = protection.CloseTaskManager;

        content.Controls.Add(_protectionPanel);
        content.Controls.Add(_setupPanel);
        content.Controls.Add(_requirementsPanel);

        _step = 0;
        ApplyStep();

        Shown += (_, _) =>
        {
            LayoutBottomBar(bottom);
            FocusStepControl();
        };
    }

    private void GoNext()
    {
        if (_step == 1 && !ValidateSetupStep())
        {
            return;
        }

        _step = Math.Min(2, _step + 1);
        ApplyStep();
    }

    private void LayoutBottomBar(Panel bottom)
    {
        const int pad = 16;
        const int gap = 8;
        var cy = Math.Max(8, (bottom.ClientSize.Height - 30) / 2);
        var x = bottom.ClientSize.Width - pad;

        void Put(Control c)
        {
            x -= c.Width;
            c.Location = new Point(x, cy);
            x -= gap;
        }

        Put(_btnCancel);
        if (_btnFinish.Visible)
        {
            Put(_btnFinish);
        }

        if (_btnNext.Visible)
        {
            Put(_btnNext);
        }

        if (_btnBack.Visible)
        {
            Put(_btnBack);
        }
    }

    private void ApplyStep()
    {
        _requirementsPanel.Visible = _step == 0;
        _setupPanel.Visible = _step == 1;
        _protectionPanel.Visible = _step == 2;

        _btnBack.Visible = _step > 0;
        _btnNext.Visible = _step < 2;
        _btnFinish.Visible = _step == 2;
        AcceptButton = _step == 2 ? _btnFinish : _btnNext;

        if (_btnCancel.Parent is Panel bottom)
        {
            LayoutBottomBar(bottom);
        }

        FocusStepControl();
    }

    private void FocusStepControl()
    {
        if (_step == 1)
        {
            _passwordBox.Focus();
        }
        else if (_step == 2)
        {
            _blockSystemToolsBox.Focus();
        }
    }

    private Panel BuildRequirementsPanel()
    {
        var p = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(ContentLeft, 20, ContentLeft, 12)
        };

        var y = 0;
        AddHeading(p, ref y, "Before you start");
        AddBody(
            p,
            ref y,
            "Windlock needs a current Windows desktop and the .NET Desktop Runtime if your build does not include it.");

        AddMutedLine(p, ref y, "•  Windows 10 or 11 (64-bit)");
        AddMutedLine(p, ref y, $"•  .NET {DotNetMajorVersion} Desktop Runtime (for the usual published .exe)");
        y += 8;

        AddLink(
            p,
            ref y,
            $"Download .NET {DotNetMajorVersion} Desktop Runtime",
            DotNetRuntimeDownloadPage);
        AddLink(
            p,
            ref y,
            "Install guide (Microsoft Learn)",
            DotNetInstallDocs);

        return p;
    }

    private sealed record SetupPanelBuild(Panel Panel, TextBox FolderBox, TextBox PasswordBox, TextBox ConfirmBox);

    private SetupPanelBuild BuildSetupPanel(string suggestedConfigDirectory)
    {
        var p = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(ContentLeft, 20, ContentLeft, 12),
            Visible = false,
            AutoScroll = true
        };

        var y = 0;
        AddHeading(p, ref y, "Storage & master password");
        AddBody(
            p,
            ref y,
            "Settings are stored as one encrypted file. The filename looks random on purpose. Pick a folder you can find later.");

        AddLabel(p, ref y, "Settings folder");
        var folderBox = new TextBox
        {
            Location = new Point(0, y),
            Size = new Size(ContentWidth - 92, 26),
            ReadOnly = true,
            Text = suggestedConfigDirectory
        };
        p.Controls.Add(folderBox);

        var browse = new Button
        {
            Text = "Browse…",
            Location = new Point(ContentWidth - 84, y - 1),
            Size = new Size(84, 28)
        };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Folder for the encrypted settings file",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(folderBox.Text)
                    ? folderBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                folderBox.Text = dlg.SelectedPath;
            }
        };
        p.Controls.Add(browse);
        y += 40;

        AddLabel(p, ref y, "Master password (at least 6 characters)");
        var passwordBox = new TextBox
        {
            Location = new Point(0, y),
            Size = new Size(ContentWidth, 26),
            UseSystemPasswordChar = true
        };
        p.Controls.Add(passwordBox);
        y += 36;

        AddLabel(p, ref y, "Confirm password");
        var confirmBox = new TextBox
        {
            Location = new Point(0, y),
            Size = new Size(ContentWidth, 26),
            UseSystemPasswordChar = true
        };
        p.Controls.Add(confirmBox);

        return new SetupPanelBuild(p, folderBox, passwordBox, confirmBox);
    }

    private sealed record ProtectionPanelBuild(Panel Panel, CheckBox BlockSystemTools, CheckBox CloseTaskManager);

    private ProtectionPanelBuild BuildProtectionPanel()
    {
        var p = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(ContentLeft, 20, ContentLeft, 12),
            Visible = false
        };

        var y = 0;
        AddHeading(p, ref y, "Extra protection");
        AddBody(
            p,
            ref y,
            "Optional extras while protection is on. They do not appear under Active locks. You can change these later in Security & recovery.");

        AddWarning(
            p,
            ref y,
            "⚠  Warning: unlocked apps (editors, browsers, games) can still open or save files on their own. "
            + "Blocking File Explorer alone is not full session protection — lock those apps too if that matters.");

        AddSectionRule(p, ref y);

        var closeTaskManager = new CheckBox
        {
            Text = "Close Task Manager while protection is on (recommended)",
            AutoSize = true,
            Location = new Point(0, y),
            Checked = true,
            MaximumSize = new Size(ContentWidth, 0)
        };
        p.Controls.Add(closeTaskManager);
        y = closeTaskManager.Bottom + 6;
        AddMutedWrap(
            p,
            ref y,
            "Makes End task harder as a bypass. Turn off if you still need Task Manager.");

        AddSectionRule(p, ref y);

        var blockSystemTools = new CheckBox
        {
            Text = "Silently block File Explorer folders, PowerShell, CMD, Terminal, and Run (Win+R)",
            AutoSize = true,
            Location = new Point(0, y),
            Checked = false,
            MaximumSize = new Size(ContentWidth, 0)
        };
        p.Controls.Add(blockSystemTools);
        y = blockSystemTools.Bottom + 6;
        AddWarning(
            p,
            ref y,
            "⚠  Off by default. Closes Explorer/shells only (no unlock dialog). Do not depend on this alone — lock apps that can browse files.");

        return new ProtectionPanelBuild(p, blockSystemTools, closeTaskManager);
    }

    private bool ValidateSetupStep()
    {
        var dir = _folderBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show(this, "Choose a valid settings folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var password = _passwordBox.Text ?? string.Empty;
        var confirm = _confirmBox.Text ?? string.Empty;
        if (password.Length < 6)
        {
            MessageBox.Show(this, "Master password must be at least 6 characters.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _passwordBox.Focus();
            return false;
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "Password and confirmation do not match.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _confirmBox.Focus();
            return false;
        }

        return true;
    }

    private static void OpenUrl(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                null,
                "Could not open the link: " + ex.Message,
                "Windlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void TryFinish()
    {
        if (!ValidateSetupStep())
        {
            _step = 1;
            ApplyStep();
            return;
        }

        var dir = _folderBox.Text.Trim();
        var password = _passwordBox.Text ?? string.Empty;

        try
        {
            Directory.CreateDirectory(dir);
            var configPath = Path.GetFullPath(Path.Combine(dir, ConfigPathResolver.OpaqueStoreFileName));

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
            if (_blockSystemToolsBox.Checked)
            {
                if (MessageBox.Show(
                        this,
                        "This silently closes File Explorer folders and shells while protection is on.\n\n"
                        + "⚠ Warning: unlocked apps (editors, browsers, games) can still open or save files on their own. "
                        + "Do not rely on Explorer blocking alone — lock those apps too if you want stronger session protection.\n\n"
                        + "Enable anyway?",
                        "Block File Explorer & shells",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    _blockSystemToolsBox.Checked = false;
                }
            }

            var cfg = new LockerConfig
            {
                Enabled = true,
                MasterPasswordHash = hash,
                LockRules = new List<PersistedLockRule>(),
                SecurityAndRecoveryIntroShown = true,
                BlockSystemToolsWhileProtected = _blockSystemToolsBox.Checked,
                CloseTaskManagerWhileLockRulesActive = _closeTaskManagerBox.Checked,
                ConfigSchemaVersion = CurrentSchemaVersion
            };
            cfg.Normalize();

            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            var encrypted = ConfigCrypto.EncryptUtf8(json, password);
            File.WriteAllBytes(configPath, encrypted);

            File.WriteAllText(ConfigPathResolver.NewPointerFileFullPath, configPath + Environment.NewLine);

            CommittedMasterPassword = password;
            ResultConfigFilePath = configPath;
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddHeading(Panel panel, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Location = new Point(0, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(28, 28, 28),
            UseMnemonic = false
        };
        panel.Controls.Add(l);
        y = l.Bottom + 10;
    }

    private void AddBody(Panel panel, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(0, y),
            MaximumSize = new Size(ContentWidth, 0),
            AutoSize = true,
            ForeColor = Color.FromArgb(55, 55, 55),
            UseMnemonic = false
        };
        panel.Controls.Add(l);
        y = l.Bottom + 16;
    }

    private void AddLabel(Panel panel, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(0, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(40, 40, 40),
            UseMnemonic = false
        };
        panel.Controls.Add(l);
        y = l.Bottom + 6;
    }

    private void AddMutedLine(Panel panel, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(0, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(70, 70, 70),
            UseMnemonic = false
        };
        panel.Controls.Add(l);
        y = l.Bottom + 8;
    }

    private void AddMutedWrap(Panel panel, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(18, y),
            MaximumSize = new Size(ContentWidth - 18, 0),
            AutoSize = true,
            ForeColor = Color.FromArgb(95, 95, 95),
            UseMnemonic = false
        };
        panel.Controls.Add(l);
        y = l.Bottom + 14;
    }

    private void AddWarning(Panel panel, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(0, y),
            MaximumSize = new Size(ContentWidth, 0),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.Firebrick,
            UseMnemonic = false
        };
        panel.Controls.Add(l);
        y = l.Bottom + 14;
    }

    private void AddSectionRule(Panel panel, ref int y)
    {
        var line = new Panel
        {
            Location = new Point(0, y),
            Size = new Size(ContentWidth, 1),
            BackColor = Color.FromArgb(215, 215, 215)
        };
        panel.Controls.Add(line);
        y += 14;
    }

    private void AddLink(Panel panel, ref int y, string text, Uri uri)
    {
        var link = new LinkLabel
        {
            Text = text,
            Location = new Point(0, y),
            AutoSize = true,
            LinkArea = new LinkArea(0, text.Length)
        };
        link.LinkClicked += (_, _) => OpenUrl(uri);
        panel.Controls.Add(link);
        y = link.Bottom + 10;
    }
}
