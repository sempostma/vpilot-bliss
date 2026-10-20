using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Bliss.Tests")]

namespace VpilotBliss
{
    public sealed class Plugin : IPlugin, IDisposable
    {
        private IBroker broker;
        private SynchronizationContext ui;
        private System.Windows.Forms.Timer timer;
        private LocalChat chat;
        private TopicMirror mirror;
        private RadioActivity radioActivity;
        private int mirrorGeneration;
        private readonly SilenceController silence = new SilenceController();
        private readonly Action<Action<IEnumerable<IAudioSession>>> visitAudio;
        private readonly Notifications notifications;
        private bool sessionActive;
        private string originTag;
        private string sessionSince;
        private string callsign;
        private readonly SelcalSound selcalSound;
        private readonly HashSet<string> remoteIds = new HashSet<string>();
        private readonly Queue<string> remoteOrder = new Queue<string>();
        private static readonly HashSet<string> RemoteCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "sleep", "wake", "status", "test", "help", "debug", "debug help", "debug sound selcal", "debug sound wake",
                "debug selcal", "debug private", "debug contact", "debug mention", "debug broadcast" };
        private readonly Action playAlert;
        private readonly Action playWakeAlert;
        private Settings settings;
        private volatile bool stopping;
        private int disposeStarted;
        private int uiThreadId;
        private Form host;
        private bool restoreAsSelcal;
        private bool audioFailed;
        private bool integrationErrorLogged;
        private readonly string settingsPath;
        public Plugin() : this(null) { }
        internal Plugin(Action playAlert, Action<Action<IEnumerable<IAudioSession>>> visitAudio = null, string settingsPath = null, Notifications notifications = null, Action playWakeAlert = null)
        {
            if (playAlert == null) selcalSound = new SelcalSound();
            this.playAlert = playAlert ?? (() => selcalSound.Play());
            this.playWakeAlert = playWakeAlert ?? (selcalSound != null ? (Action)(() => selcalSound.PlayWake()) : this.playAlert);
            this.notifications = notifications ?? new Notifications();
            this.visitAudio = visitAudio ?? new ProcessAudio().Visit;
            this.settingsPath = settingsPath ?? Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "bliss-settings.xml");
        }
        public string Name => "vPilot Bliss";

        public void Initialize(IBroker broker)
        {
            this.broker = broker;
            uiThreadId = Thread.CurrentThread.ManagedThreadId;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            ui = SynchronizationContext.Current;
            if (!(ui is WindowsFormsSynchronizationContext))
            {
                Log("Bliss needs the vPilot UI context; plugin disabled.");
                Dispose();
                return;
            }
            try
            {
                settings = Settings.Load(settingsPath);
                if (!File.Exists(settingsPath)) settings.Save(settingsPath);
            }
            catch (Exception) { settings = new Settings(); Log("Cannot load notification settings; notifications disabled."); }
            broker.SelcalAlertReceived += Selcal;
            broker.NetworkDisconnected += Disconnected;
            broker.PrivateMessageReceived += PrivateMessage;
            broker.RadioMessageReceived += RadioMessage;
            broker.BroadcastMessageReceived += BroadcastMessage;
            broker.NetworkConnected += Connected;
            broker.SessionEnded += Ended;
            try { radioActivity = RadioActivity.Attach(broker, reason => Post(() => WakeForActivity(reason))); }
            catch (Exception ex) { Log("Radio activity monitoring unavailable: " + ex.Message); }
            Application.ApplicationExit += Ended;
            timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += Tick;
            timer.Start();
            Tick(null, EventArgs.Empty);
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            if (name != "NAudio.Core" && name != "NAudio.Wasapi" && name != "QRCoder") return null;
            string file = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Bliss.lib", name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        }

        private void Tick(object sender, EventArgs e)
        {
            if (stopping) return;
            if (chat == null || chat.IsDisposed)
            {
                EndSession();
                if (silence.IsSilenced) Restore(false);
                if (host != null) host.FormClosed -= Ended;
                host = null;
                chat?.Dispose();
                chat = null;
                try
                {
                    foreach (Form form in Application.OpenForms)
                    {
                        chat = LocalChat.TryAttach(form, LocalCommand);
                        if (chat == null) continue;
                        AttachHost(form);
                        integrationErrorLogged = false;
                        break;
                    }
                }
                catch (Exception)
                {
                    if (!integrationErrorLogged) Log("Bliss chat integration unavailable. Expected vPilot 3.12.1 UI; no audio was silenced.");
                    integrationErrorLogged = true;
                }
            }
            CheckSession();
            TryAudio(() =>
            {
                if (silence.IsSilenced) visitAudio(silence.Refresh);
                else if (silence.TrackedCount > 0) visitAudio(s => silence.Restore(s, restoreAsSelcal));
            });
        }

        internal void CheckSession()
        {
            if (!stopping && !sessionActive && chat != null && chat.IsSelected) StartSession();
        }

        private void StartSession()
        {
            settings.Topic = "vpilot-" + Guid.NewGuid().ToString("N");
            settings.Enabled = true;
            originTag = "bliss-" + Guid.NewGuid().ToString("N");
            sessionSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            sessionActive = true;
            try { settings.Save(settingsPath); }
            catch (Exception) { Log("Cannot save session settings."); }
            UpdateMirror();
            chat.WriteWelcome();
            Publish("Bliss", LocalChat.WelcomeText);
            ShowSessionSetup();
        }

        private void EndSession()
        {
            sessionActive = false;
            remoteIds.Clear();
            remoteOrder.Clear();
            mirrorGeneration++;
            mirror?.Dispose();
            mirror = null;
        }

        private void LocalCommand(string text)
        {
            WriteLocal("You: " + text, Color.Silver);
            Publish("You", text);
            Command(text);
        }

        internal void Command(string text)
        {
            if (stopping) return;
            string command = text.Trim().ToLowerInvariant();
            switch (command)
            {
                case "sleep":
                    TryAudio(() => { restoreAsSelcal = false; visitAudio(silence.Silence); Say("Silenced until SELCAL. Type wake to unmute.", Color.Khaki); });
                    if (radioActivity?.PttDown == true) WakeForActivity("Push-to-talk is already held while sleep was enabled.");
                    break;
                case "wake": Restore(false); break;
                case "status": Say(silence.IsSilenced ? "Silenced - waiting for SELCAL." : "Listening.", silence.IsSilenced ? Color.Khaki : Color.PaleGreen); break;
                case "test": Notify("vPilot Bliss test", "Notifications are working.", true); break;
                case "help": chat?.WriteHelp(); Publish("Bliss", LocalChat.HelpText); break;
                case "debug":
                case "debug help":
                    Say("Debug commands:\ndebug sound selcal - Play SELCAL cue\ndebug sound wake - Play other-alert cue\ndebug selcal\ndebug private\ndebug contact\ndebug mention\ndebug broadcast\nSimulated alerts unmute audio and are mirrored to ntfy.");
                    break;
                case "debug sound selcal": PlayCue(true); break;
                case "debug sound wake": PlayCue(false); break;
                case "debug selcal": PlayerAlert("SELCAL", "DEBUG", "Simulated SELCAL alert"); break;
                case "debug private": PlayerAlert("Private message", "DEBUG", "Simulated private message"); break;
                case "debug contact": PlayerAlert("Private message", "DEBUG", "Simulated contact-me request: contact me on 123.450"); break;
                case "debug mention": PlayerAlert("Radio message", "DEBUG", (callsign ?? "PLAYER") + ", simulated callsign mention"); break;
                case "debug broadcast": PlayerAlert("Network broadcast", "DEBUG", "Simulated network announcement"); break;
                default: Say("Unknown command. Type help. Nothing was sent to VATSIM."); break;
            }
        }

        private void ShowSessionSetup()
        {
            try
            {
                Say("Scan with your phone. " + settings.Endpoint().AbsoluteUri, Color.LightSkyBlue);
                chat?.WriteQr(settings.Endpoint().AbsoluteUri);
                Say("Keep this QR private. Type test to check notifications.", Color.Khaki);
            }
            catch (Exception) { Say("Could not create setup QR codes. Check bliss-settings.xml; use the displayed server and topic manually."); }
        }

        private void TryAudio(Action action)
        {
            try { action(); audioFailed = false; }
            catch (Exception) { if (!audioFailed) Say("Audio operation failed; retrying while Bliss is running."); audioFailed = true; }
        }
        internal void AttachHost(Form form)
        {
            if (host != null) host.FormClosed -= Ended;
            host = form;
            host.FormClosed += Ended;
        }
        private void Restore(bool selcal)
        {
            restoreAsSelcal = selcal;
            silence.Stop();
            TryAudio(() => { visitAudio(s => silence.Restore(s, selcal)); Say("Listening.", Color.PaleGreen); });
        }
        private void Post(Action action)
        {
            if (stopping) return;
            try
            {
                ui.Post(_ =>
                {
                    if (stopping) return;
                    try { action(); }
                    catch (Exception ex) { Log("Background callback failed: " + ex.Message); }
                }, null);
            }
            catch (System.ComponentModel.InvalidAsynchronousStateException) { }
            catch (InvalidOperationException) { }
        }
        private void Connected(object sender, NetworkConnectedEventArgs e) => Post(() => { callsign = e.Callsign; });
        private void Selcal(object sender, SelcalAlertReceivedEventArgs e) => Post(() => PlayerAlert("SELCAL", e.From, "SELCAL alert"));
        private void Disconnected(object sender, EventArgs e) => Post(() => Restore(false));
        private void PrivateMessage(object sender, PrivateMessageReceivedEventArgs e) => Post(() =>
        { PlayerAlert("Private message", e.From, e.Message); });
        private void RadioMessage(object sender, RadioMessageReceivedEventArgs e) => Post(() =>
        { if (MentionsCallsign(e.Message, callsign)) PlayerAlert("Radio message", e.From, e.Message); });
        private void BroadcastMessage(object sender, BroadcastMessageReceivedEventArgs e) => Post(() =>
        { PlayerAlert("Network broadcast", e.From, e.Message); });

        internal static bool MentionsCallsign(string message, string playerCallsign)
        {
            if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(playerCallsign)) return false;
            string normalized = new string(playerCallsign.Where(char.IsLetterOrDigit).ToArray());
            if (normalized.Length == 0) return false;
            string pattern = @"(?<![A-Za-z0-9])" + string.Join(@"[\s-]*", normalized.Select(c => Regex.Escape(c.ToString()))) + @"(?![A-Za-z0-9])";
            return Regex.IsMatch(message, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }

        private void PlayerAlert(string kind, string from, string message)
        {
            if (!sessionActive || !silence.IsSilenced)
            {
                Notify("vPilot " + kind, from + ": " + message);
                return;
            }
            restoreAsSelcal = true;
            silence.Stop();
            try
            {
                visitAudio(s => silence.Restore(s, true));
                audioFailed = false;
                Notify("vPilot " + kind, from + ": " + message + "\nvPilot has been unmuted.", true);
            }
            catch (Exception)
            {
                Notify("vPilot " + kind, from + ": " + message + "\nAudio could not be fully unmuted; retrying.", true);
            }
            PlayCue(kind == "SELCAL");
        }

        internal void WakeForActivity(string reason)
        {
            if (stopping || !sessionActive || !silence.IsSilenced) return;
            PlayerAlert("Sleep reminder", "Bliss", reason);
        }

        private void PlayCue(bool selcal)
        {
            try { (selcal ? playAlert : playWakeAlert)(); }
            catch (Exception ex)
            {
                Log("Alert sound failed: " + ex.Message);
                WriteLocal("Alert sound unavailable.", Color.Khaki);
            }
        }
        private void Ended(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
            stopping = true;
            if (ui == null || Thread.CurrentThread.ManagedThreadId == uiThreadId)
            {
                Shutdown(true);
                return;
            }
            try { ui.Send(_ => Shutdown(true), null); }
            catch (System.ComponentModel.InvalidAsynchronousStateException) { Shutdown(false); }
            catch (InvalidOperationException) { Shutdown(false); }
        }

        private void Shutdown(bool onUiThread)
        {
            // One failed resource must not prevent the rest from being released.
            Cleanup(() => { timer?.Stop(); timer?.Dispose(); timer = null; });
            Cleanup(() =>
            {
                silence.Stop();
                if (silence.TrackedCount > 0) visitAudio(s => silence.Restore(s, false));
                if (silence.TrackedCount > 0) Log("Some audio sessions were unavailable during shutdown; check Windows Volume Mixer.");
            });
            Cleanup(EndSession);
            Cleanup(() => { radioActivity?.Dispose(); radioActivity = null; });
            Cleanup(notifications.Dispose);
            Cleanup(() => selcalSound?.Dispose());
            Cleanup(() =>
            {
                if (host != null) host.FormClosed -= Ended;
                if (onUiThread) chat?.Dispose();
                chat = null;
                host = null;
            });
            Cleanup(() =>
            {
                if (broker == null) return;
                broker.SelcalAlertReceived -= Selcal;
                broker.NetworkDisconnected -= Disconnected;
                broker.PrivateMessageReceived -= PrivateMessage;
                broker.RadioMessageReceived -= RadioMessage;
                broker.BroadcastMessageReceived -= BroadcastMessage;
                broker.NetworkConnected -= Connected;
                broker.SessionEnded -= Ended;
                broker = null;
            });
            Application.ApplicationExit -= Ended;
            AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
        }

        private void Cleanup(Action release)
        {
            try { release(); }
            catch (Exception ex) { Log("Shutdown cleanup failed: " + ex.Message); }
        }
        private void WriteLocal(string message, Color? color = null) { if (chat != null && !chat.IsDisposed) chat.Write(message, color); else Log(message); }
        private void Say(string message, Color? color = null)
        {
            WriteLocal(message, color);
            Publish("Bliss", message);
        }
        private void Log(string message)
        {
            try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(settingsPath), "bliss.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private void UpdateMirror()
        {
            int generation = ++mirrorGeneration;
            mirror?.Dispose();
            mirror = null;
            if (stopping || !sessionActive || !settings.Enabled) return;
            mirror = new TopicMirror(notifications, settings,
                message => Post(() =>
                {
                    if (generation == mirrorGeneration) ReceiveTopicMessage(message);
                }),
                () => Post(() => { if (generation == mirrorGeneration) WriteLocal("ntfy mirror disconnected; retrying.", Color.Khaki); }),
                initialSince: sessionSince);
        }

        internal void ReceiveTopicMessage(TopicMessage message)
        {
            if (stopping || !sessionActive || !settings.Enabled || message.Tags?.Contains(originTag) == true) return;
            // Keep this across listener restarts so replay cannot rerun commands.
            if (string.IsNullOrEmpty(message.Id) || !remoteIds.Add(message.Id)) return;
            remoteOrder.Enqueue(message.Id);
            if (remoteOrder.Count > 2048) remoteIds.Remove(remoteOrder.Dequeue());
            WriteLocal("ntfy: " + message.DisplayText, Color.LightSkyBlue);
            string text = (message.Message ?? "").Trim();
            if (RemoteCommands.Contains(text)) Command(text);
        }

        private void Notify(string title, string body, bool urgent = false)
        {
            WriteLocal(title + " - " + body, Color.LightSkyBlue);
            Publish(title, body, urgent);
        }

        private async void Publish(string title, string body, bool urgent = false)
        {
            if (stopping || !sessionActive || !settings.Enabled) return;
            int generation = mirrorGeneration;
            // Do not resume on a UI context that may have been destroyed during cancellation.
            try
            {
                await notifications.Send(settings, title, body, urgent, originTag).ConfigureAwait(false);
            }
            catch (Exception) { Post(() => { if (generation == mirrorGeneration) WriteLocal("ntfy send failed. Check settings and connection.", Color.Khaki); }); }
        }
    }
}



