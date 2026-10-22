using System.Collections.ObjectModel;
using System.Windows.Input;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;

namespace FileComparer.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IScanApplicationService scanService;
    private readonly IDeleteApplicationService deleteService;
    private readonly IThumbnailService thumbnailService;
    private readonly IFileLauncher fileLauncher;
    private readonly AsyncRelayCommand startCommand;
    private readonly RelayCommand cancelCommand;
    private readonly RelayCommand selectSuggestedCommand;
    private readonly RelayCommand clearSelectionCommand;
    private readonly AsyncRelayCommand<ResultItemViewModel> openFileCommand;
    private readonly AsyncRelayCommand<ResultItemViewModel> openFolderCommand;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? previewCancellation;
    private IReadOnlyList<CompareGroup> groupModels = [];
    private string rootDirectory = string.Empty;
    private bool includeSubdirectories = true;
    private CompareMode selectedMode = CompareMode.ExactFiles;
    private bool forceRefresh;
    private bool isStartVisible = true;
    private bool isProgressVisible;
    private bool isResultsVisible;
    private bool isOperationActive;
    private string statusMessage = "請選擇要掃描的資料夾。";
    private string stageText = "尚未開始";
    private int discoveredCount;
    private int processedCount;
    private double progressPercentage;
    private ResultGroupViewModel? selectedGroup;
    private ResultItemViewModel? selectedItem;
    private byte[]? previewPng;
    private string previewStatusMessage = "選擇檔案以載入預覽。";
    private bool suppressDeleteSelectionNotification;

    public MainWindowViewModel(
        IScanApplicationService scanService,
        IDeleteApplicationService deleteService,
        IThumbnailService thumbnailService,
        IFileLauncher fileLauncher)
    {
        this.scanService = scanService;
        this.deleteService = deleteService;
        this.thumbnailService = thumbnailService;
        this.fileLauncher = fileLauncher;
        startCommand = new AsyncRelayCommand(StartAsync, CanStart);
        cancelCommand = new RelayCommand(
            CancelCurrentOperation,
            () => IsOperationActive);
        selectSuggestedCommand = new RelayCommand(
            SelectSuggestedDeletes,
            () => IsResultsVisible && !IsOperationActive);
        clearSelectionCommand = new RelayCommand(
            ClearDeleteSelection,
            () => IsResultsVisible && !IsOperationActive);
        openFileCommand = new AsyncRelayCommand<ResultItemViewModel>(
            OpenFileAsync,
            item => item.Status != CompareItemStatus.Recycled);
        openFolderCommand = new AsyncRelayCommand<ResultItemViewModel>(
            OpenContainingFolderAsync);
    }

    public ObservableCollection<ResultGroupViewModel> Groups { get; } = [];

    public ObservableCollection<ScanIssue> Issues { get; } = [];

    public ICommand StartCommand => startCommand;

    public ICommand CancelCommand => cancelCommand;

    public ICommand SelectSuggestedCommand => selectSuggestedCommand;

    public ICommand ClearSelectionCommand => clearSelectionCommand;

    public ICommand OpenFileCommand => openFileCommand;

    public ICommand OpenFolderCommand => openFolderCommand;

    public string RootDirectory
    {
        get => rootDirectory;
        set
        {
            if (SetProperty(ref rootDirectory, value))
            {
                startCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IncludeSubdirectories
    {
        get => includeSubdirectories;
        set => SetProperty(ref includeSubdirectories, value);
    }

    public CompareMode SelectedMode
    {
        get => selectedMode;
        set
        {
            if (SetProperty(ref selectedMode, value))
            {
                OnPropertyChanged(nameof(IsExactMode));
                OnPropertyChanged(nameof(IsMediaMode));
            }
        }
    }

    public bool IsExactMode
    {
        get => SelectedMode == CompareMode.ExactFiles;
        set
        {
            if (value)
            {
                SelectedMode = CompareMode.ExactFiles;
            }
        }
    }

    public bool IsMediaMode
    {
        get => SelectedMode == CompareMode.SimilarMedia;
        set
        {
            if (value)
            {
                SelectedMode = CompareMode.SimilarMedia;
            }
        }
    }

    public bool ForceRefresh
    {
        get => forceRefresh;
        set => SetProperty(ref forceRefresh, value);
    }

    public bool IsStartVisible
    {
        get => isStartVisible;
        private set => SetProperty(ref isStartVisible, value);
    }

    public bool IsProgressVisible
    {
        get => isProgressVisible;
        private set => SetProperty(ref isProgressVisible, value);
    }

    public bool IsResultsVisible
    {
        get => isResultsVisible;
        private set => SetProperty(ref isResultsVisible, value);
    }

    public bool IsOperationActive
    {
        get => isOperationActive;
        private set
        {
            if (SetProperty(ref isOperationActive, value))
            {
                RefreshCommands();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string StageText
    {
        get => stageText;
        private set => SetProperty(ref stageText, value);
    }

    public int DiscoveredCount
    {
        get => discoveredCount;
        private set => SetProperty(ref discoveredCount, value);
    }

    public int ProcessedCount
    {
        get => processedCount;
        private set => SetProperty(ref processedCount, value);
    }

    public double ProgressPercentage
    {
        get => progressPercentage;
        private set => SetProperty(ref progressPercentage, value);
    }

    public ResultGroupViewModel? SelectedGroup
    {
        get => selectedGroup;
        set => SetProperty(ref selectedGroup, value);
    }

    public ResultItemViewModel? SelectedItem
    {
        get => selectedItem;
        private set => SetProperty(ref selectedItem, value);
    }

    public byte[]? PreviewPng
    {
        get => previewPng;
        private set => SetProperty(ref previewPng, value);
    }

    public string PreviewStatusMessage
    {
        get => previewStatusMessage;
        private set => SetProperty(ref previewStatusMessage, value);
    }

    public string IssueSummaryText => Issues.Count == 0
        ? "未發現可回報的問題。"
        : $"共 {Issues.Count} 個檔案或系統問題；其他檔案仍已完成處理。";

    public string SelectionSummaryText
    {
        get
        {
            var summary = GetDeleteSummary();
            return summary.Count == 0
                ? "尚未選取要移至資源回收筒的檔案。"
                : $"已選取 {summary.Count} 個檔案，共 {FileSizeFormatter.Format(summary.TotalBytes)}。";
        }
    }

    public bool HasDeleteSelection => GetDeleteSummary().Count > 0;

    public async Task StartAsync()
    {
        if (!CanStart())
        {
            return;
        }

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        SetWorkspace(start: false, progress: true, results: false);
        IsOperationActive = true;
        StatusMessage = "正在掃描資料夾…";
        StageText = "列舉檔案";
        DiscoveredCount = 0;
        ProcessedCount = 0;
        ProgressPercentage = 0;
        Groups.Clear();
        Issues.Clear();
        groupModels = [];
        OnPropertyChanged(nameof(IssueSummaryText));
        OnDeleteSelectionChanged();

        var synchronizationContext = SynchronizationContext.Current;
        var progress = new CallbackProgress<ScanProgress>(value =>
            DispatchProgress(synchronizationContext, value));
        ScanResult result;
        try
        {
            result = await scanService.ScanAsync(
                new ScanRequest(
                    RootDirectory,
                    IncludeSubdirectories,
                    SelectedMode,
                    ForceRefresh),
                progress,
                operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            result = new ScanResult(
                ScanStatus.Cancelled,
                [],
                [],
                DiscoveredCount,
                ProcessedCount);
        }
        finally
        {
            IsOperationActive = false;
        }

        ApplyScanResult(result);
    }

    public void CancelCurrentOperation() => operationCancellation?.Cancel();

    public async Task SelectItemAsync(ResultItemViewModel? item)
    {
        SelectedItem = item;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        var requestCancellation = previewCancellation;
        PreviewPng = null;

        if (item is null ||
            (!ImageFileRules.IsSupported(item.Model.File.Extension) &&
             !VideoFileRules.IsSupported(item.Model.File.Extension)))
        {
            PreviewStatusMessage = item is null
                ? "選擇檔案以載入預覽。"
                : "此檔案不是支援的圖片或影片格式。";
            return;
        }

        PreviewStatusMessage = "正在載入預覽…";
        try
        {
            var result = await thumbnailService.CreateThumbnailAsync(
                item.FullPath,
                maxWidth: 720,
                maxHeight: 520,
                requestCancellation.Token);
            if (!ReferenceEquals(item, SelectedItem) ||
                !ReferenceEquals(requestCancellation, previewCancellation))
            {
                return;
            }

            PreviewPng = result.Value;
            PreviewStatusMessage = result.IsSuccess
                ? string.Empty
                : result.Issues.Count > 0
                    ? result.Issues[0].Message
                    : "無法載入預覽。";
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this preview request.
        }
    }

    public DeleteSummary GetDeleteSummary() => deleteService.Summarize(
        groupModels,
        SelectedDeletePaths());

    public async Task DeleteSelectedAsync(bool confirmed)
    {
        if (!IsResultsVisible || IsOperationActive)
        {
            return;
        }

        var selectedPaths = SelectedDeletePaths();
        if (selectedPaths.Length == 0)
        {
            return;
        }

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        IsOperationActive = true;
        StatusMessage = "正在移至 Windows 資源回收筒…";
        var synchronizationContext = SynchronizationContext.Current;
        var progress = new CallbackProgress<ScanProgress>(value =>
            DispatchProgress(synchronizationContext, value));

        try
        {
            var result = await deleteService.DeleteAsync(
                groupModels,
                selectedPaths,
                confirmed,
                progress,
                operationCancellation.Token);
            MapGroups(result.UpdatedGroups);
            var succeeded = result.Outcomes.Count(outcome => outcome.IsSuccess);
            var failed = result.Outcomes.Count - succeeded;
            StatusMessage = result.IsCancelled
                ? $"刪除已取消；已完成 {succeeded} 個，失敗 {failed} 個。"
                : $"資源回收筒作業完成：成功 {succeeded} 個，失敗 {failed} 個。";
        }
        finally
        {
            IsOperationActive = false;
        }
    }

    public void SelectSuggestedDeletes()
    {
        var protectedPaths = Groups
            .Select(group => FindProtectedItem(group.Items)?.FullPath)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        suppressDeleteSelectionNotification = true;
        try
        {
            foreach (var group in Groups)
            {
                foreach (var item in group.Items)
                {
                    item.IsSelectedForDelete =
                        item.CanSelectForDelete &&
                        !protectedPaths.Contains(item.FullPath);
                }
            }
        }
        finally
        {
            suppressDeleteSelectionNotification = false;
        }

        OnDeleteSelectionChanged();
    }

    public void ClearDeleteSelection()
    {
        suppressDeleteSelectionNotification = true;
        try
        {
            foreach (var item in Groups.SelectMany(group => group.Items))
            {
                item.IsSelectedForDelete = false;
            }
        }
        finally
        {
            suppressDeleteSelectionNotification = false;
        }

        OnDeleteSelectionChanged();
    }

    public void ReturnToStart()
    {
        if (!IsOperationActive)
        {
            SetWorkspace(start: true, progress: false, results: false);
            StatusMessage = "可調整條件後重新掃描。";
        }
    }

    public void Dispose()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
    }

    private bool CanStart() =>
        !IsOperationActive && !string.IsNullOrWhiteSpace(RootDirectory);

    private async Task OpenFileAsync(ResultItemViewModel item)
    {
        var result = await fileLauncher.OpenFileAsync(
            item.FullPath,
            CancellationToken.None);
        if (!result.IsSuccess)
        {
            StatusMessage = result.ErrorMessage ?? "無法開啟檔案。";
        }
    }

    private async Task OpenContainingFolderAsync(ResultItemViewModel item)
    {
        var result = await fileLauncher.OpenContainingFolderAsync(
            item.FullPath,
            CancellationToken.None);
        if (!result.IsSuccess)
        {
            StatusMessage = result.ErrorMessage ?? "無法開啟檔案所在資料夾。";
        }
    }

    private void ApplyScanResult(ScanResult result)
    {
        DiscoveredCount = result.DiscoveredCount;
        ProcessedCount = result.ProcessedCount;
        Issues.Clear();
        foreach (var issue in result.Issues)
        {
            Issues.Add(issue);
        }

        OnPropertyChanged(nameof(IssueSummaryText));
        switch (result.Status)
        {
            case ScanStatus.Completed:
                ProgressPercentage = 100;
                StageText = "完成";
                MapGroups(result.Groups);
                SetWorkspace(start: false, progress: false, results: true);
                StatusMessage = result.Groups.Count == 0
                    ? "掃描完成，沒有找到符合條件的群組。"
                    : $"掃描完成，共找到 {result.Groups.Count} 個群組。";
                break;
            case ScanStatus.Cancelled:
                SetWorkspace(start: true, progress: false, results: false);
                StageText = "已取消";
                StatusMessage = "掃描已取消，未套用不完整結果。";
                break;
            default:
                SetWorkspace(start: true, progress: false, results: false);
                StageText = "失敗";
                StatusMessage = result.ErrorMessage ?? "掃描失敗。";
                break;
        }
    }

    private void MapGroups(IReadOnlyCollection<CompareGroup> groups)
    {
        groupModels = groups.ToArray();
        Groups.Clear();
        foreach (var group in groupModels)
        {
            Groups.Add(new ResultGroupViewModel(group, OnDeleteSelectionChanged));
        }

        SelectedGroup = Groups.FirstOrDefault();
        SelectedItem = null;
        PreviewPng = null;
        PreviewStatusMessage = "選擇檔案以載入預覽。";
        OnDeleteSelectionChanged();
    }

    private string[] SelectedDeletePaths() =>
        Groups
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelectedForDelete)
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void OnDeleteSelectionChanged()
    {
        if (suppressDeleteSelectionNotification)
        {
            return;
        }

        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(HasDeleteSelection));
    }

    private static ResultItemViewModel? FindProtectedItem(
        IEnumerable<ResultItemViewModel> items)
    {
        ResultItemViewModel? firstAvailable = null;
        foreach (var item in items)
        {
            if (!item.CanSelectForDelete)
            {
                continue;
            }

            firstAvailable ??= item;
            if (item.IsSuggestedKeep)
            {
                return item;
            }
        }

        return firstAvailable;
    }

    private void DispatchProgress(
        SynchronizationContext? synchronizationContext,
        ScanProgress progress)
    {
        if (synchronizationContext is null ||
            ReferenceEquals(synchronizationContext, SynchronizationContext.Current))
        {
            ApplyProgress(progress);
        }
        else
        {
            synchronizationContext.Post(_ => ApplyProgress(progress), null);
        }
    }

    private void ApplyProgress(ScanProgress progress)
    {
        DiscoveredCount = progress.DiscoveredCount;
        ProcessedCount = progress.ProcessedCount;
        if (progress.Percentage is double percentage)
        {
            ProgressPercentage = percentage;
        }

        StageText = progress.Stage switch
        {
            ScanStage.Enumerating => "列舉檔案",
            ScanStage.Fingerprinting => "建立指紋",
            ScanStage.Grouping => "建立群組",
            ScanStage.Deleting => "移至資源回收筒",
            ScanStage.Completed => "完成",
            ScanStage.Cancelled => "已取消",
            ScanStage.Failed => "失敗",
            _ => "準備中"
        };
    }

    private void SetWorkspace(bool start, bool progress, bool results)
    {
        IsStartVisible = start;
        IsProgressVisible = progress;
        IsResultsVisible = results;
    }

    private void RefreshCommands()
    {
        startCommand.NotifyCanExecuteChanged();
        cancelCommand.NotifyCanExecuteChanged();
        selectSuggestedCommand.NotifyCanExecuteChanged();
        clearSelectionCommand.NotifyCanExecuteChanged();
        openFileCommand.NotifyCanExecuteChanged();
        openFolderCommand.NotifyCanExecuteChanged();
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
