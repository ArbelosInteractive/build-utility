using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public interface IDlcPlayFabTransport
{
    Task<JObject> Call(string method, JObject body, CancellationToken cancellation);
    Task Upload(string url, byte[] bytes, CancellationToken cancellation);
}

public sealed class DlcPlayFabClient : IDlcPlayFabTransport, IDisposable
{
    readonly HttpClient http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly string title;
    string token;
    public DlcPlayFabClient(string titleId)
    {
        if (!Regex.IsMatch(titleId ?? "", "^[A-Za-z0-9]{1,32}$")) throw new InvalidOperationException("Enter a valid PlayFab title ID.");
        title = titleId;
    }
    public async Task Authenticate(string variable, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(variable ?? "", "^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidOperationException("Enter the environment variable NAME, not a secret.");
        string secret = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("Set " + variable + " outside Unity, then restart the Editor. Credentials are never stored in project assets.");
        var data = await Post("Authentication/GetEntityToken", new JObject(), "X-SecretKey", secret, cancellation);
        if ((string)data["Entity"]?["Type"] != "title" || !string.Equals((string)data["Entity"]?["Id"], title, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The authenticated entity does not match this title.");
        token = (string)data["EntityToken"];
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("PlayFab did not return a title token.");
    }
    public Task<JObject> Call(string method, JObject body, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Connect to PlayFab first.");
        if (!Regex.IsMatch(method ?? "", "^[A-Za-z]+$")) throw new ArgumentException("Invalid method.");
        return Post("Catalog/" + method, body, "X-EntityToken", token, cancellation);
    }
    async Task<JObject> Post(string path, JObject body, string header, string credential, CancellationToken cancellation)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://" + title + ".playfabapi.com/" + path))
        {
            request.Headers.Add(header, credential); request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            using (var response = await http.SendAsync(request, cancellation))
            {
                var text = await response.Content.ReadAsStringAsync();
                JObject envelope;
                try { using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(text)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None }) envelope = JObject.Load(reader); }
                catch { throw new InvalidOperationException("PlayFab returned an unreadable response (HTTP " + (int)response.StatusCode + ")."); }
                if (!response.IsSuccessStatusCode || envelope["error"] != null)
                    throw new InvalidOperationException("PlayFab " + path + " failed (HTTP " + (int)response.StatusCode + ", code " + (int?)envelope["errorCode"] + "). No automatic write retry was performed.");
                return envelope["data"] as JObject ?? throw new InvalidOperationException("PlayFab response has no data.");
            }
        }
    }
    public async Task Upload(string url, byte[] bytes, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Invalid upload URL from PlayFab.");
        using (var request = new HttpRequestMessage(HttpMethod.Put, uri))
        {
            request.Headers.Add("x-ms-blob-type", "BlockBlob"); request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            using (var response = await http.SendAsync(request, cancellation)) if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Image upload failed (HTTP " + (int)response.StatusCode + ").");
        }
    }
    public void Dispose() { token = null; http.Dispose(); }
}
