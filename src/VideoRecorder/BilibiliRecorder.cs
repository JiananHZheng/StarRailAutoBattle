using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Cv = OpenCvSharp;

namespace VideoRecorder;

/// <summary>
/// Records part of a Bilibili video by playing it in the recorder's own WebView2 window (not logged in, muted,
/// danmaku off) and capturing that window's picture. Writes recording.avi (Motion-JPEG, constant frame rate,
/// file time 0 = requested start) and recording_info.txt. Stops and explains instead of working around a login
/// requirement, a verification, protected (black) video or a changed page.
/// </summary>
public static class BilibiliRecorder
{
    public const string VideoFileName = "recording.avi";
    public const string InfoFileName = "recording_info.txt";

    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SeekTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PausedTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Average PNG capture step above which JPEG is used (PNG would need very slow playback).</summary>
    private const double SlowPngMs = 60;

    /// <summary>Playback is never slowed below this; at worst a 74-second clip takes about 8 minutes to record.</summary>
    internal const double MinPlaybackRate = 0.15;

    /// <summary>
    /// Playback rate (in steps of 0.05, at most 1) at which one capture step of <paramref name="captureMs"/> still gives
    /// <paramref name="fps"/> captures per second of video. Measured on 1080p: decoding, cropping and writing cost about
    /// a quarter on top of the timed step, so only 65 % of the timed rate is relied on.
    /// </summary>
    internal static double PlaybackRate(double captureMs, double fps)
    {
        var capturesPerSecond = 1000 / Math.Max(1, captureMs);
        var rate = Math.Floor(capturesPerSecond * 0.65 / fps * 20) / 20;
        return Math.Clamp(rate, MinPlaybackRate, 1);
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string ToolVersion =>
        typeof(BilibiliRecorder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Installed WebView2 runtime version, or null when it is missing.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Must be called on a WPF dispatcher (UI) thread; shows the recorder window while it runs.</summary>
    public static async Task<RecordingResult> RecordAsync(RecordingRequest request, IProgress<RecordingProgress>? progress, CancellationToken cancellationToken)
    {
        if (RuntimeVersion() is not { } runtime)
        {
            return Failed(RecordingFailure.RuntimeMissing);
        }

        if (request.Start < 0 || request.End is { } end && end <= request.Start)
        {
            return Failed(RecordingFailure.InvalidRange, "终点必须晚于起点。");
        }

        Directory.CreateDirectory(request.OutputDirectory);
        Directory.CreateDirectory(request.BrowserDataFolder);
        var window = new RecorderWindow();
        var session = new Session(request, window, progress, cancellationToken, runtime);
        try
        {
            return await session.RunAsync();
        }
        catch (OperationCanceledException)
        {
            return Failed(RecordingFailure.Cancelled, window.UserClosed ? "录制窗口被关闭，录制已取消。" : null);
        }
        catch (StopException stop)
        {
            return Failed(stop.Failure, stop.Detail);
        }
        finally
        {
            session.DisposeFrames();
            window.CloseByCode();
        }
    }

    /// <summary>For console use: runs <see cref="RecordAsync"/> on its own STA thread with a dispatcher.</summary>
    public static RecordingResult RecordOnNewThread(RecordingRequest request, IProgress<RecordingProgress>? progress, CancellationToken cancellationToken)
    {
        RecordingResult? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    result = await RecordAsync(request, progress, cancellationToken);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("录制时出错：" + error.Message, error);
        }

        return result!;
    }

    /// <summary>
    /// The picture's rectangle in capture pixels, or null when it is not trustworthy: partly outside the capture,
    /// tiny, or not the video's own aspect ratio (the player is mid-layout or the wrong element was measured).
    /// </summary>
    internal static Cv.Rect? CropRect(StatusInfo s, int shotWidth, int shotHeight)
    {
        var sx = shotWidth / Math.Max(1, s.Iw);
        var sy = shotHeight / Math.Max(1, s.Ih);
        var x = (int)Math.Round(s.X * sx);
        var y = (int)Math.Round(s.Y * sy);
        var w = (int)Math.Round(s.W * sx);
        var h = (int)Math.Round(s.H * sy);
        const int Slack = 3;
        if (w < 64 || h < 36 || x < -Slack || y < -Slack || x + w > shotWidth + Slack || y + h > shotHeight + Slack)
        {
            return null;
        }

        if (s.Vw > 0 && s.Vh > 0 && Math.Abs(w / (double)h / (s.Vw / (double)s.Vh) - 1) > 0.03)
        {
            return null;
        }

        return new Cv.Rect(x, y, w, h) & new Cv.Rect(0, 0, shotWidth, shotHeight);
    }

    private static RecordingResult Failed(RecordingFailure failure, string? detail = null) =>
        RecordingResult.Fail(failure, detail is null ? RecordingMessages.For(failure) : detail + (failure is RecordingFailure.Cancelled or RecordingFailure.InvalidRange ? string.Empty : " " + RecordingMessages.For(failure)));

    private static string LoadScript()
    {
        using var stream = typeof(BilibiliRecorder).Assembly.GetManifestResourceStream("VideoRecorder.bilibili.js")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class StopException(RecordingFailure failure, string? detail = null) : Exception(detail ?? failure.ToString())
    {
        public RecordingFailure Failure { get; } = failure;

        public string? Detail { get; } = detail;
    }

    private sealed record ProbeInfo(bool Video, bool Captcha, bool NotFound, int Rs, double Duration, string? Url);

    private sealed record QualityInfo(string? Chosen, string[]? Options);

    private sealed record AccountInfo(bool Login, bool Vip);

    internal sealed record StatusInfo(
        bool Video, double T, bool Paused, bool Ended, bool Seeking, int Rs, double Duration, int Vw, int Vh,
        double X, double Y, double W, double H, double Iw, double Ih, bool Login, bool Captcha, double Dpr = 1, int Media = 1);

    private sealed record MetaInfo(string? Title, string? Uploader, string? Notice, string? Url);

    private sealed class Session(
        RecordingRequest request, RecorderWindow window, IProgress<RecordingProgress>? progress, CancellationToken token, string runtime)
    {
        private readonly DateTimeOffset _accessTime = DateTimeOffset.Now;
        private CoreWebView2 _core = null!;
        private string? _blockedHost;
        private Cv.Mat? _previous;

        public void DisposeFrames()
        {
            _previous?.Dispose();
            _previous = null;
        }

        public async Task<RecordingResult> RunAsync()
        {
            progress?.Report(new RecordingProgress(RecordingStage.Opening, 0, request.Start, request.End ?? 0));
            var environment = await RecorderBrowser.CreateEnvironmentAsync(request.BrowserDataFolder);
            window.Show();
            window.SetClientPixels(1280, 720);
            await window.View.EnsureCoreWebView2Async(environment);
            _core = window.View.CoreWebView2;
            RecorderBrowser.Harden(_core, host => _blockedHost ??= host);

            var finalUri = await NavigateAsync();
            var videoId = BilibiliLinks.VideoIdOf(finalUri) ?? request.Link.VideoId;
            var probe = await WaitForPlayerAsync();

            progress?.Report(new RecordingProgress(RecordingStage.Preparing, 0, request.Start, request.End ?? probe.Duration));
            await EvalStringAsync("__srab.prepare()");
            var account = await AccountAsync();
            var quality = await SelectQualityAsync(account);
            var status = await WaitForStableQualityAsync();
            var duration = status.Duration > 0 ? status.Duration : probe.Duration;
            if (request.Start >= duration - 0.05)
            {
                throw new StopException(RecordingFailure.InvalidRange, $"起点 {TimeText.Format(request.Start)} 超出了视频长度 {TimeText.Format(duration)}。");
            }

            var end = Math.Min(request.End ?? duration, duration);
            // Resizing can re-layout the page, so fill and fit twice.
            await FillAsync();
            await FitWindowAsync(await StatusAsync());
            var fill = await FillAsync();
            await FitWindowAsync(await StatusAsync());
            var activeQuality = await EvalStringAsync("__srab.activeQuality()");

            progress?.Report(new RecordingProgress(RecordingStage.Seeking, request.Start, request.Start, end));
            await SeekAsync(request.Start);
            var plan = await CalibrateAsync();
            await EvalStringAsync($"__srab.rate({plan.Rate.ToString("0.###", CultureInfo.InvariantCulture)})");
            await EvalStringAsync("__srab.play()");

            var capture = await CaptureAsync(end, plan);

            progress?.Report(new RecordingProgress(RecordingStage.Finishing, end, request.Start, end));
            var meta = await EvalAsync<MetaInfo>("__srab.meta()") ?? new MetaInfo(null, null, null, null);
            var videoPath = Path.Combine(request.OutputDirectory, VideoFileName);
            File.Move(capture.TempPath, videoPath, overwrite: false);
            var (size, hash) = await Task.Run(() => (new FileInfo(videoPath).Length, Sha256(videoPath)), token);

            var info = new StringBuilder();
            info.AppendLine("# 录制说明（由录制工具自动生成）");
            info.AppendLine($"链接：{request.Link.Url.AbsoluteUri}（已去掉分享参数）");
            info.AppendLine($"最终页面：{finalUri.GetLeftPart(UriPartial.Path)}");
            info.AppendLine($"视频编号：{videoId}");
            info.AppendLine($"标题：{Clean(meta.Title)}");
            info.AppendLine($"UP 主：{Clean(meta.Uploader)}");
            info.AppendLine($"页面声明：{(string.IsNullOrWhiteSpace(meta.Notice) ? "（页面上未找到转载声明）" : meta.Notice)}");
            info.AppendLine($"访问时间：{_accessTime:yyyy-MM-dd HH:mm:ss zzz}");
            info.AppendLine("使用范围：仅在本地算法沙箱内研究使用，不转载、不上传");
            info.AppendLine(account.Login
                ? $"登录状态：已登录（用户在启动器里登录录制器自己的浏览器；大会员：{(account.Vip ? "是" : "否")}；不读取 Edge 浏览器的登录信息）"
                : "登录状态：未登录（录制器使用自己的浏览器数据，不读取 Edge 浏览器的登录信息）");
            info.AppendLine("弹幕：已关闭并隐藏；声音：静音");
            info.AppendLine($"视频清晰度：{capture.SourceWidth}×{capture.SourceHeight}（播放器当前选项：{Clean(activeQuality)}；可选项：{(quality.Options is { Length: > 0 } offered ? string.Join(" / ", offered) : "（未读取到清晰度菜单）")}）");
            info.AppendLine($"录制画面：{capture.Width}×{capture.Height}" + (capture.Shrunk ? "（窗口放不下原始清晰度，画面比原始清晰度小）" : string.Empty));
            info.AppendLine($"请求区间：{TimeText.Format(request.Start)}–{(request.End is { } e ? TimeText.Format(e) : "视频结尾")}");
            info.AppendLine($"实际截到：{TimeText.Format(capture.FirstTime)}–{TimeText.Format(capture.LastTime)}；文件时间 0 对应视频 {TimeText.Format(request.Start)}");
            info.AppendLine($"文件帧率：{request.OutputFps.ToString(CultureInfo.InvariantCulture)} 帧/秒，共 {capture.Written} 帧；实际截取 {capture.Captures} 次（每秒视频约 {capture.Captures / Math.Max(0.1, capture.LastTime - capture.FirstTime):0.#} 次），相邻截取最长间隔 {capture.MaxGap:0.00} 秒（视频时间）");
            var formatText = plan.Format == CoreWebView2CapturePreviewImageFormat.Png
                ? "PNG（无损）"
                : FormattableString.Invariant($"JPEG（PNG 每张约 {plan.PngMs:0} ms，太慢，已改用）");
            var slowedText = capture.FinalRate < plan.Rate ? FormattableString.Invariant($"，录制中又放慢到 {capture.FinalRate:0.##}×") : string.Empty;
            info.AppendLine(FormattableString.Invariant(
                $"截图：{formatText}，每张约 {plan.CaptureMs:0} ms；播放速度 {plan.Rate:0.##}×{slowedText}（为每秒视频截满 {request.OutputFps:0} 帧而放慢，文件时间轴仍是视频时间）；录制用时 {capture.WallSeconds:0} 秒"));
            info.AppendLine($"录制窗口：{window.ClientPixels.Width}×{window.ClientPixels.Height} 物理像素（在屏幕外，不显示），{fill}；{capture.Geometry}");
            if (capture.Rejected > 0)
            {
                info.AppendLine($"丢弃的截图：{capture.Rejected} 次（画面位置不正常，例如页面还在调整布局；第一次：{capture.RejectedGeometry}）");
            }

            info.AppendLine($"文件：{VideoFileName}，{size} 字节，sha256={hash}");
            info.AppendLine($"工具：VideoRecorder {ToolVersion}，WebView2 运行时 {runtime}");
            var infoPath = Path.Combine(request.OutputDirectory, InfoFileName);
            await File.WriteAllTextAsync(infoPath, info.ToString(), new UTF8Encoding(false), token);

            return new RecordingResult
            {
                Message = $"已录制 {TimeText.Format(capture.FirstTime)}–{TimeText.Format(capture.LastTime)}，画面 {capture.Width}×{capture.Height}。",
                VideoPath = videoPath,
                InfoPath = infoPath,
                VideoId = videoId,
                Title = Clean(meta.Title),
                Width = capture.Width,
                Height = capture.Height,
                RecordedStart = capture.FirstTime,
                RecordedEnd = capture.LastTime,
                SignedIn = account.Login,
            };
        }

        /// <summary>Whether the recorder's own profile is logged in (and has 大会员), as the page itself reports it.</summary>
        private async Task<AccountInfo> AccountAsync()
        {
            await EvalStringAsync("__srab.accountStart()");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(300, token);
                if (await EvalAsync<AccountInfo>("__srab.accountResult()") is { } account)
                {
                    return account;
                }
            }

            return new AccountInfo(false, false);
        }

        private async Task<Uri> NavigateAsync()
        {
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(LoadScript());
            var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            _core.NavigationCompleted += (_, e) => completed.TrySetResult(e);
            _core.Navigate(request.Link.Url.AbsoluteUri);
            var done = await Task.WhenAny(completed.Task, Task.Delay(NavigationTimeout, token));
            token.ThrowIfCancellationRequested();
            if (done != completed.Task)
            {
                throw new StopException(RecordingFailure.NetworkError, "打开视频页面超时。");
            }

            var result = completed.Task.Result;
            if (_blockedHost is not null)
            {
                throw new StopException(RecordingFailure.NotBilibili, $"页面试图跳转到 {_blockedHost}，已阻止。");
            }

            if (!result.IsSuccess)
            {
                throw result.HttpStatusCode == 404
                    ? new StopException(RecordingFailure.VideoUnavailable)
                    : new StopException(RecordingFailure.NetworkError, $"页面加载失败（{result.WebErrorStatus}）。");
            }

            return new Uri(_core.Source);
        }

        private async Task<ProbeInfo> WaitForPlayerAsync()
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                CheckWindow();
                var probe = await EvalAsync<ProbeInfo>("__srab.probe()");
                if (probe is not null)
                {
                    if (probe.Captcha)
                    {
                        throw new StopException(RecordingFailure.Captcha);
                    }

                    if (probe.NotFound)
                    {
                        throw new StopException(RecordingFailure.VideoUnavailable);
                    }

                    if (probe.Video && probe.Duration > 0 && probe.Rs >= 1)
                    {
                        return probe;
                    }
                }

                if (watch.Elapsed > PlayerTimeout)
                {
                    throw new StopException(RecordingFailure.PlayerNotFound);
                }

                await Task.Delay(500, token);
            }
        }

        /// <summary>Picks the best quality this account may watch once the player has built its quality menu.</summary>
        private async Task<QualityInfo> SelectQualityAsync(AccountInfo account)
        {
            var watch = Stopwatch.StartNew();
            var call = $"__srab.selectQuality({(account.Login ? "true" : "false")}, {(account.Vip ? "true" : "false")})";
            while (true)
            {
                var quality = await EvalAsync<QualityInfo>(call);
                if (quality?.Options is { Length: > 0 } || watch.Elapsed > TimeSpan.FromSeconds(8))
                {
                    return quality ?? new QualityInfo(null, null);
                }

                await Task.Delay(500, token);
            }
        }

        /// <summary>Gets the picture to fill the window; returns how (web fullscreen, pinned) or that it could not.</summary>
        private async Task<string> FillAsync()
        {
            var how = "未能铺满窗口";
            for (var i = 0; i < 6; i++)
            {
                var step = await EvalStringAsync("__srab.fill()");
                if (step == "filled")
                {
                    return how == "未能铺满窗口" ? "已铺满窗口" : how;
                }

                how = step == "pinned" ? "播放器固定到窗口（网页全屏按钮无效）" : "网页全屏";
                await Task.Delay(800, token);
            }

            return "未能铺满窗口";
        }

        /// <summary>Waits for the quality switch to settle: the same picture size in consecutive polls.</summary>
        private async Task<StatusInfo> WaitForStableQualityAsync()
        {
            await Task.Delay(1500, token);
            var watch = Stopwatch.StartNew();
            var last = await StatusAsync();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(700, token);
                var now = await StatusAsync();
                if (now.Vw > 0 && now.Vw == last.Vw && now.Vh == last.Vh)
                {
                    return now;
                }

                last = now;
            }

            return last;
        }

