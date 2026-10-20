using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;
using VpilotBliss;

internal static class SessionConversationCheck
{
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly Queue<Action> callbacks = new Queue<Action>();
        public override void Post(SendOrPostCallback callback, object state)
        { lock (callbacks) callbacks.Enqueue(() => callback(state)); }
        public void Drain()
        {
            while (true)
            {
                Action callback;
                lock (callbacks) { if (callbacks.Count == 0) return; callback = callbacks.Dequeue(); }
                callback();
            }
        }
    }
    private sealed class Broker : RealProxy
    {
        public Broker() : base(typeof(IBroker)) { }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            return new ReturnMessage(null, null, 0, call.LogicalCallContext, call);
        }
    }
    private sealed class Session : IAudioSession
    {
        public string Id => "fake";
        public bool Muted { get; set; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new List<string>();
        public string Tag;
        public int Requests;
        public bool FailPublish;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            if (request.Method == HttpMethod.Post)
            {
                if (FailPublish) throw new HttpRequestException("Simulated connection failure while publishing");
                string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                lock (Bodies) Bodies.Add(body);
                Tag = string.Join(",", request.Headers.GetValues("Tags")).Split(',').Last();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        }
    }
    public static void Run()
    {
        var previous = SynchronizationContext.Current;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bliss-session-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        using (var context = new WindowsFormsSynchronizationContext())
        using (var host = new Program.FakeHost())
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var handler = new Handler();
            var session = new Session();
            int selcalSounds = 0, wakeSounds = 0;
            var plugin = new Plugin(() => selcalSounds++, action => action(new[] { session }), System.IO.Path.Combine(directory, "settings.xml"), new Notifications(handler), () => wakeSounds++);
            try
            {
                plugin.Initialize((IBroker)new Broker().GetTransparentProxy());
                var pump = new PumpContext();
                typeof(Plugin).GetField("ui", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(plugin, pump);
                host.Tabs.TabPages.Add(new TabPage("Messages"));
                host.Tabs.SelectedIndex = 0;
                using (var chat = LocalChat.TryAttach(host, plugin.Command, false))
                {
                    typeof(Plugin).GetField("chat", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(plugin, chat);
                    plugin.CheckSession();
                    if (handler.Requests != 0) throw new Exception("ntfy started before Bliss was selected.");
                    host.Tabs.SelectedIndex = 1;
                    plugin.CheckSession();
                    WaitForBody(handler, LocalChat.WelcomeText);
                    typeof(Plugin).GetMethod("Selcal", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(plugin,
                        new object[] { null, new SelcalAlertReceivedEventArgs(new[] { 23450 }, "TEST_CTR") });
                    pump.Drain();
                    if (selcalSounds != 0 || wakeSounds != 0) throw new Exception("Bliss sound played while already awake.");
                    plugin.ReceiveTopicMessage(new TopicMessage { Id = "sleep", Event = "message", Message = "sleep" });
                    if (!session.Muted) throw new Exception("Phone sleep command not executed.");
                    plugin.ReceiveTopicMessage(new TopicMessage { Id = "help", Event = "message", Message = "help" });
                    WaitForBody(handler, LocalChat.HelpText);
                    plugin.ReceiveTopicMessage(new TopicMessage { Id = "self", Event = "message", Message = "wake", Tags = new[] { handler.Tag } });
                    if (!session.Muted) throw new Exception("Self-echo executed as a remote command.");
                    plugin.ReceiveTopicMessage(new TopicMessage { Id = "wake", Event = "message", Message = "wake" });
                    if (session.Muted) throw new Exception("Phone wake command not executed.");
                    plugin.ReceiveTopicMessage(new TopicMessage { Id = "sleep", Event = "message", Message = "sleep" });
                    if (session.Muted) throw new Exception("Replayed remote command executed twice.");
                    handler.FailPublish = true;
                    plugin.Command("status");
                    var history = host.Tabs.TabPages[1].Controls.OfType<RichTextBox>().Single();
                    for (int i = 0; i < 200 && !history.Text.Contains("ntfy send failed"); i++)
                    {
                        pump.Drain();
                        Thread.Sleep(10);
                    }
                    if (!history.Text.Contains("ntfy send failed")) throw new Exception("Publishing failure was not reported safely.");
                    handler.FailPublish = false;
                    plugin.Command("status");
                    Console.WriteLine("PASS outgoing HTTP failure stays in Bliss and subsequent commands remain available");
                    typeof(Plugin).GetMethod("Connected", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(plugin,
                        new object[] { null, new NetworkConnectedEventArgs("123", "KLM123", "B738", "ABCD", false) });
                    pump.Drain();
                    foreach (var alert in new[]
                    {
                        new { Handler = "PrivateMessage", Args = (EventArgs)new PrivateMessageReceivedEventArgs("TEST_CTR", "Contact me on 123.450") },
                        new { Handler = "RadioMessage", Args = (EventArgs)new RadioMessageReceivedEventArgs(new[] { 23450 }, "TEST_CTR", "KLM123, please respond") },
                        new { Handler = "BroadcastMessage", Args = (EventArgs)new BroadcastMessageReceivedEventArgs("SUP", "Network announcement") },
                        new { Handler = "Selcal", Args = (EventArgs)new SelcalAlertReceivedEventArgs(new[] { 23450 }, "TEST_CTR") }
                    })
                    {
                        plugin.Command("sleep");
                        typeof(Plugin).GetMethod(alert.Handler, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(plugin, new object[] { null, alert.Args });
                        pump.Drain();
                        if (session.Muted) throw new Exception(alert.Handler + " did not unmute audio.");
                    }
                    if (!history.Text.Contains("vPilot has been unmuted.")) throw new Exception("Unmute confirmation missing from Bliss.");
                    if (selcalSounds != 1 || wakeSounds != 3) throw new Exception("SELCAL and other alerts did not use separate sound cues.");
                    Console.WriteLine("PASS private/contact-me, radio mentions, broadcasts and SELCAL unmute audio and confirm in Bliss");
                    plugin.Command("debug sound selcal");
                    plugin.Command("debug sound wake");
                    if (selcalSounds != 2 || wakeSounds != 4) throw new Exception("Debug sound commands did not select the correct cue.");
                    foreach (string debug in new[] { "debug selcal", "debug private", "debug contact", "debug mention", "debug broadcast" })
                    {
                        plugin.Command("sleep");
                        plugin.Command(debug);
                        if (session.Muted) throw new Exception("Debug simulation did not restore audio: " + debug);
                    }
                    if (selcalSounds != 3 || wakeSounds != 8 || LocalChat.WelcomeText.Contains("debug"))
                        throw new Exception("Debug simulations used wrong sound cues or leaked into the welcome.");
                    Console.WriteLine("PASS separate SELCAL/wake cues and hidden debug sound/alert commands");
                    plugin.WakeForActivity("Frequency changed");
                    if (wakeSounds != 8) throw new Exception("Activity cue played while already awake.");
                    plugin.Command("sleep");
                    plugin.WakeForActivity("PTT pressed");
                    plugin.WakeForActivity("Frequency changed");
                    if (session.Muted || wakeSounds != 9) throw new Exception("Radio activity must wake once and play exactly one reminder cue.");
                    Console.WriteLine("PASS automatic alert sounds are sleep-only and radio activity wakes with one reminder");
                    plugin.Dispose();
                    plugin.ReceiveTopicMessage(new TopicMessage { Id = "late", Event = "message", Message = "sleep" });
                    if (session.Muted) throw new Exception("Remote command executed after shutdown.");
                }
                Console.WriteLine("PASS opening Bliss starts the topic, mirrors welcome/help, executes phone commands and ignores own echoes");
            }
            finally
            {
                plugin.Dispose();
                SynchronizationContext.SetSynchronizationContext(previous);
                foreach (string file in System.IO.Directory.GetFiles(directory)) System.IO.File.Delete(file);
                System.IO.Directory.Delete(directory);
            }
        }
    }
    private static void WaitForBody(Handler handler, string body)
    {
        for (int i = 0; i < 200; i++)
        {
            lock (handler.Bodies) if (handler.Bodies.Contains(body)) return;
            Thread.Sleep(10);
        }
        throw new Exception("Expected mirrored message was not published: " + body);
    }
}
