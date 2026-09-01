using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Application.Compare;

public sealed class MultiBandImageCandidateIndex : IImageCandidateIndex
{
    private readonly Dictionary<BandKey, List<ImageFingerprint>> buckets = [];
    private int bandCount;

    public long CandidateCount { get; private set; }

    public void Reset(int maxHammingDistance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHammingDistance, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxHammingDistance, 32);

        bandCount = maxHammingDistance + 1;
        buckets.Clear();
        CandidateCount = 0;
    }

    public void Add(ImageFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        EnsureInitialized();

        foreach (var hash in fingerprint.CandidateHashes.Distinct())
        {
            foreach (var key in GetBandKeys(hash))
            {
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets.Add(key, bucket);
                }

                bucket.Add(fingerprint);
            }
        }
    }

    public IEnumerable<ImageFingerprint> FindCandidates(
        ImageFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        EnsureInitialized();

        var matchesByPath = new Dictionary<string, ImageFingerprint>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var hash in fingerprint.CandidateHashes.Distinct())
        {
            foreach (var key in GetBandKeys(hash))
            {
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    continue;
                }

                foreach (var candidate in bucket)
                {
                    matchesByPath.TryAdd(candidate.FullPath, candidate);
                }
            }
        }

        var matches = matchesByPath.Values
            .OrderBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CandidateCount += matches.Length;
        return matches;
    }

    private void EnsureInitialized()
    {
        if (bandCount == 0)
        {
            throw new InvalidOperationException(
                "Reset must be called before using the candidate index.");
        }
    }

    private IEnumerable<BandKey> GetBandKeys(ulong hash)
    {
        var minimumWidth = 64 / bandCount;
        var widerBandCount = 64 % bandCount;
        var offset = 0;

        for (var bandIndex = 0; bandIndex < bandCount; bandIndex++)
        {
            var width = minimumWidth + (bandIndex < widerBandCount ? 1 : 0);
            var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            yield return new BandKey(bandIndex, (hash >> offset) & mask);
            offset += width;
        }
    }

    private readonly record struct BandKey(int Index, ulong Value);
}
