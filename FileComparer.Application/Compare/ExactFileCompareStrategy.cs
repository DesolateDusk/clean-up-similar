using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Application.Compare;

/// <summary>
/// Narrows exact-file candidates by size and quick hash before calculating the
/// authoritative full SHA-256 value.
/// </summary>
public sealed class ExactFileCompareStrategy(
    IQuickHashService quickHashService,
    IHashService hashService)
    : ICompareStrategy
{
    public CompareMode Mode => CompareMode.ExactFiles;

    public async Task<IReadOnlyList<CompareGroup>> CompareAsync(
        CompareOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var groups = new List<CompareGroup>();
        var candidatesBySize = context.Files
            .GroupBy(file => file.Size)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key)
            .ToArray();
        var total = context.Files.Count;
        var candidateCount = candidatesBySize.Sum(group => group.Count());
        var processed = total - candidateCount;

        ReportProgress(context, total, processed);

        foreach (var sizeGroup in candidatesBySize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quickCandidates = new List<(FileEntry File, string Hash)>();

            foreach (var file in sizeGroup.OrderBy(
                         item => item.FullPath,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await quickHashService
                    .GetQuickHashAsync(
                        file,
                        context.Request.ForceRefresh,
                        cancellationToken)
                    .ConfigureAwait(false);
                ReportIssues(context, result.Issues);

                if (result.Value is not null)
                {
                    quickCandidates.Add((file, result.Value));
                }
            }

            foreach (var quickGroup in quickCandidates
                         .GroupBy(item => item.Hash, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullCandidates = new List<(FileEntry File, string Hash)>();

                foreach (var candidate in quickGroup)
                {
                    var result = await hashService
                        .GetSha256Async(
                            candidate.File,
                            context.Request.ForceRefresh,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ReportIssues(context, result.Issues);

                    if (result.Value is not null)
                    {
                        fullCandidates.Add((candidate.File, result.Value));
                    }
                }

                groups.AddRange(fullCandidates
                    .GroupBy(item => item.Hash, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(CreateGroup));
            }

            processed += sizeGroup.Count();
            ReportProgress(context, total, processed);
        }

        context.Progress?.Report(
            new ScanProgress(
                ScanStage.Grouping,
                total,
                total,
                100));

        return groups
            .OrderBy(
                group => group.Items[0].File.FullPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CompareGroup CreateGroup(
        IEnumerable<(FileEntry File, string Hash)> candidates)
    {
        var ranked = candidates
            .Select(candidate => candidate.File)
            .OrderByDescending(file => file.Size)
            .ThenByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(
                file => file.FullPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var items = ranked
            .Select((file, index) => new CompareItem(
                file,
                IsSuggestedKeep: index == 0))
            .ToArray();

        return new CompareGroup(
            Guid.NewGuid(),
            CompareGroupType.ExactDuplicate,
            items);
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

    private static void ReportProgress(
        CompareOperationContext context,
        int total,
        int processed)
    {
        var percentage = total == 0
            ? 100
            : Math.Clamp(processed * 100d / total, 0, 100);

        context.Progress?.Report(
            new ScanProgress(
                ScanStage.Fingerprinting,
                total,
                processed,
                percentage));
    }
}
