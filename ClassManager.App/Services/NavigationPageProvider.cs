using ClassManager.App.ViewModels;
using ClassManager.App.Views;
using Wpf.Ui.Abstractions;

namespace ClassManager.App.Services;

/// <summary>
/// 导航页面提供器：NavigationView 通过 <see cref="INavigationViewPageProvider.GetPage"/> 创建 Page，
/// 并为页面注入对应的 ViewModel（Page 导航范式，不用 NavigationView.Content）。
/// </summary>
public sealed class NavigationPageProvider : INavigationViewPageProvider
{
    /// <summary>壳引用（由 MainWindow 装配后注入）。</summary>
    public IShell? Shell { get; set; }

    /// <summary>
    /// AI 办公的 ViewModel 缓存：切到别的模块再回来时必须保留当前会话、草稿与附件，
    /// 否则每次导航都会重建 VM，进行中的对话消失、已删除的会话又从磁盘被读回来。
    /// </summary>
    private AiOfficeViewModel? _aiOfficeViewModel;

    /// <inheritdoc />
    public object? GetPage(Type pageType)
    {
        if (pageType == typeof(PdfToWordPage))
        {
            var page = new PdfToWordPage();

            // PDF 转换页不依赖壳（不需要共享状态），直接给个 VM 就行
            page.DataContext = new PdfToWordViewModel();

            return page;
        }

        if (pageType == typeof(RollCallPage))
        {
            var page = new RollCallPage();
            if (Shell is not null)
            {
                page.DataContext = new RollCallViewModel(Shell);
            }

            return page;
        }

        if (pageType == typeof(SeatPage))
        {
            var page = new SeatPage();
            if (Shell is not null)
            {
                page.DataContext = new SeatViewModel(Shell);
            }

            return page;
        }

        if (pageType == typeof(Exam2PptPage))
        {
            var page = new Exam2PptPage();
            if (Shell is not null)
            {
                page.DataContext = new Exam2PptViewModel(Shell);
            }

            return page;
        }

        if (pageType == typeof(AiOfficePage))
        {
            var page = new AiOfficePage();
            if (Shell is not null)
            {
                _aiOfficeViewModel ??= new AiOfficeViewModel(Shell);
                page.DataContext = _aiOfficeViewModel;
            }

            return page;
        }

        return Activator.CreateInstance(pageType);
    }
}
