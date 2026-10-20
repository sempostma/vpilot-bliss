using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace VpilotBliss
{
    // Unsupported integration, verified against vPilot 3.12.1. No network messaging methods are used.
    internal sealed class LocalChat : IDisposable
    {
        private readonly TabPage page;
        private readonly RichTextBox history;
        private readonly TextBox input;
        private readonly InputWindow window;
        private bool disposed;
        public bool IsDisposed => disposed || page.IsDisposed || page.Parent == null;
        public bool IsSelected => !IsDisposed && (page.Parent as TabControl)?.SelectedTab == page;
        internal const string WelcomeText = "BLISS\nMutes vPilot until SELCAL calls.\n\nsleep - Mute audio\nwake - Restore audio\nhelp - All commands\n\nType a command below, then press Enter.";
        internal const string HelpText = "BLISS COMMANDS\nsleep - Mute until SELCAL\nwake - Restore audio\nstatus - Check audio state\ntest - Send a test alert\nhelp - Show all commands\n\nDEBUG COMMANDS\ndebug / debug help - Show debug commands\ndebug sound selcal - Play SELCAL cue\ndebug sound wake - Play wake cue\ndebug selcal - Simulate SELCAL\ndebug private - Simulate private message\ndebug contact - Simulate contact-me request\ndebug mention - Simulate radio mention\ndebug broadcast - Simulate broadcast\nSimulated alerts wake audio only when sleeping; sound previews always play.";

        private LocalChat(TabPage page, RichTextBox history, TextBox input, Action<string> command)
        {
            this.page = page;
            this.history = history;
            this.input = input;
            // The native private-chat tab requests ATIS for its recipient on double-click.
            // Bliss is local, so remove that handler while preserving normal text selection.
            var atisHandler = page.GetType().GetMethod("a", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(object), typeof(MouseEventArgs) }, null);
            if (atisHandler != null)
                history.MouseDoubleClick -= (MouseEventHandler)Delegate.CreateDelegate(typeof(MouseEventHandler), page, atisHandler);
            window = new InputWindow(input, command);
            window.AssignHandle(input.Handle);
            input.HandleCreated += HandleCreated;
            input.HandleDestroyed += HandleDestroyed;
        }

        internal static LocalChat TryAttach(Form form, Action<string> command, bool verifyHost = true)
        {
            var tabs = form.Controls.Find("TabsMain", true).OfType<TabControl>().SingleOrDefault();
            if (tabs == null) return null;
            if (verifyHost)
            {
                var assembly = form.GetType().Assembly;
                if (assembly.GetName().Name != "vPilot" || System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion != "3.12.1.0")
                    throw new NotSupportedException("This integration supports vPilot 3.12.1 only.");
            }
            var factory = form.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .SingleOrDefault(m => m.Name == "b" && typeof(TabPage).IsAssignableFrom(m.ReturnType)
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (factory == null) throw new NotSupportedException("Chat factory unavailable.");
            var page = (TabPage)factory.Invoke(form, new object[] { "Bliss" });
            try
            {
                var input = Descendants(page).OfType<TextBox>().Single();
                var history = Descendants(page).OfType<RichTextBox>().Single();
                return new LocalChat(page, history, input, command);
            }
            catch { tabs.TabPages.Remove(page); page.Dispose(); throw; }
        }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        public void Write(string text, System.Drawing.Color? color = null)
        {
            if (history.IsDisposed) return;
            history.SelectionStart = history.TextLength;
            history.SelectionLength = 0;
            history.SelectionColor = color ?? System.Drawing.Color.FromArgb(224, 224, 224);
            history.SelectionBackColor = history.BackColor;
            history.SelectionFont = history.Font;
            history.AppendText("Bliss: " + text + Environment.NewLine);
            history.SelectionStart = history.TextLength;
            history.ScrollToCaret();
        }
        public void WriteWelcome()
        {
            if (history.IsDisposed) return;
            Styled("BLISS\n", System.Drawing.Color.LightSkyBlue, System.Drawing.FontStyle.Bold, 13);
            Styled("Mutes vPilot until SELCAL calls.\n\n", System.Drawing.Color.Silver);
            CommandRow("sleep", "Mute audio", System.Drawing.Color.Khaki);
            CommandRow("wake", "Restore audio", System.Drawing.Color.PaleGreen);
            CommandRow("help", "All commands", System.Drawing.Color.LightSkyBlue);
            Styled("\nType a command below, then press Enter.\n", System.Drawing.Color.Silver, System.Drawing.FontStyle.Italic);
            ResetStyle();
        }
        public void WriteHelp()
        {
            if (history.IsDisposed) return;
            Styled("\nBLISS COMMANDS\n", System.Drawing.Color.LightSkyBlue, System.Drawing.FontStyle.Bold, 11);
            CommandRow("sleep", "Mute until SELCAL", System.Drawing.Color.Khaki);
            CommandRow("wake", "Restore audio", System.Drawing.Color.PaleGreen);
            CommandRow("status", "Check audio state", System.Drawing.Color.LightSkyBlue);
            CommandRow("test", "Send a test alert", System.Drawing.Color.LightSkyBlue);
            CommandRow("help", "Show all commands", System.Drawing.Color.LightSkyBlue);
            Styled("\nDEBUG COMMANDS\n", System.Drawing.Color.Khaki, System.Drawing.FontStyle.Bold, 11);
            CommandRow("debug / debug help", "Show debug commands", System.Drawing.Color.Khaki);
            CommandRow("debug sound selcal", "Play SELCAL cue", System.Drawing.Color.Khaki);
            CommandRow("debug sound wake", "Play wake cue", System.Drawing.Color.Khaki);
            CommandRow("debug selcal", "Simulate SELCAL", System.Drawing.Color.Khaki);
            CommandRow("debug private", "Simulate private message", System.Drawing.Color.Khaki);
            CommandRow("debug contact", "Simulate contact-me request", System.Drawing.Color.Khaki);
            CommandRow("debug mention", "Simulate radio mention", System.Drawing.Color.Khaki);
            CommandRow("debug broadcast", "Simulate broadcast", System.Drawing.Color.Khaki);
            Styled("\nSimulated alerts wake audio only when sleeping.\nSound previews always play.\n", System.Drawing.Color.Silver, System.Drawing.FontStyle.Italic);
            Styled("\nCommands work here and from your ntfy topic.\n", System.Drawing.Color.Silver, System.Drawing.FontStyle.Italic);
            ResetStyle();
        }
        private void CommandRow(string command, string description, System.Drawing.Color color)
        {
            history.Select(history.TextLength, 0);
            history.SelectionTabs = new[] { 115 };
            Styled(command + "\t", color, System.Drawing.FontStyle.Bold);
            Styled(description + "\n", System.Drawing.Color.Gainsboro);
        }
        private void Styled(string text, System.Drawing.Color color, System.Drawing.FontStyle style = System.Drawing.FontStyle.Regular, float? size = null)
        {
            history.Select(history.TextLength, 0);
            history.SelectionColor = color;
            history.SelectionBackColor = history.BackColor;
            using (var font = new System.Drawing.Font(history.Font.FontFamily, size ?? history.Font.Size, style))
            {
                history.SelectionFont = font;
                history.SelectedText = text;
            }
        }
        private void ResetStyle()
        {
            history.Select(history.TextLength, 0);
            history.SelectionFont = history.Font;
            history.SelectionColor = System.Drawing.Color.Gainsboro;
            history.SelectionBackColor = history.BackColor;
            history.ScrollToCaret();
        }
        public void WriteQr(string link)
        {
            if (history.IsDisposed) return;
            history.SelectionStart = history.TextLength;
            history.SelectionLength = 0;
            int start = history.TextLength;
            history.SelectedRtf = PhoneSetup.QrRtf(link);
            int length = history.TextLength - start;
            // Calibrate on the destination control: its DPI/zoom and RTF font rounding
            // can differ from a separate measurement control or the print renderer.
            int width = history.GetPositionFromCharIndex(start + 2).X - history.GetPositionFromCharIndex(start).X;
            if (width > 0)
            {
                using (var graphics = history.CreateGraphics())
                {
                    int height = (int)Math.Round(width * 1440 / graphics.DpiY / history.ZoomFactor);
                    history.Select(start, length);
                    history.SelectedRtf = PhoneSetup.QrRtf(link, height);
                }
            }
            ResetStyle();
            history.AppendText(Environment.NewLine);
            history.ScrollToCaret();
        }
        private void HandleCreated(object sender, EventArgs e) => window.AssignHandle(input.Handle);
        private void HandleDestroyed(object sender, EventArgs e) => window.ReleaseHandle();
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            input.HandleCreated -= HandleCreated;
            input.HandleDestroyed -= HandleDestroyed;
            // Dispose the owned tab before releasing the interceptor: never leave an active unguarded input.
            try
            {
                if (!page.IsDisposed) { (page.Parent as TabControl)?.TabPages.Remove(page); page.Dispose(); }
            }
            finally { window.ReleaseHandle(); }
        }

        internal sealed class InputWindow : NativeWindow
        {
            private readonly TextBox input;
            private readonly Action<string> command;
            public InputWindow(TextBox input, Action<string> command) { this.input = input; this.command = command; }
            protected override void WndProc(ref Message message)
            {
                // Consume every Enter in this tab, including unknown commands, before vPilot's input handler runs.
                if ((message.Msg == 0x100 || message.Msg == 0x104) && message.WParam.ToInt32() == 13)
                {
                    string text = input.Text;
                    input.Clear();
                    if (!string.IsNullOrWhiteSpace(text))
                        try { command(text); }
                        catch (Exception) { /* Never fall through to the original network message handler. */ }
                    message.Result = IntPtr.Zero;
                    return;
                }
                if (message.Msg == 0x102 && message.WParam.ToInt32() == 13) return;
                base.WndProc(ref message);
            }
        }
    }
}

