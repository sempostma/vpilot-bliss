using System.Text;
using System;
using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace VpilotBliss
{
    internal static class PhoneSetup
    {
        public static string QrRtf(string link, int? cellHeight = null)
        {
            // Measure RichEdit's rounded glyph advances instead of assuming font proportions.
            int rowHeight = cellHeight ?? MeasureRowHeight();
            var rtf = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}{\colortbl;\red0\green0\blue0;\red255\green255\blue255;}\pard\li0\ri0\sa0\sb0\sl-");
            rtf.Append(rowHeight).Append(@"\slmult0\f0\fs9\cf1 ");
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M))
            {
                // Trim two of the default four quiet-zone modules on every side.
                // Keep a visible border; the rendered result is checked with a QR decoder.
                var matrix = data.ModuleMatrix;
                for (int row = 2; row < matrix.Count - 2; row++)
                {
                    for (int col = 2; col < matrix.Count - 2; col++)
                        rtf.Append(matrix[row][col] ? @"\highlight1\~\~" : @"\highlight2\~\~");
                    rtf.Append(@"\par ");
                }
            }
            return rtf.Append('}').ToString();
        }

        private static int MeasureRowHeight()
        {
            using (var font = new Font("Consolas", 4.5f))
            using (var measure = new RichTextBox { Font = font, Text = new string('\u00a0', 20), Width = 1000 })
            using (var graphics = measure.CreateGraphics())
            {
                int width = measure.GetPositionFromCharIndex(20).X - measure.GetPositionFromCharIndex(0).X;
                if (width <= 0) throw new InvalidOperationException("Cannot measure QR cell width.");
                return (int)Math.Round(width / 10.0 * 1440 / graphics.DpiX);
            }
        }
    }
}
