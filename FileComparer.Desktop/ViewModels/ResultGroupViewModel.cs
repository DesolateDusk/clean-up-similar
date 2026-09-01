using System.Collections.ObjectModel;
using FileComparer.Domain.Models;

namespace FileComparer.Desktop.ViewModels;

public sealed class ResultGroupViewModel
{
    public ResultGroupViewModel(
        CompareGroup model,
        Action selectionChanged)
    {
        Model = model;
        Items = new ObservableCollection<ResultItemViewModel>(
            model.Items.Select(item =>
                new ResultItemViewModel(item, selectionChanged)));
    }

    public CompareGroup Model { get; }

    public string Title => Model.Type switch
    {
        CompareGroupType.ExactDuplicate => $"完全相同 · {Items.Count} 個檔案",
        CompareGroupType.SimilarImage => $"相似圖片 · {Items.Count} 張",
        CompareGroupType.SimilarVideo => $"相似影片 · {Items.Count} 支",
        _ => $"比對結果 · {Items.Count} 個項目"
    };

    public string Subtitle
    {
        get
        {
            var active = Items.Count(item =>
                item.Status != CompareItemStatus.Recycled);
            return $"尚有 {active} 個項目可處理";
        }
    }

    public ObservableCollection<ResultItemViewModel> Items { get; }
}
