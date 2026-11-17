using System;
using System.Collections.Generic;
using UnityEngine;

public interface IDlcProjectAdapter
{
    string DisplayName { get; }
    string CatalogPath { get; }
    string BackupDirectory { get; }
    UnityEngine.Object UnlockableCatalog { get; }
    List<DlcReleaseContent> Resolve(string productId, List<string> diagnostics);
    string Populate(DlcRelease release);
}

public static class DlcProject
{
    public static IDlcProjectAdapter Adapter { get; set; }
    public static IDlcProjectAdapter Required => Adapter ?? throw new InvalidOperationException("Register an IDlcProjectAdapter for this project before authoring DLC releases.");
}

/// <summary>
/// Optional, on the project adapter: extra game data the Pump publishes next to the IAP catalog
/// (data/&lt;appVersion&gt;/&lt;file&gt; in the DLC container), which the game downloads with DlcRemoteData.
/// </summary>
public interface IDlcGameData
{
    /// <summary>File name (letters, digits, '.', '_', '-') to JSON text.</summary>
    IDictionary<string, string> GameDataFiles();
}

public interface IDlcPackAuthoring
{
    string CreatePack(string productId, string title, string description, string[] members);
}
