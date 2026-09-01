using OpenCvSharp;
using OpenCvSharp.ImgHash;
using FileComparer.Tests.TestDoubles;
using Xunit;

namespace FileComparer.Tests.Compatibility;

public sealed class OpenCvNativeCompatibilityTests
{
    [Fact]
    public void PHashComputeLoadsNativeRuntimeAndProducesEightByteHash()
    {
        using var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(image, new Rect(8, 8, 48, 48), Scalar.White, -1);
        using var hash = new Mat();
        using var algorithm = PHash.Create();

        algorithm.Compute(image, hash);

        Assert.False(hash.Empty());
        Assert.Equal(8L, hash.Total() * hash.ElemSize());
        Assert.Equal(0d, algorithm.Compare(hash, hash));
    }

    [Fact]
    public void VideoCaptureReadsMjpegVideoWrittenByNativeRuntime()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("native-round-trip.avi");
        var size = new Size(64, 64);

        using (var writer = new VideoWriter(
                   path,
                   VideoWriter.FourCC('M', 'J', 'P', 'G'),
                   fps: 12,
                   size,
                   isColor: true))
        {
            Assert.True(writer.IsOpened());
            using var frame = new Mat(size.Height, size.Width, MatType.CV_8UC3);
            for (var index = 0; index < 12; index++)
            {
                frame.SetTo(new Scalar(index * 20, 40, 220 - index * 10));
                Cv2.Circle(
                    frame,
                    new Point(8 + index * 4, 32),
                    6,
                    Scalar.White,
                    thickness: -1);
                writer.Write(frame);
            }
        }

        using var capture = new VideoCapture(path);
        using var decoded = new Mat();

        Assert.True(capture.IsOpened());
        Assert.True(capture.Read(decoded));
        Assert.False(decoded.Empty());
        Assert.Equal(size.Width, decoded.Width);
        Assert.Equal(size.Height, decoded.Height);
        Assert.True(capture.Get(VideoCaptureProperties.Fps) > 0);
        Assert.True(capture.Get(VideoCaptureProperties.FrameCount) >= 12);
    }
}