        /// <summary>
        /// Sizes the window so the picture is shown at the video's own resolution: larger would only cost capture time,
        /// smaller would lose detail (then it grows as far as the screen allows).
        /// </summary>
        private async Task FitWindowAsync(StatusInfo status)
        {
            if (status.Vw <= 0 || status.W <= 0)
            {
                return;
            }

            var shownW = ShownWidth(status);
            if (shownW > status.Vw * 1.02)
            {
                // Try once; the page may lay the player out differently in a small window, so check and undo if so.
                var (clientW, clientH) = window.ClientPixels;
                var factor = status.Vw / shownW;
                window.SetClientPixels((int)Math.Ceiling(clientW * factor), (int)Math.Ceiling(clientH * factor));
                await Task.Delay(800, token);
                var shrunk = await StatusAsync();
                if (ShownWidth(shrunk) < status.Vw * 0.98)
                {
                    window.SetClientPixels(clientW, clientH);
                    await Task.Delay(800, token);
                }

                return;
            }

            for (var i = 0; i < 3 && shownW < status.Vw * 0.98; i++)
            {
                var (clientW, clientH) = window.ClientPixels;
                var factor = status.Vw / shownW;
                var (maxW, maxH) = window.MaxClientPixels;
                var targetW = Math.Min((int)Math.Ceiling(clientW * factor), maxW);
                var targetH = Math.Min((int)Math.Ceiling(clientH * factor), maxH);
                if (targetW <= clientW && targetH <= clientH)
                {
                    return;
                }

                window.SetClientPixels(targetW, targetH);
                await Task.Delay(800, token);
                status = await StatusAsync();
                shownW = ShownWidth(status);
            }
        }

