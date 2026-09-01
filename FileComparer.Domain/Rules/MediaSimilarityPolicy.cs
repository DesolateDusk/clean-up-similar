namespace FileComparer.Domain.Rules;

/// <summary>
/// Holds the single, intentionally non-user-configurable media comparison
/// policy. Changing a fingerprint-affecting value requires a version update.
/// </summary>
public static class MediaSimilarityPolicy
{
    public const int ImageCandidateMaxHammingDistance = 10;
    public const int ImageDirectPHashMaxHammingDistance = 10;
    public const int ImageMinimumFeatureMatches = 12;
    public const int ImageMinimumInlierMatches = 8;
    public const double ImageMinimumInlierRatio = 0.35;
    public const int ImageOrbFeatureCount = 1_000;
    public const int VideoSampleFrameCount = 12;
    public const int VideoFrameMaxHammingDistance = 12;
    public const int VideoMinimumDistinctMatches = 4;
    public const double VideoMinimumMatchedFrameRatio = 0.30;
    public const double VideoMaximumAspectRatioDifference = 0.15;
    public const double MinimumFrameContrast = 8;
    public const int MaximumMediaProcessingConcurrency = 2;
    public const string VideoFingerprintVersion = "video-frame-phash-v1-n12";
}
