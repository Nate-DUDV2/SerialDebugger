using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.IO;
using Color = Avalonia.Media.Color;

namespace SerialDebugger
{
    public partial class ByteVisualizerWindow : Window
    {
        private readonly IBrush _startBrush = new SolidColorBrush(Color.FromRgb(57, 255, 20));   // Neon Green START
        private readonly IBrush _evenByteBrush = new SolidColorBrush(Color.FromRgb(255, 59, 48)); // Red (Bytes 0, 2, 4...)
        private readonly IBrush _oddByteBrush = new SolidColorBrush(Color.FromRgb(0, 162, 255));  // Blue (Bytes 1, 3, 5...)
        private readonly IBrush _stopBrush = new SolidColorBrush(Color.FromRgb(224, 64, 251));   // Magenta STOP
        private readonly IBrush _textBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230));

        public ByteVisualizerWindow()
        {
            InitializeComponent();
        }

        public void FeedBytes(string directionLabel, byte[] data)
        {
            if (ChkPause.IsChecked == true || data.Length == 0) return;

            TxtHeader.Text = $"Visualizing: {directionLabel} ({data.Length} {(data.Length == 1 ? "byte" : "bytes")})";
            RenderWaveform(data);
        }

        private void RenderWaveform(byte[] data)
        {
            WaveCanvas.Children.Clear();

            const double topY = 18;
            const double bottomY = 122;
            const double bitWidth = 34;
            const double stopWidth = 44;
            const double strokeThickness = 2.6;

            double x = 24;
            double prevY = topY; // UART idles HIGH

            // Short idle lead-in
            DrawSegment(x - 16, topY, x, topY, _evenByteBrush, strokeThickness);

            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                double byteStartX = x;
                IBrush byteBrush = (i % 2 == 0) ? _evenByteBrush : _oddByteBrush;

                // 1. START BIT (Logic 0 - Low, Green)
                if (Math.Abs(prevY - bottomY) > 0.1)
                {
                    DrawSegment(x, prevY, x, bottomY, _startBrush, strokeThickness);
                }
                DrawSegment(x, bottomY, x + bitWidth, bottomY, _startBrush, strokeThickness);
                DrawLabel("START", x + 1, bottomY + 10, _startBrush, 11, FontWeight.Bold);
                prevY = bottomY;
                x += bitWidth;

                // 2. DATA BITS 0..7 (LSB First, Alternating Red / Blue)
                for (int bit = 0; bit < 8; bit++)
                {
                    int bitVal = (b >> bit) & 1;
                    double targetY = (bitVal == 1) ? topY : bottomY;

                    if (Math.Abs(prevY - targetY) > 0.1)
                    {
                        DrawSegment(x, prevY, x, targetY, byteBrush, strokeThickness);
                    }
                    DrawSegment(x, targetY, x + bitWidth, targetY, byteBrush, strokeThickness);

                    double labelY = (bitVal == 1) ? (topY + 6) : (bottomY - 20);
                    DrawLabel(bit.ToString(), x + (bitWidth * 0.36), labelY, byteBrush, 12, FontWeight.Bold);

                    prevY = targetY;
                    x += bitWidth;
                }

                // 3. STOP BIT (Logic 1 - High, Magenta)
                if (Math.Abs(prevY - topY) > 0.1)
                {
                    DrawSegment(x, prevY, x, topY, _stopBrush, strokeThickness);
                }
                DrawSegment(x, topY, x + stopWidth, topY, _stopBrush, strokeThickness);
                DrawLabel("STOP", x + 6, topY + 6, _stopBrush, 11, FontWeight.Bold);
                prevY = topY;
                x += stopWidth;

                // 4. BYTE METADATA BLOCK UNDERNEATH
                string asciiText = b switch
                {
                    0x0D => "\\r (CR)",
                    0x0A => "\\n (LF)",
                    0x09 => "\\t (TAB)",
                    0x20 => "' ' (SPC)",
                    0x00 => "NUL",
                    >= 33 and <= 126 => ((char)b).ToString(),
                    _ => $"0x{b:X2}"
                };

                string binText = Convert.ToString(b, 2).PadLeft(8, '0');
                string infoBlock =
                    $"Byte: {i}\n" +
                    $"ASCII: {asciiText}\n" +
                    $"HEX: 0x{b:X2}\n" +
                    $"DEC: {b}\n" +
                    $"BIN: {binText}";

                DrawLabel(infoBlock, byteStartX + bitWidth, bottomY + 38, _textBrush, 12.5, FontWeight.Normal);
            }

            WaveCanvas.Width = Math.Max(900, x + 32);
            WaveCanvas.Height = 295;
        }

        private void DrawSegment(double x1, double y1, double x2, double y2, IBrush brush, double thickness)
        {
            WaveCanvas.Children.Add(new Line
            {
                StartPoint = new Point(x1, y1),
                EndPoint = new Point(x2, y2),
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeLineCap = PenLineCap.Square
            });
        }

        private void DrawLabel(string text, double left, double top, IBrush brush, double fontSize, FontWeight weight)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontFamily = new FontFamily("Consolas, Menlo, Monaco, 'DejaVu Sans Mono', monospace"),
                FontSize = fontSize,
                FontWeight = weight
            };
            Canvas.SetLeft(tb, left);
            Canvas.SetTop(tb, top);
            WaveCanvas.Children.Add(tb);
        }

        private void BtnClear_Click(object? sender, RoutedEventArgs e)
        {
            WaveCanvas.Children.Clear();
            TxtHeader.Text = "Visualizing: Cleared (0 bytes)";
        }
    }
}