using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Build;

public class DlcPlayerContentTests
{
    string root;

    [SetUp] public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "dlc-player-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "aa", "AddressablesLink"));
        File.WriteAllText(Path.Combine(root, "aa", "settings.json"), "{\"m_CatalogLocations\":[\"{x}/catalog_2026.hash\"]}");
        File.WriteAllText(Path.Combine(root, "aa", "catalog.bin"), "local catalog");
        File.WriteAllText(Path.Combine(root, "aa", "AddressablesLink", "link.xml"), "<linker/>");
        Directory.CreateDirectory(Path.Combine(root, "remote"));
        File.WriteAllText(Path.Combine(root, "remote", "catalog_2026.bin"), "remote catalog");
        File.WriteAllText(Path.Combine(root, "remote", "skins_ff00.bundle"), "bundle");
    }

    [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    string Pack(string name = "a.zip")
    {
        string zip = Path.Combine(root, name);
        DlcPlayerContent.Write(zip, Path.Combine(root, "aa"), "Android", "1.4.0", "catalog_2026.bin", "abc", Path.Combine(root, "remote"));
        return zip;
    }

    [Test] public void RoundTripRestoresTheFolderAndCrcs()
    {
        byte[] data = File.ReadAllBytes(Pack());
        string target = Path.Combine(root, "restored", "Android");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "stale.txt"), "old");
        var manifest = DlcPlayerContent.Extract(data, target, "Android", "1.4.0");
        Assert.AreEqual("local catalog", File.ReadAllText(Path.Combine(target, "catalog.bin")));
        Assert.AreEqual("<linker/>", File.ReadAllText(Path.Combine(target, "AddressablesLink", "link.xml")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "stale.txt")));
        CollectionAssert.AreEqual(new[] { "catalog_2026.bin", "skins_ff00.bundle" }, manifest["crc"].Select(c => (string)c["key"]).ToArray());
    }

    [Test] public void SameBuildGivesTheSameZip()
    {
        CollectionAssert.AreEqual(File.ReadAllBytes(Pack("a.zip")), File.ReadAllBytes(Pack("b.zip")));
    }

    [Test] public void RefusesAnotherPlatformOrVersion()
    {
        byte[] data = File.ReadAllBytes(Pack());
        string target = Path.Combine(root, "restored");
        Assert.Throws<BuildFailedException>(() => DlcPlayerContent.Extract(data, target, "iOS", "1.4.0"));
        Assert.Throws<BuildFailedException>(() => DlcPlayerContent.Extract(data, target, "Android", "1.5.0"));
        Assert.IsFalse(Directory.Exists(target));
    }

    [Test] public void CheckRequiresTheExportedCatalog()
    {
        Assert.IsNull(DlcPlayerContent.Check(Path.Combine(root, "aa"), "catalog_2026.bin"));
        StringAssert.Contains("another Addressables build", DlcPlayerContent.Check(Path.Combine(root, "aa"), "catalog_2027.bin"));
        StringAssert.Contains("no Addressables player output", DlcPlayerContent.Check(Path.Combine(root, "missing"), "catalog_2026.bin"));
    }
}
