namespace AppLockerOverlay;

/// <summary>
/// Opaque shield over a locked app window so its content stays hidden while the unlock dialog is shown.
/// </summary>
internal sealed class LockCoverForm : Form
{
    private bool _stayOnTop;

    public nint TargetHwnd { get; }

    public LockCoverForm(nint targetHwnd, Rectangle bounds, bool stayOnTop)
    {
        TargetHwnd = targetHwnd;
        _stayOnTop = stayOnTop;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(20, 20, 22);
        Bounds = bounds;
        TopMost = stayOnTop;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000;
            const int WsExToolwindow = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolwindow;
            return cp;
        }
    }

    public void ApplyStayOnTop(bool stayOnTop)
    {
        _stayOnTop = stayOnTop;
        if (TopMost != stayOnTop)
        {
            TopMost = stayOnTop;
        }
    }

    public void SyncBounds(Rectangle bounds)
    {
        if (Bounds != bounds)
        {
            Bounds = bounds;
        }

        if (_stayOnTop && !TopMost)
        {
            TopMost = true;
        }
    }
}
