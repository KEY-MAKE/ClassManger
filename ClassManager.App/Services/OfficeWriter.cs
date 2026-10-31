using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClassManager.App.Services;

/// <summary>
/// 用引擎自带的 Python 运行时（内含 python-docx / python-pptx）生成 Word 与 PPT。
/// <para>
/// 手搓 OOXML 很容易产出 Word 打不开的文件，这里直接复用打包好的依赖，
/// 生成的 docx / pptx 一定能被 Office 正常打开。
/// </para>
/// </summary>
public sealed class OfficeWriter
{
    /// <summary>随应用附带的转换脚本（Markdown → docx / pptx）。</summary>
    private const string Script = """
        # -*- coding: utf-8 -*-
        import sys, os
        try:
            sys.stdout.reconfigure(encoding='utf-8')
            sys.stderr.reconfigure(encoding='utf-8')
        except Exception:
            pass

        def read_text(path):
            with open(path, 'r', encoding='utf-8') as f:
                return f.read()

        def build_docx(src, out):
            from docx import Document
            from docx.shared import Pt
            from docx.oxml.ns import qn

            doc = Document()
            style = doc.styles['Normal']
            style.font.name = '等线'
            style.font.size = Pt(11)
            try:
                style.element.rPr.rFonts.set(qn('w:eastAsia'), '等线')
            except Exception:
                pass

            for raw in src.splitlines():
                line = raw.rstrip()
                if not line.strip():
                    continue
                if line.startswith('### '):
                    doc.add_heading(line[4:].strip(), level=3)
                elif line.startswith('## '):
                    doc.add_heading(line[3:].strip(), level=2)
                elif line.startswith('# '):
                    doc.add_heading(line[2:].strip(), level=1)
                elif line.startswith('- ') or line.startswith('* '):
                    doc.add_paragraph(line[2:].strip(), style='List Bullet')
                elif line.startswith('> '):
                    doc.add_paragraph(line[2:].strip(), style='Intense Quote')
                else:
                    doc.add_paragraph(line)

            doc.save(out)
            return len(doc.paragraphs)

        def build_pptx(src, out):
            from pptx import Presentation
            from pptx.util import Pt

            prs = Presentation()
            blocks = []
            cur = None

            for raw in src.splitlines():
                line = raw.rstrip()
                if line.startswith('# '):
                    cur = [line[2:].strip(), []]
                    blocks.append(cur)
                elif line.startswith('## '):
                    cur = [line[3:].strip(), []]
                    blocks.append(cur)
                elif line.startswith('- ') or line.startswith('* '):
                    if cur is None:
                        cur = ['要点', []]
                        blocks.append(cur)
                    cur[1].append(line[2:].strip())
                elif line.strip():
                    if cur is None:
                        cur = ['要点', []]
                        blocks.append(cur)
                    cur[1].append(line.strip())

            if not blocks:
                blocks = [['（空文档）', []]]

            for title, bullets in blocks:
                slide = prs.slides.add_slide(prs.slide_layouts[1])
                slide.shapes.title.text = title
                if bullets:
                    frame = slide.placeholders[1].text_frame
                    frame.text = bullets[0]
                    for item in bullets[1:]:
                        para = frame.add_paragraph()
                        para.text = item
                        para.level = 0
                        para.font.size = Pt(18)

            prs.save(out)
            return len(blocks)

        if __name__ == '__main__':
            if len(sys.argv) < 4:
                print('usage: office_gen.py docx|pptx <src> <out>')
                sys.exit(2)
            mode, src, out = sys.argv[1], sys.argv[2], sys.argv[3]
            if mode == 'docx':
                n = build_docx(read_text(src), out)
            elif mode == 'pptx':
                n = build_pptx(read_text(src), out)
            else:
                print('unknown mode: ' + mode)
                sys.exit(2)
            print('OK %d %s' % (n, out))
        """;

    private static string? _scriptPath;

    /// <summary>生成 Word 文档（内容用 Markdown 写）。</summary>
    public static Task<string> WriteDocxAsync(string engineRoot, string markdown, string outputPath, CancellationToken ct)
        => RunAsync(engineRoot, "docx", markdown, outputPath, ct);

    /// <summary>生成 PPT（内容用 Markdown 写，# 标题分页、- 列表成条目）。</summary>
    public static Task<string> WritePptxAsync(string engineRoot, string markdown, string outputPath, CancellationToken ct)
        => RunAsync(engineRoot, "pptx", markdown, outputPath, ct);

    private static async Task<string> RunAsync(
        string engineRoot,
        string mode,
        string markdown,
        string outputPath,
        CancellationToken ct)
    {
        var python = Path.Combine(engineRoot, "runtime", "python.exe");

        if (!File.Exists(python))
        {
            return $"内置 Python 运行时不存在：{python}（请先在「评讲课件」里触发一次引擎解压）";
        }

        var script = _scriptPath;

        if (script is null || !File.Exists(script))
        {
            var dir = Path.Combine(Path.GetTempPath(), "cm_office_gen");
            Directory.CreateDirectory(dir);
            script = Path.Combine(dir, "office_gen.py");
            await File.WriteAllTextAsync(script, Script, new UTF8Encoding(false), ct).ConfigureAwait(false);
            _scriptPath = script;
        }

        var source = Path.Combine(Path.GetTempPath(), $"cm_office_src_{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(source, markdown, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = engineRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(mode);
            startInfo.ArgumentList.Add(source);
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "无法启动内置 Python。";
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (detail.Length > 400)
                {
                    detail = detail[..400] + "…";
                }

                return $"生成 {mode} 失败（退出码 {process.ExitCode}）：{detail}";
            }

            var size = new FileInfo(outputPath).Length;
            var name = mode == "docx" ? "Word 文档" : "PPT";
            return $"已生成{name}：{outputPath}（{size:N0} 字节）{stdout.Trim()}";
        }
        catch (Exception ex)
        {
            return $"生成 {mode} 失败：{ex.Message}";
        }
        finally
        {
            try
            {
                File.Delete(source);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
