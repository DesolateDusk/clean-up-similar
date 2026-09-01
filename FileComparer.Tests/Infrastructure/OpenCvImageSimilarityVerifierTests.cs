using FileComparer.Domain.Models;
using FileComparer.Infrastructure.Imaging;
using FileComparer.Infrastructure.Media;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class OpenCvImageSimilarityVerifierTests
{
    [Fact]
    public async Task VerifiesRequiredImageVariantsAfterTheyBecomeCandidates()
    {
        using var directory = new TemporaryDirectory();
        var originalPath = directory.GetPath("original.png");
        CreateFeatureRichImage(originalPath);
        var variants = new Dictionary<string, Action<string>>
        {
            ["crop"] = CreateCrop,
            ["rotation"] = CreateRotation,
            ["local-zoom"] = CreateLocalZoom,
            ["border"] = CreateBorder,
            ["watermark"] = CreateWatermark,
            ["color"] = CreateColorAdjustment,
            ["reencode"] = CreateReencodedJpeg
        };
        using var scheduler = new MediaProcessingScheduler();
        var fingerprints = new OpenCvImageFingerprintService(
            new StubCacheRepository(),
            TimeProvider.System,
            NullLogger<OpenCvImageFingerprintService>.Instance);
        var verifier = new OpenCvImageSimilarityVerifier(
            scheduler,
            NullLogger<OpenCvImageSimilarityVerifier>.Instance);
        var original = await FingerprintAsync(fingerprints, originalPath);

        foreach (var (name, createVariant) in variants)
        {
            var path = directory.GetPath($"{name}." +
                (name == "reencode" ? "jpg" : "png"));
            createVariant(path);
            var variant = await FingerprintAsync(fingerprints, path);
            var verification = await verifier.VerifyAsync(
                original,
                variant,
                TestContext.Current.CancellationToken);

            Assert.True(
                MinimumDistance(original, variant) <= 10,
                $"{name} should enter the fixed pHash candidate set.");
            Assert.True(
                verification.Value?.IsSimilar == true,
                $"{name} should pass ORB geometry verification: {verification.Value}.");
        }
    }

    [Fact]
    public async Task RejectsUnrelatedImage()
    {
        using var directory = new TemporaryDirectory();
        var originalPath = directory.GetPath("original.png");
        var unrelatedPath = directory.GetPath("unrelated.png");
        CreateFeatureRichImage(originalPath);
        CreateUnrelatedImage(unrelatedPath);
        using var scheduler = new MediaProcessingScheduler();
        var fingerprints = new OpenCvImageFingerprintService(
            new StubCacheRepository(),
            TimeProvider.System,
            NullLogger<OpenCvImageFingerprintService>.Instance);
        var verifier = new OpenCvImageSimilarityVerifier(
            scheduler,
            NullLogger<OpenCvImageSimilarityVerifier>.Instance);

        var result = await verifier.VerifyAsync(
            await FingerprintAsync(fingerprints, originalPath),
            await FingerprintAsync(fingerprints, unrelatedPath),
            TestContext.Current.CancellationToken);

        Assert.False(result.Value?.IsSimilar == true);
    }

    private static async Task<ImageFingerprint> FingerprintAsync(
        OpenCvImageFingerprintService service,
        string path)
    {
        var info = new FileInfo(path);
        var result = await service.GetFingerprintAsync(
            new FileEntry(
                info.FullName,
                info.Name,
                info.Extension,
                info.Length,
                info.LastWriteTimeUtc),
            forceRefresh: true,
            TestContext.Current.CancellationToken);
        return Assert.IsType<ImageFingerprint>(result.Value);
    }

    private static int MinimumDistance(
        ImageFingerprint left,
        ImageFingerprint right) => left.CandidateHashes
        .SelectMany(
            leftHash => right.CandidateHashes,
            (leftHash, rightHash) =>
                FileComparer.Domain.Rules.PerceptualHashDistance.Calculate(
                    leftHash,
                    rightHash))
        .Min();

    private static void CreateFeatureRichImage(string path)
    {
        using var image = new Mat(240, 320, MatType.CV_8UC3, Scalar.Black);
        for (var index = 0; index < 12; index++)
        {
            var x = 16 + (index % 4) * 72;
            var y = 16 + (index / 4) * 72;
            Cv2.Rectangle(
                image,
                new Rect(x, y, 48, 42),
                new Scalar(20 + index * 15, 220 - index * 10, 40 + index * 12),
                thickness: 2);
            Cv2.Circle(
                image,
                new Point(x + 24, y + 21),
                9 + index % 5,
                new Scalar(240 - index * 12, 30 + index * 10, 180),
                thickness: -1);
        }

        Cv2.PutText(
            image,
            "FILE COMPARER 2026",
            new Point(22, 226),
            HersheyFonts.HersheySimplex,
            0.7,
            Scalar.White,
            2);
        Assert.True(Cv2.ImWrite(path, image));
    }

    private static void CreateCrop(string path)
    {
        using var source = LoadOriginal(path);
        using var crop = new Mat(source, new Rect(32, 24, 256, 192));
        Assert.True(Cv2.ImWrite(path, crop));
    }

    private static void CreateRotation(string path)
    {
        using var source = LoadOriginal(path);
        using var rotated = new Mat();
        Cv2.Rotate(source, rotated, RotateFlags.Rotate90Clockwise);
        Assert.True(Cv2.ImWrite(path, rotated));
    }

    private static void CreateLocalZoom(string path)
    {
        using var source = LoadOriginal(path);
        using var crop = new Mat(source, new Rect(64, 48, 192, 144));
        using var zoomed = new Mat();
        Cv2.Resize(crop, zoomed, source.Size(), interpolation: InterpolationFlags.Cubic);
        Assert.True(Cv2.ImWrite(path, zoomed));
    }

    private static void CreateBorder(string path)
    {
        using var source = LoadOriginal(path);
        using var bordered = new Mat();
        Cv2.CopyMakeBorder(
            source,
            bordered,
            12,
            12,
            12,
            12,
            BorderTypes.Constant,
            new Scalar(240, 240, 240));
        Assert.True(Cv2.ImWrite(path, bordered));
    }

    private static void CreateWatermark(string path)
    {
        using var source = LoadOriginal(path);
        Cv2.PutText(
            source,
            "SAMPLE",
            new Point(190, 32),
            HersheyFonts.HersheySimplex,
            0.7,
            new Scalar(220, 220, 220),
            1);
        Assert.True(Cv2.ImWrite(path, source));
    }

    private static void CreateColorAdjustment(string path)
    {
        using var source = LoadOriginal(path);
        using var adjusted = new Mat();
        source.ConvertTo(adjusted, MatType.CV_8UC3, alpha: 0.92, beta: 12);
        Assert.True(Cv2.ImWrite(path, adjusted));
    }

    private static void CreateReencodedJpeg(string path)
    {
        using var source = LoadOriginal(path);
        Assert.True(Cv2.ImWrite(
            path,
            source,
            [(int)ImwriteFlags.JpegQuality, 72]));
    }

    private static Mat LoadOriginal(string targetVariantPath) => Cv2.ImRead(
        Path.Combine(Path.GetDirectoryName(targetVariantPath)!, "original.png"),
        ImreadModes.Color);

    private static void CreateUnrelatedImage(string path)
    {
        using var image = new Mat(240, 320, MatType.CV_8UC3, new Scalar(80, 30, 200));
        for (var index = 0; index < 10; index++)
        {
            Cv2.Line(
                image,
                new Point(0, index * 24),
                new Point(319, 239 - index * 20),
                new Scalar(10 + index * 12, 220, 30),
                4);
        }

        Assert.True(Cv2.ImWrite(path, image));
    }
}
