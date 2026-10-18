using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class DlcPumpTests
{
    [Test]
    public void ListingPatchPreservesUnknownFields()
    {
        var original = JObject.Parse("{products:[{id:'pack',custom:{keep:true},defaultDescription:{title:'old',description:'old'}}]}");
        var patched = DlcCatalogEditing.Patch(original, "pack", "New", "Description");
        Assert.AreEqual("old", (string)original["products"][0]["defaultDescription"]["title"]);
        Assert.IsTrue((bool)patched["products"][0]["custom"]["keep"]);
        Assert.Throws<InvalidOperationException>(() => DlcCatalogEditing.Patch(original, "absent", "New", "Description"));
    }

    [Test]
    public void ArtifactVerificationDetectsTamperingAndExtraFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dlcpump-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "bundle"), "original");
            var inventory = DlcArtifactInventory.Capture(folder);
            StringAssert.StartsWith("PASS:", DlcArtifactInventory.Verify(inventory, folder));
            File.WriteAllText(Path.Combine(folder, "bundle"), "changed");
            File.WriteAllText(Path.Combine(folder, "extra"), "extra");
            string result = DlcArtifactInventory.Verify(inventory, folder);
            StringAssert.Contains("CHANGED bundle", result);
            StringAssert.Contains("UNEXPECTED extra", result);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public void ExportNeverOverwritesExistingFile()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "original");
            Assert.Throws<IOException>(() => DlcReleaseService.WriteNew(file, "replacement"));
            Assert.AreEqual("original", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }
}
