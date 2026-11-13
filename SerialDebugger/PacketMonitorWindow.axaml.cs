using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Color = Avalonia.Media.Color;

namespace SerialDebugger
{
    public class PacketRow
    {
        public long Id { get; set; }
        public string Time { get; set; } = "";
        public string Direction { get; set; } = "";
        public string AsciiData { get; set; } = "";
        public int Length { get; set; }
        public IBrush DirectionBrush { get; set; } = Brushes.White;
    }

    public partial class PacketMonitorWindow : Window
    {
        private readonly ObservableCollection<PacketRow> _packets = new ObservableCollection<PacketRow>();
        private long _packetId = 0;

        private readonly IBrush _writeBrush = new SolidColorBrush(Color.FromRgb(0, 162, 255));
        private readonly IBrush _readBrush = new SolidColorBrush(Color.FromRgb(74, 246, 38));

        private const int MaxRows = 1000;
        private const int MaxDumpChars = 100000;

        public PacketMonitorWindow()
        {
            InitializeComponent();
            LstPackets.ItemsSource = _packets;
        }

        public void FeedPacket(string direction, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            _packetId++;
            string stamp = DateTime.Now.ToString("HH:mm:ss.fff");

            bool isWrite = direction.Contains("TX", StringComparison.OrdinalIgnoreCase);
            string displayDir = direction switch
            {
                "A_TX" => "PC -> HW",
                "A_RX" => "HW -> PC",
                "B_TX" => "PORT B TX",
                "B_RX" => "PORT B RX",
                _ => direction
            };

            var asciiSb = new StringBuilder(data.Length);
            foreach (byte b in data)
            {
                if (b == 0x0D) asciiSb.Append("\\r");
                else if (b == 0x0A) asciiSb.Append("\\n");
                else if (b >= 32 && b <= 126) asciiSb.Append((char)b);
                else asciiSb.Append('.');
            }

            var row = new PacketRow
            {
                Id = _packetId,
                Time = stamp,
                Direction = displayDir,
                AsciiData = asciiSb.ToString(),
                Length = data.Length,
                DirectionBrush = isWrite ? _writeBrush : _readBrush
            };

            _packets.Add(row);
            if (_packets.Count > MaxRows)
                _packets.RemoveAt(0);

            LstPackets.ScrollIntoView(row);

            string hexLine = $"[{stamp}] {string.Join(" ", data.Select(b => b.ToString("X2")))}\n";
            if (isWrite)
            {
                AppendHexDump(TxtHexWrite, hexLine);
            }
            else
            {
                AppendHexDump(TxtHexRead, hexLine);
            }
        }

        private static void AppendHexDump(TextBox target, string line)
        {
            string updated = (target.Text ?? "") + line;
            if (updated.Length > MaxDumpChars)
                updated = updated.Substring(updated.Length - MaxDumpChars);

            target.Text = updated;
            target.CaretIndex = target.Text.Length;
        }

        private void BtnClear_Click(object? sender, RoutedEventArgs e)
        {
            _packets.Clear();
            _packetId = 0;
            TxtHexWrite.Clear();
            TxtHexRead.Clear();
        }
    }
}