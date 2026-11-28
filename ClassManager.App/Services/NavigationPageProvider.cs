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

    /// <summary>
    /// PDF 转 Word 的 ViewModel 缓存。
    /// <para>
    /// 不缓存的话，切到别的模块再回来会重建 VM —— 正在跑的转换任务还在后台跑，
    /// 但界面上的进度、日志、输入框全清空了，用户看到的就是「进度被重置」。
    /// </para>
    /// </summary>
    private PdfToWordViewModel? _pdfToWordViewModel;

    /// <summary>
    /// 证件照的 ViewModel 缓存：生成是后台任务，切走再回来要能看到已出的图和日志，
    /// 而不是一张白板。
    /// </summary>
    private IdPhotoViewModel? _idPhotoViewModel;

    /// <inheritdoc />
    public object? GetPage(Type pageType)
    {
        if (pageType == typeof(IdPhotoPage))
        {
            var page = new IdPhotoPage();

            _idPhotoViewModel ??= new IdPhotoViewModel();
            page.DataContext = _idPhotoViewModel;

            return page;
        }

        if (pageType == typeof(PdfToWordPage))
        {
            var page = new PdfToWordPage();

            // 复用同一个 VM：转换是后台任务，切走再回来要能看到还在跑的进度与日志
            _pdfToWordViewModel ??= new PdfToWordViewModel();
            page.DataContext = _pdfToWordViewModel;

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
