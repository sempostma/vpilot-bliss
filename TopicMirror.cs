using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VpilotBliss
{
    internal sealed class TopicMirror : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Task worker;
        private int disposed;

        internal Task Completion => worker;

        public TopicMirror(Notifications notifications, Settings settings, Action<TopicMessage> received, Action failed, TimeSpan? interval = null, string initialSince = "10m", TimeSpan? retryInterval = null)
        {
            var token = cancellation.Token;
            worker = Task.Run(async () =>
            {
                string since = initialSince;
                var seen = new HashSet<string>();
                var order = new Queue<string>();
                bool reportedFailure = false;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var message in await notifications.Poll(settings, since, token).ConfigureAwait(false))
                        {
                            token.ThrowIfCancellationRequested();
                            if (seen.Add(message.Id))
                            {
                                order.Enqueue(message.Id);
                                if (order.Count > 2048) seen.Remove(order.Dequeue());
                                received(message);
                            }
                            since = message.Id;
                        }
                        reportedFailure = false;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception)
                    {
                        if (!reportedFailure && !token.IsCancellationRequested)
                            try { failed(); } catch (Exception) { /* A UI/reporting failure must not kill reconnection. */ }
                        reportedFailure = true;
                    }
                    try { await Task.Delay(reportedFailure ? retryInterval ?? TimeSpan.FromSeconds(15) : interval ?? TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { cancellation.Cancel(); }
            catch (AggregateException) { /* Continue cleanup even if a registered cancellation callback fails. */ }
            // Release the cancellation source after the worker exits, without blocking vPilot's UI.
            worker.ContinueWith(completed =>
            {
                var observed = completed.Exception;
                cancellation.Dispose();
            }, TaskScheduler.Default);
        }
    }
}
