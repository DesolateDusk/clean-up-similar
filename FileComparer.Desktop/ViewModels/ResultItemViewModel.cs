using FileComparer.Domain.Models;
using System.Globalization;

namespace FileComparer.Desktop.ViewModels;

public sealed class ResultItemViewModel : ObservableObject
{
    private readonly Action selectionChanged;
    private bool isSelectedForDelete;

    public ResultItemViewModel(
        CompareItem model,
        Action selectionChanged)
    {
        Model = model;
        this.selectionChanged = selectionChanged;
    }

    public CompareItem Model { get; }

    public string Name => Model.File.Name;

    public string FullPath => Model.File.FullPath;

    public long Size => Model.File.Size;

    public string SizeText => FileSizeFormatter.Format(Model.File.Size);

    public string ModifiedText =>
        Model.File.LastWriteTimeUtc.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.CurrentCulture);

    public string DimensionsText => Model.Image is null
        ? Model.Video is null
            ? "—"
            : $"{Model.Video.Width} × {Model.Video.Height}"
        : $"{Model.Image.Width} × {Model.Image.Height}";

    public string DurationText => Model.Video is null
        ? ""
        : Model.Video.Duration.TotalHours >= 1
            ? Model.Video.Duration.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
            : Model.Video.Duration.ToString(@"m\:ss", CultureInfo.CurrentCulture);

    public string CodecText => Model.Video?.VideoCodec is { Length: > 0 } codec
        ? codec
        : "";

    public string DistanceText => Model.HammingDistance is null
        ? "—"
        : Model.HammingDistance.Value.ToString(CultureInfo.CurrentCulture);

    public string SimilarityText => Model.SimilarityScore is double score
        ? score.ToString("P0", CultureInfo.CurrentCulture)
        : "—";

    public bool IsSuggestedKeep => Model.IsSuggestedKeep;

    public CompareItemStatus Status => Model.Status;

    public string StatusText => Model.Status switch
    {
        CompareItemStatus.Active => "可處理",
        CompareItemStatus.Recycled => "已移至資源回收筒",
        CompareItemStatus.DeleteFailed =>
            Model.StatusMessage ?? "移至資源回收筒失敗",
        _ => "未知狀態"
    };

    public bool CanSelectForDelete => Model.Status != CompareItemStatus.Recycled;

    public bool IsSelectedForDelete
    {
        get => isSelectedForDelete;
        set
        {
            var allowedValue = value && CanSelectForDelete;
            if (SetProperty(ref isSelectedForDelete, allowedValue))
            {
                selectionChanged();
            }
        }
    }

}
