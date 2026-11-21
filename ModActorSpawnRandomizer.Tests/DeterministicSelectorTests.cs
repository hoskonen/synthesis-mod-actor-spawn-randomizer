using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mutagen.Bethesda.Plugins;

namespace ModActorSpawnRandomizer.Tests;

[TestClass]
public sealed class DeterministicSelectorTests
{
    private static readonly ModKey SourceMod = ModKey.FromFileName("mihailbats.esp");

    [TestMethod]
    public void ReproducesSpectersV1GoldenVectorsWithSpectersDomain()
    {
        // Values frozen in the Specters patcher's own test suite; proves the hashing is byte-for-byte reused.
        var specters = ModKey.FromFileName("SpectersOfSkyrim.esp");
        const string domain = "SpectersOfSkyrimRandomizer/v1";
        Assert.AreEqual(9_427_528_420_039_680_475UL, DeterministicSelector.ComputeScore(domain, new FormKey(specters, 0x0008F5), 38174));
        Assert.AreEqual(14_887_669_196_581_907_784UL, DeterministicSelector.ComputeScore(domain, new FormKey(specters, 0x0009C8), 38174));
        Assert.AreEqual(2_561_833_816_178_138_347UL, DeterministicSelector.ComputeScore(domain, new FormKey(specters, 0x0008F5), -1));
    }

    [TestMethod]
    public void GoldenVectorsFreezeOwnV1Contract()
    {
        Assert.AreEqual(GoldenA, DeterministicSelector.ComputeScore(new FormKey(SourceMod, 0x000803), 38174));
        Assert.AreEqual(GoldenB, DeterministicSelector.ComputeScore(new FormKey(ModKey.FromFileName("Hand Placed Enemies.esp"), 0x000AFB), 38174));
        Assert.AreEqual(GoldenC, DeterministicSelector.ComputeScore(new FormKey(SourceMod, 0x000803), -1));
    }

    // Frozen from the first run of the v1 implementation. Changing them means changing every user's selection.
    private const ulong GoldenA = 13_470_156_017_296_196_843UL;
    private const ulong GoldenB = 4_933_540_479_921_001_440UL;
    private const ulong GoldenC = 7_482_990_137_199_017_135UL;

    [TestMethod]
    public void PluginFileNameIsCaseInsensitive()
    {
        var lower = new FormKey(ModKey.FromFileName("mihailbats.esp"), 0x000803);
        var upper = new FormKey(ModKey.FromFileName("MihailBats.ESP"), 0x000803);
        Assert.AreEqual(DeterministicSelector.ComputeScore(lower, 1), DeterministicSelector.ComputeScore(upper, 1));
    }

    [TestMethod]
    public void ZeroPercentAlwaysRejectsAndHundredAlwaysSelects()
    {
        foreach (var key in Fixture())
        {
            Assert.IsFalse(DeterministicSelector.IsSelected(key, 38174, 0.0));
            Assert.IsTrue(DeterministicSelector.IsSelected(key, 38174, 100.0));
        }
    }

    [TestMethod]
    public void InvalidProbabilitiesAreRejected()
    {
        var key = new FormKey(SourceMod, 0x000803);
        foreach (var probability in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.001, 100.001 })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DeterministicSelector.IsSelected(key, 0, probability));
        }
    }

    [TestMethod]
    public void EnumerationOrderDoesNotChangeResults()
    {
        var fixture = Fixture().ToArray();
        CollectionAssert.AreEquivalent(Select(fixture, 38174, 30), Select(fixture.Reverse(), 38174, 30));
    }

    [TestMethod]
    public void UnrelatedIdentitiesDoNotChangeExistingResults()
    {
        var fixture = Fixture().ToArray();
        var before = fixture.ToDictionary(k => k, k => DeterministicSelector.IsSelected(k, 38174, 30));
        var otherPlugin = new FormKey(ModKey.FromFileName("Other.esp"), 0x000900);
        _ = DeterministicSelector.IsSelected(otherPlugin, 38174, 30);
        foreach (var (key, selected) in before)
            Assert.AreEqual(selected, DeterministicSelector.IsSelected(key, 38174, 30));
    }

    [TestMethod]
    public void SameLocalIdInDifferentPluginsIsIndependent()
    {
        var a = Enumerable.Range(0, 200).Select(i => new FormKey(ModKey.FromFileName("A.esp"), (uint)(0x800 + i)));
        var b = Enumerable.Range(0, 200).Select(i => new FormKey(ModKey.FromFileName("B.esp"), (uint)(0x800 + i)));
        var sa = a.Select(k => DeterministicSelector.IsSelected(k, 1, 50)).ToArray();
        var sb = b.Select(k => DeterministicSelector.IsSelected(k, 1, 50)).ToArray();
        Assert.IsTrue(sa.Zip(sb).Any(p => p.First != p.Second));
    }

    [TestMethod]
    public void DifferentSeedsChangeSelection()
    {
        var fixture = Fixture().ToArray();
        var one = fixture.Select(k => DeterministicSelector.IsSelected(k, 38174, 50)).ToArray();
        var two = fixture.Select(k => DeterministicSelector.IsSelected(k, 12345, 50)).ToArray();
        Assert.IsTrue(one.Zip(two).Any(p => p.First != p.Second));
    }

    [TestMethod]
    public void SelectedShareApproximatesProbability()
    {
        var keys = Enumerable.Range(0, 10_000).Select(i => new FormKey(SourceMod, (uint)(0x800 + i))).ToArray();
        var share = keys.Count(k => DeterministicSelector.IsSelected(k, 7, 30)) / (double)keys.Length;
        Assert.IsTrue(share is > 0.28 and < 0.32, $"share {share}");
    }

    private static IEnumerable<FormKey> Fixture() =>
        Enumerable.Range(0, 101).Select(i => new FormKey(SourceMod, (uint)(0x800 + i * 3)));

    private static FormKey[] Select(IEnumerable<FormKey> keys, int seed, double probability) =>
        keys.Where(k => DeterministicSelector.IsSelected(k, seed, probability)).OrderBy(k => k.ID).ToArray();
}
