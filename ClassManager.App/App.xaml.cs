using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassManager.App.Models;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;
using ClassManager.App.Views;

namespace ClassManager.App;

/// <summary>
/// 应用入口（组合根）：
/// 命令行解析 → 单实例判定 → 安装判定 → 主题初始化 → 服务装配 → 启动向导或主窗口。
/// </summary>
public partial class App : Application
{
    private Window? _mainWindow;

    /// <summary>启动时通过 `--import &lt;文件&gt;` 指定的待导入成绩单路径。</summary>
    private string? _importOnStart;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallCrashGuards();

        // 0) `--import <文件>` 需进入 UI（导入后停在主界面），不属于「处理完即退出」的命令
        _importOnStart = ResolveImportPath(e.Args);

        // 1) 命令行优先处理（不进入 UI，便于静默部署与自动化测试）
        if (TryHandleCommandLine(e.Args, out var cliExitCode))
        {
            Environment.ExitCode = cliExitCode;
            Shutdown(cliExitCode);
            return;
        }

        // 2) 组合根装配
        try
        {
            AppServices.Initialize();
        }
        catch (Exception ex)
        {
            ConsoleHost.AttachOnly();
            ConsoleHost.Log($"初始化失败: {ex.Message}");
            Environment.ExitCode = ExitCodes.Error;
            Shutdown(ExitCodes.Error);
            return;
        }

        // 3) 单实例判定（必须早于任何窗口显示）
        try
        {
            AppServices.SingleInstance.Acquire();
        }
        catch (Exception ex)
        {
            ConsoleHost.AttachOnly();
            ConsoleHost.Log($"单实例初始化失败: {ex.Message}");
        }

        if (!AppServices.SingleInstance.IsFirstInstance)
        {
            // 非首实例：通知首实例激活窗口后立即退出，退出码 3
            AppServices.SingleInstance.SignalFirstInstance();
            Environment.ExitCode = ExitCodes.AlreadyRunning;
            Shutdown(ExitCodes.AlreadyRunning);
            return;
        }

        AppServices.SingleInstance.ActivatedBySecondInstance += OnActivatedBySecondInstance;
        AppServices.SingleInstance.StartListening();

        // 4) 主题初始化（跟随系统 / 手动覆盖由设置决定）
        AppServices.Theme.Apply(AppServices.Settings.Current.Theme);

        // 4.5) 安装完整性自检 + 自愈：每次启动都做。
        //      已安装但主程序缺失（环境/清理工具删文件）或版本不一致时，静默修复并重启到安装副本；
        //      只有「确实没装过」才走安装向导——避免「装完再打开又弹向导」。
        var installStatus = AppServices.Install.GetStatus();

        if (_importOnStart is null && installStatus.State is InstallState.Broken or InstallState.Outdated)
        {
            if (EnsureInstallHealthy(installStatus))
            {
                Environment.ExitCode = ExitCodes.Ok;
                Shutdown(ExitCodes.Ok);
                return;
            }
        }

