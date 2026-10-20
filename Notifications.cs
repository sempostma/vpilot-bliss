using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.Serialization.Json;

namespace VpilotBliss
{
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public bool Enabled;
        [DataMember] public string Server = "https://ntfy.sh";
        [DataMember] public string Topic = "vpilot-" + Guid.NewGuid().ToString("N");
        [DataMember] public string ProtectedToken = "";

        public static Settings Load(string path)
        {
            if (!File.Exists(path)) return new Settings();
            using (var stream = File.OpenRead(path))
                return (Settings)new DataContractSerializer(typeof(Settings)).ReadObject(stream);
        }
        public void Save(string path)
        {
            string temp = path + ".tmp";
            using (var stream = File.Create(temp)) new DataContractSerializer(typeof(Settings)).WriteObject(stream, this);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        public string Token
        {
            get => string.IsNullOrEmpty(ProtectedToken) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser));
            set => ProtectedToken = string.IsNullOrWhiteSpace(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
        public Uri Endpoint()
        {
            Uri server;
            if (!Uri.TryCreate(Server, UriKind.Absolute, out server) || server.Scheme != "https" || !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
                throw new InvalidOperationException("Enter an HTTPS ntfy server URL without credentials, query or fragment.");
            if (string.IsNullOrWhiteSpace(Topic) || Topic.Length > 128)
                throw new InvalidOperationException("Enter a topic of 1 to 128 letters, digits, underscores or hyphens.");
            foreach (char c in Topic)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-')
                    throw new InvalidOperationException("Topic may contain only letters, digits, underscores and hyphens.");
            return new Uri(server.AbsoluteUri.TrimEnd('/') + "/" + Topic);
        }
    }

    internal sealed class Notifications : IDisposable
    {
        private readonly HttpClient http;
        private int disposed;
        public Notifications() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
        internal Notifications(HttpMessageHandler handler)
        { http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 }; }
        public async Task Send(Settings settings, string title, string body, bool urgent = false, string originTag = null)
        {
            if (!settings.Enabled || Volatile.Read(ref disposed) != 0) return;
            using (var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint()))
            {
                request.Headers.Add("Title", title);
                request.Headers.Add("Priority", urgent ? "max" : "high");
                request.Headers.Add("Tags", string.IsNullOrEmpty(originTag) ? "airplane" : "airplane," + originTag);
                if (!string.IsNullOrEmpty(settings.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
                request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
                using (var response = await http.SendAsync(request).ConfigureAwait(false)) response.EnsureSuccessStatusCode();
            }
        }
        internal async Task<List<TopicMessage>> Poll(Settings settings, string since, CancellationToken cancellation)
        {
            var endpoint = new Uri(settings.Endpoint().AbsoluteUri + "/json?poll=1&since=" + Uri.EscapeDataString(since));
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                if (!string.IsNullOrEmpty(settings.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
                using (var response = await http.SendAsync(request, cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return ParseMessages(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }
        internal static List<TopicMessage> ParseMessages(string jsonLines)
        {
            var result = new List<TopicMessage>();
            using (var reader = new StringReader(jsonLines))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                        {
                            var message = (TopicMessage)new DataContractJsonSerializer(typeof(TopicMessage)).ReadObject(stream);
                            if (message?.Event == "message" && !string.IsNullOrEmpty(message.Id)) result.Add(message);
                        }
                    }
                    catch (SerializationException) { /* Skip a malformed event without losing the rest of the batch. */ }
                }
            }
            return result;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            http.Dispose(); // Cancels in-flight requests and disposes the underlying handler/connections.
        }
    }

    [DataContract]
    internal sealed class TopicMessage
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "event")] public string Event;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "tags")] public string[] Tags;
        public string DisplayText => string.IsNullOrWhiteSpace(Title) ? Message ?? "" : Title + " - " + Message;
    }
}
