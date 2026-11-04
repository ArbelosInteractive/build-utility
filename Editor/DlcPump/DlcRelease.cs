using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Editor-only release planning data. Runtime entitlement rules remain authoritative.
// A release covers every store platform (Android and iOS): export, build and publish use the editor's active build target.
public sealed class DlcRelease : ScriptableObject
{
    public string releaseId = "new-release";
    public string minimumClientVersion = "";
    public string addressablesProfile = "";
    [TextArea(3, 10)] public string notes;
    public List<string> productIds = new List<string>();
    public List<DlcReleaseContent> content = new List<DlcReleaseContent>();
}

[Serializable]
public sealed class DlcReleaseContent
{
    public string productId;
    public UnityEngine.Object asset;
    [Tooltip("Optional for content bundled through another asset. Must resolve when supplied.")]
    public string addressablesKey;
}
