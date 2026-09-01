using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileComparer.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace FileComparer.Desktop;

public sealed partial class App : Avalonia.Application
{
    private ServiceProvider? serviceProvider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                serviceProvider = Bootstrapper.CreateServiceProvider();
                serviceProvider.GetRequiredService<ICacheRepository>()
                    .InitializeAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                desktop.MainWindow = serviceProvider
                    .GetRequiredService<MainWindow>();
                desktop.Exit += OnDesktopExit;
                Log.Information("File comparer desktop application started");
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "Desktop application startup failed");
                serviceProvider?.Dispose();
                Log.CloseAndFlush();
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Log.Information("File comparer desktop application is shutting down");
        serviceProvider?.Dispose();
        serviceProvider = null;
        Log.CloseAndFlush();
    }
}
