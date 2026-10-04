using System.Runtime.InteropServices;

namespace Series4.Desktop;

public sealed record VisualAnchorSearchResult(
    int CandidateCount,
    int ScreenX,
    int ScreenY,
    double BestAverageDifference);

public static class WindowsVisualAnchor
{
    private const uint DibRgbColors = 0;
    private const uint Srccopy = 0x00CC0020;
    private const int DefaultSampleSize = 13;
    private const int DefaultSpanPixels = 80;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    public static SemanticVisualAnchor CaptureUniqueAtScreenPoint(IntPtr window, int screenX, int screenY)
    {
        var capture = Capture(window);
        var localX = screenX - capture.Left;
        var localY = screenY - capture.Top;
        var anchor = CreateAnchorFromPixels(capture.Bgra32, capture.Width, capture.Height, localX, localY);
        var search = FindUnique(capture.Bgra32, capture.Width, capture.Height, capture.Left, capture.Top, anchor);
        if (search.CandidateCount != 1
            || Math.Abs(search.ScreenX - screenX) > anchor.ClusterRadius
            || Math.Abs(search.ScreenY - screenY) > anchor.ClusterRadius)
            throw new InvalidOperationException(
                $"시각 대상이 녹화 화면에서 고유하지 않습니다: 후보 {search.CandidateCount}개.");
        return anchor;
    }

    public static VisualAnchorSearchResult FindUnique(IntPtr window, SemanticVisualAnchor anchor)
    {
        var capture = Capture(window);
        return FindUnique(capture.Bgra32, capture.Width, capture.Height, capture.Left, capture.Top, anchor);
    }

    public static SemanticVisualSignature CaptureSignature(IntPtr window, int width = 96, int height = 64)
    {
        if (width is < 16 or > 256 || height is < 16 or > 256)
            throw new ArgumentOutOfRangeException(nameof(width));
        var capture = Capture(window);
        var gray = new byte[width * height];
        for (var targetY = 0; targetY < height; targetY++)
        {
            var sourceTop = targetY * capture.Height / height;
            var sourceBottom = Math.Max(sourceTop + 1, (targetY + 1) * capture.Height / height);
            for (var targetX = 0; targetX < width; targetX++)
            {
                var sourceLeft = targetX * capture.Width / width;
                var sourceRight = Math.Max(sourceLeft + 1, (targetX + 1) * capture.Width / width);
                long total = 0;
                var count = 0;
                for (var sourceY = sourceTop; sourceY < sourceBottom; sourceY++)
                for (var sourceX = sourceLeft; sourceX < sourceRight; sourceX++)
                {
                    total += Gray(capture.Bgra32, capture.Width, sourceX, sourceY);
                    count++;
                }
                gray[targetY * width + targetX] = checked((byte)(total / count));
            }
        }
        return new SemanticVisualSignature(1, width, height, Convert.ToBase64String(gray));
    }

    public static SemanticVisualAnchor CreateAnchorFromPixels(
        byte[] bgra32,
        int width,
        int height,
        int centerX,
        int centerY,
        int sampleSize = DefaultSampleSize,
        int spanPixels = DefaultSpanPixels)
    {
        ValidatePixels(bgra32, width, height);
        if (sampleSize is < 7 or > 25 || spanPixels is < 24 or > 160 || spanPixels % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(sampleSize));
        var radius = spanPixels / 2;
        if (centerX < radius || centerX >= width - radius || centerY < radius || centerY >= height - radius)
            throw new InvalidOperationException("시각 대상 주변의 충분한 영상 픽셀을 확보할 수 없습니다.");
        var samples = Sample(bgra32, width, centerX, centerY, sampleSize, spanPixels);
        if (samples.Max() - samples.Min() < 18)
            throw new InvalidOperationException("시각 대상 패치의 대비가 부족합니다.");
        return new SemanticVisualAnchor(
            1,
            sampleSize,
            spanPixels,
            Convert.ToBase64String(samples),
            MaximumAverageDifference: 10,
            ClusterRadius: 30);
    }

