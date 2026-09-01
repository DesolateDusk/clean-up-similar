using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;

namespace FileComparer.Application.Compare;

/// <summary>
/// Compares every sampled frame of aspect-compatible video pairs. Frame hashes
/// are already decoded and cached, so the N² phase never reopens a video.
/// </summary>
public sealed class VideoSimilarityCompareStrategy(
    IVideoFingerprintService fingerprintService)
{
    public async Task<IReadOnlyList<CompareGroup>> CompareAsync(
        CompareOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var videoFiles = context.Files
            .Where(file => VideoFileRules.IsSupported(file.Extension))
            .OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var total = context.Files.Count;
        var processed = total - videoFiles.Length;
        var valid = new List<(FileEntry File, VideoFingerprint Fingerprint)>();
        ReportFingerprintProgress(context, total, processed, currentPath: null);

        foreach (var file in videoFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await fingerprintService
                .GetFingerprintAsync(
                    file,
                    context.Request.ForceRefresh,
                    cancellationToken)
                .ConfigureAwait(false);
            ReportIssues(context, result.Issues);

            if (result.Value?.Frames.Count > 0)
            {
                valid.Add((file, result.Value));
            }
            else if (result.IsSuccess)
            {
                context.ReportIssue(new ScanIssue(
                    ScanStage.Fingerprinting,
                    file.FullPath,
                    ScanIssueCategory.InvalidVideo,
                    "影片指紋沒有可用抽偵，已略過。"));
            }

            processed++;
            ReportFingerprintProgress(context, total, processed, file.FullPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var groups = GroupMatches(valid, cancellationToken);
        context.Progress?.Report(new ScanProgress(
            ScanStage.Grouping,
            total,
            total,
            100));
        return groups;
    }

    private static CompareGroup[] GroupMatches(
        List<(FileEntry File, VideoFingerprint Fingerprint)> items,
        CancellationToken cancellationToken)
    {
        var sets = new PathDisjointSet();
        var matches = new Dictionary<string, VideoPairMatch>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            sets.Add(item.File.FullPath);
        }

        for (var leftIndex = 0; leftIndex < items.Count; leftIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = items[leftIndex];
            for (var rightIndex = leftIndex + 1;
                 rightIndex < items.Count;
                 rightIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var right = items[rightIndex];
                if (!AreAspectRatiosCompatible(
                        left.Fingerprint,
                        right.Fingerprint))
                {
                    continue;
                }

                var result = CompareFrames(left.Fingerprint, right.Fingerprint);
                if (!result.IsSimilar)
                {
                    continue;
                }

                sets.Union(left.File.FullPath, right.File.FullPath);
                matches[PairKey(left.File.FullPath, right.File.FullPath)] = result;
            }
        }

        return items
            .GroupBy(
                item => sets.Find(item.File.FullPath),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => CreateGroup(group.ToArray(), matches))
            .OrderBy(
                group => group.Items[0].File.FullPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static VideoPairMatch CompareFrames(
        VideoFingerprint left,
        VideoFingerprint right)
    {
        var leftFrames = left.Frames
            .Select((frame, index) => new IndexedFrame(index, frame))
            .Where(frame => frame.Value.Contrast >= MediaSimilarityPolicy.MinimumFrameContrast)
            .ToArray();
        var rightFrames = right.Frames
            .Select((frame, index) => new IndexedFrame(index, frame))
            .Where(frame => frame.Value.Contrast >= MediaSimilarityPolicy.MinimumFrameContrast)
            .ToArray();
        if (leftFrames.Length == 0 || rightFrames.Length == 0)
        {
            return VideoPairMatch.NotSimilar;
        }

        var candidatePairs = leftFrames
            .SelectMany(
                leftFrame => rightFrames,
                (leftFrame, rightFrame) => new FramePair(
                    leftFrame.Index,
                    rightFrame.Index,
                    PerceptualHashDistance.Calculate(
                        leftFrame.Value.PerceptualHash,
                        rightFrame.Value.PerceptualHash)))
            .Where(pair => pair.Distance <= MediaSimilarityPolicy.VideoFrameMaxHammingDistance)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.LeftIndex)
            .ThenBy(pair => pair.RightIndex)
            .ToArray();

        var usedLeft = new HashSet<int>();
        var usedRight = new HashSet<int>();
        var selected = new List<FramePair>();
        foreach (var candidate in candidatePairs)
        {
            if (usedLeft.Add(candidate.LeftIndex) &&
                usedRight.Add(candidate.RightIndex))
            {
                selected.Add(candidate);
            }
        }

        var matchedRatio = selected.Count /
            (double)Math.Min(leftFrames.Length, rightFrames.Length);
        var averageDistance = selected.Count == 0
            ? 64d
            : selected.Average(pair => pair.Distance);
        var score = Math.Clamp(
            matchedRatio * (1d - averageDistance / 64d),
            0,
            1);
        var isSimilar = selected.Count >= MediaSimilarityPolicy.VideoMinimumDistinctMatches &&
                        matchedRatio >= MediaSimilarityPolicy.VideoMinimumMatchedFrameRatio;
        return new VideoPairMatch(
            selected.Count,
            matchedRatio,
            averageDistance,
            score,
            isSimilar);
    }

    private static CompareGroup CreateGroup(
        IReadOnlyCollection<(FileEntry File, VideoFingerprint Fingerprint)> candidates,
        IReadOnlyDictionary<string, VideoPairMatch> matches)
    {
        var ranked = candidates
            .OrderByDescending(candidate =>
                (long)candidate.Fingerprint.Width * candidate.Fingerprint.Height)
            .ThenByDescending(candidate => candidate.Fingerprint.Duration)
            .ThenByDescending(candidate => candidate.File.Size)
            .ThenByDescending(candidate => candidate.File.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.File.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var keep = ranked[0];
        var items = ranked
            .Select((candidate, index) =>
            {
                matches.TryGetValue(
                    PairKey(keep.File.FullPath, candidate.File.FullPath),
                    out var match);
                return new CompareItem(
                    candidate.File,
                    HammingDistance: index == 0
                        ? 0
                        : match.IsSimilar
                            ? (int)Math.Round(match.AverageDistance)
                            : null,
                    IsSuggestedKeep: index == 0,
                    Video: candidate.Fingerprint,
                    SimilarityScore: index == 0
                        ? 1
                        : match.IsSimilar
                            ? match.Score
                            : null);
            })
            .ToArray();
        return new CompareGroup(
            Guid.NewGuid(),
            CompareGroupType.SimilarVideo,
            items);
    }

    private static bool AreAspectRatiosCompatible(
        VideoFingerprint left,
        VideoFingerprint right)
    {
        if (left.Width <= 0 || left.Height <= 0 ||
            right.Width <= 0 || right.Height <= 0)
        {
            return true;
        }

        var leftRatio = (double)left.Width / left.Height;
        var rightRatio = (double)right.Width / right.Height;
        return Math.Abs(leftRatio - rightRatio) /
               Math.Max(leftRatio, rightRatio) <=
               MediaSimilarityPolicy.VideoMaximumAspectRatioDifference;
    }

    private static string PairKey(string left, string right) =>
        string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? string.Concat(left, "\0", right)
            : string.Concat(right, "\0", left);

    private static void ReportFingerprintProgress(
        CompareOperationContext context,
        int total,
        int processed,
        string? currentPath)
    {
        var percentage = total == 0
            ? 100
            : Math.Clamp(processed * 100d / total, 0, 100);
        context.Progress?.Report(new ScanProgress(
            ScanStage.Fingerprinting,
            total,
            processed,
            percentage,
            currentPath));
    }

    private static void ReportIssues(
        CompareOperationContext context,
        IEnumerable<ScanIssue> issues)
    {
        foreach (var issue in issues)
        {
            context.ReportIssue(issue);
        }
    }

    private readonly record struct IndexedFrame(
        int Index,
        VideoFrameFingerprint Value);

    private readonly record struct FramePair(
        int LeftIndex,
        int RightIndex,
        int Distance);

    private readonly record struct VideoPairMatch(
        int MatchedFrameCount,
        double MatchedRatio,
        double AverageDistance,
        double Score,
        bool IsSimilar)
    {
        public static VideoPairMatch NotSimilar => new(0, 0, 64, 0, false);
    }
}
