using FileComparer.Domain.Models;
using FileComparer.Infrastructure.Imaging;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class OpenCvThumbnailServiceTests
{
    [Fact]
    public async Task CreatesPngWithinRequestedBoundsWithoutUpscaling()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("wide.png");
        using (var source = new Mat(
                   rows: 100,
                   cols: 200,
                   MatType.CV_8UC3,
                   new Scalar(40, 80, 120)))
        {
            Assert.True(Cv2.ImWrite(path, source));
        }

        var service = new OpenCvThumbnailService(
            NullLogger<OpenCvThumbnailService>.Instance);
        var result = await service.CreateThumbnailAsync(
            path,
            maxWidth: 80,
            maxHeight: 80,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var thumbnail = Cv2.ImDecode(
            result.Value,
            ImreadModes.Unchanged);
        Assert.False(thumbnail.Empty());
        Assert.Equal(80, thumbnail.Width);
        Assert.Equal(40, thumbnail.Height);
    }

    [Fact]
    public async Task ReportsInvalidImageWithoutThrowing()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("broken.png");
        await File.WriteAllBytesAsync(
            path,
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        var service = new OpenCvThumbnailService(
            NullLogger<OpenCvThumbnailService>.Instance);

        var result = await service.CreateThumbnailAsync(
            path,
            200,
            200,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Issues,
            issue => issue.Category == ScanIssueCategory.InvalidImage);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    public async Task RejectsInvalidBounds(int maxWidth, int maxHeight)
    {
        var service = new OpenCvThumbnailService(
            NullLogger<OpenCvThumbnailService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateThumbnailAsync(
                "unused.png",
                maxWidth,
                maxHeight,
                TestContext.Current.CancellationToken));
    }
}
