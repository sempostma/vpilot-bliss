using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace VpilotBliss
{
    public interface IAudioSession
    {
        string Id { get; }
        bool Muted { get; set; }
    }

    public sealed class SilenceController
    {
        private readonly Dictionary<string, bool> previous = new Dictionary<string, bool>();
        public bool IsSilenced { get; private set; }
        public int TrackedCount => previous.Count;

        public void Silence(IEnumerable<IAudioSession> sessions)
        {
            IsSilenced = true;
            Refresh(sessions);
        }

        public void Refresh(IEnumerable<IAudioSession> sessions)
        {
            if (!IsSilenced) return;
            foreach (var session in sessions)
            {
                if (!previous.ContainsKey(session.Id)) previous.Add(session.Id, session.Muted);
                session.Muted = true;
            }
        }

        // Keep failed restorations pending so a device change can be retried.
        public void Stop() => IsSilenced = false;

        public void Restore(IEnumerable<IAudioSession> sessions, bool selcal)
        {
            IsSilenced = false;
            var errors = new List<Exception>();
            foreach (var session in sessions)
            {
                bool original;
                if (!previous.TryGetValue(session.Id, out original))
                {
                    if (!selcal) continue;
                    previous[session.Id] = original = false;
                }
                try
                {
                    session.Muted = selcal ? false : original;
                    previous.Remove(session.Id);
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new AggregateException("Could not restore every audio session.", errors);
        }
    }

    internal sealed class ProcessAudio
    {
        private sealed class Session : IAudioSession
        {
            private readonly AudioSessionControl control;
            public Session(AudioSessionControl control, string deviceId)
            {
                this.control = control;
                Id = deviceId + ":" + control.GetSessionInstanceIdentifier;
            }
            public string Id { get; }
            public bool Muted { get => control.SimpleAudioVolume.Mute; set => control.SimpleAudioVolume.Mute = value; }
        }

        public void Visit(Action<IEnumerable<IAudioSession>> action)
        {
            var devices = new List<MMDevice>();
            var controls = new List<AudioSessionControl>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var result = new List<IAudioSession>();
                    int pid;
                    using (var process = Process.GetCurrentProcess()) pid = process.Id;
                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        devices.Add(device);
                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var control = sessions[i];
                            controls.Add(control);
                            if (control.GetProcessID == pid) result.Add(new Session(control, device.ID));
                        }
                    }
                    action(result);
                }
            }
            finally
            {
                foreach (var control in controls) control.Dispose();
                foreach (var device in devices) device.Dispose();
            }
        }
    }
}
