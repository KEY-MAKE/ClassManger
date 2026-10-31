using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 并发分片下载的正确性验证。
/// <para>
/// 国内直连 GitHub 实测：单连接 14KB/s，8 路 152KB/s，16 路 285KB/s。
/// 分片能提速，但**写错偏移就会拼出损坏的安装包**——那比慢更糟。
/// 这里起一个本地 Range 服务器，下载已知内容，逐字节比对。
/// </para>
/// </summary>
public class SegmentedDownloadTests
{
    /// <summary>支持 Range 的极简 HTTP 服务器（用 TcpListener，不依赖 HttpListener 的 URL ACL）。</summary>
    private sealed class RangeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public RangeServer(byte[] payload)
        {
            _payload = payload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Url => $"http://127.0.0.1:{Port}/file.bin";

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    var header = Encoding.ASCII.GetString(buffer, 0, read);

                    long start = 0;
                    long end = _payload.Length - 1;

                    var rangeIndex = header.IndexOf("Range:", StringComparison.OrdinalIgnoreCase);

                    if (rangeIndex >= 0)
                    {
                        var rangeLine = header.Substring(rangeIndex, Math.Min(60, header.Length - rangeIndex));
                        var parts = rangeLine.Split('=');

                        if (parts.Length == 2)
                        {
                            var bounds = parts[1].Split('-');
                            start = long.Parse(bounds[0]);
                            end = bounds.Length > 1 && bounds[1].Length > 0 ? long.Parse(bounds[1]) : end;
                        }
                    }

                    var length = end - start + 1;

                    var head = new StringBuilder();
                    head.Append("HTTP/1.1 206 Partial Content\r\n");
                    head.Append($"Content-Range: bytes {start}-{end}/{_payload.Length}\r\n");
                    head.Append($"Content-Length: {length}\r\n");
                    head.Append("Accept-Ranges: bytes\r\n");
                    head.Append("Content-Type: application/octet-stream\r\n\r\n");

                    var headBytes = Encoding.ASCII.GetBytes(head.ToString());
                    await stream.WriteAsync(headBytes, _cts.Token);
                    await stream.WriteAsync(_payload.AsMemory((int)start, (int)length), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            }
            catch
            {
                // 忽略
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Segmented_download_produces_byte_identical_file()
    {
        // 造 3MB 随机数据（各段内容不同，能暴露错位问题）
        var payload = new byte[3 * 1024 * 1024];
        new Random(20261031).NextBytes(payload);

        using var server = new RangeServer(payload);

        var service = new UpdateService(
            Path.Combine(Path.GetTempPath(), "cm_seg_settings"),
            Path.Combine(Path.GetTempPath(), "cm_seg_downloads"));

        var info = new UpdateManifest
        {
            Version = "9.9.9",
            Url = server.Url,
            SizeBytes = payload.Length,
        };

        var result = await service.DownloadAsync(info);

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(result.FilePath));

        var downloaded = await File.ReadAllBytesAsync(result.FilePath);

        Assert.Equal(payload.Length, downloaded.Length);

        // 逐字节比对：分片写错偏移这里就会炸
        Assert.True(payload.AsSpan().SequenceEqual(downloaded), "下载内容与源文件不一致（分片错位？）");
    }

    [Fact]
    public async Task Sha256_mismatch_is_rejected()
    {
        var payload = new byte[64 * 1024];
        new Random(7).NextBytes(payload);

        using var server = new RangeServer(payload);

        var service = new UpdateService(
            Path.Combine(Path.GetTempPath(), "cm_seg_settings2"),
            Path.Combine(Path.GetTempPath(), "cm_seg_downloads2"));

        var info = new UpdateManifest
        {
            Version = "9.9.8",
            Url = server.Url,
            SizeBytes = payload.Length,
            Sha256 = new string('a', 64), // 故意写错
        };

        var result = await service.DownloadAsync(info);

        Assert.False(result.Ok);
        Assert.Contains("SHA256", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Falls_back_to_mirror_when_primary_fails()
    {
        var payload = new byte[128 * 1024];
        new Random(9).NextBytes(payload);

        using var server = new RangeServer(payload);

        var service = new UpdateService(
            Path.Combine(Path.GetTempPath(), "cm_seg_settings3"),
            Path.Combine(Path.GetTempPath(), "cm_seg_downloads3"));

        var info = new UpdateManifest
        {
            Version = "9.9.7",
            Url = "http://127.0.0.1:1/does-not-exist.bin", // 必然失败
            SizeBytes = payload.Length,
            Mirrors = new List<string> { server.Url },
        };

        var result = await service.DownloadAsync(info);

        Assert.True(result.Ok, result.Message);

        var downloaded = await File.ReadAllBytesAsync(result.FilePath);
        Assert.True(payload.AsSpan().SequenceEqual(downloaded));
    }

    [Fact]
    public async Task All_addresses_failing_reports_each_one()
    {
        var service = new UpdateService(
            Path.Combine(Path.GetTempPath(), "cm_seg_settings4"),
            Path.Combine(Path.GetTempPath(), "cm_seg_downloads4"));

        var info = new UpdateManifest
        {
            Version = "9.9.6",
            Url = "http://127.0.0.1:1/a.bin",
            Mirrors = new List<string> { "http://127.0.0.1:2/b.bin" },
        };

        var result = await service.DownloadAsync(info);

        Assert.False(result.Ok);
        Assert.Contains("所有下载地址都失败", result.Message, StringComparison.Ordinal);
    }
}