        /// <summary>Width in physical pixels at which the page currently shows the picture.</summary>
        private double ShownWidth(StatusInfo status) => status.W * window.ClientPixels.Width / Math.Max(1, status.Iw);

        private async Task SeekAsync(double start)
        {
            await EvalStringAsync("__srab.pause()");
            await EvalStringAsync($"__srab.seek({start.ToString("0.###", CultureInfo.InvariantCulture)})");
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var status = await StatusAsync();
                if (!status.Seeking && status.Rs >= 3 && Math.Abs(status.T - start) < 2)
                {
                    break;
                }

                if (watch.Elapsed > SeekTimeout)
                {
                    throw new StopException(status.Login ? RecordingFailure.LoginRequired : RecordingFailure.Stalled, "跳转到起点后视频一直没有加载出来。");
                }

                await Task.Delay(300, token);
            }
        }

        private sealed record CapturePlan(CoreWebView2CapturePreviewImageFormat Format, double Rate, double PngMs, double CaptureMs);

        /// <summary>
        /// While paused at the start, times captures to pick the format (PNG unless too slow) and the playback rate at
        /// which every second of video still gets the requested number of captures.
        /// </summary>
        private async Task<CapturePlan> CalibrateAsync()
        {
            var pngMs = await TimeCapturesAsync(CoreWebView2CapturePreviewImageFormat.Png);
            var format = pngMs <= SlowPngMs ? CoreWebView2CapturePreviewImageFormat.Png : CoreWebView2CapturePreviewImageFormat.Jpeg;
            var captureMs = format == CoreWebView2CapturePreviewImageFormat.Png ? pngMs : await TimeCapturesAsync(format);
            return new CapturePlan(format, PlaybackRate(captureMs, request.OutputFps), pngMs, captureMs);
        }

