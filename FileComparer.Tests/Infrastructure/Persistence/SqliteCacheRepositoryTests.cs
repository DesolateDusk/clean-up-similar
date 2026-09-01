using FileComparer.Domain.Models;
using FileComparer.Infrastructure.Persistence;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileComparer.Tests.Infrastructure.Persistence;

public sealed class SqliteCacheRepositoryTests
{
    [Fact]
    public async Task ReturnsRecordOnlyWhenPathSizeAndTimestampMatch()
    {
        using var temp = new TemporaryDirectory();
        using var repository = CreateRepository(temp);
        var original = Entry(temp, size: 10, ticks: 100);
        var record = FileCacheRecord.Create(
            original,
            new DateTime(200, DateTimeKind.Utc)) with
        {
            Sha256 = "ABC"
        };
        await repository.InitializeAsync(CancellationToken.None);
        await repository.UpsertAsync(record, CancellationToken.None);

        var hit = await repository.GetAsync(original, CancellationToken.None);
        var changedSize = await repository.GetAsync(
            original with { Size = 11 },
            CancellationToken.None);
        var changedTime = await repository.GetAsync(
            original with
            {
                LastWriteTimeUtc = new DateTime(101, DateTimeKind.Utc)
            },
            CancellationToken.None);

        Assert.Equal("ABC", hit?.Sha256);
        Assert.Null(changedSize);
        Assert.Null(changedTime);
    }

    [Fact]
    public async Task MergesPartialFeaturesForTheSameIdentity()
    {
        using var temp = new TemporaryDirectory();
        using var repository = CreateRepository(temp);
        var entry = Entry(temp, size: 10, ticks: 100);
        await repository.InitializeAsync(CancellationToken.None);

        await repository.UpsertAsync(
            FileCacheRecord.Create(
                entry,
                new DateTime(200, DateTimeKind.Utc)) with
            {
                QuickHash = "QUICK"
            },
            CancellationToken.None);
        await repository.UpsertAsync(
            FileCacheRecord.Create(
                entry,
                new DateTime(300, DateTimeKind.Utc)) with
            {
                Sha256 = "FULL",
                Width = 640,
                Height = 480,
                PerceptualHash = "0123456789ABCDEF"
            },
            CancellationToken.None);

        var actual = await repository.GetAsync(entry, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal("QUICK", actual.QuickHash);
        Assert.Equal("FULL", actual.Sha256);
        Assert.Equal(640, actual.Width);
        Assert.Equal(480, actual.Height);
        Assert.Equal("0123456789ABCDEF", actual.PerceptualHash);
        Assert.Equal(300, actual.UpdatedAtUtcTicks);
    }

    [Fact]
    public async Task ClearsOldFeaturesWhenIdentityChanges()
    {
        using var temp = new TemporaryDirectory();
        using var repository = CreateRepository(temp);
        var original = Entry(temp, size: 10, ticks: 100);
        await repository.InitializeAsync(CancellationToken.None);
        await repository.UpsertAsync(
            FileCacheRecord.Create(
                original,
                new DateTime(200, DateTimeKind.Utc)) with
            {
                QuickHash = "OLD",
                Sha256 = "OLD",
                PerceptualHash = "FFFFFFFFFFFFFFFF"
            },
            CancellationToken.None);

        var changed = original with
        {
            Size = 20,
            LastWriteTimeUtc = new DateTime(101, DateTimeKind.Utc)
        };
        await repository.UpsertAsync(
            FileCacheRecord.Create(
                changed,
                new DateTime(300, DateTimeKind.Utc)) with
            {
                QuickHash = "NEW"
            },
            CancellationToken.None);

        var actual = await repository.GetAsync(changed, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal("NEW", actual.QuickHash);
        Assert.Null(actual.Sha256);
        Assert.Null(actual.PerceptualHash);
    }

    [Fact]
    public async Task StoresAndReplacesVideoMetadataAndFramesAtomically()
    {
        using var temp = new TemporaryDirectory();
        using var repository = CreateRepository(temp);
        var entry = Entry(temp, size: 10, ticks: 100) with
        {
            FullPath = temp.GetPath("clip.avi"),
            Name = "clip.avi",
            Extension = ".avi"
        };
        await repository.InitializeAsync(CancellationToken.None);
        await repository.UpsertVideoAsync(
            VideoRecord(entry, "0000000000000001", "0000000000000002"),
            CancellationToken.None);

        await repository.UpsertVideoAsync(
            VideoRecord(entry, "0000000000000003"),
            CancellationToken.None);
        var actual = await repository.GetVideoAsync(entry, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal("video-frame-phash-v1-n12", actual.FingerprintVersion);
        var frame = Assert.Single(actual.Frames);
        Assert.Equal("0000000000000003", frame.PerceptualHash);
        Assert.Null(await repository.GetVideoAsync(
            entry with { Size = 11 },
            CancellationToken.None));
    }

    private static VideoCacheRecord VideoRecord(
        FileEntry entry,
        params string[] hashes) => new(
        entry.FullPath,
        entry.Size,
        entry.LastWriteTimeUtc.Ticks,
        DurationMilliseconds: 1_000,
        Width: 640,
        Height: 480,
        VideoCodec: "MJPG",
        FingerprintVersion: "video-frame-phash-v1-n12",
        Frames: hashes
            .Select((hash, index) => new VideoFrameCacheRecord(
                index,
                index / 11d,
                index * 100,
                hash,
                Contrast: 20))
            .ToArray(),
        UpdatedAtUtcTicks: 200);

    private static SqliteCacheRepository CreateRepository(
        TemporaryDirectory temp) =>
        new(
            new StubAppDataPaths(
                temp.GetPath("cache.db"),
                temp.GetPath("logs")),
            NullLogger<SqliteCacheRepository>.Instance);

    private static FileEntry Entry(
        TemporaryDirectory temp,
        long size,
        long ticks) =>
        new(
            temp.GetPath("file.bin"),
            "file.bin",
            ".bin",
            size,
            new DateTime(ticks, DateTimeKind.Utc));
}
