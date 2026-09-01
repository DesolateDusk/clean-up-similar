using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;

namespace FileComparer.Application.Compare;

/// <summary>
/// Finds image candidates with cached pHashes, then delegates the expensive
/// local-feature decision to an Infrastructure verifier only for those pairs.
/// </summary>
public sealed class ImageSimilarityCompareStrategy(
    IImageFingerprintService fingerprintService,
    IImageCandidateIndex candidateIndex,
    IImageSimilarityVerifier verifier)
{
    public async Task<IReadOnlyList<CompareGroup>> CompareAsync(
        CompareOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var imageFiles = context.Files
            .Where(file => ImageFileRules.IsSupported(file.Extension))
            .OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var total = context.Files.Count;
        var processed = total - imageFiles.Length;
        var valid = new List<(FileEntry File, ImageFingerprint Fingerprint)>();
        ReportFingerprintProgress(context, total, processed, currentPath: null);

        foreach (var file in imageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await fingerprintService
                .GetFingerprintAsync(
                    file,
                    context.Request.ForceRefresh,
                    cancellationToken)
                .ConfigureAwait(false);
            ReportIssues(context, result.Issues);

            if (result.Value?.PerceptualHash is not null &&
                result.Value.CandidateHashes.Count > 0)
            {
                valid.Add((file, result.Value));
            }
            else if (result.IsSuccess)
            {
                context.ReportIssue(new ScanIssue(
                    ScanStage.Fingerprinting,
                    file.FullPath,
                    ScanIssueCategory.InvalidImage,
                    "圖片指紋沒有可用的候選 pHash，已略過。"));
            }

            processed++;
            ReportFingerprintProgress(context, total, processed, file.FullPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var groups = await GroupMatchesAsync(valid, context, cancellationToken)
            .ConfigureAwait(false);
        context.Progress?.Report(new ScanProgress(
            ScanStage.Grouping,
            total,
            total,
            100));
        return groups;
    }

    private async Task<CompareGroup[]> GroupMatchesAsync(
        IReadOnlyList<(FileEntry File, ImageFingerprint Fingerprint)> items,
        CompareOperationContext context,
        CancellationToken cancellationToken)
    {
        candidateIndex.Reset(MediaSimilarityPolicy.ImageCandidateMaxHammingDistance);
        var sets = new PathDisjointSet();
        var matches = new Dictionary<string, ImageSimilarityResult>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sets.Add(item.File.FullPath);

            foreach (var candidate in candidateIndex.FindCandidates(item.Fingerprint))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hashDistance = PerceptualHashDistance.CalculateMinimum(
                    candidate.CandidateHashes,
                    item.Fingerprint.CandidateHashes);
                if (hashDistance > MediaSimilarityPolicy.ImageCandidateMaxHammingDistance)
                {
                    continue;
                }

                var verification = await verifier
                    .VerifyAsync(candidate, item.Fingerprint, cancellationToken)
                    .ConfigureAwait(false);
                ReportIssues(context, verification.Issues);
                if (verification.Value?.IsSimilar != true)
                {
                    continue;
                }

                var result = verification.Value with { HashDistance = hashDistance };
                sets.Union(candidate.FullPath, item.File.FullPath);
                matches[PairKey(candidate.FullPath, item.File.FullPath)] = result;
            }

            candidateIndex.Add(item.Fingerprint);
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

    private static CompareGroup CreateGroup(
        IReadOnlyCollection<(FileEntry File, ImageFingerprint Fingerprint)> candidates,
        Dictionary<string, ImageSimilarityResult> matches)
    {
        var ranked = candidates
            .OrderByDescending(candidate =>
                (long)candidate.Fingerprint.Width * candidate.Fingerprint.Height)
            .ThenByDescending(candidate => MetadataCompleteness(candidate.Fingerprint))
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
                    out var verification);
                return new CompareItem(
                    candidate.File,
                    candidate.Fingerprint,
                    PerceptualHashDistance.CalculateMinimum(
                        keep.Fingerprint.CandidateHashes,
                        candidate.Fingerprint.CandidateHashes),
                    IsSuggestedKeep: index == 0,
                    SimilarityScore: index == 0
                        ? 1
                        : verification?.FinalScore);
            })
            .ToArray();

        return new CompareGroup(
            Guid.NewGuid(),
            CompareGroupType.SimilarImage,
            items);
    }

    private static string PairKey(string left, string right) =>
        string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? string.Concat(left, "\0", right)
            : string.Concat(right, "\0", left);

    private static int MetadataCompleteness(ImageFingerprint fingerprint) =>
        (fingerprint.Width > 0 ? 1 : 0) +
        (fingerprint.Height > 0 ? 1 : 0) +
        (fingerprint.AspectRatio > 0 ? 1 : 0);

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
}
