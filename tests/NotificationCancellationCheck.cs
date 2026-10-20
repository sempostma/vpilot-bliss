using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VpilotBliss;

internal static class NotificationCancellationCheck
{
    private sealed class PendingHandler : HttpMessageHandler
    {
        public int Requests;
        public bool WasDisposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => completion.TrySetCanceled())) return await completion.Task.ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    public static void Run()
    {
        var handler = new PendingHandler();
        var notifications = new Notifications(handler);
        var settings = new Settings { Enabled = true };
        var pending = notifications.Send(settings, "test", "No network is used by this test.");
        notifications.Dispose();
        notifications.Dispose();
        if (Task.WhenAny(pending, Task.Delay(2000)).GetAwaiter().GetResult() != pending)
            throw new Exception("Shutdown did not cancel notification request.");
        try { pending.GetAwaiter().GetResult(); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        notifications.Send(settings, "test", "ignored").GetAwaiter().GetResult();
        if (!handler.WasDisposed || handler.Requests != 1) throw new Exception("Notification resources were not released.");
        Console.WriteLine("PASS notification shutdown cancels pending requests, releases connections, and rejects new work");
    }
}
