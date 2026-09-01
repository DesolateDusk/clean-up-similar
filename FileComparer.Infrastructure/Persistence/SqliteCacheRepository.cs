using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FileComparer.Infrastructure.Persistence;

/// <summary>
/// Stores reusable file and image fingerprints. Cache misses and stale entries
/// never alter comparison correctness; callers can always recompute features.
/// </summary>
public sealed class SqliteCacheRepository(
    IAppDataPaths appDataPaths,
    ILogger<SqliteCacheRepository> logger)
    : ICacheRepository, IVideoFingerprintCache, IDisposable
{
    private static readonly Action<ILogger, string, Exception?>
        LogInitializationFailure = LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1001, nameof(LogInitializationFailure)),
            "Unable to initialize fingerprint cache at {CachePath}");

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = appDataPaths.CacheDatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString();
    private bool initialized;
    private bool disposed;
    private SqliteConnection? connection;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initialized)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            var databaseDirectory = Path.GetDirectoryName(
                appDataPaths.CacheDatabasePath);
            if (!string.IsNullOrEmpty(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }

            connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS FileCache
                (
                    Path TEXT PRIMARY KEY COLLATE NOCASE,
                    Size INTEGER NOT NULL,
                    LastWriteTimeUtc INTEGER NOT NULL,
                    QuickHash TEXT NULL,
                    Sha256 TEXT NULL,
                    Width INTEGER NULL,
                    Height INTEGER NULL,
                    PHash TEXT NULL,
                    ImageCandidateHashes TEXT NULL,
                    UpdatedAtUtc INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS VideoCache
                (
                    Path TEXT PRIMARY KEY COLLATE NOCASE,
                    Size INTEGER NOT NULL,
                    LastWriteTimeUtc INTEGER NOT NULL,
                    DurationMs INTEGER NOT NULL,
                    Width INTEGER NOT NULL,
                    Height INTEGER NOT NULL,
                    VideoCodec TEXT NULL,
                    FingerprintVersion TEXT NOT NULL,
                    UpdatedAtUtc INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS VideoFrameCache
                (
                    Path TEXT NOT NULL COLLATE NOCASE,
                    SampleIndex INTEGER NOT NULL,
                    PositionRatio REAL NOT NULL,
                    TimestampMs INTEGER NOT NULL,
                    PHash TEXT NOT NULL,
                    Contrast REAL NOT NULL,
                    PRIMARY KEY (Path, SampleIndex),
                    FOREIGN KEY (Path) REFERENCES VideoCache(Path) ON DELETE CASCADE
                );
                PRAGMA user_version = 2;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await EnsureImageCandidateHashesColumnAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            initialized = true;
        }
        catch (Exception exception)
            when (exception is SqliteException or IOException or
                  UnauthorizedAccessException)
        {
            connection?.Dispose();
            connection = null;
            LogInitializationFailure(
                logger,
                appDataPaths.CacheDatabasePath,
                exception);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FileCacheRecord?> GetAsync(
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var record = await ReadAsync(
                    connection!,
                    entry.FullPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return record?.Matches(entry) == true ? record : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpsertAsync(
        FileCacheRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var transaction = connection!.BeginTransaction();
            var existing = await ReadAsync(
                    connection,
                    record.Path,
                    cancellationToken,
                    transaction)
                .ConfigureAwait(false);
            var value = HasSameIdentity(existing, record)
                ? Merge(existing!, record)
                : record;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO FileCache
                (
                    Path,
                    Size,
                    LastWriteTimeUtc,
                    QuickHash,
                    Sha256,
                    Width,
                    Height,
                    PHash,
                    ImageCandidateHashes,
                    UpdatedAtUtc
                )
                VALUES
                (
                    $path,
                    $size,
                    $lastWriteTimeUtc,
                    $quickHash,
                    $sha256,
                    $width,
                    $height,
                    $pHash,
                    $imageCandidateHashes,
                    $updatedAtUtc
                )
                ON CONFLICT(Path) DO UPDATE SET
                    Size = excluded.Size,
                    LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                    QuickHash = excluded.QuickHash,
                    Sha256 = excluded.Sha256,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    PHash = excluded.PHash,
                    ImageCandidateHashes = excluded.ImageCandidateHashes,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            AddParameters(command, value);
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<VideoCacheRecord?> GetVideoAsync(
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var record = await ReadVideoAsync(
                    connection!,
                    entry.FullPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return record?.Matches(entry) == true ? record : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpsertVideoAsync(
        VideoCacheRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.FingerprintVersion);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var transaction = connection!.BeginTransaction();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO VideoCache
                    (
                        Path,
                        Size,
                        LastWriteTimeUtc,
                        DurationMs,
                        Width,
                        Height,
                        VideoCodec,
                        FingerprintVersion,
                        UpdatedAtUtc
                    )
                    VALUES
                    (
                        $path,
                        $size,
                        $lastWriteTimeUtc,
                        $durationMs,
                        $width,
                        $height,
                        $videoCodec,
                        $fingerprintVersion,
                        $updatedAtUtc
                    )
                    ON CONFLICT(Path) DO UPDATE SET
                        Size = excluded.Size,
                        LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                        DurationMs = excluded.DurationMs,
                        Width = excluded.Width,
                        Height = excluded.Height,
                        VideoCodec = excluded.VideoCodec,
                        FingerprintVersion = excluded.FingerprintVersion,
                        UpdatedAtUtc = excluded.UpdatedAtUtc;
                    """;
                AddVideoParameters(command, record);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await using (var deleteFrames = connection.CreateCommand())
            {
                deleteFrames.Transaction = transaction;
                deleteFrames.CommandText =
                    "DELETE FROM VideoFrameCache WHERE Path = $path;";
                deleteFrames.Parameters.AddWithValue("$path", record.Path);
                await deleteFrames.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var frame in record.Frames.OrderBy(frame => frame.SampleIndex))
            {
                await using var insertFrame = connection.CreateCommand();
                insertFrame.Transaction = transaction;
                insertFrame.CommandText =
                    """
                    INSERT INTO VideoFrameCache
                    (
                        Path,
                        SampleIndex,
                        PositionRatio,
                        TimestampMs,
                        PHash,
                        Contrast
                    )
                    VALUES
                    (
                        $path,
                        $sampleIndex,
                        $positionRatio,
                        $timestampMs,
                        $pHash,
                        $contrast
                    );
                    """;
                AddVideoFrameParameters(insertFrame, record.Path, frame);
                await insertFrame.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        connection?.Dispose();
        gate.Dispose();
        disposed = true;
    }

    private SqliteConnection CreateConnection() => new(connectionString);

    private static async Task<FileCacheRecord?> ReadAsync(
        SqliteConnection connection,
        string path,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                Path,
                Size,
                LastWriteTimeUtc,
                QuickHash,
                Sha256,
                Width,
                Height,
                PHash,
                ImageCandidateHashes,
                UpdatedAtUtc
            FROM FileCache
            WHERE Path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new FileCacheRecord(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(9),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static async Task<VideoCacheRecord?> ReadVideoAsync(
        SqliteConnection connection,
        string path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                Path,
                Size,
                LastWriteTimeUtc,
                DurationMs,
                Width,
                Height,
                VideoCodec,
                FingerprintVersion,
                UpdatedAtUtc
            FROM VideoCache
            WHERE Path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        VideoCacheRecord? metadata;
        await using (var reader = await command
                         .ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            metadata = new VideoCacheRecord(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                [],
                reader.GetInt64(8));
        }

        var frames = await ReadVideoFramesAsync(
                connection,
                path,
                cancellationToken)
            .ConfigureAwait(false);
        return metadata with { Frames = frames };
    }

    private static async Task<VideoFrameCacheRecord[]> ReadVideoFramesAsync(
        SqliteConnection connection,
        string path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                SampleIndex,
                PositionRatio,
                TimestampMs,
                PHash,
                Contrast
            FROM VideoFrameCache
            WHERE Path = $path
            ORDER BY SampleIndex;
            """;
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var frames = new List<VideoFrameCacheRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            frames.Add(new VideoFrameCacheRecord(
                reader.GetInt32(0),
                reader.GetDouble(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetDouble(4)));
        }

        return frames.ToArray();
    }

    private static bool HasSameIdentity(
        FileCacheRecord? left,
        FileCacheRecord right) =>
        left is not null &&
        left.Size == right.Size &&
        left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks;

    private static FileCacheRecord Merge(
        FileCacheRecord existing,
        FileCacheRecord update) =>
        update with
        {
            QuickHash = update.QuickHash ?? existing.QuickHash,
            Sha256 = update.Sha256 ?? existing.Sha256,
            Width = update.Width ?? existing.Width,
            Height = update.Height ?? existing.Height,
            PerceptualHash =
                update.PerceptualHash ?? existing.PerceptualHash,
            ImageCandidateHashes =
                update.ImageCandidateHashes ?? existing.ImageCandidateHashes
        };

    private static void AddParameters(
        SqliteCommand command,
        FileCacheRecord record)
    {
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue(
            "$lastWriteTimeUtc",
            record.LastWriteTimeUtcTicks);
        command.Parameters.AddWithValue(
            "$quickHash",
            (object?)record.QuickHash ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sha256",
            (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$width",
            (object?)record.Width ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$height",
            (object?)record.Height ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$pHash",
            (object?)record.PerceptualHash ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$imageCandidateHashes",
            (object?)record.ImageCandidateHashes ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            record.UpdatedAtUtcTicks);
    }

    private static void AddVideoParameters(
        SqliteCommand command,
        VideoCacheRecord record)
    {
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue(
            "$lastWriteTimeUtc",
            record.LastWriteTimeUtcTicks);
        command.Parameters.AddWithValue("$durationMs", record.DurationMilliseconds);
        command.Parameters.AddWithValue("$width", record.Width);
        command.Parameters.AddWithValue("$height", record.Height);
        command.Parameters.AddWithValue(
            "$videoCodec",
            (object?)record.VideoCodec ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$fingerprintVersion",
            record.FingerprintVersion);
        command.Parameters.AddWithValue("$updatedAtUtc", record.UpdatedAtUtcTicks);
    }

    private static void AddVideoFrameParameters(
        SqliteCommand command,
        string path,
        VideoFrameCacheRecord frame)
    {
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$sampleIndex", frame.SampleIndex);
        command.Parameters.AddWithValue("$positionRatio", frame.PositionRatio);
        command.Parameters.AddWithValue("$timestampMs", frame.TimestampMilliseconds);
        command.Parameters.AddWithValue("$pHash", frame.PerceptualHash);
        command.Parameters.AddWithValue("$contrast", frame.Contrast);
    }

    private static async Task EnsureImageCandidateHashesColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var tableInfo = connection.CreateCommand();
        tableInfo.CommandText = "PRAGMA table_info(FileCache);";
        var hasColumn = false;
        await using (var reader = await tableInfo
                         .ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(
                        reader.GetString(1),
                        "ImageCandidateHashes",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText =
            "ALTER TABLE FileCache ADD COLUMN ImageCandidateHashes TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
