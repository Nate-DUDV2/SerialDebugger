using System;
using System.Collections.Generic;
using System.IO.Ports;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Color = Avalonia.Media.Color;

namespace SerialDebugger
{
    public partial class ScopeWindow : Window
    {
        private readonly Func<(bool isOpen, bool cts, bool dsr, bool cd, bool rts, bool dtr)> _lineSampler;
        private readonly DispatcherTimer _sampleTimer;

        private readonly List<int> _uartBits = new List<int>();
        private string _lastFrameDesc = "Waiting for UART frame...";

        private const int MaxLineSamples = 120;
        private readonly List<bool> _ctsHistory = new List<bool>();
        private readonly List<bool> _dsrHistory = new List<bool>();
        private readonly List<bool> _cdHistory = new List<bool>();
        private readonly List<bool> _rtsHistory = new List<bool>();
        private readonly List<bool> _dtrHistory = new List<bool>();

        private readonly IBrush _gridBrush = new SolidColorBrush(Color.FromRgb(36, 40, 46));
        private readonly IBrush _uartBrush = new SolidColorBrush(Color.FromRgb(74, 246, 38));
        private readonly IBrush _ctsBrush = new SolidColorBrush(Color.FromRgb(0, 162, 255));
        private readonly IBrush _dsrBrush = new SolidColorBrush(Color.FromRgb(255, 183, 77));
        private readonly IBrush _cdBrush = new SolidColorBrush(Color.FromRgb(239, 83, 80));
        private readonly IBrush _rtsBrush = new SolidColorBrush(Color.FromRgb(171, 71, 188));
        private readonly IBrush _dtrBrush = new SolidColorBrush(Color.FromRgb(38, 198, 218));

        public ScopeWindow() : this(() => (false, false, false, false, false, false))
        {
        }

        public ScopeWindow(Func<(bool isOpen, bool cts, bool dsr, bool cd, bool rts, bool dtr)> lineSampler)
        {
            InitializeComponent();
            _lineSampler = lineSampler;

            for (int i = 0; i < 64; i++) _uartBits.Add(1);

            _sampleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _sampleTimer.Tick += (_, _) => SampleControlLines();
            _sampleTimer.Start();
        }

        public void FeedUartFrame(string source, byte[] data, int baudRate, int dataBits, Parity parity, StopBits stopBits)
        {
            if (ChkPause.IsChecked == true || data.Length == 0) return;

            int startIdx = Math.Max(0, data.Length - 6);
            var bits = new List<int> { 1, 1 };

            for (int idx = startIdx; idx < data.Length; idx++)
            {
                byte b = data[idx];
                bits.Add(0);

                int ones = 0;
                for (int bit = 0; bit < dataBits; bit++)
                {
                    int val = (b >> bit) & 1;
                    bits.Add(val);
                    ones += val;
                }

                if (parity == Parity.Even)
                    bits.Add(ones % 2 == 0 ? 0 : 1);
                else if (parity == Parity.Odd)
                    bits.Add(ones % 2 == 0 ? 1 : 0);

                bits.Add(1);
                if (stopBits == StopBits.Two)
                    bits.Add(1);
            }

            bits.Add(1);
            bits.Add(1);

            _uartBits.Clear();
            _uartBits.AddRange(bits);

            double bitUs = 1_000_000.0 / Math.Max(1, baudRate);
            byte lastByte = data[^1];
            _lastFrameDesc = $"{source} | {baudRate} bps ({bitUs:F1} µs/bit) | Last Byte: 0x{lastByte:X2} ({dataBits}{parity.ToString()[0]}{(stopBits == StopBits.Two ? 2 : 1)})";
            TxtFrameInfo.Text = _lastFrameDesc;

            RedrawScope();
        }

        private void SampleControlLines()
        {
            if (ChkPause.IsChecked == true) return;

            var state = _lineSampler();
            PushSample(_ctsHistory, state.isOpen && state.cts);
            PushSample(_dsrHistory, state.isOpen && state.dsr);
            PushSample(_cdHistory, state.isOpen && state.cd);
            PushSample(_rtsHistory, state.isOpen && state.rts);
            PushSample(_dtrHistory, state.isOpen && state.dtr);

            RedrawScope();
        }

