using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Arbelos.BuildUtility.Runtime
{
    /// <summary>
    /// Small data files the game downloads at runtime instead of only using the copy built into the app, e.g. the IAP
    /// product catalog. They live in the DLC container at <c>data/&lt;Application.version&gt;/&lt;file&gt;</c> (one copy for
    /// every platform), are uploaded by the DLC Pump's "Publish game data" and copied to production by "Promote game data".
    /// <para>
    /// Each successful download is cached under Application.persistentDataPath (per environment and app version) and
    /// revalidated with If-None-Match, so an unchanged file costs a 304. When the server can't be reached in time the last
    /// cached copy is used; with no cached copy the caller gets null and uses its bundled file. A file that fails
    /// <c>isValid</c> is never cached or returned.
    /// </para>
    /// </summary>
    public static class DlcRemoteData
    {
        public const string Folder = "data";

        /// <summary>The file's URL in the current environment, or null when <see cref="DlcRemoteConfig"/> has no URL for it.</summary>
        public static string Url(string file)
        {
            string root = DlcEnvironment.ContentUrl;
            return string.IsNullOrEmpty(root) || !DlcContentPaths.IsSafeSegment(file) || !DlcContentPaths.IsSafeSegment(Application.version)
                ? null : $"{root}/{Folder}/{Application.version}/{file}";
        }

        /// <summary>
        /// Downloads the file (or confirms the cached copy is current) and returns its text. Falls back to the cached copy
        /// when the request fails or takes longer than <paramref name="timeoutSeconds"/>. Returns null when neither is usable.
        /// </summary>
        public static async Task<string> GetTextAsync(string file, Func<string, bool> isValid = null, float timeoutSeconds = 5f)
        {
            string url = Url(file);
            if (url == null)
            {
                Debug.LogWarning($"[DLC data] No {DlcEnvironment.Name} URL in DlcRemoteConfig (or bad file name '{file}'); using the bundled {file}.");
                return null;
            }

            string path = CachePath(file), etagPath = path + ".etag";
            string cached = Cached(file, isValid);
            string etag = cached != null && File.Exists(etagPath) ? SafeRead(etagPath) : null;

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = Math.Max(1, Mathf.CeilToInt(timeoutSeconds));
                if (!string.IsNullOrEmpty(etag)) request.SetRequestHeader("If-None-Match", etag);
                var op = request.SendWebRequest();
                var done = new TaskCompletionSource<bool>();
                if (op.isDone) done.TrySetResult(true);
                else op.completed += _ => done.TrySetResult(true);
                await done.Task;

                long code = request.responseCode;
                if (code == 304 && cached != null) return cached;
                if (request.result == UnityWebRequest.Result.Success && code == 200)
                {
                    string text = request.downloadHandler.text;
                    if (Valid(text, isValid))
                    {
                        WriteAtomic(path, text);
                        string fresh = request.GetResponseHeader("ETag");
                        if (!string.IsNullOrEmpty(fresh)) WriteAtomic(etagPath, fresh);
                        else TryDelete(etagPath);
                        return text;
                    }
                    Debug.LogWarning($"[DLC data] {url} is invalid; using the {(cached != null ? "cached" : "bundled")} copy.");
                    return cached;
                }

                string reason = code == 404 ? "not published (404)" : string.IsNullOrEmpty(request.error) ? "HTTP " + code : request.error;
                if (code != 404 || cached != null)
                    Debug.LogWarning($"[DLC data] {file}: {reason}; using the {(cached != null ? "cached" : "bundled")} copy.");
                return cached;
            }
        }

        /// <summary>The last downloaded copy for this environment and app version, or null.</summary>
        public static string Cached(string file, Func<string, bool> isValid = null)
        {
            if (!DlcContentPaths.IsSafeSegment(file)) return null;
            string path = CachePath(file);
            string text = File.Exists(path) ? SafeRead(path) : null;
            return Valid(text, isValid) ? text : null;
        }

        static bool Valid(string text, Func<string, bool> isValid)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            try { return isValid == null || isValid(text); }
            catch (Exception) { return false; }
        }

        static string CachePath(string file) =>
            Path.Combine(Application.persistentDataPath, "dlc-data", DlcEnvironment.Name, Application.version, file);

        static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return null; }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        static void WriteAtomic(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DLC data] Couldn't cache {path}: {e.Message}");
            }
        }
    }
}
