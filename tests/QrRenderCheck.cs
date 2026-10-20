using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VpilotBliss;

internal static class QrRenderCheck
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Range { public int Min, Max; }
    [StructLayout(LayoutKind.Sequential)] private struct FormatRange
    { public IntPtr Hdc, Target; public Rect Area, Page; public Range Text; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref FormatRange range);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr range);

    public static void Run()
    {
        const string link = "https://ntfy.sh/vpilot-816922f4edf54ff89e5d859fcb08471c";
        using (var box = new RichTextBox { Size = new Size(440, 600), BackColor = Color.Black })
        using (var image = new Bitmap(600, 600))
        using (var graphics = Graphics.FromImage(image))
        {
            box.Rtf = PhoneSetup.QrRtf(link);
            graphics.Clear(Color.Black);
            IntPtr hdc = graphics.GetHdc();
            try
            {
                var rect = new Rect { Right = 6600, Bottom = 9000 };
                var format = new FormatRange { Hdc = hdc, Target = hdc, Area = rect, Page = rect, Text = new Range { Min = 0, Max = -1 } };
                SendMessage(box.Handle, 0x439, (IntPtr)1, ref format);
                SendMessage(box.Handle, 0x439, IntPtr.Zero, IntPtr.Zero);
            }
            finally { graphics.ReleaseHdc(hdc); }
            byte[] rgb = new byte[600 * 600 * 3];
            for (int y = 0; y < 600; y++)
                for (int x = 0; x < 600; x++)
                {
                    Color c = image.GetPixel(x, y);
                    int offset = (y * 600 + x) * 3;
                    rgb[offset] = c.R; rgb[offset + 1] = c.G; rgb[offset + 2] = c.B;
                }
            var source = new ZXing.RGBLuminanceSource(rgb, 600, 600, ZXing.RGBLuminanceSource.BitmapFormat.RGB24);
            var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true, Options = new ZXing.Common.DecodingOptions { TryHarder = true } };
            var result = reader.Decode(source);
            image.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qr-render.png"));
            if (result == null || result.Text != link) throw new Exception("Rendered RichTextBox QR cannot be decoded.");
            Console.WriteLine("PASS QR decoded from actual RichTextBox rendered pixels");
        }
    }
}