        private static void PushSample(List<bool> list, bool value)
        {
            list.Add(value);
            if (list.Count > MaxLineSamples)
                list.RemoveAt(0);
        }

        private void ScopeCanvas_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            RedrawScope();
        }

        private void RedrawScope()
        {
            double w = ScopeCanvas.Bounds.Width;
            double h = ScopeCanvas.Bounds.Height;
            if (w < 50 || h < 50) return;

            ScopeCanvas.Children.Clear();

            for (int gx = 120; gx < w; gx += 60)
            {
                ScopeCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(gx, 0),
                    EndPoint = new Point(gx, h),
                    Stroke = _gridBrush,
                    StrokeThickness = 1
                });
            }

            double leftMargin = 120;
            double plotWidth = w - leftMargin - 16;
            if (plotWidth <= 20) return;

            DrawChannelLabel("UART TX/RX", 28, _uartBrush);
            DrawDigitalWave(_uartBits, leftMargin, plotWidth, 18, 72, _uartBrush, 2.2);

            double rowStart = 115;
            double rowStep = (h - rowStart - 16) / 5.0;
            double waveHeight = Math.Max(18, rowStep * 0.58);

            DrawControlTrace("CTS (Pin 8)", _ctsHistory, leftMargin, plotWidth, rowStart + rowStep * 0, waveHeight, _ctsBrush);
            DrawControlTrace("DSR (Pin 6)", _dsrHistory, leftMargin, plotWidth, rowStart + rowStep * 1, waveHeight, _dsrBrush);
            DrawControlTrace("CD  (Pin 1)", _cdHistory, leftMargin, plotWidth, rowStart + rowStep * 2, waveHeight, _cdBrush);
            DrawControlTrace("RTS (Pin 7)", _rtsHistory, leftMargin, plotWidth, rowStart + rowStep * 3, waveHeight, _rtsBrush);
            DrawControlTrace("DTR (Pin 4)", _dtrHistory, leftMargin, plotWidth, rowStart + rowStep * 4, waveHeight, _dtrBrush);
        }

        private void DrawChannelLabel(string text, double y, IBrush brush)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 12,
                FontWeight = FontWeight.Bold
            };
            Canvas.SetLeft(tb, 12);
            Canvas.SetTop(tb, y);
            ScopeCanvas.Children.Add(tb);
        }

        private void DrawControlTrace(string label, List<bool> samples, double left, double width, double topY, double height, IBrush brush)
        {
            DrawChannelLabel(label, topY + height * 0.2, brush);
            if (samples.Count < 2) return;

            var intSamples = new List<int>(samples.Count);
            foreach (bool b in samples) intSamples.Add(b ? 1 : 0);

            DrawDigitalWave(intSamples, left, width, topY, topY + height, brush, 1.8);
        }

        private void DrawDigitalWave(List<int> bits, double left, double width, double highY, double lowY, IBrush stroke, double thickness)
        {
            if (bits.Count == 0) return;

            var pts = new Points();
            double stepX = width / bits.Count;

            for (int i = 0; i < bits.Count; i++)
            {
                double x1 = left + i * stepX;
                double x2 = left + (i + 1) * stepX;
                double y = bits[i] == 1 ? highY : lowY;

                pts.Add(new Point(x1, y));
                pts.Add(new Point(x2, y));
            }

            ScopeCanvas.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = stroke,
                StrokeThickness = thickness
            });
        }

        private void BtnClear_Click(object? sender, RoutedEventArgs e)
        {
            _uartBits.Clear();
            for (int i = 0; i < 64; i++) _uartBits.Add(1);
            _ctsHistory.Clear();
            _dsrHistory.Clear();
            _cdHistory.Clear();
            _rtsHistory.Clear();
            _dtrHistory.Clear();
            TxtFrameInfo.Text = "Cleared.";
            RedrawScope();
        }

        private void Window_Closing(object? sender, WindowClosingEventArgs e)
        {
            _sampleTimer.Stop();
        }
    }
}