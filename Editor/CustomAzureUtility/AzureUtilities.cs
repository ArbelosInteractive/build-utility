using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Arbelos.BuildUtility.Runtime;

namespace Arbelos.BuildUtility.Editor
{
    public static class AzureUtilities
    {
        private static string containerName = "";
        private static string localBuildPath = "";
        private static ProjectData currentProjectData = null;

        [MenuItem("Build Utility/Addressables/Upload Addressables")]
        public static async Task UploadAddressables(string sharedKey)
        {
            localBuildPath = $"{Directory.GetCurrentDirectory()}/ServerData/{EditorUserBuildSettings.activeBuildTarget.ToString()}";
            containerName = CustomAddressableBuild.GetAzureFriendlyBuildTarget();
            currentProjectData = Resources.Load<ProjectData>("BuildUtilityProjectData");

            if (currentProjectData == null)
            {
                Debug.LogError("BUILD UTILITY - Project Data File not found. Please create a ProjectData asset in the Resources folder and assign the values.");
                return;
            }

            if (string.IsNullOrEmpty(currentProjectData.azureStorageAccountName) || string.IsNullOrEmpty(sharedKey) || string.IsNullOrEmpty(currentProjectData.azureStorageAccountURL))
            {
                Debug.LogError("BUILD UTILITY - Project Data file contains null values.");
                return;
            }
            string[] files = Directory.GetFiles(localBuildPath, "*", SearchOption.AllDirectories);
            foreach (string filePath in files)
            {
                string blobName = Path.GetRelativePath(localBuildPath, filePath).Replace("\\", "/");
                string uploadUrl = $"{currentProjectData.azureStorageAccountURL}/{containerName}/{blobName}";

                byte[] fileData = File.ReadAllBytes(filePath);
                string dateHeader = DateTime.UtcNow.ToString("R");
                string contentLength = fileData.Length.ToString();
                string authorizationHeader = GetAuthorizationHeader("PUT", contentLength, blobName, dateHeader, sharedKey);

                Debug.Log($"Uploading {blobName} to {uploadUrl}");

                UnityWebRequest request = new UnityWebRequest(uploadUrl, "PUT");
                request.uploadHandler = new UploadHandlerRaw(fileData);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("x-ms-blob-type", "BlockBlob");
                request.SetRequestHeader("Authorization", authorizationHeader);
                request.SetRequestHeader("x-ms-date", dateHeader);
                request.SetRequestHeader("x-ms-version", "2019-12-12");
                request.SetRequestHeader("Content-Type", "application/octet-stream");

                await SendRequest(request);
            }

            Debug.Log("Upload complete!");
        }

        private static string GetAuthorizationHeader(string method, string contentLength, string blobName, string date, string sharedKey)
        {
            string canonicalizedHeaders = $"x-ms-blob-type:BlockBlob\nx-ms-date:{date}\nx-ms-version:2019-12-12\n";
            string canonicalizedResource = $"/{currentProjectData.azureStorageAccountName}/{containerName}/{blobName}";

            string stringToSign = $"{method}\n\n\n{contentLength}\n\napplication/octet-stream\n\n\n\n\n\n\n{canonicalizedHeaders}{canonicalizedResource}";

            string signature = ComputeHMACSHA256(stringToSign, sharedKey);
            return $"SharedKey {currentProjectData.azureStorageAccountName}:{signature}";
        }

        private static string ComputeHMACSHA256(string message, string secret)
        {
            byte[] key = Convert.FromBase64String(secret);
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                byte[] hashMessage = hmac.ComputeHash(messageBytes);
                return Convert.ToBase64String(hashMessage);
            }
        }

        private static async Task SendRequest(UnityWebRequest request)
        {
            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("File uploaded successfully.");
            }
            else
            {
                Debug.LogError($"Error uploading file: {request.error} - Response Code: {request.responseCode}");
                Debug.LogError($"Response: {request.downloadHandler.text}");
            }
        }
    }
}
