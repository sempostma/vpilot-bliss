using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using System.Windows.Forms;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;
using VpilotBliss;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    internal sealed class FakeHost : Form
    {
        public readonly TabControl Tabs = new TabControl { Name = "TabsMain" };
        public FakeHost() { Controls.Add(Tabs); }
        public void SimulateClose() => OnFormClosed(new FormClosedEventArgs(CloseReason.UserClosing));
        private TabPage b(string name)
        {
            var page = new FakeChatPage { Name = name, Text = name };
            page.Controls.Add(new RichTextBox { Size = new System.Drawing.Size(440, 600), BackColor = System.Drawing.Color.Black, ForeColor = System.Drawing.Color.Black });
            page.Controls.Add(new OutboundInput());
            page.HookAtis();
            Tabs.TabPages.Add(page);
            return page;
        }
    }
    private sealed class FakeChatPage : TabPage
    {
        public int AtisRequests;
        public void HookAtis() => Controls.OfType<RichTextBox>().Single().MouseDoubleClick += a;
        private void a(object sender, MouseEventArgs e) => AtisRequests++;
    }
    private sealed class OutboundInput : TextBox
    {
        public int Transmissions;
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) Transmissions++;
            base.OnKeyDown(e);
        }
        public void Recreate() => RecreateHandle();
    }
    private sealed class Session : IAudioSession
    {
        public string Id { get; set; }
        private bool muted;
        public bool Fail;
        public bool Muted { get => muted; set { if (Fail) throw new InvalidOperationException("Device unavailable"); muted = value; } }
    }
    private sealed class Broker : RealProxy
    {
        public readonly Dictionary<string, Delegate> Events = new Dictionary<string, Delegate>();
        public Broker() : base(typeof(IBroker)) { }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            lock (Events)
            {
                bool adding = call.MethodName.StartsWith("add_");
                string key = call.MethodName.Substring(adding ? 4 : 7);
                Delegate current;
                Events.TryGetValue(key, out current);
                Events[key] = adding ? Delegate.Combine(current, (Delegate)call.Args[0]) : Delegate.Remove(current, (Delegate)call.Args[0]);
            }
            return new ReturnMessage(null, null, 0, call.LogicalCallContext, call);
        }
        public void Raise(string name, EventArgs args)
        {
            Delegate handler;
            lock (Events) Events.TryGetValue(name, out handler);
            handler?.DynamicInvoke(this, args);
        }
    }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var a = new Session { Id = "voice" };
            var b = new Session { Id = "already-muted", Muted = true };
            var controller = new SilenceController();
            controller.Silence(new[] { a, b });
            Check(a.Muted && b.Muted, "silence mutes sessions");
            var c = new Session { Id = "new-device" };
            controller.Refresh(new[] { a, b, c });
            controller.Restore(new[] { a, b, c }, false);
            Check(!a.Muted && b.Muted && !c.Muted, "manual restore preserves initial states including new sessions");
            controller.Silence(new[] { a, b });
            controller.Refresh(new[] { a, b });
            controller.Restore(new[] { a, b }, true);
            Check(!a.Muted && !b.Muted && !controller.IsSilenced, "SELCAL unmutes tracked sessions without overwriting saved states");
            controller.Silence(new[] { a });
            a.Fail = true;
            try { controller.Restore(new[] { a }, true); } catch (AggregateException) { }
            Check(!controller.IsSilenced && controller.TrackedCount == 1, "failed restore stays pending and stops remuting");
            a.Fail = false;
            controller.Restore(new[] { a }, true);

            var untracked = new Session { Id = "manually-muted", Muted = true };
            controller.Restore(new[] { untracked }, true);
            Check(!untracked.Muted, "player alert also unmutes sessions muted outside Bliss");
            Check(Plugin.MentionsCallsign("KLM123, contact me on 123.45", "KLM123"), "contact-me callsign matched");
            Check(Plugin.MentionsCallsign("klm 123: please reply", "KLM123"), "spaced and case-insensitive callsign matched");
            Check(!Plugin.MentionsCallsign("KLM1234 contact me", "KLM123"), "another aircraft's longer callsign is not a mention");
            Check(!Plugin.MentionsCallsign("Other pilots chatting", "KLM123"), "unrelated radio chat does not wake audio");

            controller.Silence(new[] { a, b });
            a.Fail = true;
            try { controller.Restore(new[] { a, b }, true); } catch (AggregateException) { }
            Check(!b.Muted && controller.TrackedCount == 1, "one broken session does not block restoring the others");
            a.Fail = false;
            controller.Restore(new[] { a }, true);
            Check(!a.Muted && controller.TrackedCount == 0, "failed restore can recover");
            controller.Silence(new[] { a });
            controller.Stop();
            controller.Refresh(new[] { c });
            Check(!c.Muted, "enumeration failure after SELCAL cannot resume silence");
            controller.Restore(new[] { a }, true);

            var settings = new Settings { Token = "test-secret", Enabled = true };
            Check(settings.Token == "test-secret" && settings.ProtectedToken != "test-secret", "token protected with Windows user encryption");
            string path = Path.GetTempFileName();
            try { settings.Save(path); Check(Settings.Load(path).Token == "test-secret", "settings round trip"); }
            finally { File.Delete(path); }
            settings.Topic = "bad/topic";
            try { settings.Endpoint(); throw new Exception("Invalid topic accepted"); }
            catch (InvalidOperationException) { Console.WriteLine("PASS topic validation"); }

            using (var host = new FakeHost())
            {
                var commands = new List<string>();
                try { LocalChat.TryAttach(host, commands.Add); throw new Exception("Unsupported host accepted"); }
                catch (NotSupportedException) { Console.WriteLine("PASS unsupported host rejected before creating chat"); }
                using (var chat = LocalChat.TryAttach(host, commands.Add, false))
                {
                    var input = host.Tabs.TabPages[0].Controls.OfType<OutboundInput>().Single();
                    var localPage = (FakeChatPage)host.Tabs.TabPages[0];
                    var localHistory = localPage.Controls.OfType<RichTextBox>().Single();
                    typeof(Control).GetMethod("OnMouseDoubleClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(localHistory, new object[] { new MouseEventArgs(MouseButtons.Left, 2, 10, 10, 0) });
                    Check(localPage.AtisRequests == 0, "double-click in Bliss cannot request ATIS or send network commands");
                    foreach (string text in new[] { "sleep", "wake", "unknown", ".sleep" })
                    {
                        input.Text = text;
                        SendMessage(input.Handle, 0x100, (IntPtr)13, IntPtr.Zero);
                    }
                    Check(commands.SequenceEqual(new[] { "sleep", "wake", "unknown", ".sleep" }), "local chat receives commands");
                    Check(input.Transmissions == 0 && input.Text == "", "all commands consumed before outbound handler");
                    input.Recreate();
                    input.Text = "status";
                    SendMessage(input.Handle, 0x100, (IntPtr)13, IntPtr.Zero);
                    Check(commands.Last() == "status" && input.Transmissions == 0, "interceptor survives handle recreation");
                    chat.Write("Welcome");
                    Check(host.Tabs.TabPages[0].Controls.OfType<RichTextBox>().Single().Text.Contains("Bliss: Welcome"), "welcome displayed locally");
                    var darkHistory = host.Tabs.TabPages[0].Controls.OfType<RichTextBox>().Single();
                    darkHistory.Select(0, 1);
                    Check(darkHistory.SelectionColor.ToArgb() == System.Drawing.Color.FromArgb(224, 224, 224).ToArgb(), "welcome text is readable on vPilot's black chat background");
                    var phone = new Settings { Topic = "vpilot-test" };
                    Check(phone.Endpoint().AbsoluteUri == "https://ntfy.sh/vpilot-test", "phone link targets configured topic");
                    string qrRtf = PhoneSetup.QrRtf(phone.Endpoint().AbsoluteUri);
                    Check(qrRtf.Contains(@"\highlight1\~\~") && qrRtf.Contains(@"\highlight2\~\~"), "QR uses solid black and white background cells");
                    chat.WriteQr(phone.Endpoint().AbsoluteUri);
                    var transcript = host.Tabs.TabPages[0].Controls.OfType<RichTextBox>().Single();
                    Check(transcript.Text.Contains("\u00a0") && transcript.Text.Contains("Bliss: Welcome"), "QR cells appear as text without replacing chat history");
                    int qrStart = transcript.Text.IndexOf('\u00a0');
                    int rowTwo = transcript.Text.IndexOf('\n', qrStart) + 1;
                    var origin = transcript.GetPositionFromCharIndex(qrStart);
                    int cellWidth = transcript.GetPositionFromCharIndex(qrStart + 2).X - origin.X;
                    int cellHeight = transcript.GetPositionFromCharIndex(rowTwo).Y - origin.Y;
                    Check(Math.Abs(cellWidth - cellHeight) <= 1, "QR cells are square in the destination RichTextBox screen layout");
                }
                Check(host.Tabs.TabPages.Count == 0, "shutdown removes tab and input together");
            }
            LifecycleChecks();
            NotificationCancellationCheck.Run();
            TopicMirrorCheck.Run();
            NetworkFailureCheck.Run();
            SessionConversationCheck.Run();
            RadioActivityCheck.Run();
            var resolve = typeof(Plugin).GetMethod("Resolve", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => (System.Reflection.Assembly)resolve.Invoke(null, new object[] { s, e });
            new ProcessAudio().Visit(sessions => Check(sessions != null, "native audio enumeration (read only, test process only)"));
            QrRenderCheck.Run();
            SelcalSoundCheck.Run();
            Console.WriteLine("All tests passed. No windows shown, audio changed or phone notifications sent.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void LifecycleChecks()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bliss-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var priorContext = SynchronizationContext.Current;
        using (var context = new WindowsFormsSynchronizationContext())
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var broker = new Broker();
                var session = new Session { Id = "test" };
                int alerts = 0;
                var plugin = new Plugin(() => alerts++, action => action(new[] { session }), Path.Combine(directory, "settings.xml"));
                plugin.Initialize((IBroker)broker.GetTransparentProxy());
                plugin.Command("sleep");
                Check(session.Muted, "lifecycle test silences fake audio only");
                broker.Raise("SelcalAlertReceived", new SelcalAlertReceivedEventArgs(new[] { 23000 }, "TEST"));
                broker.Raise("SessionEnded", EventArgs.Empty);
                plugin.Dispose();
                Application.DoEvents();
                plugin.Command("sleep");
                Check(!session.Muted && alerts == 0, "session close restores audio and ignores queued events and commands");
                Check(broker.Events.Values.All(handler => handler == null), "shutdown unsubscribes every SDK event and is idempotent");

                using (var host = new FakeHost())
                {
                    var closingBroker = new Broker();
                    var closingPlugin = new Plugin(() => { }, action => action(new[] { session }), Path.Combine(directory, "settings.xml"));
                    closingPlugin.Initialize((IBroker)closingBroker.GetTransparentProxy());
                    closingPlugin.AttachHost(host);
                    closingPlugin.Command("sleep");
                    host.SimulateClose();
                    Check(!session.Muted && closingBroker.Events.Values.All(handler => handler == null), "vPilot form close restores audio and removes subscriptions");
                }

                var failingBroker = new Broker();
                var failingPlugin = new Plugin(() => { }, action => action(new[] { session }), Path.Combine(directory, "settings.xml"));
                failingPlugin.Initialize((IBroker)failingBroker.GetTransparentProxy());
                failingPlugin.Command("sleep");
                session.Fail = true;
                failingPlugin.Dispose();
                Check(failingBroker.Events.Values.All(handler => handler == null), "audio restoration error does not prevent remaining shutdown cleanup");
                session.Fail = false;
                session.Muted = false;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(priorContext);
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}