        // 5) 已安装（含"有安装记录但文件缺失、且修复失败"）→ 主界面；
        //    `--import` 直接进主界面；**只有确实没安装过才弹安装向导**。
        if (installStatus.State != InstallState.NotInstalled || _importOnStart is not null)
        {
            ShowMainWindow(_importOnStart);
        }
        else
        {
            ShowInstallWizard();
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.SingleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 安装需要修复时执行自愈；修复成功后若当前不在安装目录，则启动安装副本。
    /// </summary>
    /// <returns>true 表示已启动安装副本并应结束当前进程。</returns>
    private bool EnsureInstallHealthy(InstallStatus status)
    {
        try
        {
            ConsoleHost.AttachOnly();
            ConsoleHost.Log($"检测到安装需要修复（{status.State}）：{status.Detail}");

            if (!AppServices.Install.RepairInstall(out var message))
            {
                ConsoleHost.Log(message);
                return false;
            }

            ConsoleHost.Log(message);

            var currentDir = AppServices.Install.CurrentProcessDirectory;
            var installDir = status.InstallDir;

            if (string.Equals(
                    Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                // 本身就在安装目录里跑：修复完成，继续正常启动即可
                return false;
            }

            ConsoleHost.Log("正在启动修复后的程序…");
            AppServices.Install.LaunchInstalledCopy(installDir);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleHost.AttachOnly();
            ConsoleHost.Log($"安装修复失败: {ex.Message}");
            return false;
        }
    }

    private void ShowMainWindow(string? importPath)
    {
        var viewModel = new MainWindowViewModel();
        AppServices.PageProvider.Shell = viewModel;

        var window = new MainWindow(viewModel);
        _mainWindow = window;
        MainWindow = window;
        window.Show();

        // 主界面（含点名页）已就绪后再导入，保证 RosterImported 事件被页面订阅
        if (!string.IsNullOrWhiteSpace(importPath))
        {
            _ = viewModel.ImportRosterFromPathAsync(importPath);
        }

        // 恢复上次导入的名单（用户不做任何操作就能直接点名）
        _ = viewModel.RestoreLastRosterAsync();

        // 启动时静默检查更新（可在「关于 → 更新源设置」里关闭）
        _ = viewModel.CheckUpdateOnStartupAsync();
    }

    private void ShowInstallWizard()
    {
        var viewModel = new InstallWizardViewModel();
        var window = new InstallWizardWindow(viewModel);
        _mainWindow = window;
        MainWindow = window;
        window.Show();
    }

    private void OnActivatedBySecondInstance(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow is not null)
            {
                AppServices.WindowActivation.BringToFront(_mainWindow);
            }
        });
    }

    /// <summary>
    /// 兜底：任何未捕获异常都不许让程序静默消失。
    /// <para>
    /// 真实反馈：0.39 里点「立即更新」下载超时，异常从
    /// <c>AsyncRelayCommand</c> 逃出来，进程直接崩 —— 用户看到的是
    /// 「进度条卡 0% 然后闪退」，连一句错误提示都没有。
    /// </para>
    /// <para>
    /// 这里把 UI 线程异常拦下来弹提示，并记录到日志；后台任务异常只记日志。
    /// 更新功能本身已经各自 try/catch，这里只是最后一道网。
    /// </para>
    /// </summary>
    private void InstallCrashGuards()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                AppendCrashLog("UI", args.Exception);
            }
            catch
            {
                // 日志都写不了就算了
            }

            try
            {
                MessageBox.Show(
                    "程序遇到一个未处理的错误，已记录到日志。\n\n"
                    + args.Exception.Message
                    + "\n\n可以继续使用，若反复出现请把 install.log 发给我。",
                    "班级综合管理系统",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch
            {
                // 弹不出提示也不能再崩一次
            }

            // 吃掉这次异常，别让进程退出
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                AppendCrashLog("AppDomain", args.ExceptionObject as Exception);
            }
            catch
            {
                // 忽略
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                AppendCrashLog("Task", args.Exception);
            }
            catch
            {
                // 忽略
            }

            args.SetObserved();
        };
    }

    /// <summary>把崩溃信息写到 install.log 同目录，方便事后排查。</summary>
    private static void AppendCrashLog(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClassManager");

            Directory.CreateDirectory(dir);

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [崩溃/{source}] {ex?.GetType().Name}: {ex?.Message}"
                       + Environment.NewLine
                       + ex?.StackTrace
                       + Environment.NewLine;

            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>处理命令行参数；返回 true 表示已处理并应直接退出。</summary>
    private static bool TryHandleCommandLine(string[] args, out int exitCode)
    {
        exitCode = ExitCodes.Ok;
        if (args is null || args.Length == 0)
        {
            return false;
        }

        if (HasFlag(args, "--version"))
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine(SelfTest.GetVersionString());
            exitCode = ExitCodes.Ok;
            return true;
        }

        if (HasFlag(args, "--selftest"))
        {
            exitCode = SelfTest.Run();
            return true;
        }

        var publishUpdateIndex = IndexOfFlag(args, "--publish-update");
        if (publishUpdateIndex >= 0)
        {
            // 发布更新源：--publish-update [更新源目录] [--notes "说明"]
            // 不给目录就写 %AppData%\ClassManager\update-source
            exitCode = RunPublishUpdate(args, publishUpdateIndex);
            return true;
        }

        var exam2PptIndex = IndexOfFlag(args, "--exam2ppt");
        if (exam2PptIndex >= 0)
        {
            // 评讲课件生成（静默，供自动化验证）：--exam2ppt <试题docx> <答案docx> <输出pptx>
            exitCode = RunExam2Ppt(args, exam2PptIndex);
            return true;
        }

        var purgeIndex = IndexOfFlag(args, "--purge");
        if (purgeIndex >= 0)
        {
            // 独立卸载清理模式：不进 UI、不受单实例互斥影响
            exitCode = RunPurge(args, purgeIndex);
            return true;
        }

        var installIndex = IndexOfFlag(args, "--install");
        if (installIndex >= 0)
        {
            exitCode = RunInstall(args, installIndex);
            return true;
        }

        if (HasFlag(args, "--uninstall"))
        {
            exitCode = RunUninstall(args);
            return true;
        }

        if (HasFlag(args, "--upgrade"))
        {
            exitCode = RunUpgrade();
            return true;
        }

        if (HasFlag(args, "--layout-probe"))
        {
            exitCode = RunLayoutProbe();
            return true;
        }

        var aiIndex = IndexOfFlag(args, "--ai");
        if (aiIndex >= 0)
        {
            exitCode = RunAiProbe(args, aiIndex);
            return true;
        }

        return false;
    }

    private static int RunInstall(string[] args, int installIndex)
    {
        try
        {
            AppServices.Initialize();
            ConsoleHost.Ensure();

            var quiet = HasFlag(args, "--quiet");
            var directory = installIndex + 1 < args.Length && !args[installIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[installIndex + 1]
                : AppServices.Install.DefaultInstallDirectory;

            var options = new InstallOptions
            {
                InstallDirectory = directory,
                CreateDesktopShortcut = !HasFlag(args, "--no-desktop-shortcut"),
                CreateStartMenuShortcut = !HasFlag(args, "--no-startmenu-shortcut"),
                LaunchAfterInstall = HasFlag(args, "--launch"),
            };

            IProgress<string>? progress = quiet ? null : new Progress<string>(ConsoleHost.WriteLine);
            AppServices.Install.Install(options, progress);
            ConsoleHost.WriteLine($"安装完成: {directory}");

            if (options.LaunchAfterInstall)
            {
                AppServices.Install.LaunchInstalledCopy(directory);
            }

            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine($"安装失败: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static int RunPurge(string[] args, int purgeIndex)
    {
        try
        {
            ConsoleHost.Ensure();

            if (purgeIndex + 1 >= args.Length
                || !int.TryParse(args[purgeIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mainPid))
            {
                ConsoleHost.WriteLine("--purge 需要 <pid> 参数。");
                return ExitCodes.Error;
            }

            var installDir = purgeIndex + 2 < args.Length
                             && !args[purgeIndex + 2].StartsWith("--", StringComparison.Ordinal)
                ? args[purgeIndex + 2]
                : string.Empty;

            var removeSettings = purgeIndex + 3 < args.Length
                                 && string.Equals(args[purgeIndex + 3], "true", StringComparison.OrdinalIgnoreCase);

            return UninstallPurge.Run(mainPid, installDir, removeSettings);
        }
        catch (Exception ex)
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine($"卸载清理失败: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static int RunUninstall(string[] args)
    {
        try
        {
            AppServices.Initialize();
            ConsoleHost.Ensure();

            var removeSettings = !HasFlag(args, "--keep-settings");

            // 「应用和功能」里点卸载走的就是这条：同样启动独立卸载程序，实现干净卸载
            var launched = AppServices.Install.LaunchUninstaller(removeSettings);
            if (launched)
            {
                ConsoleHost.WriteLine("卸载程序已启动，将在本进程退出后完成清理。");
                return ExitCodes.Ok;
            }

            ConsoleHost.WriteLine("启动独立卸载程序失败，回退到内置清理...");
            AppServices.Install.Uninstall(removeSettings);
            ConsoleHost.WriteLine("卸载完成。");
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine($"卸载失败: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    /// <summary>静默覆盖安装（诊断 / 运维用）：把当前程序目录覆盖到已安装目录并更新版本。</summary>
    private static int RunUpgrade()
    {
        try
        {
            AppServices.Initialize();
            ConsoleHost.Ensure();

            ConsoleHost.WriteLine($"当前版本: {AppVersion.Display}");

            var status = AppServices.Install.GetStatus();
            if (status.State == InstallState.NotInstalled)
            {
                ConsoleHost.WriteLine("安装状态: 未安装，无需修复/覆盖。");
                return ExitCodes.Ok;
            }

            ConsoleHost.WriteLine($"安装状态: {status.State}（已装 v{status.InstalledVersion ?? "?"}）");
            var upgraded = AppServices.Install.RepairInstall(out var message);
            ConsoleHost.WriteLine($"结果: {(upgraded ? "已修复/覆盖安装" : "未变更")} — {message}");

            if (upgraded)
            {
                var after = AppServices.Install.GetStatus();
                ConsoleHost.WriteLine($"新安装状态: {after.State}（v{after.InstalledVersion ?? "?"}，{after.ExeLength:N0} 字节）");
            }

            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine($"覆盖安装失败: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    /// <summary>
    /// 布局探针（诊断用）：无头环境下用 Measure/Arrange 真实计算一次布局，
    /// 把聊天页各层的高量出来，用来确认消息区是否有界、输入区是否停在底部。
    /// </summary>
    private static int RunLayoutProbe()
    {
        try
        {
            AppServices.Initialize();
            ConsoleHost.Ensure();

            var mainViewModel = new MainWindowViewModel();
            AppServices.PageProvider.Shell = mainViewModel;

            var window = new MainWindow(mainViewModel) { Width = 1400d, Height = 800d };

            var frame = new System.Windows.Threading.DispatcherFrame();

            void Measure()
            {
                try
                {
                    var page = FindVisualChild<AiOfficePage>(window) ?? FindLogicalChild<AiOfficePage>(window);
                    if (page is null)
                    {
                        ConsoleHost.WriteLine("PROBE 未找到 AI 办公页");
                        return;
                    }

                    page.UpdateLayout();

                    if (page.DataContext is AiOfficeViewModel vm)
                    {
                        for (var i = 1; i <= 40; i++)
                        {
                            vm.Messages.Add(new ChatItem
                            {
                                Role = i % 2 == 0 ? "user" : "assistant",
                                Content = $"测试消息 {i}：这是一行用于撑高内容的文本，用来验证消息区是否真的能滚动。",
                            });
                        }
                    }

                    window.UpdateLayout();

                    ConsoleHost.WriteLine($"窗口 ActualHeight       = {window.ActualHeight:0.#}");
                    ConsoleHost.WriteLine($"导航栏 ActualHeight     = {window.RootNavigation.ActualHeight:0.#}");
                    ConsoleHost.WriteLine($"页面根 ActualHeight     = {((FrameworkElement)page.Content).ActualHeight:0.#}");

                    var scroll = FindVisualChildByName<ScrollViewer>(page, "MessageScroll");
                    if (scroll is not null)
                    {
                        ConsoleHost.WriteLine($"消息区 ActualHeight     = {scroll.ActualHeight:0.#}");
                        ConsoleHost.WriteLine($"消息区 ViewportHeight   = {scroll.ViewportHeight:0.#}");
                        ConsoleHost.WriteLine($"消息区 ExtentHeight     = {scroll.ExtentHeight:0.#}");
                        ConsoleHost.WriteLine($"消息区 ScrollableHeight = {scroll.ScrollableHeight:0.#}   <- >0 才能滚动");
                    }
                    else
                    {
                        ConsoleHost.WriteLine("消息区 ScrollViewer 未找到");
                    }

                    var inputBox = FindVisualChildByName<Wpf.Ui.Controls.TextBox>(page, "InputBox");
                    if (inputBox is not null)
                    {
                        var y = inputBox.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
                        ConsoleHost.WriteLine($"输入框 相对窗口 Y       = {y:0.#} (窗口高 {window.ActualHeight:0.#})");
                        ConsoleHost.WriteLine(y + inputBox.ActualHeight <= window.ActualHeight
                            ? "输入框 在可视区域内 OK"
                            : "输入框 被挤出可视区 NG");
                    }
                    else
                    {
                        ConsoleHost.WriteLine("输入框 未找到");
                    }
                }
                catch (Exception ex)
                {
                    ConsoleHost.WriteLine($"布局探针内部错误: {ex.Message}");
                }
                finally
                {
                    frame.Continue = false;
                }
            }

            window.Loaded += (_, _) =>
            {
                try
                {
                    window.RootNavigation.Navigate(typeof(AiOfficePage));
                    // 页面是延迟创建的，等一个空闲周期再量
                    window.Dispatcher.BeginInvoke(new Action(Measure),
                        System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
                catch (Exception ex)
                {
                    ConsoleHost.WriteLine($"导航失败: {ex.Message}");
                    frame.Continue = false;
                }
            };

            window.Show();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            window.Close();

            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine($"布局探针失败: {ex.Message}\n{ex.StackTrace}");
            return ExitCodes.Error;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            var deeper = FindVisualChild<T>(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    private static T? FindLogicalChild<T>(DependencyObject root) where T : class
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T typed)
            {
                return typed;
            }

            if (child is DependencyObject d)
            {
                var deeper = FindLogicalChild<T>(d);
                if (deeper is not null)
                {
                    return deeper;
                }
            }
        }

        return null;
    }

    private static T? FindVisualChildByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name)
            {
                return typed;
            }

            var deeper = FindVisualChildByName<T>(child, name);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>AI 办公静默探针：--ai "&lt;提示词&gt;" [--ai-tools]，用于自动化验证链路。</summary>
    private static int RunAiProbe(string[] args, int index)
    {
        try
        {
            AppServices.Initialize();
            ConsoleHost.Ensure();

            if (index + 1 >= args.Length)
            {
                ConsoleHost.WriteLine("用法: --ai \"<提示词>\" [--ai-tools]");
                return ExitCodes.Error;
            }

            var prompt = args[index + 1];
            var withTools = HasFlag(args, "--ai-tools");

            ConsoleHost.WriteLine($"模型: {AppServices.Ai.ModelName}");
            ConsoleHost.WriteLine($"当前 key: {AppServices.Ai.ActiveKeyLabel}");
            ConsoleHost.WriteLine($"工具: {(withTools ? "启用" : "未启用")}");
            ConsoleHost.WriteLine($"提示词: {prompt}");
            ConsoleHost.WriteLine(new string('-', 60));

            var history = new List<AiMessage> { new() { Role = "user", Content = prompt } };
            var specs = withTools ? AiFileTools.Specs : Array.Empty<AiToolSpec>();

            // 完整 agent 循环：工具结果回灌给模型，最多 4 轮
            var totalPrompt = 0L;
            var totalCompletion = 0L;
            AiTurnResult? last = null;

            var continuations = 0;
            var round = 0;

            while (round < 12)
            {
                round++;
                var result = AppServices.Ai
                    .CompleteAsync(history, specs, new ConsoleAiStream(), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                last = result;

                if (!result.Ok)
                {
                    ConsoleHost.WriteLine($"[失败] {result.Error}");
                    return ExitCodes.Error;
                }

                totalPrompt += result.Usage.PromptTokens;
                totalCompletion += result.Usage.CompletionTokens;

                history.Add(new AiMessage
                {
                    Role = "assistant",
                    Content = result.Content,
                    Reasoning = result.Reasoning,
                    ToolCalls = result.ToolCalls.Count > 0 ? result.ToolCalls.ToList() : null,
                });

                if (result.ToolCalls.Count == 0)
                {
                    // 截断（finish_reason=length）或网关泄漏了 DSML 标记 → 都不是「模型说完了」，
                    // 不能直接当完成返回，否则命令行看到的也是「活没干完却报完成」。
                    var truncated = result.FinishReason == "length";
                    var abnormal = truncated
                                   || result.DsmlLeak
                                   || result.ReasoningOnly
                                   || result.MarkerLoop;

                    if (abnormal && round < 8)
                    {
                        ConsoleHost.WriteLine(string.Empty);
                        ConsoleHost.WriteLine(result.MarkerLoop
                            ? "  ⚠ 模型陷入重复输出内部标记的循环，重试…"
                            : result.ReasoningOnly
                                ? "  ⚠ 只输出了思考内容没有正文，重试…"
                                : result.DsmlLeak
                                    ? "  ⚠ 工具调用格式异常（DSML 泄漏），重试…"
                                    : "  ⚠ 输出被长度限制截断，重试…");

                        history.Add(new AiMessage
                        {
                            Role = "user",
                            Content = result.MarkerLoop
                                ? "上一轮你陷入了重复输出内部标记的循环。请停止重复，直接给出面向用户的回答或调用工具，不要输出任何形如 <result> 的内部标记。"
                                : result.ReasoningOnly
                                    ? "上一轮你只输出了内部思考内容，没有给用户可读的回答。请直接给出面向用户的回答（思考过程不需要写出来），不要输出任何形如 <result> 的内部标记。"
                                    : result.DsmlLeak
                                        ? "上一轮你输出的工具调用标记无法被解析。请只通过平台提供的结构化工具调用接口发起调用，不要在正文里输出任何形如 ｜DSML｜ 的标记。"
                                        : "上一轮你的输出被长度限制截断了。请接着上一轮继续，把没做完的部分做完。",
                        });

                        continue;
                    }

                    ConsoleHost.WriteLine(string.Empty);
                    ConsoleHost.WriteLine(new string('-', 60));
                    ConsoleHost.WriteLine($"完成：共 {round} 轮，正文 {result.Content.Length} 字");
                    ConsoleHost.WriteLine($"用量：输入 {totalPrompt:N0} · 输出 {totalCompletion:N0}");
                    ConsoleHost.WriteLine($"使用 key：{result.KeyLabel}");

                    if (abnormal)
                    {
                        ConsoleHost.WriteLine("注意：最后一轮输出异常（截断或工具调用格式错误），任务可能还没做完。");
                    }

                    return ExitCodes.Ok;
                }

                foreach (var call in result.ToolCalls)
                {
                    ConsoleHost.WriteLine(string.Empty);
                    ConsoleHost.WriteLine($"[第 {round} 轮] 工具 {call.Name} 参数 {call.Arguments}");

                    // 默认探针绝不写入用户文件；显式加 --ai-allow-write 才允许（仅限工作区内）
                    var allowWrite = HasFlag(args, "--ai-allow-write");

                    var tools = new AiFileTools(
                        new AiSettings { WorkspaceDirectory = AiSettings.DefaultWorkspace },
                        _ => Task.FromResult(allowWrite));

                    var output = tools.ExecuteAsync(call.Name, call.Arguments, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    ConsoleHost.WriteLine($"  结果: {(output.Length > 200 ? output[..200] + "…" : output).Trim()}");

                    history.Add(new AiMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        Content = output,
                    });
                }

                // 轮次用尽但还在调工具 → 自动续跑（与 GUI 的 AiOfficeViewModel 同一套逻辑）
                if (round >= 12 && continuations < 2)
                {
                    continuations++;
                    round = 0;

                    history.Add(new AiMessage
                    {
                        Role = "user",
                        Content = "请继续完成剩下的工作，直到全部做完再汇报。",
                    });

                    ConsoleHost.WriteLine("  ⚠ 工具轮次用尽，正在自动续跑…");
                    continue;
                }
            }

            ConsoleHost.WriteLine(string.Empty);
            ConsoleHost.WriteLine($"达到最大轮数，最后一轮正文 {last?.Content.Length ?? 0} 字。");
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine($"AI 探针失败: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    /// <summary>把流式增量打到控制台（正文直接输出，思考内容用前缀标注）。</summary>
    private sealed class ConsoleAiStream : IProgress<AiStreamUpdate>
    {
        public void Report(AiStreamUpdate value)
        {
            if (!string.IsNullOrEmpty(value.ContentDelta))
            {
                Console.Out.Write(value.ContentDelta);
                Console.Out.Flush();
            }
        }
    }

    /// <summary>评讲课件生成（静默 CLI）：先确保引擎就绪，再调用引擎生成课件。</summary>
    /// <summary>
    /// 发布更新源：把安装包复制过去并生成 version.json，
    /// 之后在应用里「关于 → 更新源设置」填上清单路径就能「检查更新」了。
    /// </summary>
    private static int RunPublishUpdate(string[] args, int flagIndex)
    {
        ConsoleHost.Ensure();

        var directory = UpdatePublisher.DefaultSourceDirectory;
        var notes = string.Empty;
        var page = string.Empty;
        var github = string.Empty;
        var extraMirrors = new List<string>();

        for (var i = flagIndex + 1; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--notes", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    notes = args[++i];
                }

                continue;
            }

            if (arg.StartsWith("--mirror", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    extraMirrors.Add(args[++i]);
                }

                continue;
            }

            if (arg.StartsWith("--github", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    github = args[++i];
                }

                continue;
            }

            if (arg.StartsWith("--page", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    page = args[++i];
                }

                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            directory = arg;
        }

        // 安装包默认取发布目录里的 exe
        var package = Path.Combine(AppContext.BaseDirectory, "ClassManager.App.exe");

        if (!File.Exists(package))
        {
            ConsoleHost.WriteLine($"找不到安装包：{package}");
            ConsoleHost.WriteLine("请在 publish 目录里运行本命令。");
            return ExitCodes.Error;
        }

        var message = UpdatePublisher.Publish(
            package,
            directory,
            notes,
            pageUrl: page,
            githubRepo: github,
            extraMirrors: extraMirrors);
        ConsoleHost.WriteLine(message);

        return message.StartsWith("已发布", StringComparison.Ordinal) ? ExitCodes.Ok : ExitCodes.Error;
    }

    private static int RunExam2Ppt(string[] args, int index)
    {
        try
        {
            AppServices.Initialize();
            ConsoleHost.Ensure();

            if (index + 3 >= args.Length)
            {
                ConsoleHost.WriteLine("用法: --exam2ppt <试题docx> <答案docx> <输出pptx>");
                return ExitCodes.Error;
            }

            var examPath = args[index + 1];
            var answerPath = args[index + 2];
            var outputPath = args[index + 3];

            ConsoleHost.WriteLine($"引擎载荷版本: {AppServices.Exam2Ppt.PayloadVersion}");

            // 引擎准备（首次会解压内嵌载荷）
            var engineWatch = Stopwatch.StartNew();
            var ready = AppServices.Exam2Ppt
                .EnsureEngineAsync(new ConsoleTextProgress(), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            engineWatch.Stop();

            if (!ready)
            {
                ConsoleHost.WriteLine($"[失败] {AppServices.Exam2Ppt.LastEngineError ?? "引擎未就绪"}");
                return ExitCodes.Error;
            }

            ConsoleHost.WriteLine($"引擎准备完成，用时 {engineWatch.ElapsedMilliseconds} ms（已解压则接近 0）");
            ConsoleHost.WriteLine($"引擎目录: {AppServices.Exam2Ppt.EngineRoot}");
            ConsoleHost.WriteLine(string.Empty);

            var options = new Exam2PptOptions
            {
                ExamDocxPath = examPath,
                AnswerDocxPath = answerPath,
                OutputPath = outputPath,
            };

            var watch = Stopwatch.StartNew();
            var result = AppServices.Exam2Ppt
                .GenerateAsync(options, new ConsoleExam2PptProgress(), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            watch.Stop();

            if (!result.Ok)
            {
                ConsoleHost.WriteLine($"[失败] {result.Message}");
                return ExitCodes.Error;
            }

            ConsoleHost.WriteLine(string.Empty);
            ConsoleHost.WriteLine($"产物: {result.OutputPath}");
            ConsoleHost.WriteLine($"页数: {result.SlideCount}");
            ConsoleHost.WriteLine($"生成用时: {watch.ElapsedMilliseconds} ms");

            if (result.MissingQuestions.Count > 0)
            {
                ConsoleHost.WriteLine($"未匹配到答案/解析的题: {string.Join("、", result.MissingQuestions)}");
            }

            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine($"评讲课件生成失败: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    /// <summary>把引擎阶段消息按行同步写到控制台（不用 Progress&lt;T&gt;，避免控制台无同步上下文导致乱序）。</summary>
    private sealed class ConsoleTextProgress : IProgress<string>
    {
        public void Report(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ConsoleHost.WriteLine(value);
            }
        }
    }

    /// <summary>把引擎逐行输出原样写到控制台。</summary>
    private sealed class ConsoleExam2PptProgress : IProgress<Exam2PptProgress>
    {
        public void Report(Exam2PptProgress value)
        {
            if (!string.IsNullOrWhiteSpace(value.Message))
            {
                ConsoleHost.WriteLine(value.Message);
            }
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>解析 `--import &lt;文件&gt;` 的参数值；无有效值返回 null。</summary>
    private static string? ResolveImportPath(string[] args)
    {
        var index = IndexOfFlag(args, "--import");
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var value = args[index + 1];
        return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }

    private static int IndexOfFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
