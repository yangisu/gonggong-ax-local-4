using System.Security.Cryptography;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Series4.Desktop;

public static class VideoFrameEvidenceExtractor
{
    public static async Task<IReadOnlyDictionary<string, string>> ExtractAsync(
        string videoPath,
        IReadOnlyList<SemanticDemonstrationFrame> frames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(frames);
        if (!File.Exists(videoPath)) throw new FileNotFoundException("원본 녹화 영상을 찾지 못했습니다.", videoPath);
        if (frames.Count == 0) return new Dictionary<string, string>();
        if (Application.Current?.Dispatcher.CheckAccess() != true)
            throw new InvalidOperationException("MP4 프레임 증거 추출은 WPF Dispatcher 스레드에서 실행해야 합니다.");

        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, error) => opened.TrySetException(
            new InvalidOperationException($"MP4 프레임을 열지 못했습니다: {error.ErrorException?.Message}", error.ErrorException));
        player.Open(new Uri(Path.GetFullPath(videoPath), UriKind.Absolute));
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (player.NaturalVideoWidth < 1 || player.NaturalVideoHeight < 1)
                throw new InvalidOperationException("MP4 영상 크기를 확인할 수 없습니다.");
            var scale = Math.Min(1.0, 640.0 / player.NaturalVideoWidth);
            var width = Math.Max(1, (int)Math.Round(player.NaturalVideoWidth * scale));
            var height = Math.Max(1, (int)Math.Round(player.NaturalVideoHeight * scale));
            var duration = player.NaturalDuration.HasTimeSpan
                ? player.NaturalDuration.TimeSpan.TotalSeconds
                : frames.Max(frame => frame.OffsetSeconds) + .25;
            var hashesByOffset = new Dictionary<double, string>();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var frame in frames.OrderBy(item => item.OffsetSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = Math.Clamp(frame.OffsetSeconds, 0, Math.Max(0, duration - .04));
                var key = Math.Round(requested, 3, MidpointRounding.AwayFromZero);
                if (!hashesByOffset.TryGetValue(key, out var hash))
                {
                    player.Position = TimeSpan.FromSeconds(requested);
                    player.Play();
                    await Task.Delay(180, cancellationToken);
                    player.Pause();
                    hash = RenderHash(player, width, height);
                    hashesByOffset[key] = hash;
                }
                result[frame.Id] = hash;
            }
            return result;
        }
        finally
        {
            player.Close();
        }
    }

    private static string RenderHash(MediaPlayer player, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawVideo(player, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        var first = pixels[0];
        if (pixels.All(value => value == first))
            throw new InvalidOperationException("MP4 시점에서 검증 가능한 영상 픽셀을 추출하지 못했습니다.");
        return Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
    }
}
