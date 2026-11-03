using System;
using System.Collections.Generic;
using System.Linq;
using Arbelos.BuildUtility.Runtime;
using NUnit.Framework;
using UnityEngine;

public class DlcContentExportTests
{
    readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();

    [TearDown]
    public void TearDown()
    {
        foreach (var o in created) UnityEngine.Object.DestroyImmediate(o);
        created.Clear();
    }

    T Make<T>(string name) where T : ScriptableObject
    {
        var o = ScriptableObject.CreateInstance<T>();
        o.name = name;
        created.Add(o);
        return o;
    }

    DlcRelease Release(string id, params string[] products)
    {
        var r = Make<DlcRelease>(id);
        r.releaseId = id;
        r.productIds = products.ToList();
        return r;
    }

    static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal) { "Jazz/Gear/a", "Jazz/Gear/b", "Shared/Explicit" };

    static DlcContentExportResult Run(IList<DlcRelease> releases, ISet<string> catalog = null, string appVersion = "1.4.0")
    {
        var r = new DlcContentExportResult();
        DlcContentExport.Validate(releases, Known, catalog, appVersion, r);
        return r;
    }

    [Test]
    public void KeysComeFromAddressablesKeyGroupedPerProductAndSorted()
    {
        var asset = Make<DlcRelease>("someAsset");
        var release = Release("rel", "wrestler_jazz", "pack_x");
        release.content.Add(new DlcReleaseContent { productId = "wrestler_jazz", asset = asset, addressablesKey = "Jazz/Gear/b" });
        release.content.Add(new DlcReleaseContent { productId = "wrestler_jazz", addressablesKey = "Jazz/Gear/a" });
        release.content.Add(new DlcReleaseContent { productId = "pack_x", addressablesKey = "Jazz/Gear/a" });
        release.content.Add(new DlcReleaseContent { productId = "pack_x", addressablesKey = " Shared/Explicit " });

        var r = Run(new[] { release });

        Assert.IsTrue(r.Ok, string.Join("\n", r.errors));
        CollectionAssert.AreEqual(new[] { "Jazz/Gear/a", "Jazz/Gear/b" }, r.products.Single(p => p.productId == "wrestler_jazz").keys);
        // Packs may share addresses with their members.
        CollectionAssert.AreEqual(new[] { "Jazz/Gear/a", "Shared/Explicit" }, r.products.Single(p => p.productId == "pack_x").keys);
    }

    [Test]
    public void BlankKeysAreLocalContentAndKeylessProductsAreStillExported()
    {
        var local = Make<DlcRelease>("localClip");
        var release = Release("rel", "animation_pack_taunt", "wrestler_jazz", "no_entries_yet");
        release.content.Add(new DlcReleaseContent { productId = "animation_pack_taunt", asset = local });
        release.content.Add(new DlcReleaseContent { productId = "animation_pack_taunt", asset = local, addressablesKey = "  " });
        release.content.Add(new DlcReleaseContent { productId = "wrestler_jazz", asset = local });
        release.content.Add(new DlcReleaseContent { productId = "wrestler_jazz", addressablesKey = "Jazz/Gear/a" });

        var r = Run(new[] { release });

        Assert.IsTrue(r.Ok, string.Join("\n", r.errors));
        Assert.AreEqual(3, r.ignoredEntries);
        CollectionAssert.AreEqual(new[] { "animation_pack_taunt", "no_entries_yet" }, r.localOnlyProducts);
        CollectionAssert.AreEqual(new[] { "animation_pack_taunt", "no_entries_yet", "wrestler_jazz" }, r.products.Select(p => p.productId));
        CollectionAssert.IsEmpty(r.products.Single(p => p.productId == "animation_pack_taunt").keys);
        CollectionAssert.AreEqual(new[] { "Jazz/Gear/a" }, r.products.Single(p => p.productId == "wrestler_jazz").keys);
    }

    [Test]
    public void RejectsUnlistedProductAndUnresolvedKey()
    {
        var release = Release("rel", "wrestler_jazz");
        release.content.Add(new DlcReleaseContent { productId = "other", addressablesKey = "Shared/Explicit" });
        release.content.Add(new DlcReleaseContent { productId = "wrestler_jazz", addressablesKey = "Missing/Key" });

        var r = Run(new[] { release });

        Assert.IsTrue(r.errors.Any(e => e.Contains("'other' isn't listed")));
        Assert.IsTrue(r.errors.Any(e => e.Contains("'Missing/Key' isn't an address")));
    }

    [Test]
    public void RejectsKeyMissingFromBuiltCatalogAndProductInTwoReleases()
    {
        var first = Release("r1", "wrestler_jazz");
        first.content.Add(new DlcReleaseContent { productId = "wrestler_jazz", addressablesKey = "Jazz/Gear/a" });
        var second = Release("r2", "wrestler_jazz");
        second.content.Add(new DlcReleaseContent { productId = "wrestler_jazz", addressablesKey = "Jazz/Gear/a" });

        var r = Run(new[] { first, second }, catalog: new HashSet<string> { "Something/else" });

        Assert.IsTrue(r.errors.Any(e => e.Contains("isn't in the built remote catalog")));
        Assert.IsTrue(r.errors.Any(e => e.Contains("is in both release 'r1' and 'r2'")));
    }

    [Test]
    public void RejectsUnsafeProductIds()
    {
        var r = Run(new[] { Release("rel", "bad/id", "..") });
        Assert.AreEqual(2, r.errors.Count(e => e.Contains("can only contain")));
    }

    [Test]
    public void VersionComparisonIsNumeric()
    {
        Assert.AreEqual(1, DlcVersion.Compare("1.10.0", "1.9.2"));
        Assert.AreEqual(0, DlcVersion.Compare("1.4", "1.4.0"));
        Assert.IsTrue(DlcVersion.Satisfies("1.4.0", ""));
        Assert.IsFalse(DlcVersion.Satisfies("1.4.0", "1.5.0"));
        Assert.IsTrue(DlcContentPaths.IsVersionFolder("v12"));
        Assert.IsFalse(DlcContentPaths.IsVersionFolder("v0"));
        Assert.AreEqual("https://cdn/x/Android/1.4.0/", DlcContentPaths.AppVersionFolderUrl("https://cdn/x/", "Android", "1.4.0"));
    }

    [Test]
    public void ManifestJsonRoundTripsThroughJsonUtility()
    {
        const string json = "{\"schemaVersion\":1,\"platform\":\"Android\",\"appVersion\":\"1.4.0\",\"products\":[{\"productId\":\"a\",\"current\":\"v2\",\"versions\":[\"v2\",\"v1\"]}]}";
        var index = JsonUtility.FromJson<DlcProductsIndex>(json);
        Assert.AreEqual("v2", index.products[0].current);
        CollectionAssert.AreEqual(new[] { "v2", "v1" }, index.products[0].versions);
    }
}
