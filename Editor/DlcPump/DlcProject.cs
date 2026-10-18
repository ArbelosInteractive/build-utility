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

public interface IDlcPackAuthoring
{
    string CreatePack(string productId, string title, string description, string[] members);
}
