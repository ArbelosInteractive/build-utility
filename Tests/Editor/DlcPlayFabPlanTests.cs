using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class DlcPlayFabPlanTests
{
    sealed class Fake : IDlcPlayFabTransport
    {
        public JObject published = JObject.Parse("{Id:'item',ETag:'one',Title:{NEUTRAL:'Arena'},PriceOptions:{Prices:[]},DisplayProperties:{custom:'keep'},Images:[{Id:'detail',Type:'Screenshot',Url:'https://cdn.example/detail.png'}]}");
        public JObject draft;
        public readonly List<string> calls = new List<string>();
        public JObject publishRequest;
        public bool failPublish, mismatch;
        public int uploads;
        public Fake() { draft = (JObject)published.DeepClone(); }
        public Task<JObject> Call(string method, JObject body, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested(); calls.Add(method);
            JObject result;
            switch (method)
            {
                case "GetItem": result = new JObject { ["Item"] = published.DeepClone() }; break;
                case "GetItems": result = new JObject { ["Items"] = new JArray(published.DeepClone()) }; break;
                case "GetDraftItem": result = new JObject { ["Item"] = draft.DeepClone() }; break;
                case "CreateUploadUrls": result = JObject.Parse("{UploadUrls:[{Id:'new-image',Url:'https://cdn.example/icon.png?secret=temporary'}]}"); break;
                case "UpdateDraftItem": Assert.AreEqual((string)draft["ETag"], (string)body["Item"]["ETag"]); draft = (JObject)body["Item"].DeepClone(); draft["ETag"] = "two"; result = new JObject { ["Item"] = draft.DeepClone() }; break;
                case "PublishDraftItem": publishRequest = (JObject)body.DeepClone(); if (!failPublish) published = (JObject)draft.DeepClone(); if (mismatch) published["IsHidden"] = true; result = new JObject(); break;
                case "GetItemPublishStatus": result = new JObject { ["Result"] = failPublish ? "Failed" : "Succeeded" }; break;
                default: throw new Exception("Unexpected method " + method);
            }
            return Task.FromResult(result);
        }
        public Task Upload(string url, byte[] bytes, CancellationToken cancel) { cancel.ThrowIfCancellationRequested(); uploads++; Assert.IsTrue(url.Contains("?secret=")); return Task.CompletedTask; }
    }
    static JObject Schedule(string kind = "release") => JObject.Parse("{scheduleId:'season',waves:[{id:'wave',kind:'" + kind + "',startsAtUtc:'2026-11-01T00:00:00Z',endsAtUtc:null,paused:false,productIds:['arena']}]}");
    static DlcPlayFabChange Read(Fake api, JObject schedule = null, byte[] png = null) => DlcPlayFabPlan.Read(api, "arena", schedule, png, true, CancellationToken.None).GetAwaiter().GetResult();
    static void Publish(Fake api, DlcPlayFabChange change) => DlcPlayFabPlan.Publish(api, change, CancellationToken.None).GetAwaiter().GetResult();

    [Test] public void ReleasePreservesPricesTitlesAndCustomProperties()
    {
        var api = new Fake(); var change = Read(api, Schedule());
        Assert.IsTrue(JToken.DeepEquals(api.published["PriceOptions"], change.after["PriceOptions"]));
        Assert.IsTrue(JToken.DeepEquals(api.published["Title"], change.after["Title"]));
        Assert.AreEqual("keep", (string)change.after["DisplayProperties"]["custom"]);
        Assert.IsFalse((bool)change.after["IsHidden"]); Assert.AreEqual(JTokenType.Null, change.after["EndDate"].Type);
        Assert.IsFalse(api.calls.Any(c => c.Contains("Update") || c.Contains("Publish")));
    }
    [Test] public void PromotionDoesNotChangeAvailability()
    {
        var api = new Fake(); api.published["IsHidden"] = api.draft["IsHidden"] = true;
        var change = Read(api, Schedule("promotion")); Assert.IsTrue((bool)change.after["IsHidden"]);
        Assert.IsNull(change.after["StartDate"]); Assert.AreEqual(1, ((JArray)change.after["DisplayProperties"]["dlcPump"]["promotions"]).Count);
    }
    [Test] public void PausedReleaseHidesTheOffer()
    { var schedule = Schedule(); schedule["waves"][0]["paused"] = true; Assert.IsTrue((bool)Read(new Fake(), schedule).after["IsHidden"]); }
    [Test] public void DifferentScheduleCannotTakeOverAnItem()
    {
        var api = new Fake(); api.published["DisplayProperties"]["dlcPump"] = JObject.Parse("{scheduleId:'other'}"); api.draft = (JObject)api.published.DeepClone();
        Assert.Throws<InvalidOperationException>(() => Read(api, Schedule()));
    }
    [Test] public void UnpublishedDraftChangesBlockReview()
    { var api = new Fake(); api.draft["Title"]["NEUTRAL"] = "Someone else's change"; Assert.Throws<InvalidOperationException>(() => Read(api, Schedule())); }
    [Test] public void PublishedFriendlyIdProjectionDoesNotCreateADraftConflict()
    {
        var api = new Fake();
        api.published["AlternateIds"] = JArray.Parse("[{Type:'FriendlyId',Value:'arena'}]");
        api.draft["AlternateIds"] = api.published["AlternateIds"].DeepClone();
        api.published["FriendlyId"] = "arena";
        var change = Read(api, Schedule());
        Assert.IsNull(change.after["FriendlyId"]);
        Assert.IsTrue(JToken.DeepEquals(api.published["AlternateIds"], change.after["AlternateIds"]));
        api.draft["AlternateIds"][0]["Value"] = "changed-id";
        Assert.Throws<InvalidOperationException>(() => Read(api, Schedule()));
    }
    [Test] public void EquivalentDateTokensAndOmittedEndDateDoNotRepublish()
    {
        var api = new Fake();
        api.published["StartDate"] = "2026-11-01T00:00:00Z";
        api.draft = (JObject)api.published.DeepClone();
        var schedule = Schedule();
        var initial = Read(api, schedule); Publish(api, initial);
        api.published.Remove("EndDate"); api.draft.Remove("EndDate");
        api.published["StartDate"] = "2026-11-01T00:00:00Z";
        api.draft["StartDate"] = "2026-11-01T00:00:00Z";
        api.calls.Clear();
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-GB");
            var change = Read(api, schedule); Publish(api, change);
            Assert.IsTrue(change.verified);
            Assert.IsFalse(api.calls.Contains("UpdateDraftItem"));
            Assert.IsEmpty((JObject)DlcPlayFabPlan.Summary(change)["changes"]);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
    [Test] public void PlayFabSignedDraftAndPublishedCdnUrlsCompareByContentIdentity()
    {
        var api = new Fake();
        api.published["Images"][0]["Url"] = "https://content1.prod.catalog.playfab.com/title/image/icon.png";
        api.draft["Images"][0]["Url"] = "https://pfcatalogcontent1.blob.core.windows.net/title/image/icon.png?sig=test";
        Assert.DoesNotThrow(() => Read(api));
        api.draft["Images"][0]["Url"] = "https://pfcatalogcontent1.blob.core.windows.net/title/image/different.png?sig=test";
        Assert.Throws<InvalidOperationException>(() => Read(api));
        api.draft["Images"][0]["Url"] = "https://unrelated.example/title/image/icon.png";
        Assert.Throws<InvalidOperationException>(() => Read(api));
    }
    [Test] public void MissingETagBlocksReview()
    { var api = new Fake(); api.draft.Remove("ETag"); Assert.Throws<InvalidOperationException>(() => Read(api, Schedule())); }
    [Test] public void StaleReviewBlocksEveryWriteAndUpload()
    {
        var api = new Fake(); var change = Read(api, Schedule(), new byte[] { 1 }); api.draft["ETag"] = "someone-else"; api.calls.Clear();
        Assert.Throws<InvalidOperationException>(() => Publish(api, change)); CollectionAssert.AreEqual(new[] { "GetDraftItem" }, api.calls); Assert.AreEqual(0, api.uploads);
    }
    [Test] public void ThumbnailPreservesDetailAndPublishesWithNewETag()
    {
        var api = new Fake(); var change = Read(api, Schedule(), new byte[] { 1, 2 }); Publish(api, change);
        Assert.AreEqual(1, api.uploads); Assert.AreEqual("two", (string)api.publishRequest["ETag"]); Assert.IsTrue(change.verified);
        var images = (JArray)api.published["Images"]; Assert.AreEqual(2, images.Count); Assert.AreEqual("detail", (string)images[0]["Id"]);
        Assert.AreEqual("https://cdn.example/icon.png", (string)images[1]["Url"]); Assert.AreEqual("GetItems", api.calls.Last());
    }
    [Test] public void RepeatedReviewIsIdempotentButMissingThumbnailIsRepaired()
    {
        var api = new Fake(); var bytes = new byte[] { 3 }; Publish(api, Read(api, Schedule(), bytes)); api.calls.Clear();
        var repeat = Read(api, Schedule(), bytes); Assert.IsNull(repeat.png); Publish(api, repeat);
        Assert.IsFalse(api.calls.Contains("UpdateDraftItem")); Assert.AreEqual(1, api.uploads);
        api.published["Images"] = new JArray(); api.draft = (JObject)api.published.DeepClone(); Assert.IsNotNull(Read(api, Schedule(), bytes).png);
    }
    [Test] public void PublishFailureAndReadbackMismatchNeverReportVerified()
    {
        foreach (bool failure in new[] { true, false }) { var api = new Fake { failPublish = failure, mismatch = !failure }; var change = Read(api, Schedule()); Assert.Throws<InvalidOperationException>(() => Publish(api, change)); Assert.IsFalse(change.verified); }
    }
    [Test] public void CancellationStopsBeforeAnyWrite()
    {
        var api = new Fake(); var change = Read(api, Schedule()); api.calls.Clear(); var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => DlcPlayFabPlan.Publish(api, change, cancel.Token).GetAwaiter().GetResult()); Assert.IsEmpty(api.calls); cancel.Dispose();
    }
    [Test] public void OversizedMetadataBlocksReview()
    {
        var api = new Fake(); api.published["DisplayProperties"]["custom"] = new string('x', 10000); api.draft = (JObject)api.published.DeepClone();
        Assert.Throws<InvalidOperationException>(() => Read(api, Schedule()));
    }
}
