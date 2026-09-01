using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Application.Compare;

/// <summary>
/// Single user-visible content mode. Images and videos are processed together
/// in one scan but returned in distinct group types for clear review.
/// </summary>
public sealed class SimilarMediaCompareStrategy(
    ImageSimilarityCompareStrategy imageStrategy,
    VideoSimilarityCompareStrategy videoStrategy)
    : ICompareStrategy
{
    public CompareMode Mode => CompareMode.SimilarMedia;

    public async Task<IReadOnlyList<CompareGroup>> CompareAsync(
        CompareOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var images = await imageStrategy
            .CompareAsync(context, cancellationToken)
            .ConfigureAwait(false);
        var videos = await videoStrategy
            .CompareAsync(context, cancellationToken)
            .ConfigureAwait(false);
        return images
            .Concat(videos)
            .OrderBy(group => group.Type)
            .ThenBy(
                group => group.Items[0].File.FullPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
