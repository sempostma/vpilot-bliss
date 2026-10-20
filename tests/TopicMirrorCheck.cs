using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VpilotBliss;

internal static class TopicMirrorCheck
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Requests = new List<string>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            lock (Requests) Requests.Add(request.RequestUri.AbsoluteUri);
            string first = "{\"event\":\"message\",\"id\":\"one\",\"title\":\"From phone\",\"message\":\"hello\"}";
            string second = "{\"event\":\"message\",\"id\":\"two\",\"title\":\"SELCAL\",\"message\":\"check radio\"}";
            string payload = Requests.Count == 1 ? "{\"event\":\"open\",\"id\":\"open\"}\n" + first : first + "\n" + second;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }

    public static void Run()
    {
        var handler = new Handler();
        using (var notifications = new Notifications(handler))
        {
            var messages = new List<TopicMessage>();
            var receivedBoth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mirror = new TopicMirror(notifications, new Settings { Enabled = true, Topic = "mirror-test" }, message =>
            {
                lock (messages)
                {
                    messages.Add(message);
                    if (messages.Count >= 2) receivedBoth.TrySetResult(true);
                }
            }, () => receivedBoth.TrySetException(new Exception("Mirror failed")), TimeSpan.FromMilliseconds(10));
            try
            {
                if (Task.WhenAny(receivedBoth.Task, Task.Delay(2000)).GetAwaiter().GetResult() != receivedBoth.Task)
                    throw new Exception("Mirror did not deliver messages.");
                receivedBoth.Task.GetAwaiter().GetResult();
            }
            finally { mirror.Dispose(); mirror.Dispose(); }
            if (Task.WhenAny(mirror.Completion, Task.Delay(2000)).GetAwaiter().GetResult() != mirror.Completion)
                throw new Exception("Mirror did not shut down.");
            mirror.Completion.GetAwaiter().GetResult();
            if (messages.Count != 2 || messages[0].DisplayText != "From phone - hello" || messages[1].Title != "SELCAL")
                throw new Exception("Mirror duplicated or lost messages.");
            if (!handler.Requests[0].Contains("since=10m") || !handler.Requests[1].Contains("since=one"))
                throw new Exception("Mirror did not resume from last message ID.");
            Console.WriteLine("PASS ntfy mirror reads phone and outgoing messages once, resumes by ID, and stops on disposal");
        }
    }
}
