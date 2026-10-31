using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassManager.App.ViewModels;

/// <summary>「间距设置」窗口中的单行：一个说明标签 + 一个可编辑的像素值。</summary>
public sealed partial class GapSettingItem : ObservableObject
{
    /// <summary>说明标签（如「第1列 ↔ 第2列」）。</summary>
    [ObservableProperty]
    private string _label;

    /// <summary>间距值（像素）。</summary>
    [ObservableProperty]
    private double _value;

    public GapSettingItem(string label, double value)
    {
        _label = label;
        _value = value;
    }
}
