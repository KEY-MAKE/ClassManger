using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace ClassManager.App.Services;

/// <summary>
/// 证件照生成：抠人像 → 换纯色底 → 按证件照构图规范裁切。
/// <para>
/// <b>抠图</b>：<c>rembg</c>（U2Net，CPU 可跑）。首次运行会下载约 170MB 模型，
/// 这里先用国内可达的地址预下载到 <c>~/.u2net/u2net.onnx</c>，失败再交给 rembg 自己下。
/// </para>
/// <para>
/// <b>构图</b>：opencv 的 haar 人脸分类器找人脸，按「头顶留 8%、人脸占画面 60%」裁切；
/// 找不到人脸就居中放置 —— 不报错，老师拿到的照样是一张能用的图。
/// </para>
/// <para>
/// 全部在后台进程里跑，照片不出本机。
/// </para>
/// </summary>
public sealed class IdPhotoService : IIdPhotoService
{
    /// <summary>装好之后的标记文件，启动时据此快速判断（不跑进程探测）。</summary>
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassManager",
        "idphoto-ready.txt");

    /// <summary>U2Net 模型落盘位置（rembg 默认读这里）。</summary>
    private static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".u2net",
        "u2net.onnx");

    /// <summary>模型完整大小约 170MB，低于这个值视为没下完。</summary>
    private const long ModelMinBytes = 150L * 1024 * 1024;

    /// <summary>官方 u2net.onnx 的 MD5（大小 175,997,641 字节）。</summary>
    private const string ModelMd5 = "60024c5c889badc19c04ad937298a77b";

    /// <summary>
    /// 模型下载地址。
    /// <para>
    /// <b>ModelScope 放第一</b>：实测国内直连 2~3MB/s，两分钟能下完；
    /// GitHub 直连被限到 ~11KB/s（170MB 得下几个小时），加速前缀时好时坏，只能兜底。
    /// </para>
    /// </summary>
    private static readonly string[] ModelUrls =
    {
        "https://modelscope.cn/models/AI-ModelScope/u2net/resolve/master/u2net.onnx",
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx",
        "https://gh-proxy.com/https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx",
    };

    /// <summary>
    /// 精细档模型落盘位置。
    /// <para>
    /// 用 ISNet 而不是 RMBG 2.0 / BiRefNet：后两者虽然头发更准（90%+ vs 81%），
    /// 但模型 900MB+，在无 GPU 的机器上实测**单张超 10 分钟且会把内存打爆**
    /// （用户机器上直接把主程序拖崩了）。ISNet 170MB、CPU 上几秒，是能用的上限。
    /// </para>
    /// </summary>
    private static string FineModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".rembg",
        "models",
        "isnet-general-use",
        "isnet-general-use.onnx");

    /// <summary>ISNet 约 170MB，低于这个值视为没下完。</summary>
    private const long FineModelMinBytes = 150L * 1024 * 1024;

    /// <summary>
    /// 精细模型下载地址。
    /// <para>
    /// GitHub 直连对这个 977MB 的包只有 11KB/s（要下二十多小时），
    /// 所以加速前缀放最前面；实测 gh-proxy / ghproxy.net 都能正常返回。
    /// </para>
    /// </summary>
    private static readonly string[] FineModelUrls =
    {
        "https://gh-proxy.com/https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
        "https://ghproxy.net/https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
    };

    /// <summary>需要的 Python 模块（import 名）。</summary>
    private static readonly string[] RequiredModules = { "rembg", "PIL", "cv2" };

    /// <inheritdoc />
    public bool IsReady
    {
        get
        {
            try
            {
                return File.Exists(MarkerPath) && ToolInstaller.FindPython() is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public async Task<bool> EnsureDependenciesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        LastError = null;

        try
        {
            // 装过就直接用：首次 import rembg 会被杀软拖到 1~2 分钟，
            // 每次生成都跑三遍 import 探测太亏，标记可信就不重复探。
            if (IsReady)
            {
                progress?.Report("图像处理组件已就绪。");
                return true;
            }

            progress?.Report("正在准备 Python 环境…");

            var python = await PythonBootstrap
                .EnsureAsync(new Progress<string>(s => progress?.Report(s)), ct)
                .ConfigureAwait(false);

            if (!python.Ok || python.PythonPath is null)
            {
                LastError = python.Message;
                return false;
            }

            var interpreter = python.PythonPath;

            // 1) 缺哪个补哪个
            var missing = new List<string>();

            foreach (var module in RequiredModules)
            {
                if (!await HasModuleAsync(interpreter, module, ct).ConfigureAwait(false))
                {
                    missing.Add(module);
                }
            }

            if (missing.Contains("rembg"))
            {
                progress?.Report(
                    "正在安装图像处理组件（rembg / onnxruntime，约 150MB，仅首次）…");

                var install = await PackageMirrors.InstallWithFallbackAsync(
                    interpreter,
                    new[] { "rembg", "onnxruntime" },
                    progress,
                    ct,
                    TimeSpan.FromMinutes(30)).ConfigureAwait(false);

                if (!install.Ok)
                {
                    LastError = install.Detail;
                    return false;
                }
            }

            if (missing.Contains("PIL") || missing.Contains("cv2"))
            {
                progress?.Report("正在补齐 pillow / opencv…");

                var install = await PackageMirrors.InstallWithFallbackAsync(
                    interpreter,
                    new[] { "pillow", "opencv-python" },
                    progress,
                    ct,
                    TimeSpan.FromMinutes(20)).ConfigureAwait(false);

                if (!install.Ok)
                {
                    LastError = install.Detail;
                    return false;
                }
            }

            // 2) 装完必须能真的 import（pip 成功但依赖坏了的情况见过）
            foreach (var module in RequiredModules)
            {
                if (!await HasModuleAsync(interpreter, module, ct).ConfigureAwait(false))
                {
                    LastError = $"组件装上了但 {module} 无法导入，可能被其他包破坏了依赖。";
                    return false;
                }
            }

            // 3) 先把模型下好，免得生成到一半卡在下载上
            await PrefetchModelAsync(progress, ct).ConfigureAwait(false);

            // 4) 写标记
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            progress?.Report("图像处理组件已就绪。");
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "已取消。";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IdPhotoResult> GenerateAsync(
        string srcImage,
        string outDir,
        IdPhotoSpec spec,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(srcImage) || !File.Exists(srcImage))
        {
            return Fail($"照片不存在：{srcImage}");
        }

        if (spec.Backgrounds.Count == 0)
        {
            return Fail("至少要选一种底色。");
        }

        if (spec.Width <= 0 || spec.Height <= 0)
        {
            return Fail("证件照规格不正确。");
        }

        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            return Fail("创建输出目录失败：" + ex.Message);
        }

        var wasReady = IsReady;

        if (!await EnsureDependenciesAsync(progress, ct).ConfigureAwait(false))
        {
            return Fail("需要先安装图像处理组件：" + (LastError ?? "未知原因"));
        }

        var python = ToolInstaller.FindPython();

        if (python is null)
        {
            return Fail("找不到 Python 解释器。");
        }

        // 精细档要额外的 977MB 模型；没有就在这里下（走加速通道），
        // 下不动也不放弃 —— 后面 rembg 会自己再试一次。
        if (spec.Matting == IdPhotoMatting.Fine)
        {
            await PrefetchFineModelAsync(progress, ct).ConfigureAwait(false);
        }

        progress?.Report("正在抠图并换底色（CPU 上约几秒到十几秒）…");

        var script = BuildGenerateScript(srcImage, outDir, spec);

        var run = await Task.Run(
            async () => await RunPythonAsync(python, script, TimeSpan.FromMinutes(5), ct)
                .ConfigureAwait(false),
            ct).ConfigureAwait(false);

        if (!run.Ok || !run.Output.Contains("DONE", StringComparison.Ordinal))
        {
            LastError = ExtractError(run);
            InvalidateMarkerIfMissingModule(run);
            return Fail("生成失败：" + LastError);
        }

        var outputs = run.Output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("OUT|", StringComparison.Ordinal))
            .Select(line => line[4..].Trim())
            .Where(File.Exists)
            .ToList();

        if (outputs.Count == 0)
        {
            LastError = ExtractError(run);
            return Fail("生成完成但没拿到图片，请检查输出目录权限。");
        }

        var installedNow = !wasReady;

        var message = $"生成完成：{outputs.Count} 张（{spec.Name} {spec.Width}×{spec.Height}）";

        if (run.Output.Contains("FACE|no", StringComparison.Ordinal))
        {
            message += "；没检测到人脸，已按人像居中构图";
        }

        if (installedNow)
        {
            message += "，这次顺带装好了图像处理组件";
        }

        return new IdPhotoResult(true, message, outputs, installedNow);
    }

    /// <inheritdoc />
    public async Task<string?> ComposeSheetAsync(
        string imagePath,
        string outputPath,
        IdPhotoSpec spec,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        LastError = null;

        if (!File.Exists(imagePath))
        {
            LastError = $"证件照不存在：{imagePath}";
            return null;
        }

        if (!await EnsureDependenciesAsync(progress, ct).ConfigureAwait(false))
        {
            LastError = "需要先安装图像处理组件：" + (LastError ?? "未知原因");
            return null;
        }

        var python = ToolInstaller.FindPython();

        if (python is null)
        {
            LastError = "找不到 Python 解释器。";
            return null;
        }

        // 一寸排 4×2（6 寸相纸横放 1800×1200）；二寸排 2×3（竖放 1200×1800）
        var oneInch = spec.Width <= 350;
        var cols = oneInch ? 4 : 2;
        var rows = oneInch ? 2 : 3;
        var sheetWidth = oneInch ? 1800 : 1200;
        var sheetHeight = oneInch ? 1200 : 1800;

        progress?.Report("正在排版 6 寸相纸…");

        var script = BuildSheetScript(
            imagePath, outputPath, cols, rows, sheetWidth, sheetHeight);

        var run = await Task.Run(
            async () => await RunPythonAsync(python, script, TimeSpan.FromMinutes(10), ct)
                .ConfigureAwait(false),
            ct).ConfigureAwait(false);

        if (!run.Ok || !run.Output.Contains("DONE", StringComparison.Ordinal) || !File.Exists(outputPath))
        {
            LastError = ExtractError(run);
            return null;
        }

        return outputPath;
    }

    // ---------- Python 脚本 ----------

    private static string BuildGenerateScript(string srcImage, string outDir, IdPhotoSpec spec)
    {
        var backgrounds = string.Join(", ", spec.Backgrounds.Select(b =>
            $"('{IdPhotoSpec.SuffixOf(b)}', {ToPyRgb(b)})"));

        return $$"""
            import io, os, sys

            try:
                sys.stdout.reconfigure(encoding='utf-8', errors='replace')
                sys.stderr.reconfigure(encoding='utf-8', errors='replace')
            except Exception:
                pass

            def emit(text):
                print(text, flush=True)

            from PIL import Image

            src = {{ToPyLiteral(srcImage)}}
            out_dir = {{ToPyLiteral(outDir)}}
            target_w = {{spec.Width}}
            target_h = {{spec.Height}}
            spec_name = {{ToPyLiteral(spec.Name)}}
            keep_whole = {{(spec.KeepWholePerson ? "True" : "False")}}
            model_name = {{ToPyLiteral(spec.Matting == IdPhotoMatting.Fine ? "isnet-general-use" : "u2net")}}
            backgrounds = [{{backgrounds}}]

            os.makedirs(out_dir, exist_ok=True)

            with open(src, 'rb') as f:
                raw = f.read()

            emit('STEP|正在抠出人像…')

            from rembg import remove, new_session
            emit('STEP|正在加载抠图模型（' + model_name + '）…')
            session = new_session(model_name)
            cut = remove(raw, session=session)
            fg = Image.open(io.BytesIO(cut)).convert('RGBA')

            # 连通域过滤：只保留面积最大的一块（人物主体），
            # 去掉旁边纯色板子/杂物被误判成前景的碎块。
            try:
                import numpy as np
                import cv2
                alpha = np.array(fg.getchannel('A'))
                solid = (alpha > 127).astype('uint8')
                num, labels, stats, _ = cv2.connectedComponentsWithStats(solid, 8)
                if num > 2:
                    areas = stats[1:, cv2.CC_STAT_AREA]
                    keep_idx = 1 + int(np.argmax(areas))
                    total = float(areas.sum())
                    kept = float(areas.max())
                    # 最大块占比太小时（比如人物被切成几块）就别乱删
                    if kept / total >= 0.55:
                        alpha = np.where(labels == keep_idx, alpha, 0).astype('uint8')
                        fg.putalpha(Image.fromarray(alpha))
                        emit('STEP|已清理 ' + str(num - 2) + ' 处背景残留')
            except Exception as exc:
                emit('WARN|残留清理跳过：' + str(exc))

            # 边缘轻微羽化：把硬边磨掉 1px，减少锯齿（证件照缩小后尤其明显）
            try:
                from PIL import ImageFilter
                a = fg.getchannel('A').filter(ImageFilter.GaussianBlur(0.6))
                fg.putalpha(a)
            except Exception:
                pass

            # 裁掉抠图后四周全透明的空白，后续按人像真实包围盒构图
            bbox = fg.getchannel('A').getbbox()
            if bbox is None:
                emit('ERROR|抠图结果为空（可能是纯色图片或人像太小）')
                sys.exit(3)
            fg = fg.crop(bbox)
            fg_w, fg_h = fg.size

            if keep_whole:
                # 完整保留原构图：等比缩放到画布内，居中，四边留纯色底。
                # 不裁切 —— 用户上传什么构图就输出什么构图。
                emit('STEP|正在按原构图铺底…')
                # 高度铺满：证件照主体是人物，上下留纯色条很丑（用户实测反馈）。
                # 横向不够宽时（原图偏窄）退回按宽度铺满、纵向居中。
                scale = target_h / float(fg_h)
                new_w = fg_w * scale
                new_h = float(target_h)

                if new_w < target_w:
                    scale = target_w / float(fg_w)
                    new_w = float(target_w)
                    new_h = fg_h * scale

                new_w = max(1, int(round(new_w)))
                new_h = max(1, int(round(new_h)))
                person = fg.resize((new_w, new_h), Image.LANCZOS)
                # 居中对齐；铺满时对应方向偏移为 0，不会出现纯色条
                off_x = (target_w - new_w) / 2.0
                off_y = (target_h - new_h) / 2.0
                emit('FACE|whole')
                face_done = True
            else:
                face_done = False

            if not face_done:
                # 老逻辑：按人脸比例裁切（KeepWholePerson=false 时才走）
                emit('STEP|正在按证件照规范构图…')

                face = None
                try:
                    import cv2
                    import numpy as np
                    bgr = cv2.imdecode(np.frombuffer(raw, np.uint8), cv2.IMREAD_COLOR)
                    if bgr is not None:
                        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
                        cascade = cv2.CascadeClassifier(
                            cv2.data.haarcascades + 'haarcascade_frontalface_default.xml')
                        rects = cascade.detectMultiScale(gray, 1.1, 5, minSize=(48, 48))
                        if len(rects) > 0:
                            r = max(rects, key=lambda x: int(x[2]) * int(x[3]))
                            face = (int(r[0]) - bbox[0], int(r[1]) - bbox[1], int(r[2]), int(r[3]))
                except Exception as exc:
                    emit('WARN|人脸检测不可用，改用人像居中：' + str(exc))

                if face is not None and face[2] > 20 and face[3] > 20:
                    # 人脸高度占画面 60%（规范 55%~65%），头顶留 8% 空白
                    scale = (0.60 * target_h) / face[3]
                    scale = max(scale, target_w / fg_w, target_h / fg_h)
                    new_w = max(1, int(round(fg_w * scale)))
                    new_h = max(1, int(round(fg_h * scale)))
                    person = fg.resize((new_w, new_h), Image.LANCZOS)
                    face_cx = (face[0] + face[2] / 2.0) * scale
                    head_top = (face[1] - 0.35 * face[3]) * scale
                    off_x = target_w / 2.0 - face_cx
                    off_y = 0.08 * target_h - head_top
                    # 不允许出现空白边：把偏移夹进「铺满画面」的取值范围
                    off_x = min(0.0, max(float(target_w - new_w), off_x))
                    off_y = min(0.0, max(float(target_h - new_h), off_y))
                    emit('FACE|yes')
                else:
                    # 检测不到人脸：居中放置，不报错
                    scale = max(target_w / fg_w, target_h / fg_h)
                    new_w = max(1, int(round(fg_w * scale)))
                    new_h = max(1, int(round(fg_h * scale)))
                    person = fg.resize((new_w, new_h), Image.LANCZOS)
                    off_x = (target_w - new_w) / 2.0
                    off_y = (target_h - new_h) / 2.0
                    emit('FACE|no')

            base = os.path.splitext(os.path.basename(src))[0]

            for suffix, color in backgrounds:
                canvas = Image.new('RGB', (target_w, target_h), color)
                canvas.paste(person, (int(round(off_x)), int(round(off_y))), person)
                out_path = os.path.join(out_dir, base + '_' + spec_name + '_' + suffix + '.jpg')
                canvas.save(out_path, 'JPEG', quality=95, dpi=(300, 300), subsampling=0)
                emit('OUT|' + out_path)
                emit('SIZE|' + str(target_w) + 'x' + str(target_h))

            emit('DONE')
            """;
    }

    private static string BuildSheetScript(
        string imagePath,
        string outputPath,
        int cols,
        int rows,
        int sheetWidth,
        int sheetHeight)
    {
        return $$"""
            import os, sys

            try:
                sys.stdout.reconfigure(encoding='utf-8', errors='replace')
                sys.stderr.reconfigure(encoding='utf-8', errors='replace')
            except Exception:
                pass

            def emit(text):
                print(text, flush=True)

            from PIL import Image

            src = {{ToPyLiteral(imagePath)}}
            out = {{ToPyLiteral(outputPath)}}
            cols = {{cols}}
            rows = {{rows}}
            sheet_w = {{sheetWidth}}
            sheet_h = {{sheetHeight}}
            gap = 24  # 2mm @300DPI，正好当裁剪白边

            img = Image.open(src).convert('RGB')
            w, h = img.size

            total_w = cols * w + (cols - 1) * gap
            total_h = rows * h + (rows - 1) * gap

            if total_w > sheet_w or total_h > sheet_h:
                emit('ERROR|相纸放不下（需要 %dx%d，相纸 %dx%d）' % (total_w, total_h, sheet_w, sheet_h))
                sys.exit(3)

            sheet = Image.new('RGB', (sheet_w, sheet_h), (255, 255, 255))
            ox = (sheet_w - total_w) // 2
            oy = (sheet_h - total_h) // 2

            for r in range(rows):
                for c in range(cols):
                    sheet.paste(img, (ox + c * (w + gap), oy + r * (h + gap)))

            os.makedirs(os.path.dirname(out), exist_ok=True)
            sheet.save(out, 'JPEG', quality=95, dpi=(300, 300), subsampling=0)
            emit('SHEET|' + out)
            emit('DONE')
            """;
    }

    // ---------- 内部 ----------

    /// <summary>把底色换成 PIL 的 RGB 元组字面量。</summary>
    private static string ToPyRgb(IdPhotoBackground background) => background switch
    {
        IdPhotoBackground.Red => "(212, 0, 0)",      // #D40000
        IdPhotoBackground.Blue => "(67, 142, 219)",  // #438EDB
        _ => "(255, 255, 255)",                      // #FFFFFF
    };

    /// <summary>
    /// 预下载 RMBG 2.0（精细档，约 977MB）。
    /// 失败不阻断 —— rembg 会自己再试，只是会慢很多。
    /// </summary>
    private static async Task PrefetchFineModelAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            if (File.Exists(FineModelPath) && new FileInfo(FineModelPath).Length >= FineModelMinBytes)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FineModelPath)!);

            var temp = FineModelPath + ".part";
            var errors = new List<string>();

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(120) };

            foreach (var url in FineModelUrls)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    progress?.Report("正在下载精细抠图模型（约 170MB，仅首次，走加速通道）…");

                    using var response = await client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? 0;

                    await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var destination = File.Create(temp))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        var lastReported = -1;
                        int n;

                        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            read += n;

                            if (total > 0)
                            {
                                var percent = (int)(read * 100 / total);

                                if (percent != lastReported && percent % 5 == 0)
                                {
                                    lastReported = percent;
                                    progress?.Report($"正在下载精细抠图模型… {percent}%（{read / 1024 / 1024}MB / {total / 1024 / 1024}MB）");
                                }
                            }
                        }
                    }

                    if (new FileInfo(temp).Length >= FineModelMinBytes)
                    {
                        File.Move(temp, FineModelPath, overwrite: true);
                        progress?.Report("精细抠图模型已就绪。");
                        return;
                    }

                    errors.Add(url + " → 文件不完整");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add(url + " → " + ex.Message);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temp))
                        {
                            File.Delete(temp);
                        }
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            }

            progress?.Report("精细模型下载失败，将退回标准档：" + string.Join(" | ", errors.Take(2)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report("精细模型预下载异常（不影响出图，只是慢）：" + ex.Message);
        }
    }

    /// <summary>预下载 U2Net 模型。失败不阻断 —— 交给 rembg 自己再试一次。</summary>
    private static async Task PrefetchModelAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            if (File.Exists(ModelPath) && new FileInfo(ModelPath).Length >= ModelMinBytes)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);

            var temp = ModelPath + ".part";
            var errors = new List<string>();

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            foreach (var url in ModelUrls)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    progress?.Report("正在下载抠图模型（约 170MB，仅首次）…");

                    using var response = await client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? 0;

                    await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var destination = File.Create(temp))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        var lastPercent = -1;
                        int n;

                        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            read += n;

                            if (total > 0)
                            {
                                var percent = (int)(read * 100 / total);

                                if (percent / 10 != lastPercent / 10)
                                {
                                    lastPercent = percent;
                                    progress?.Report(
                                        $"正在下载抠图模型 {percent}%（{read / 1048576} MB / {total / 1048576} MB）…");
                                }
                            }
                        }
                    }

                    if (new FileInfo(temp).Length < ModelMinBytes)
                    {
                        throw new InvalidOperationException("下载不完整。");
                    }

                    var md5 = await Md5OfFileAsync(temp, ct).ConfigureAwait(false);

                    if (!string.Equals(md5, ModelMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"校验不通过（MD5 {md5}）。");
                    }

                    File.Move(temp, ModelPath, overwrite: true);
                    progress?.Report("抠图模型已就绪。");
                    return;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(temp);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(temp);
                    errors.Add(ex.Message);
                }
            }

            // 不抛：rembg 内部还会再尝试下载一次
            progress?.Report(
                "模型预下载没成功，交给 rembg 自动下载…（" + string.Join("；", errors) + "）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report("模型预下载出错，交给 rembg 自动下载：" + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就留着，下次覆盖
        }
    }

    /// <summary>算文件 MD5（模型校验用，176MB 约 0.5 秒）。</summary>
    private static async Task<string> Md5OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.MD5
            .HashDataAsync(stream, ct)
            .ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>检查某个 Python 模块能不能 import。</summary>
    /// <remarks>
    /// 超时给到 5 分钟：rembg 第一次 import（numba / scikit-image / onnxruntime）在
    /// 开了实时防护的机器上实测能到 100 秒，给 2 分钟会误判成「没装」。
    /// </remarks>
    private static async Task<bool> HasModuleAsync(string python, string module, CancellationToken ct)
    {
        var run = await RunPythonAsync(
            python,
            $"import {module}\nprint('HAS_MODULE')\n",
            TimeSpan.FromMinutes(5),
            ct).ConfigureAwait(false);

        return run.Ok && run.Output.Contains("HAS_MODULE", StringComparison.Ordinal);
    }

    private static ProcessStartInfo NewPythonStartInfo(string python)
    {
        var startInfo = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };

        // 中文路径 / 中文日志都按 UTF-8 走，避免 cp936 乱码
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        return startInfo;
    }

    private static async Task<(bool Ok, string Output, string Error)> RunPythonAsync(
        string python,
        string code,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = NewPythonStartInfo(python);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);

        return await RunProcessAsync(startInfo, timeout, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Ok, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return (false, string.Empty, "无法启动进程。");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            return (process.ExitCode == 0,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 忽略
            }

            return (false, string.Empty, ct.IsCancellationRequested ? "已取消。" : "执行超时。");
        }
    }

    /// <summary>
    /// 标记文件可能过期（用户手动卸了包 / Python 被换掉）。这种情况删掉标记，
    /// 下次调用会重新检测并安装，而不是一直走进「已就绪」的死路。
    /// </summary>
    private static void InvalidateMarkerIfMissingModule((bool Ok, string Output, string Error) run)
    {
        var text = run.Output + "\n" + run.Error;

        if (!text.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("No module named", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch
        {
            // 删不掉就留着，不影响本次报错
        }
    }

    /// <summary>从脚本输出里挑出最适合给老师看的一句错误。</summary>
    private static string ExtractError((bool Ok, string Output, string Error) run)
    {
        var explicitError = run.Output
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("ERROR|", StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(explicitError))
        {
            return explicitError[6..].Trim();
        }

        var detail = string.IsNullOrWhiteSpace(run.Error) ? run.Output : run.Error;

        if (string.IsNullOrWhiteSpace(detail))
        {
            return "未知错误。";
        }

        detail = detail.Trim();

        return detail.Length > 600 ? detail[^600..] : detail;
    }

    private IdPhotoResult Fail(string message)
    {
        LastError ??= message;
        return new IdPhotoResult(false, message, Array.Empty<string>(), false);
    }

    /// <summary>把路径变成 Python 字符串字面量（统一正斜杠，避开转义问题）。</summary>
    private static string ToPyLiteral(string path) =>
        "\"" + path.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
}
