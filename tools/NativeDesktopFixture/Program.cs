using System.Windows.Forms;

ApplicationConfiguration.Initialize();
Application.Run(new Fixture());

sealed class Fixture : Form
{
    public Fixture()
    {
        Text = "PinyinTranslate Native Synthetic Fixture";
        Width = 880; Height = 510;
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20), WrapContents = false };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Independent synthetic input test — no user documents", AutoSize = true });
        layout.Controls.Add(new Label { Text = "Standard native TextBox", AutoSize = true });
        var plain = new TextBox { Name = "NativePlainText", AccessibleName = "NativePlainText", Width = 800, Text = "PREFIX | SUFFIX" };
        layout.Controls.Add(plain);
        layout.Controls.Add(new Label { Text = "Native RichTextBox (plain synthetic contents)", AutoSize = true });
        var rich = new RichTextBox { Name = "NativeRichText", AccessibleName = "NativeRichText", Width = 800, Height = 150, Text = "PREFIX | SUFFIX", DetectUrls = false };
        layout.Controls.Add(rich);
        var reset = new Button { Text = "Reset plain and focus middle", AutoSize = true };
        reset.Click += (_, _) => { plain.Text = "PREFIX | SUFFIX"; plain.Focus(); plain.SelectionStart = 7; plain.SelectionLength = 0; };
        layout.Controls.Add(reset);
        var resetRich = new Button { Text = "Reset rich and focus middle", AutoSize = true };
        resetRich.Click += (_, _) => { rich.Text = "PREFIX | SUFFIX"; rich.Focus(); rich.SelectionStart = 7; rich.SelectionLength = 0; };
        layout.Controls.Add(resetRich);
        layout.Controls.Add(new Label { Text = "Password exclusion test", AutoSize = true });
        layout.Controls.Add(new TextBox { Name = "PasswordFixture", AccessibleName = "PasswordFixture", Width = 300, UseSystemPasswordChar = true });
        Shown += (_, _) => { plain.Focus(); plain.SelectionStart = 7; plain.SelectionLength = 0; };
    }
}
