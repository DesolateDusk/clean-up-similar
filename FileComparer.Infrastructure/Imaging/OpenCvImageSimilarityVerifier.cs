using System.Numerics;
using System.Security;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace FileComparer.Infrastructure.Imaging;

/// <summary>
/// Verifies only pre-selected image candidates with ORB feature matching and
/// RANSAC geometry validation. It intentionally does not choose candidates.
/// </summary>
public sealed class OpenCvImageSimilarityVerifier(
    IMediaProcessingScheduler scheduler,
    ILogger<OpenCvImageSimilarityVerifier> logger)
    : IImageSimilarityVerifier
{
    private static readonly Action<ILogger, string, string, Exception?>
        LogVerificationFailure = LoggerMessage.Define<string, string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1304, nameof(LogVerificationFailure)),
            "Unable to verify image similarity between {SourcePath} and {TargetPath}");

    public async Task<ProcessingResult<ImageSimilarityResult>> VerifyAsync(
        ImageFingerprint source,
        ImageFingerprint target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            var result = await scheduler.RunAsync(
                    token => Task.Run(
                        () => Verify(source, target, token),
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            return ProcessingResult.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            LogVerificationFailure(logger, source.FullPath, target.FullPath, exception);
            return ProcessingResult.Failure<ImageSimilarityResult>(
                CreateIssue(source.FullPath, exception));
        }
    }

    private static ImageSimilarityResult Verify(
        ImageFingerprint source,
        ImageFingerprint target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sourceImage = Cv2.ImRead(source.FullPath, ImreadModes.Grayscale);
        using var targetImage = Cv2.ImRead(target.FullPath, ImreadModes.Grayscale);
        if (sourceImage.Empty() || targetImage.Empty())
        {
            throw new InvalidDataException("OpenCV could not decode an image candidate.");
        }

        using var sourceDescriptors = new Mat();
        using var targetDescriptors = new Mat();
        using var orb = ORB.Create(nFeatures: MediaSimilarityPolicy.ImageOrbFeatureCount);
        orb.DetectAndCompute(
            sourceImage,
            null,
            out KeyPoint[] sourceKeyPoints,
            sourceDescriptors);
        orb.DetectAndCompute(
            targetImage,
            null,
            out KeyPoint[] targetKeyPoints,
            targetDescriptors);
        cancellationToken.ThrowIfCancellationRequested();

        var hashDistance = MinimumHashDistance(source, target);
        var directHashDistance = DirectHashDistance(source, target);
        var hasStrongDirectPHashMatch = directHashDistance <=
                                        MediaSimilarityPolicy.ImageDirectPHashMaxHammingDistance;
        if (sourceDescriptors.Empty() || targetDescriptors.Empty())
        {
            return GeometryUnavailable(
                hashDistance,
                directHashDistance,
                hasStrongDirectPHashMatch);
        }

        using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
        var forwardMatches = FindRatioMatches(
            matcher.KnnMatch(sourceDescriptors, targetDescriptors, k: 2));
        var reverseMatches = FindRatioMatches(
            matcher.KnnMatch(targetDescriptors, sourceDescriptors, k: 2))
            .ToDictionary(match => match.QueryIdx, match => match.TrainIdx);
        var goodMatches = forwardMatches
            .Where(match => reverseMatches.TryGetValue(match.TrainIdx, out var reverseTrainIndex) &&
                            reverseTrainIndex == match.QueryIdx)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        if (goodMatches.Length < MediaSimilarityPolicy.ImageMinimumFeatureMatches)
        {
            return GeometryUnavailable(
                hashDistance,
                directHashDistance,
                hasStrongDirectPHashMatch,
                goodMatches.Length);
        }

        var sourcePoints = goodMatches
            .Select(match => new Point2d(
                sourceKeyPoints[match.QueryIdx].Pt.X,
                sourceKeyPoints[match.QueryIdx].Pt.Y))
            .ToArray();
        var targetPoints = goodMatches
            .Select(match => new Point2d(
                targetKeyPoints[match.TrainIdx].Pt.X,
                targetKeyPoints[match.TrainIdx].Pt.Y))
            .ToArray();
        using var inlierMask = new Mat();
        using var homography = Cv2.FindHomography(
            sourcePoints,
            targetPoints,
            HomographyMethods.Ransac,
            3,
            inlierMask);
        var inlierCount = homography.Empty() || inlierMask.Empty()
            ? 0
            : Cv2.CountNonZero(inlierMask);
        var inlierRatio = goodMatches.Length == 0
            ? 0
            : (double)inlierCount / goodMatches.Length;
        var isSimilar = inlierCount >= MediaSimilarityPolicy.ImageMinimumInlierMatches &&
                        inlierRatio >= MediaSimilarityPolicy.ImageMinimumInlierRatio;
        var featureScore = Math.Min(
            1d,
            goodMatches.Length / (double)(MediaSimilarityPolicy.ImageOrbFeatureCount / 4));
        var finalScore = Math.Clamp(
            0.20 * (1d - hashDistance / 64d) +
            0.25 * featureScore +
            0.55 * inlierRatio,
            0,
            1);
        return new ImageSimilarityResult(
            hashDistance,
            goodMatches.Length,
            inlierRatio,
            finalScore,
            isSimilar);
    }

    private static IEnumerable<DMatch> FindRatioMatches(IEnumerable<DMatch[]> rawMatches) =>
        rawMatches
            .Where(match => match.Length >= 2 &&
                            match[0].Distance < match[1].Distance * 0.75)
            .Select(match => match[0]);

    // V1 accepted a close global pHash for feature-poor images. Keep that
    // compatibility path only when ORB cannot form a geometry estimate; once
    // sufficient local features exist, RANSAC remains the deciding signal.
    private static ImageSimilarityResult GeometryUnavailable(
        int hashDistance,
        int directHashDistance,
        bool hasStrongDirectPHashMatch,
        int matchedFeatureCount = 0) => new(
        hashDistance,
        matchedFeatureCount,
        InlierRatio: 0,
        FinalScore: hasStrongDirectPHashMatch
            ? 1d - directHashDistance / 64d
            : 0,
        IsSimilar: hasStrongDirectPHashMatch);

    private static int DirectHashDistance(
        ImageFingerprint source,
        ImageFingerprint target) => source.PerceptualHash is ulong sourceHash &&
                                    target.PerceptualHash is ulong targetHash
        ? BitOperations.PopCount(sourceHash ^ targetHash)
        : 64;

    private static int MinimumHashDistance(
        ImageFingerprint source,
        ImageFingerprint target) => PerceptualHashDistance.CalculateMinimum(
            source.CandidateHashes,
            target.CandidateHashes);

    private static ScanIssue CreateIssue(string path, Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException or SecurityException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.AccessDenied,
                "沒有權限讀取圖片候選。"),
            FileNotFoundException or DirectoryNotFoundException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.NotFound,
                "圖片候選已不存在。"),
            PathTooLongException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.PathTooLong,
                "圖片候選路徑過長。"),
            IOException when exception is not InvalidDataException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.Locked,
                "圖片候選目前無法讀取。"),
            _ => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.InvalidImage,
                "圖片候選無法完成局部特徵驗證。")
        };

    private static bool IsExpectedError(Exception exception) =>
        exception is OpenCVException or InvalidDataException or IOException or
            UnauthorizedAccessException or PathTooLongException or
            NotSupportedException or SecurityException;
}
