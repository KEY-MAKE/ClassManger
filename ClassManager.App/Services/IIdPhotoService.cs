namespace ClassManager.App.Services;

/// <summary>
/// 证件照底色。
/// <para>用标准证件照的三种底色，色值不要随手改 —— 照相馆和学校收表都认这三个。</para>
/// </summary>
public enum IdPhotoBackground
{
    /// <summary>红底 #D40000。</summary>
    Red,

    /// <summary>蓝底 #438EDB。</summary>
    Blue,

    /// <summary>白底 #FFFFFF。</summary>
    White,
}

/// <summary>
/// 抠图精度档位。
/// <para>
/// U2Net 对发丝这类细边界只有约 70% 准确度，头发边缘会发糊；
/// BRIA RMBG 2.0（BiRefNet 架构）能到 90% 以上，但模型 977MB，下载要等。
/// 所以给两档：快 vs 细。
/// </para>
/// </summary>
public enum IdPhotoMatting
{
    /// <summary>标准：U2Net，168MB，速度快，头发一般。</summary>
    Standard,

    /// <summary>精细：RMBG 2.0，977MB，发丝级边缘（首次需下载）。</summary>
    Fine,
}

/// <summary>
/// 证件照规格（300 DPI 下的像素尺寸）。
/// <para>一寸 25×35 mm → 295×413 px；二寸 35×49 mm → 413×579 px。</para>
/// </summary>
public sealed record IdPhotoSpec
{
    /// <summary>规格名（用在文件名里）。</summary>
    public string Name { get; init; } = "一寸";

    /// <summary>输出宽度（像素，300 DPI）。</summary>
    public int Width { get; init; } = 295;

    /// <summary>输出高度（像素，300 DPI）。</summary>
    public int Height { get; init; } = 413;

    /// <summary>
    /// 完整保留人像，不裁切。
    /// <para>
    /// 默认 true：用户上传什么构图就输出什么构图，只把背景换成纯色。
    /// 设 false 才走「按人脸比例裁切」的老逻辑。
    /// </para>
    /// </summary>
    public bool KeepWholePerson { get; init; } = true;

    /// <summary>抠图精度（默认精细，头发边缘更干净）。</summary>
    public IdPhotoMatting Matting { get; init; } = IdPhotoMatting.Fine;

    /// <summary>要生成的底色（可多选，一次出多张）。</summary>
    public IReadOnlyList<IdPhotoBackground> Backgrounds { get; init; } =
        new[] { IdPhotoBackground.Red, IdPhotoBackground.Blue, IdPhotoBackground.White };

    /// <summary>一寸（295×413）。</summary>
    public static IdPhotoSpec OneInch => new() { Name = "一寸", Width = 295, Height = 413 };

    /// <summary>二寸（413×579）。</summary>
    public static IdPhotoSpec TwoInch => new() { Name = "二寸", Width = 413, Height = 579 };

    /// <summary>底色在文件名里的中文后缀。</summary>
    public static string SuffixOf(IdPhotoBackground background) => background switch
    {
        IdPhotoBackground.Red => "红底",
        IdPhotoBackground.Blue => "蓝底",
        _ => "白底",
    };
}

/// <summary>生成结果。</summary>
public sealed record IdPhotoResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> OutputPaths,
    bool EngineInstalledNow);

/// <summary>
/// 证件照生成：抠人像（rembg / U2Net）→ 换纯色底 → 按证件照构图规范裁切。
/// <para>
/// 纯本地运行，照片不上传；首次使用需要装 Python 包并下载约 170MB 的抠图模型，
/// 之后就一直是秒级出图。
/// </para>
/// </summary>
public interface IIdPhotoService
{
    /// <summary>图像处理组件是否已就绪（只看安装标记，不跑进程）。</summary>
    bool IsReady { get; }

    /// <summary>最近一次失败原因（成功为 null）。</summary>
    string? LastError { get; }

    /// <summary>
    /// 确保 rembg / onnxruntime / opencv / pillow 可用。装不上返回 false（不抛异常），
    /// 调用方据此降级提示「需要先安装图像处理组件」。
    /// </summary>
    Task<bool> EnsureDependenciesAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>
    /// 生成证件照。按 <paramref name="spec"/> 里勾选的每个底色各出一张。
    /// </summary>
    Task<IdPhotoResult> GenerateAsync(
        string srcImage,
        string outDir,
        IdPhotoSpec spec,
        IProgress<string>? progress,
        CancellationToken ct);

    /// <summary>
    /// 把一张证件照拼成 6 寸相纸（一寸 4×2 / 二寸 2×3，间留 2mm 白边便于裁剪）。
    /// 成功返回输出路径，失败返回 null。
    /// </summary>
    Task<string?> ComposeSheetAsync(
        string imagePath,
        string outputPath,
        IdPhotoSpec spec,
        IProgress<string>? progress,
        CancellationToken ct);
}
