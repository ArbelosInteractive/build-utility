using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class DlcPlayFabPlanTests
{
    static JObject Item() => JObject.Parse("{Id:'item',ETag:'one',Title:{NEUTRAL:'Arena'},PriceOptions:{Prices:[]},DisplayProperties:{custom:'keep'},Images:[{Id:'detail',Type:'Screenshot',Url:'https://cdn.example/detail.png'}]}");
    static JObject Schedule(string kind = "release") => JObject.Parse("{scheduleId:'season',waves:[{id:'wave',kind:'" + kind + "',startsAtUtc:'2026-11-01T00:00:00Z',endsAtUtc:null,paused:false,productIds:['arena']}]}");

    [Test] public void ReleasePreservesPricesTitlesAndCustomProperties()
    {
        var item = Item(); var after = DlcPlayFabPlan.Apply(item, "arena", Schedule());
        Assert.IsTrue(JToken.DeepEquals(item["PriceOptions"], after["PriceOptions"]));
        Assert.IsTrue(JToken.DeepEquals(item["Title"], after["Title"]));
        Assert.AreEqual("keep", (string)after["DisplayProperties"]["custom"]);
        Assert.IsNotNull(after["StartDate"]);
        Assert.IsFalse((bool)after["IsHidden"]); Assert.AreEqual(JTokenType.Null, after["EndDate"].Type);
    }
    [Test] public void PromotionDoesNotChangeAvailability()
    {
        var item = Item(); item["IsHidden"] = true;
        var after = DlcPlayFabPlan.Apply(item, "arena", Schedule("promotion")); Assert.IsTrue((bool)after["IsHidden"]);
        Assert.IsNull(after["StartDate"]); Assert.AreEqual(1, ((JArray)after["DisplayProperties"]["dlcPump"]["promotions"]).Count);
    }
    [Test] public void PausedReleaseHidesTheOffer()
    { var schedule = Schedule(); schedule["waves"][0]["paused"] = true; Assert.IsTrue((bool)DlcPlayFabPlan.Apply(Item(), "arena", schedule)["IsHidden"]); }
    [Test] public void DifferentScheduleCannotTakeOverAnItem()
    {
        var item = Item(); item["DisplayProperties"]["dlcPump"] = JObject.Parse("{scheduleId:'other'}");
        Assert.Throws<InvalidOperationException>(() => DlcPlayFabPlan.Apply(item, "arena", Schedule()));
    }
    [Test] public void ProductsOutsideTheScheduleAreUnchanged()
    {
        var item = Item(); var after = DlcPlayFabPlan.Apply(item, "other_product", Schedule());
        Assert.IsTrue(JToken.DeepEquals(DlcPlayFabPlan.Editable(item), after));
    }
}
