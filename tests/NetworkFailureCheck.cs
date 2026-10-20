using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VpilotBliss;

internal static class NetworkFailureCheck
{
    private sealed class Handler : HttpMessageHandler
    {
        private int attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            switch (Interlocked.Increment(ref attempts))
            {
                case 1: throw new HttpRequestException("Connection reset", new SocketException((int)SocketError.ConnectionReset));
                case 2: throw new TaskCanceledException("Request timed out");
                case 3: return Response(HttpStatusCode.ServiceUnavailable, "unavailable");
                case 4: return Response((HttpStatusCode)429, "rate limited");
                default: return Response(HttpStatusCode.OK, "not-json\nnull\n{\"event\":\"message\",\"id\":\"recovered\",\"message\":\"working\"}");
            }
        }
        private static Task<HttpResponseMessage> Response(HttpStatusCode code, string body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    public static void Run()
    {
        using (var notifications = new Notifications(new Handler()))
        {
            var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int warnings = 0;
            var mirror = new TopicMirror(notifications, new Settings { Enabled = true }, message => recovered.TrySetResult(message.Message == "working"),
                () => { Interlocked.Increment(ref warnings); throw new InvalidOperationException("Simulated reporting failure"); },
                TimeSpan.FromMilliseconds(10), retryInterval: TimeSpan.FromMilliseconds(10));
            try
            {
                if (Task.WhenAny(recovered.Task, Task.Delay(3000)).GetAwaiter().GetResult() != recovered.Task || !recovered.Task.GetAwaiter().GetResult())
                    throw new Exception("Mirror did not recover from network failures.");
                if (warnings != 1) throw new Exception("Connection errors flooded the UI.");
            }
            finally { mirror.Dispose(); }
            if (Task.WhenAny(mirror.Completion, Task.Delay(2000)).GetAwaiter().GetResult() != mirror.Completion)
                throw new Exception("Mirror failed to shut down after network errors.");
            mirror.Completion.GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS TCP reset, HTTP 503/429, timeout, malformed JSON and reporting failures recover without escaping the worker");
    }
}
