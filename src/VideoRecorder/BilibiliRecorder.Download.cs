using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AxisRecognizer;

namespace VideoRecorder;

public static partial class BilibiliRecorder
{
    /// <summary>
    /// Bytes per in-page request; each comes back to the recorder as one base64 string. Kept below the ~515 KB after
    /// which one mirror was seen to cut answers (2026-10-29); larger requests gave no speed-up there.
    /// </summary>
    private const int ChunkBytes = 512 * 1024;

    private static readonly TimeSpan StreamsTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    private sealed record StreamList(string? Source, bool Drm, double Duration, string[]? Accept, DashVideo[]? Videos);

    private sealed record FetchInfo(bool Ok, long Size, string? Data, string? Error);

    /// <summary>Null when the file opens and yields a picture; otherwise why not.</summary>
    private static string? CheckDecodes(string path)
    {
        try
        {
            foreach (var (_, frame) in VideoFrames.Sample(path, 1, 0, 0.5))
            {
                using (frame)
                {
                    return null;
                }
            }

            return "没有解出画面";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return ex.Message;
        }
    }

    private sealed partial class Session
    {
        private sealed record DownloadOutcome(RecordingResult? Result, string? Problem);

        private string? _fetchError;

        /// <summary>
        /// Downloads only the fragments of the picture stream that cover the requested span, from inside the page (the
        /// CDN refuses requests from outside a browser). Returns a problem instead of a result when capture should be
        /// tried; DRM-protected streams stop the recording, since capture would only show a black picture.
        /// </summary>
        private async Task<DownloadOutcome> TryDownloadAsync(Uri finalUri, string videoId, AccountInfo account, double playerDuration)
        {
            await EvalStringAsync("__srab.streamsStart()");
            var streams = await PollAsync<StreamList>("__srab.streamsResult()", StreamsTimeout);
            if (streams is null)
            {
                return new DownloadOutcome(null, "没有读取到视频流列表");
            }

            if (streams.Drm)
            {
                throw new StopException(RecordingFailure.Protected);
            }

            var videos = streams.Videos ?? [];
            if (DashStream.Choose(videos) is not { } video)
            {
                return new DownloadOutcome(null, videos.Length == 0 ? "页面没有提供分段视频流" : "没有 1080P、30 帧以内的可用视频流");
            }

            var duration = streams.Duration > 0 ? streams.Duration : playerDuration;
            if (duration > 0 && request.Start >= duration - 0.05)
            {
                throw new StopException(RecordingFailure.InvalidRange, $"起点 {TimeText.Format(request.Start)} 超出了视频长度 {TimeText.Format(duration)}。");
            }

            DashStream.TryParseRange(video.Init, out _, out var initTo);
            DashStream.TryParseRange(video.Index, out _, out var indexTo);
            var head = await FetchAsync(video.Urls, 0, Math.Max(initTo, indexTo));
            if (head is null || head.Length < Math.Max(initTo, indexTo) + 1)
            {
                return new DownloadOutcome(null, $"读取视频流开头失败（{_fetchError}）");
            }

            if (DashStream.ParseSidx(head) is not { Count: > 0 } fragments)
            {
                return new DownloadOutcome(null, "视频流里没有找到分段索引");
            }

            var end = request.End is { } e && (duration <= 0 || e < duration) ? e : (double?)null;
            if (DashStream.Select(fragments, request.Start, end) is not { } selected)
            {
                return new DownloadOutcome(null, "所选时间段不在视频流的分段里");
            }

            var (first, last) = selected;
            var fileStart = fragments[first].Start;
            var fileEnd = duration > 0 ? Math.Min(fragments[last].End, duration) : fragments[last].End;
            var from = fragments[first].Offset;
            var to = fragments[last].Offset + fragments[last].Size - 1;
            var wall = Stopwatch.StartNew();
            var tempPath = Path.Combine(Path.GetTempPath(), $"srab_dl_{Guid.NewGuid():N}.mp4");
            var kept = false;
            try
            {
                // The init segment (ftyp + moov) and the chosen fragments make a playable fragmented MP4; the sidx is
                // left out because its offsets would no longer fit.
                await using (var file = File.Create(tempPath))
                {
                    await file.WriteAsync(head.AsMemory(0, (int)initTo + 1), token);
                    for (var at = from; at <= to;)
                    {
                        CheckWindow();
                        var chunkTo = Math.Min(to, at + ChunkBytes - 1);
                        progress?.Report(new RecordingProgress(RecordingStage.Downloading,
                            fileStart + (fileEnd - fileStart) * (at - from) / (to - from + 1), fileStart, fileEnd));
                        // The page has checked that the answer starts at `at`; a short one is continued from its end.
                        var chunk = await FetchAsync(video.Urls, at, chunkTo);
                        if (chunk is not { Length: > 0 } || chunk.Length > chunkTo - at + 1)
                        {
                            return new DownloadOutcome(null, $"下载中断（{_fetchError ?? "收到的数据长度不对"}）");
                        }

                        await file.WriteAsync(chunk, token);
                        at += chunk.Length;
                    }
                }

                if (await Task.Run(() => CheckDecodes(tempPath), token) is { } decodeProblem)
                {
                    return new DownloadOutcome(null, $"下载的文件无法解码（编码 {video.Codecs}：{decodeProblem}）");
                }

                progress?.Report(new RecordingProgress(RecordingStage.Finishing, fileEnd, fileStart, fileEnd));
                var meta = await EvalAsync<MetaInfo>("__srab.meta()") ?? new MetaInfo(null, null, null, null);
                var videoPath = Path.Combine(request.OutputDirectory, DownloadFileName);
                File.Move(tempPath, videoPath, overwrite: false);
                kept = true;
                var (size, hash) = await Task.Run(() => (new FileInfo(videoPath).Length, Sha256(videoPath)), token);

                var info = InfoHeader(finalUri, videoId, meta, account);
                info.AppendLine("方式：下载（在录制器自己的页面里按字节范围取播放器拿到的视频流；只取画面，不取声音和弹幕；没有解密）");
                info.AppendLine($"视频流来源：{(streams.Source == "playinfo" ? "页面的播放信息" : "页面向 B 站播放地址接口的请求")}");
                info.AppendLine(FormattableString.Invariant(
                    $"视频清晰度：{video.Width}×{video.Height}，{video.Fps:0.###} 帧/秒，编码 {video.Codecs}，清晰度编号 {video.Id}（上限 1080P、30 帧；页面可选项：{(streams.Accept is { Length: > 0 } offered ? string.Join(" / ", offered) : "（未读取到）")}）"));
                info.AppendLine($"请求区间：{TimeText.Format(request.Start)}–{(request.End is { } re ? TimeText.Format(re) : "视频结尾")}");
                info.AppendLine($"实际下载：第 {first + 1}–{last + 1} 段（共 {fragments.Count} 段），视频 {TimeText.Format(fileStart)}–{TimeText.Format(fileEnd)}；文件从起点处或之前最近的关键帧开始，文件时间 0 对应视频 {TimeText.Format(fileStart)}");
                info.AppendLine(FormattableString.Invariant($"字节：开头 0–{initTo}，分段 {from}–{to}；下载用时 {wall.Elapsed.TotalSeconds:0} 秒"));
                info.AppendLine($"文件：{DownloadFileName}，{size} 字节，sha256={hash}");
                info.AppendLine($"工具：VideoRecorder {ToolVersion}，WebView2 运行时 {runtime}");
                var infoPath = Path.Combine(request.OutputDirectory, InfoFileName);
                await File.WriteAllTextAsync(infoPath, info.ToString(), new UTF8Encoding(false), token);

                return new DownloadOutcome(new RecordingResult
                {
                    Message = $"已下载 {TimeText.Format(fileStart)}–{TimeText.Format(fileEnd)}，画面 {video.Width}×{video.Height}。",
                    VideoPath = videoPath,
                    InfoPath = infoPath,
                    VideoId = videoId,
                    Title = Clean(meta.Title),
                    Width = video.Width,
                    Height = video.Height,
                    RecordedStart = fileStart,
                    RecordedEnd = fileEnd,
                    FileStart = fileStart,
                    Downloaded = true,
                    SignedIn = account.Login,
                }, null);
            }
            finally
            {
                if (!kept)
                {
                    TryDelete(tempPath);
                }
            }
        }

        /// <summary>Bytes <paramref name="from"/>–<paramref name="to"/> (inclusive), fetched by the page; null on failure.</summary>
        private async Task<byte[]?> FetchAsync(string[] urls, long from, long to)
        {
            _fetchError = null;
            await EvalStringAsync(string.Create(CultureInfo.InvariantCulture, $"__srab.fetchStart({JsonSerializer.Serialize(urls)}, {from}, {to})"));
            var got = await PollAsync<FetchInfo>("__srab.fetchResult()", FetchTimeout);
            if (got is not { Ok: true, Data: { } data })
            {
                _fetchError = got?.Error is { Length: > 0 } error ? error : got is null ? "超时" : "失败";
                return null;
            }

            return Convert.FromBase64String(data);
        }

        /// <summary>Polls a helper that answers "" until its asynchronous work is done; null after the timeout.</summary>
        private async Task<T?> PollAsync<T>(string script, TimeSpan timeout) where T : class
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                CheckWindow();
                if (await EvalAsync<T>(script) is { } result)
                {
                    return result;
                }

                await Task.Delay(150, token);
            }

            return null;
        }
    }
}
