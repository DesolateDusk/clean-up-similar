using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FileComparer.Desktop.ViewModels;
using FileComparer.Domain.Models;

namespace FileComparer.Desktop;

public sealed partial class MainWindow : Window, IDisposable
{
    private MainWindowViewModel? viewModel;
    private Bitmap? previewBitmap;
    private bool disposed;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        }

        DisposePreview();
        disposed = true;
    }

    private async void BrowseFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "選擇要掃描的資料夾",
                AllowMultiple = false
            });
        var path = folders.Count > 0
            ? folders[0].TryGetLocalPath()
            : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.RootDirectory = path;
        }
    }

    private async void ResultItemsList_OnSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        var selected = (sender as ListBox)?.SelectedItem as ResultItemViewModel;
        await ViewModel.SelectItemAsync(selected);
    }

    private void OpenFile_OnClick(object? sender, RoutedEventArgs e) =>
        ExecuteItemCommand(sender, ViewModel.OpenFileCommand);

    private void OpenFolder_OnClick(object? sender, RoutedEventArgs e) =>
        ExecuteItemCommand(sender, ViewModel.OpenFolderCommand);

    private async void DeleteSelected_OnClick(object? sender, RoutedEventArgs e)
    {
        var summary = ViewModel.GetDeleteSummary();
        if (summary.Count == 0)
        {
            return;
        }

        var confirmed = await new DeleteConfirmationWindow(summary)
            .ShowDialog<bool>(this);
        if (confirmed)
        {
            await ViewModel.DeleteSelectedAsync(confirmed: true);
        }
    }

    private void ReturnToStart_OnClick(object? sender, RoutedEventArgs e) =>
        ViewModel.ReturnToStart();

    private static void ExecuteItemCommand(object? sender, ICommand command)
    {
        if (sender is Button { Tag: ResultItemViewModel item } &&
            command.CanExecute(item))
        {
            command.Execute(item);
        }
    }

    private void ViewModelOnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.PreviewPng))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdatePreview();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdatePreview);
        }
    }

    private void UpdatePreview()
    {
        DisposePreview();
        if (ViewModel.PreviewPng is not { Length: > 0 } bytes)
        {
            return;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        previewBitmap = new Bitmap(stream);
        this.FindControl<Image>("PreviewImage")!.Source = previewBitmap;
    }

    private void DisposePreview()
    {
        var image = this.FindControl<Image>("PreviewImage");
        if (image is not null)
        {
            image.Source = null;
        }

        previewBitmap?.Dispose();
        previewBitmap = null;
    }

    private MainWindowViewModel ViewModel => viewModel ??
        throw new InvalidOperationException(
            "MainWindow requires a MainWindowViewModel before interaction.");
}
