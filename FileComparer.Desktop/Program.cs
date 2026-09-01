using Avalonia;
using OpenCvSharp;
using OpenCvSharp.ImgHash;

namespace FileComparer.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--native-smoke", StringComparer.Ordinal))
        {
            return RunNativeSmoke();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RunNativeSmoke()
    {
        try
        {
            using var image = new Mat(
                rows: 64,
                cols: 64,
                MatType.CV_8UC1,
                Scalar.Black);
            Cv2.Rectangle(
                image,
                new OpenCvSharp.Rect(8, 8, 48, 48),
                Scalar.White,
                thickness: -1);
            using var hash = new Mat();
            using var algorithm = PHash.Create();
            algorithm.Compute(image, hash);
            return hash.Total() * hash.ElemSize() == 8 ? 0 : 2;
        }
        catch (OpenCVException)
        {
            return 3;
        }
        catch (DllNotFoundException)
        {
            return 4;
        }
        catch (TypeInitializationException)
        {
            return 5;
        }
    }
}
