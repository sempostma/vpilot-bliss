using System;
using System.Collections.Generic;
using VpilotBliss;

internal static class RadioActivityCheck
{
    private sealed class Ptt { public bool Down { get; set; } }
    private sealed class StateEvent { public State RadioStackState { get; set; } }
    private sealed class State
    {
        public uint Com1ActiveFrequency { get; set; } = 123450;
        public uint Com2ActiveFrequency { get; set; } = 122800;
        public bool Com1TransmitEnabled { get; set; } = true;
        public bool Com2TransmitEnabled { get; set; }
        public bool Com1ReceiveEnabled { get; set; } = true;
        public bool Com2ReceiveEnabled { get; set; }
    }
    public static void Run()
    {
        var reasons = new List<string>();
        using (var monitor = new RadioActivity(reasons.Add))
        {
            var state = new State();
            monitor.Handle("RadioStackStateChanged", new StateEvent { RadioStackState = state });
            monitor.Handle("RadioStackStateChanged", new StateEvent { RadioStackState = state });
            if (reasons.Count != 0) throw new Exception("Baseline and repeated radio state must not wake.");
            monitor.Handle("RadioMessageReceived", EventArgs.Empty);
            monitor.Handle("RadioReceiveStateChanged", EventArgs.Empty);
            monitor.Handle("UnknownEvent", EventArgs.Empty);
            if (reasons.Count != 0) throw new Exception("Incoming frequency activity must not wake.");
            state.Com1ActiveFrequency = 124000;
            monitor.Handle("RadioStackStateChanged", new StateEvent { RadioStackState = state });
            state.Com2ReceiveEnabled = true;
            monitor.Handle("RadioStackStateChanged", new StateEvent { RadioStackState = state });
            monitor.Handle("PushToTalkStateChanged", new Ptt { Down = true });
            monitor.Handle("PushToTalkStateChanged", new Ptt { Down = true });
            monitor.Handle("PushToTalkStateChanged", new Ptt { Down = false });
            monitor.Handle("RadioMessageSent", EventArgs.Empty);
            monitor.Handle("PrivateMessageSent", EventArgs.Empty);
            if (reasons.Count != 5 || monitor.PttDown) throw new Exception("Radio action detection failed.");
        }
        Console.WriteLine("PASS frequency, TX/RX, PTT press and outgoing text detection ignores unchanged state and PTT release");
    }
}