        /// <summary>Average time of one loop step (status query + capture), after a warm-up capture.</summary>
        private async Task<double> TimeCapturesAsync(CoreWebView2CapturePreviewImageFormat format)
        {
            using (var warmUp = new MemoryStream())
            {
                await _core.CapturePreviewAsync(format, warmUp);
            }

            var watch = Stopwatch.StartNew();
            const int Count = 4;
            for (var i = 0; i < Count; i++)
            {
                await StatusAsync();
                using var image = new MemoryStream();
                await _core.CapturePreviewAsync(format, image);
            }

            return watch.Elapsed.TotalMilliseconds / Count;
        }

        private sealed record CaptureStats(
            string TempPath, int Width, int Height, int SourceWidth, int SourceHeight, bool Shrunk,
            int Written, int Captures, double MaxGap, double FirstTime, double LastTime,
            int Rejected, string Geometry, string? RejectedGeometry, double FinalRate, double WallSeconds);

        private async Task<CaptureStats> CaptureAsync(double end, CapturePlan plan)
        {
            var start = request.Start;
            var fps = request.OutputFps;
            var format = plan.Format;
            var rate = plan.Rate;
            var interval = TimeSpan.FromSeconds(1 / fps / rate);
            var wall = Stopwatch.StartNew();
            var timeline = new FrameTimeline(start, fps);
            var tempPath = Path.Combine(Path.GetTempPath(), $"srab_rec_{Guid.NewGuid():N}.avi");
            Cv.VideoWriter? writer = null;
            var size = default(Cv.Size);
            var (captures, black, rejected, sourceW, sourceH, shrunk) = (0, 0, 0, 0, 0, false);
            string? firstAccepted = null, firstRejected = null;
            var windowStart = (Captures: 0, Time: start);
            double? first = null, last = null;
            var maxGap = 0.0;
            var lastT = double.NegativeInfinity;
            var advanced = Stopwatch.StartNew();
            var paused = new Stopwatch();
            try
            {
                while (true)
                {
                    CheckWindow();
                    var tick = Stopwatch.StartNew();
                    var status = await StatusAsync();
                    if (status.Captcha)
                    {
                        throw new StopException(RecordingFailure.Captcha);
                    }

                    if (status.Ended || status.T >= end - 0.5 / fps)
                    {
                        break;
                    }

                    if (status.T > lastT + 0.001)
                    {
                        lastT = status.T;
                        advanced.Restart();
                    }
                    else if (advanced.Elapsed > StallTimeout)
                    {
                        throw new StopException(status.Login ? RecordingFailure.LoginRequired : RecordingFailure.Stalled);
                    }

                    if (status.Paused)
                    {
                        // Something on the page paused the video: resume once in a while, give up if it keeps happening.
                        if (!paused.IsRunning)
                        {
                            paused.Restart();
                        }
                        else if (paused.Elapsed > PausedTimeout)
                        {
                            throw new StopException(status.Login ? RecordingFailure.LoginRequired : RecordingFailure.Stalled, "视频被页面暂停，无法继续播放。");
                        }

                        await EvalStringAsync("__srab.play()");
                        await Task.Delay(500, token);
                        continue;
                    }

                    paused.Reset();
                    if (status.Rs < 2 || status.T < start - 0.05 || status.W <= 0)
                    {
                        await Task.Delay(100, token);
                        continue;
                    }

                    using var image = new MemoryStream();
                    await _core.CapturePreviewAsync(format, image);
                    var bytes = image.ToArray();
                    var target = size;
                    var (cropped, isBlack, smaller, geometry) = await Task.Run(() => DecodeAndCrop(bytes, status, target), token);
                    if (cropped is not { } frame)
                    {
                        rejected++;
                        firstRejected ??= geometry;
                        if (rejected >= 30 && rejected > captures * 2)
                        {
                            throw new StopException(RecordingFailure.PlayerNotFound, $"视频画面在录制窗口里的位置一直不正常（{firstRejected}）。");
                        }

                        await Task.Delay(100, token);
                        continue;
                    }

                    firstAccepted ??= geometry;
                    captures++;
                    black += isBlack ? 1 : 0;
                    shrunk |= smaller;
                    (sourceW, sourceH) = (status.Vw, status.Vh);
                    if (captures >= 30 && black > captures * 0.9)
                    {
                        frame.Dispose();
                        throw new StopException(RecordingFailure.BlackScreen);
                    }

                    if (writer is null)
                    {
                        size = frame.Size();
                        writer = new Cv.VideoWriter(tempPath, Cv.VideoCaptureAPIs.OPENCV_MJPEG, Cv.FourCC.MJPG, fps, size);
                        if (!writer.IsOpened())
                        {
                            frame.Dispose();
                            throw new StopException(RecordingFailure.WriteFailed);
                        }

                        writer.Set(Cv.VideoWriterProperties.Quality, request.JpegQuality);
                    }

                    var (fromPrevious, fromCurrent) = timeline.Add(status.T);
                    WriteRepeated(writer, _previous, fromPrevious);
                    WriteRepeated(writer, frame, fromCurrent);
                    _previous?.Dispose();
                    _previous = frame;
                    if (last is { } previousTime)
                    {
                        maxGap = Math.Max(maxGap, status.T - previousTime);
                    }

                    first ??= status.T;
                    last = status.T;
                    progress?.Report(new RecordingProgress(RecordingStage.Recording, status.T, start, end));

                    // Still too few captures per second of video (e.g. the computer got busier): slow down further.
                    if (captures - windowStart.Captures >= 20)
                    {
                        var perCapture = (status.T - windowStart.Time) / (captures - windowStart.Captures);
                        if (perCapture > 1.08 / fps && rate > MinPlaybackRate)
                        {
                            rate = Math.Max(MinPlaybackRate, Math.Round(rate * 0.75, 2));
                            interval = TimeSpan.FromSeconds(1 / fps / rate);
                            await EvalStringAsync($"__srab.rate({rate.ToString("0.###", CultureInfo.InvariantCulture)})");
                        }

                        windowStart = (captures, status.T);
                    }

                    var wait = interval - tick.Elapsed;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, token);
                    }
                }

                if (writer is null || first is null)
                {
                    throw new StopException(RecordingFailure.Stalled, "没有截取到任何画面。");
                }

                if (black > captures / 2)
                {
                    throw new StopException(RecordingFailure.BlackScreen);
                }

                WriteRepeated(writer, _previous, timeline.Finish(end));
                writer.Release();
                writer.Dispose();
                writer = null;
                return new CaptureStats(tempPath, size.Width, size.Height, sourceW, sourceH, shrunk, timeline.Written, captures, maxGap, first.Value, last!.Value,
                    rejected, firstAccepted ?? string.Empty, firstRejected, rate, wall.Elapsed.TotalSeconds);
            }
            catch
            {
                writer?.Dispose();
                writer = null;
                TryDelete(tempPath);
                throw;
            }
        }

        /// <summary>
        /// Crops the video picture out of the window capture. A picture shown larger than the video's own resolution is
        /// scaled back down to it, so frames match the source size; a smaller one (screen too small) is kept as is.
        /// Returns no frame when the player's reported position does not fit the capture (page still laying out).
        /// </summary>
        private static (Cv.Mat? Frame, bool Black, bool Smaller, string Geometry) DecodeAndCrop(byte[] png, StatusInfo s, Cv.Size target)
        {
            using var shot = Cv.Cv2.ImDecode(png, Cv.ImreadModes.Color);
            if (shot.Empty())
            {
                throw new StopException(RecordingFailure.WriteFailed, "截取的画面无法解码。");
            }

            var geometry = Geometry(s, shot.Width, shot.Height);
            if (CropRect(s, shot.Width, shot.Height) is not { } rect)
            {
                return (null, false, false, geometry);
            }

            using var crop = new Cv.Mat(shot, rect);
            var smaller = s.Vw > 0 && rect.Width < s.Vw * 0.98;
            var size = target.Width > 0 ? target
                : !smaller && s.Vw > 0 && s.Vh > 0 ? new Cv.Size(s.Vw, s.Vh)
                : new Cv.Size(rect.Width, rect.Height);
            var frame = new Cv.Mat();
            if (size.Width == rect.Width && size.Height == rect.Height)
            {
                crop.CopyTo(frame);
            }
            else
            {
                Cv.Cv2.Resize(crop, frame, size, 0, 0, Cv.InterpolationFlags.Area);
            }

            using var gray = new Cv.Mat();
            Cv.Cv2.CvtColor(frame, gray, Cv.ColorConversionCodes.BGR2GRAY);
            Cv.Cv2.MeanStdDev(gray, out var mean, out var std);
            return (frame, mean.Val0 < 4 && std.Val0 < 3, smaller, geometry);
        }

        private static string Geometry(StatusInfo s, int shotWidth, int shotHeight) =>
            string.Create(CultureInfo.InvariantCulture,
                $"截图 {shotWidth}×{shotHeight}，页面视口 {s.Iw:0}×{s.Ih:0}（缩放 {s.Dpr:0.##}），视频元素 {s.Media} 个，画面区域 x={s.X:0.#} y={s.Y:0.#} w={s.W:0.#} h={s.H:0.#}，视频 {s.Vw}×{s.Vh}");

        private static void WriteRepeated(Cv.VideoWriter writer, Cv.Mat? frame, int count)
        {
            for (var i = 0; frame is not null && i < count; i++)
            {
                writer.Write(frame);
            }
        }

        private void CheckWindow()
        {
            token.ThrowIfCancellationRequested();
            if (window.UserClosed)
            {
                throw new OperationCanceledException();
            }
        }

        private async Task<StatusInfo> StatusAsync()
        {
            CheckWindow();
            var status = await EvalAsync<StatusInfo>("__srab.status()");
            if (status is null || !status.Video)
            {
                throw new StopException(status?.Captcha == true ? RecordingFailure.Captcha : RecordingFailure.PlayerNotFound);
            }

            return status;
        }

        private async Task<T?> EvalAsync<T>(string script) where T : class
        {
            var inner = await EvalStringAsync(script);
            return inner.Length == 0 ? null : JsonSerializer.Deserialize<T>(inner, Json);
        }

        /// <summary>ExecuteScriptAsync returns JSON; our helpers return strings, so unwrap once. Missing helper → "".</summary>
        private async Task<string> EvalStringAsync(string script)
        {
            var json = await _core.ExecuteScriptAsync($"(window.__srab ? {script} : null)");
            return JsonSerializer.Deserialize<string?>(json) ?? string.Empty;
        }

        private static string Clean(string? text) => string.IsNullOrWhiteSpace(text) ? "（未读取到）" : text.ReplaceLineEndings(" ").Trim();

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
