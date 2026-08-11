using System.Drawing;

namespace AppLockerOverlay;

public sealed class PasswordPromptForm : Form
{
    private readonly TextBox _passwordTextBox;
    private readonly TextBox _promptBox;

    public string Password => _passwordTextBox.Text;

    /// <param name="advisoryNotice">Optional extra paragraph (warnings, scope notes); shown below the main prompt with word wrap.</param>
    public PasswordPromptForm(string title, string prompt, string? advisoryNotice = null)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = SystemColors.Control;

        const int wrapWidth = 460;
        var bodyFont = new Font("Segoe UI", 9f);
        var fullText = string.IsNullOrWhiteSpace(advisoryNotice)
            ? prompt.Trim()
            : $"{prompt.Trim()}{Environment.NewLine}{advisoryNotice.Trim()}";

        var textSize = TextRenderer.MeasureText(
            fullText,
            bodyFont,
            new Size(wrapWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);

        const int textBlockMax = 300;
        var contentH = textSize.Height + 12;
        var textBlockHeight = Math.Min(textBlockMax, Math.Max(44, contentH));
        var clientW = wrapWidth + 40;
        var topPad = 12;
        var textY = topPad;
        var gapAfterText = 10;
        var pwdY = textY + textBlockHeight + gapAfterText;
        var btnY = pwdY + 34;
        ClientSize = new Size(clientW, btnY + 44);

        _promptBox = new TextBox
        {
            Text = fullText,
            Location = new Point(18, textY),
            Size = new Size(wrapWidth, textBlockHeight),
            Font = bodyFont,
            Multiline = true,
            WordWrap = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            TabStop = false,
            ScrollBars = contentH > textBlockHeight ? ScrollBars.Vertical : ScrollBars.None
        };
        Controls.Add(_promptBox);

        _passwordTextBox = new TextBox
        {
            UseSystemPasswordChar = true,
            Location = new Point(20, pwdY),
            Size = new Size(clientW - 40, 25)
        };
        Controls.Add(_passwordTextBox);

        var okButton = new Button
        {
            Text = "OK",
            Location = new Point(clientW - 180, btnY),
            Size = new Size(70, 28),
            DialogResult = DialogResult.OK
        };
        Controls.Add(okButton);

        var cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(clientW - 98, btnY),
            Size = new Size(73, 28),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        Shown += (_, _) => _passwordTextBox.Focus();
    }
}
