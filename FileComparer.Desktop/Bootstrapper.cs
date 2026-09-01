using FileComparer.Application.Compare;
using FileComparer.Application.Delete;
using FileComparer.Application.Scan;
using FileComparer.Desktop.ViewModels;
using FileComparer.Domain.Interfaces;
using FileComparer.Infrastructure.FileSystem;
using FileComparer.Infrastructure.Hashing;
using FileComparer.Infrastructure.Imaging;
using FileComparer.Infrastructure.Media;
using FileComparer.Infrastructure.Persistence;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FileComparer.Desktop;

public static class Bootstrapper
{
    public static ServiceProvider CreateServiceProvider()
    {
        var paths = new LocalAppDataPaths();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogDirectoryPath, "file-comparer-.log"),
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
        });

        services.AddSingleton<SqliteCacheRepository>();
        services.AddSingleton<ICacheRepository>(provider =>
            provider.GetRequiredService<SqliteCacheRepository>());
        services.AddSingleton<IVideoFingerprintCache>(provider =>
            provider.GetRequiredService<SqliteCacheRepository>());
        services.AddSingleton<IFileScanner, DirectoryFileScanner>();
        services.AddSingleton<FileHashService>();
        services.AddSingleton<IQuickHashService>(provider =>
            provider.GetRequiredService<FileHashService>());
        services.AddSingleton<IHashService>(provider =>
            provider.GetRequiredService<FileHashService>());
        services.AddSingleton<IImageFingerprintService,
            OpenCvImageFingerprintService>();
        services.AddSingleton<IMediaProcessingScheduler,
            MediaProcessingScheduler>();
        services.AddSingleton<IImageSimilarityVerifier,
            OpenCvImageSimilarityVerifier>();
        services.AddSingleton<IVideoFingerprintService,
            OpenCvVideoFingerprintService>();
        services.AddSingleton<IImageCandidateIndex,
            MultiBandImageCandidateIndex>();
        services.AddSingleton<IThumbnailService, OpenCvThumbnailService>();
        services.AddSingleton<IFileLauncher, WindowsFileLauncher>();
        services.AddSingleton<IRecycleBinService, WindowsRecycleBinService>();

        services.AddSingleton<ICompareStrategy, ExactFileCompareStrategy>();
        services.AddSingleton<ImageSimilarityCompareStrategy>();
        services.AddSingleton<VideoSimilarityCompareStrategy>();
        services.AddSingleton<ICompareStrategy, SimilarMediaCompareStrategy>();
        services.AddSingleton<IScanApplicationService,
            ScanApplicationService>();
        services.AddSingleton<IDeleteApplicationService,
            DeleteApplicationService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
