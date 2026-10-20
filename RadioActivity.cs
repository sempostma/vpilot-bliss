using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace VpilotBliss
{
    // The public SDK omits pilot radio actions. Uses the same version-gated
    // internal integration as the chat; no simulator connection is created.
    internal sealed class RadioActivity : IDisposable
    {
        private object bus;
        private readonly Action<string> wake;
        private string state;
        private readonly List<object> subscriptions = new List<object>();
        public bool PttDown { get; private set; }

        private sealed class Subscription<T> where T : EventArgs
        {
            private readonly RadioActivity monitor;
            private readonly string topic;
            public Subscription(RadioActivity monitor, string topic) { this.monitor = monitor; this.topic = topic; }
            public void OnEvent(object sender, T args) => monitor.HandleSafely(topic, args);
        }

        internal RadioActivity(Action<string> wake) { this.wake = wake; }

        public static RadioActivity Attach(object broker, Action<string> wake)
        {
            var type = broker.GetType();
            if (type.Assembly.GetName().Name != "vPilot") return null;
            if (System.Diagnostics.FileVersionInfo.GetVersionInfo(type.Assembly.Location).FileVersion != "3.12.1.0") return null;
            var field = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(f => f.FieldType.FullName == "bbv.Common.EventBroker.IEventBroker");
            var monitor = new RadioActivity(wake) { bus = field.GetValue(broker) };
            try
            {
                var busType = field.FieldType;
                var handler = Activator.CreateInstance(busType.Assembly.GetType("bbv.Common.EventBroker.Handlers.Publisher", true));
                var register = busType.GetMethods().Single(m => m.Name == "RegisterHandlerMethod" && m.IsGenericMethod);
                foreach (string topic in new[] { "PushToTalkStateChanged", "RadioStackStateChanged", "RadioMessageSent", "PrivateMessageSent" })
                {
                    var eventType = type.Assembly.GetType("RossCarlson.Vatsim.Vpilot.Events.Arguments." + topic + "EventArgs", true);
                    var subscriptionType = typeof(Subscription<>).MakeGenericType(eventType);
                    var subscription = Activator.CreateInstance(subscriptionType, monitor, topic);
                    var callback = Delegate.CreateDelegate(typeof(EventHandler<>).MakeGenericType(eventType), subscription, subscriptionType.GetMethod("OnEvent"));
                    monitor.subscriptions.Add(subscription);
                    var typedRegister = register.MakeGenericMethod(eventType);
                    var matchers = Array.CreateInstance(typedRegister.GetParameters()[4].ParameterType.GetElementType(), 0);
                    typedRegister.Invoke(monitor.bus, new object[] { topic, subscription, callback, handler, matchers });
                }
                return monitor;
            }
            catch { monitor.Dispose(); throw; }
        }

        public void HandleSafely(string topic, object args)
        {
            try { Handle(topic, args); }
            catch (Exception) { /* Internal event changes must not break vPilot's event dispatch. */ }
        }

        internal void Handle(string topic, object args)
        {
            if (args == null) return;
            if (topic == "PushToTalkStateChanged")
            {
                bool down = (bool)args.GetType().GetProperty("Down").GetValue(args);
                bool pressed = down && !PttDown;
                PttDown = down;
                if (pressed) wake("Push-to-talk pressed while sleep was on.");
            }
            else if (topic == "RadioStackStateChanged")
            {
                object radio = args.GetType().GetProperty("RadioStackState").GetValue(args);
                if (radio == null) return;
                string snapshot = string.Join("|", new[] { "Com1ActiveFrequency", "Com2ActiveFrequency", "Com1TransmitEnabled", "Com2TransmitEnabled", "Com1ReceiveEnabled", "Com2ReceiveEnabled" }
                    .Select(name => radio.GetType().GetProperty(name).GetValue(radio).ToString()));
                bool changed = state != null && state != snapshot;
                state = snapshot;
                if (changed) wake("Radio frequency or TX/RX selection changed while sleep was on.");
            }
            else if (topic == "RadioMessageSent" || topic == "PrivateMessageSent")
                wake("Outgoing " + (topic == "RadioMessageSent" ? "radio" : "private") + " message sent while sleep was on.");
        }

        public void Dispose()
        {
            object current = bus;
            bus = null;
            if (current == null) return;
            var contract = current.GetType().GetInterfaces().Single(i => i.FullName == "bbv.Common.EventBroker.IEventBroker");
            foreach (var subscription in subscriptions)
                contract.GetMethod("Unregister").Invoke(current, new object[] { subscription });
            subscriptions.Clear();
        }
    }
}