    public static VisualAnchorSearchResult FindUnique(
        byte[] bgra32,
        int width,
        int height,
        int originLeft,
        int originTop,
        SemanticVisualAnchor anchor)
    {
        ValidatePixels(bgra32, width, height);
        if (!anchor.TryDecode(out var expected))
            throw new InvalidOperationException("시각 대상 템플릿이 손상되었습니다.");
        var radius = anchor.SpanPixels / 2;
        if (width <= radius * 2 || height <= radius * 2)
            return new VisualAnchorSearchResult(0, 0, 0, double.PositiveInfinity);
        var accepted = new List<(int X, int Y, double Difference)>();
        var maximumTotal = anchor.MaximumAverageDifference * expected.Length;
        for (var y = radius; y < height - radius; y += 2)
        {
            for (var x = radius; x < width - radius; x += 2)
            {
                var total = Difference(bgra32, width, x, y, anchor.SampleSize, anchor.SpanPixels, expected, maximumTotal);
                if (total <= maximumTotal)
                    accepted.Add((x, y, (double)total / expected.Length));
            }
        }
        if (accepted.Count == 0)
            return new VisualAnchorSearchResult(0, 0, 0, double.PositiveInfinity);
        var clusters = new List<(int X, int Y, double Difference)>();
        foreach (var candidate in accepted.OrderBy(item => item.Difference))
        {
            if (clusters.Any(cluster =>
                Math.Pow(cluster.X - candidate.X, 2) + Math.Pow(cluster.Y - candidate.Y, 2)
                    <= anchor.ClusterRadius * anchor.ClusterRadius))
                continue;
            clusters.Add(candidate);
        }
        var best = clusters[0];
        return new VisualAnchorSearchResult(
            clusters.Count,
            originLeft + best.X,
            originTop + best.Y,
            best.Difference);
    }

    private static int Difference(
        byte[] pixels,
        int width,
        int centerX,
        int centerY,
        int sampleSize,
        int spanPixels,
        byte[] expected,
        int stopAfter)
    {
        var radius = spanPixels / 2;
        var total = 0;
        var index = 0;
        for (var row = 0; row < sampleSize; row++)
        {
            var y = centerY - radius + row * spanPixels / (sampleSize - 1);
            for (var column = 0; column < sampleSize; column++)
            {
                var x = centerX - radius + column * spanPixels / (sampleSize - 1);
                total += Math.Abs(Gray(pixels, width, x, y) - expected[index++]);
                if (total > stopAfter) return total;
            }
        }
        return total;
    }

    private static byte[] Sample(
        byte[] pixels,
        int width,
        int centerX,
        int centerY,
        int sampleSize,
        int spanPixels)
    {
        var result = new byte[sampleSize * sampleSize];
        var radius = spanPixels / 2;
        var index = 0;
        for (var row = 0; row < sampleSize; row++)
        {
            var y = centerY - radius + row * spanPixels / (sampleSize - 1);
            for (var column = 0; column < sampleSize; column++)
            {
                var x = centerX - radius + column * spanPixels / (sampleSize - 1);
                result[index++] = Gray(pixels, width, x, y);
            }
        }
        return result;
    }

    private static byte Gray(byte[] pixels, int width, int x, int y)
    {
        var offset = checked((y * width + x) * 4);
        return checked((byte)((pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8));
    }

    private static void ValidatePixels(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width < 1 || height < 1 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("BGRA 화면 픽셀 크기가 올바르지 않습니다.");
    }

    private static WindowPixels Capture(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rectangle))
            throw new InvalidOperationException("시각 대상 창 경계를 확인할 수 없습니다.");
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width is < 1 or > 10000 || height is < 1 or > 10000)
            throw new InvalidOperationException("시각 대상 창 크기가 올바르지 않습니다.");
        var source = GetWindowDC(window);
        if (source == IntPtr.Zero) throw new InvalidOperationException("시각 대상 창 화면을 읽을 수 없습니다.");
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(source);
            bitmap = CreateCompatibleBitmap(source, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("시각 대상 화면 버퍼를 만들 수 없습니다.");
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, source, 0, 0, Srccopy))
                throw new InvalidOperationException("시각 대상 창 픽셀을 복사할 수 없습니다.");
            var pixels = new byte[checked(width * height * 4)];
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                },
            };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors) == 0)
                throw new InvalidOperationException("시각 대상 창 픽셀을 읽을 수 없습니다.");
            return new WindowPixels(rectangle.Left, rectangle.Top, width, height, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(window, source);
        }
    }

    private sealed record WindowPixels(int Left, int Top, int Width, int Height, byte[] Bgra32);
}
