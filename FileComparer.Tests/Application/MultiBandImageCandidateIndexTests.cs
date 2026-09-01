using FileComparer.Application.Compare;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;
using Xunit;

namespace FileComparer.Tests.Application;

public sealed class MultiBandImageCandidateIndexTests
{
    [Fact]
    public void FindsEveryPreviouslyAddedHashWithinConfiguredDistance()
    {
        var index = new MultiBandImageCandidateIndex();
        const ulong originalHash = 0x0123456789ABCDEF;
        var original = Fingerprint("original.jpg", originalHash);
        index.Reset(maxHammingDistance: 3);
        index.Add(original);

        for (var firstBit = 0; firstBit < 64; firstBit += 7)
        {
            var variantHash = originalHash ^ (1UL << firstBit);
            variantHash ^= 1UL << ((firstBit + 19) % 64);
            variantHash ^= 1UL << ((firstBit + 41) % 64);

            var candidates = index.FindCandidates(
                    Fingerprint($"variant-{firstBit}.jpg", variantHash))
                .ToArray();

            Assert.Contains(original, candidates);
        }
    }

    [Fact]
    public void ReturnsCandidateOnlyOnceWhenSeveralBandsMatch()
    {
        var index = new MultiBandImageCandidateIndex();
        var original = Fingerprint("original.jpg", 0);
        index.Reset(maxHammingDistance: 10);
        index.Add(original);

        var candidates = index.FindCandidates(
                Fingerprint("same.jpg", 0))
            .ToArray();

        Assert.Equal(original, Assert.Single(candidates));
        Assert.Equal(1, index.CandidateCount);
    }

    [Fact]
    public void ResetClearsPreviousScanState()
    {
        var index = new MultiBandImageCandidateIndex();
        var original = Fingerprint("original.jpg", 0);
        index.Reset(maxHammingDistance: 10);
        index.Add(original);
        index.Reset(maxHammingDistance: 10);

        var candidates = index.FindCandidates(
                Fingerprint("same.jpg", 0))
            .ToArray();

        Assert.Empty(candidates);
        Assert.Equal(0, index.CandidateCount);
    }

    [Theory]
    [InlineData(0UL, 0UL, 0)]
    [InlineData(0UL, 1UL, 1)]
    [InlineData(0UL, ulong.MaxValue, 64)]
    [InlineData(0b1010UL, 0b0101UL, 4)]
    public void CalculatesHammingDistance(
        ulong left,
        ulong right,
        int expected)
    {
        Assert.Equal(
            expected,
            PerceptualHashDistance.Calculate(left, right));
    }

    private static ImageFingerprint Fingerprint(
        string name,
        ulong hash) =>
        new(
            Path.Combine("C:\\images", name),
            100,
            100,
            1,
            hash);
}
