using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Color = Avalonia.Media.Color;

namespace SerialDebugger
{
    public partial class MainWindow : Window
    {
        private readonly SerialPort _portA;
        private readonly SerialPort _portB;
        private long _portATxCount = 0;
        private long _portARxCount = 0;
        private volatile bool _isBenchTest = true;

        private volatile bool _isRunningDiag = false;
        private readonly object _diagLock = new object();
        private readonly List<byte> _diagRxA = new List<byte>();
        private readonly List<byte> _diagRxB = new List<byte>();
        private readonly List<string> _diagErrorsA = new List<string>();
        private readonly List<string> _diagErrorsB = new List<string>();

        private readonly DispatcherTimer _pinTimer;
        private readonly DispatcherTimer _repeatTimer;
        private string _rxBufferA = "";
        private string _rxBufferB = "";

        private string _lastVisDirection = "";
        private byte[] _lastVisBytes = Array.Empty<byte>();

        // Floating Diagnostic Windows
        private Window? _popOutWindow;
        private TextBox? _popOutTextBox;
        private Window? _pinoutWindow;
        private ByteVisualizerWindow? _byteVisualizerWindow;
        private PacketMonitorWindow? _packetMonitorWindow;
        private ScopeWindow? _scopeWindow;

        private readonly IBrush _ledOnBrush = new SolidColorBrush(Color.FromRgb(74, 246, 38));
        private readonly IBrush _ledOffBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85));

        private const int MaxLogChars = 150000;

        public MainWindow()
        {
            InitializeComponent();

            _portA = new SerialPort();
            _portA.DataReceived += PortA_DataReceived;
            _portA.ErrorReceived += PortA_ErrorReceived;

            _portB = new SerialPort();
            _portB.DataReceived += PortB_DataReceived;
            _portB.ErrorReceived += PortB_ErrorReceived;

            _pinTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _pinTimer.Tick += (_, _) => UpdatePinIndicators();
            _pinTimer.Start();

            _repeatTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _repeatTimer.Tick += async (_, _) => await RepeatTimer_TickAsync();
        }

        private void Window_Loaded(object? sender, RoutedEventArgs e)
        {
            TxtPlatform.Text = $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
            RefreshSerialPorts();
        }

        private void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e)
        {
            RefreshSerialPorts();
            AppendText("[SYSTEM] Serial port list refreshed.\n");
        }

        private void RefreshSerialPorts()
        {
            string? prevA = CmbPortA.SelectedItem?.ToString();
            string? prevB = CmbPortB.SelectedItem?.ToString();

            List<string> ports = GetCrossPlatformPorts();

            CmbPortA.ItemsSource = ports;
            if (prevA != null && ports.Contains(prevA))
                CmbPortA.SelectedItem = prevA;
            else if (ports.Count > 0)
                CmbPortA.SelectedIndex = 0;

            CmbPortB.ItemsSource = ports;
            if (prevB != null && ports.Contains(prevB))
                CmbPortB.SelectedItem = prevB;
            else if (ports.Count > 0)
                CmbPortB.SelectedIndex = 0;
        }

        private List<string> GetCrossPlatformPorts()
        {
            if (OperatingSystem.IsWindows())
                return GetWindowsPortDescriptions();

            if (OperatingSystem.IsLinux())
                return GetLinuxPortDescriptions();

            if (OperatingSystem.IsMacOS())
                return GetMacPortDescriptions();

            return SerialPort.GetPortNames().OrderBy(p => p).ToList();
        }

        [SupportedOSPlatform("windows")]
        private List<string> GetWindowsPortDescriptions()
        {
            var list = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");
                foreach (ManagementBaseObject p in searcher.Get())
                {
                    if (p["Caption"] != null)
                        list.Add(p["Caption"].ToString()!);
                }
            }
            catch { }

            if (list.Count == 0)
                list.AddRange(SerialPort.GetPortNames());

            return list;
        }

        private List<string> GetLinuxPortDescriptions()
        {
            var results = new List<string>();
            var candidates = new List<string>();

            try
            {
                if (Directory.Exists("/dev"))
                {
                    candidates.AddRange(Directory.GetFiles("/dev", "ttyUSB*"));
                    candidates.AddRange(Directory.GetFiles("/dev", "ttyACM*"));
                    candidates.AddRange(Directory.GetFiles("/dev", "ttyS*"));
                }
            }
            catch { }

            if (candidates.Count == 0)
                candidates.AddRange(SerialPort.GetPortNames());

            foreach (string devPath in candidates.Distinct().OrderBy(p => p))
            {
                string shortName = Path.GetFileName(devPath);
                string driverTag = "";
                try
                {
                    string driverLink = $"/sys/class/tty/{shortName}/device/driver";
                    if (Directory.Exists(driverLink))
                    {
                        var target = Directory.ResolveLinkTarget(driverLink, false);
                        if (target != null)
                            driverTag = $" ({target.Name})";
                    }
                }
                catch { }

                if (shortName.StartsWith("ttyS") && string.IsNullOrEmpty(driverTag))
                    continue;

                results.Add($"{devPath}{driverTag}");
            }

            if (results.Count == 0)
                results.AddRange(SerialPort.GetPortNames());

            return results;
        }

        private List<string> GetMacPortDescriptions()
        {
            var results = new List<string>();
            try
            {
                if (Directory.Exists("/dev"))
                {
                    var cuPorts = Directory.GetFiles("/dev", "cu.*")
                        .Where(p => !p.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(p => p)
                        .ToList();

                    results.AddRange(cuPorts);
                }
            }
            catch { }

            if (results.Count == 0)
                results.AddRange(SerialPort.GetPortNames());

            return results;
        }

        private string ExtractDevicePath(string displayItem)
        {
            if (OperatingSystem.IsWindows())
            {
                Match match = Regex.Match(displayItem, @"\((COM\d+)\)");
                return match.Success ? match.Groups[1].Value : displayItem.Trim();
            }

            int spaceIdx = displayItem.IndexOf(' ');
            return spaceIdx > 0 ? displayItem.Substring(0, spaceIdx) : displayItem.Trim();
        }

        private void Mode_Changed(object? sender, RoutedEventArgs e)
        {
            if (CmbPortB == null || BtnConnect == null) return;

            bool isSpyMode = RdoSpyMode.IsChecked == true;
            CmbPortB.IsEnabled = isSpyMode && BtnConnect.IsEnabled;
            BtnConnect.Content = isSpyMode ? "START SNIFFER" : "CONNECT";
        }

        private void ChkBenchTest_Changed(object? sender, RoutedEventArgs e)
        {
            _isBenchTest = ChkBenchTest.IsChecked == true;
        }

        private Parity GetSelectedParity()
        {
            string val = (CmbParity.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
            return val switch
            {
                "Even" => Parity.Even,
                "Odd" => Parity.Odd,
                _ => Parity.None
            };
        }

        private StopBits GetSelectedStopBits()
        {
            string val = (CmbStopBits.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1";
            return val == "2" ? StopBits.Two : StopBits.One;
        }

        private Handshake GetSelectedHandshake()
        {
            string val = (CmbHandshake.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
            return val switch
            {
                "RTS/CTS" => Handshake.RequestToSend,
                "XON/XOFF" => Handshake.XOnXOff,
                "RTS/CTS + XON/XOFF" => Handshake.RequestToSendXOnXOff,
                _ => Handshake.None
            };
        }

        private async void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            if (CmbPortA.SelectedItem == null) return;
            bool isSpyMode = RdoSpyMode.IsChecked == true;

            string portAName = ExtractDevicePath(CmbPortA.SelectedItem.ToString()!);
            string? portBName = (isSpyMode && CmbPortB.SelectedItem != null)
                ? ExtractDevicePath(CmbPortB.SelectedItem.ToString()!)
                : null;

            if (isSpyMode && portAName == portBName)
            {
                await ShowDialogAsync("Same Port Selected", "Hardware Port (A) and Secondary Port (B) must be two different serial ports.");
                return;
            }

            string baudText = (CmbBaudRate.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "115200";
            if (!int.TryParse(baudText, out int baudRate) || baudRate <= 0)
            {
                await ShowDialogAsync("Invalid Baud Rate", "Please select a valid numeric Baud Rate.");
                return;
            }

            try
            {
                int dataBits = int.Parse((CmbDataBits.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "8");
                Parity parity = GetSelectedParity();
                StopBits stopBits = GetSelectedStopBits();
                Handshake handshake = GetSelectedHandshake();
                bool hardwareRts = handshake == Handshake.RequestToSend || handshake == Handshake.RequestToSendXOnXOff;

                _rxBufferA = "";
                _rxBufferB = "";

                _portA.PortName = portAName;
                _portA.BaudRate = baudRate;
                _portA.DataBits = dataBits;
                _portA.Parity = parity;
                _portA.StopBits = stopBits;
                _portA.Handshake = handshake;
                _portA.Open();

                try
                {
                    if (!hardwareRts)
                        _portA.RtsEnable = ChkRTS.IsChecked == true;
                    _portA.DtrEnable = ChkDTR.IsChecked == true;
                }
                catch { }

                AppendText($"[SYSTEM] Opened {_portA.PortName} ({baudRate} {dataBits}{parity.ToString()[0]}{(stopBits == StopBits.Two ? 2 : 1)}, Flow: {handshake}).\n");

                if (isSpyMode && portBName != null)
                {
                    _portB.PortName = portBName;
                    _portB.BaudRate = baudRate;
                    _portB.DataBits = dataBits;
                    _portB.Parity = parity;
                    _portB.StopBits = stopBits;
                    _portB.Handshake = handshake;
                    _portB.Open();
                    AppendText($"[SYSTEM] Opened {_portB.PortName} ({(_isBenchTest ? "Bench Test" : "Proxy")}). ACTIVE.\n\n");
                }
                else
                {
                    AppendText("[SYSTEM] Normal Terminal Mode Active.\n\n");
                }

                SetConnectionControlsState(true, isSpyMode, hardwareRts);
                UpdatePinIndicators();
            }
            catch (Exception ex)
            {
                string hint = OperatingSystem.IsLinux()
                    ? "\n\nLinux Tip: Ensure your user is in the 'dialout' group (sudo usermod -aG dialout $USER) or run with sudo."
                    : "";
                await ShowDialogAsync("Port Open Error", $"Error opening serial port: {ex.Message}{hint}");
                if (_portA.IsOpen) _portA.Close();
                if (_portB.IsOpen) _portB.Close();
                UpdatePinIndicators();
            }
        }

        private void BtnDisconnect_Click(object? sender, RoutedEventArgs e)
        {
            _repeatTimer.Stop();
            ChkRepeat.IsChecked = false;

            if (_portA.IsOpen) _portA.Close();
            if (_portB.IsOpen) _portB.Close();

            SetConnectionControlsState(false, RdoSpyMode.IsChecked == true, false);
            UpdatePinIndicators();

            AppendText("\n[SYSTEM] Disconnected.\n");
        }

        private void SetConnectionControlsState(bool isConnected, bool isSpyMode, bool hardwareRts)
        {
            BtnConnect.IsEnabled = !isConnected;
            BtnRefreshPorts.IsEnabled = !isConnected;
            CmbPortA.IsEnabled = !isConnected;
            CmbPortB.IsEnabled = !isConnected && isSpyMode;
            CmbBaudRate.IsEnabled = !isConnected;
            CmbDataBits.IsEnabled = !isConnected;
            CmbParity.IsEnabled = !isConnected;
            CmbStopBits.IsEnabled = !isConnected;
            CmbHandshake.IsEnabled = !isConnected;
            RdoNormalMode.IsEnabled = !isConnected;
            RdoSpyMode.IsEnabled = !isConnected;

            BtnDisconnect.IsEnabled = isConnected;
            BtnRunDiag.IsEnabled = isConnected;
            BtnBreak.IsEnabled = isConnected;
            BtnSendA.IsEnabled = isConnected;
            BtnSendB.IsEnabled = isConnected && isSpyMode;
            ChkRepeat.IsEnabled = isConnected;
            ChkRTS.IsEnabled = isConnected && !hardwareRts;
            ChkDTR.IsEnabled = isConnected;

            BtnMacro1.IsEnabled = isConnected;
            BtnMacro2.IsEnabled = isConnected;
            BtnMacro3.IsEnabled = isConnected;
            BtnMacro4.IsEnabled = isConnected;
        }

        private void UpdatePinIndicators()
        {
            if (_portA.IsOpen)
            {
                try
                {
                    LedCTS.Fill = _portA.CtsHolding ? _ledOnBrush : _ledOffBrush;
                    LedDSR.Fill = _portA.DsrHolding ? _ledOnBrush : _ledOffBrush;
                    LedCD.Fill = _portA.CDHolding ? _ledOnBrush : _ledOffBrush;
                    return;
                }
                catch { }
            }

            LedCTS.Fill = _ledOffBrush;
            LedDSR.Fill = _ledOffBrush;
            LedCD.Fill = _ledOffBrush;
        }

        private void DispatchToVisualizers(string shortTag, string bannerTag, byte[] buffer)
        {
            _lastVisDirection = bannerTag;
            _lastVisBytes = buffer;

            _byteVisualizerWindow?.FeedBytes(bannerTag, buffer);
            _packetMonitorWindow?.FeedPacket(shortTag, buffer);

            if (_scopeWindow != null)
            {
                int baud = _portA.IsOpen ? _portA.BaudRate : 115200;
                int dataBits = _portA.IsOpen ? _portA.DataBits : 8;
                Parity parity = _portA.IsOpen ? _portA.Parity : Parity.None;
                StopBits stopBits = _portA.IsOpen ? _portA.StopBits : StopBits.One;
                _scopeWindow.FeedUartFrame(shortTag, buffer, baud, dataBits, parity, stopBits);
            }
        }

        private void CheckAutoReply(SerialPort replyPort, ref string rxBuffer, string incomingAscii, string portLabel)
        {
            if (!replyPort.IsOpen || ChkTrap.IsChecked != true) return;

            string trigger = TxtTrigger.Text?.Trim() ?? "";
            string replyText = TxtReply.Text ?? "";
            if (string.IsNullOrEmpty(trigger) || string.IsNullOrEmpty(replyText)) return;

            rxBuffer += incomingAscii;
            if (rxBuffer.Length > 2048)
                rxBuffer = rxBuffer.Substring(rxBuffer.Length - 1024);

            int matchIdx = rxBuffer.IndexOf(trigger, StringComparison.OrdinalIgnoreCase);
            if (matchIdx >= 0)
            {
                rxBuffer = rxBuffer.Substring(matchIdx + trigger.Length);

                string fullReply = replyText + GetSelectedEOL();
                byte[] replyBytes = Encoding.ASCII.GetBytes(fullReply);

                replyPort.Write(replyBytes, 0, replyBytes.Length);

                if (ReferenceEquals(replyPort, _portA))
                {
                    _portATxCount += replyBytes.Length;
                    UpdateCounters();
                    DispatchToVisualizers("A_TX", "[PORT A TX]", replyBytes);
                }
                else
                {
                    DispatchToVisualizers("B_TX", "[PORT B TX]", replyBytes);
                }

                AppendText($"[AUTO-REPLY ({portLabel})]: {replyText}\n");
            }
        }

        private void PortA_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            string err = e.EventType.ToString();
            if (_isRunningDiag)
            {
                lock (_diagLock) { _diagErrorsA.Add(err); }
            }
            Dispatcher.UIThread.Post(() => AppendText($"[UART HARDWARE ERROR - {_portA.PortName}]: {err}\n"));
        }

        private void PortB_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            string err = e.EventType.ToString();
            if (_isRunningDiag)
            {
                lock (_diagLock) { _diagErrorsB.Add(err); }
            }
            Dispatcher.UIThread.Post(() => AppendText($"[UART HARDWARE ERROR - {_portB.PortName}]: {err}\n"));
        }

        private void PortA_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int bytes = _portA.BytesToRead;
            if (bytes <= 0) return;

            byte[] buffer = new byte[bytes];
            _portA.Read(buffer, 0, bytes);

            if (_isRunningDiag)
            {
                lock (_diagLock) { _diagRxA.AddRange(buffer); }
            }
            else if (_portB.IsOpen && !_isBenchTest)
            {
                _portB.Write(buffer, 0, bytes);
            }

            string asciiData = Encoding.ASCII.GetString(buffer);

            Dispatcher.UIThread.Post(() =>
            {
                _portARxCount += bytes;
                UpdateCounters();
                DispatchToVisualizers("A_RX", _isBenchTest ? "[PORT A RX]" : "[HW -> PC]", buffer);

                if (_isRunningDiag) return;

                LogTraffic(_isBenchTest ? "[PORT A RX]: " : "[HW -> PC]: ", buffer);
                CheckAutoReply(_portA, ref _rxBufferA, asciiData, _portA.PortName);
            });
        }

        private void PortB_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int bytes = _portB.BytesToRead;
            if (bytes <= 0) return;

            byte[] buffer = new byte[bytes];
            _portB.Read(buffer, 0, bytes);

            if (_isRunningDiag)
            {
                lock (_diagLock) { _diagRxB.AddRange(buffer); }
            }
            else if (_portA.IsOpen && !_isBenchTest)
            {
                _portA.Write(buffer, 0, bytes);
            }

            string asciiData = Encoding.ASCII.GetString(buffer);

            Dispatcher.UIThread.Post(() =>
            {
                DispatchToVisualizers("B_RX", _isBenchTest ? "[PORT B RX]" : "[PC -> HW]", buffer);

                if (_isRunningDiag) return;

                if (!_isBenchTest)
                {
                    _portATxCount += bytes;
                    UpdateCounters();
                }

                LogTraffic(_isBenchTest ? "[PORT B RX]: " : "[PC -> HW]: ", buffer);

                if (_isBenchTest)
                {
                    CheckAutoReply(_portB, ref _rxBufferB, asciiData, _portB.PortName);
                }
            });
        }

        // --- 1-CLICK AUTOMATED PASS/FAIL DIAGNOSTIC ---
        private async void BtnRunDiag_Click(object? sender, RoutedEventArgs e)
        {
            if (!_portA.IsOpen || _isRunningDiag) return;

            bool dualPort = _portB.IsOpen;
            string portAName = _portA.PortName;
            string portBName = dualPort ? _portB.PortName : "Loopback";
            Handshake hs = _portA.Handshake;
            bool hardwareRts = hs == Handshake.RequestToSend || hs == Handshake.RequestToSendXOnXOff;

            BtnRunDiag.IsEnabled = false;
            BtnBreak.IsEnabled = false;
            BtnSendA.IsEnabled = false;
            BtnSendB.IsEnabled = false;
            BtnDisconnect.IsEnabled = false;

            try
            {
                _isRunningDiag = true;
                _rxBufferA = "";
                _rxBufferB = "";

                if (_portA.IsOpen) _portA.DiscardInBuffer();
                if (dualPort && _portB.IsOpen) _portB.DiscardInBuffer();

                lock (_diagLock)
                {
                    _diagRxA.Clear();
                    _diagRxB.Clear();
                    _diagErrorsA.Clear();
                    _diagErrorsB.Clear();
                }

                var packets = new List<byte[]>();
                for (int i = 1; i <= 16; i++)
                {
                    packets.Add(Encoding.ASCII.GetBytes($"SERIAL_DIAG_{i:D2}\r\n"));
                }
                byte[] bitStress = { 0x55, 0xAA, 0x00, 0xFF, 0x55, 0xAA, 0x0D, 0x0A };
                for (int i = 0; i < 4; i++)
                {
                    packets.Add(bitStress);
                }

                byte[] expectedStream = packets.SelectMany(p => p).ToArray();

                AppendText($"\n[DIAGNOSTIC] Running 20-packet stress & UART error check ({portAName} <-> {portBName})...\n");

                foreach (byte[] pkt in packets)
                {
                    _portA.Write(pkt, 0, pkt.Length);
                    _portATxCount += pkt.Length;
                    UpdateCounters();
                    DispatchToVisualizers("A_TX", "[PORT A TX]", pkt);
                    await Task.Delay(15);
                }
                await Task.Delay(250);

                byte[] receivedPhase1;
                lock (_diagLock)
                {
                    receivedPhase1 = dualPort ? _diagRxB.ToArray() : _diagRxA.ToArray();
                }

                bool phase1Pass = receivedPhase1.SequenceEqual(expectedStream);

                bool phase2Pass = true;
                byte[] receivedPhase2 = Array.Empty<byte>();

                if (dualPort)
                {
                    lock (_diagLock) { _diagRxA.Clear(); }

                    foreach (byte[] pkt in packets)
                    {
                        _portB.Write(pkt, 0, pkt.Length);
                        DispatchToVisualizers("B_TX", "[PORT B TX]", pkt);
                        await Task.Delay(15);
                    }
                    await Task.Delay(250);

                    lock (_diagLock)
                    {
                        receivedPhase2 = _diagRxA.ToArray();
                    }
                    phase2Pass = receivedPhase2.SequenceEqual(expectedStream);
                }

                List<string> errA, errB;
                lock (_diagLock)
                {
                    errA = new List<string>(_diagErrorsA);
                    errB = new List<string>(_diagErrorsB);
                }

                int totalUartErrors = errA.Count + errB.Count;
                bool overallPass = phase1Pass && phase2Pass && totalUartErrors == 0;

                AppendText("==========================================================\n");
                AppendText($"  RESULT: {(overallPass ? "[ PASS ]" : "[ FAIL ]")}  |  {portAName} <-> {portBName} @ {_portA.BaudRate} baud\n");

                if (dualPort)
                {
                    AppendText($"  • {portAName} -> {portBName} : {(phase1Pass ? "PASS" : "FAIL")} ({receivedPhase1.Length} / {expectedStream.Length} bytes verified)\n");
                    AppendText($"  • {portBName} -> {portAName} : {(phase2Pass ? "PASS" : "FAIL")} ({receivedPhase2.Length} / {expectedStream.Length} bytes verified)\n");
                }
                else
                {
                    AppendText($"  • {portAName} Loopback : {(phase1Pass ? "PASS" : "FAIL")} ({receivedPhase1.Length} / {expectedStream.Length} bytes verified)\n");
                }

                string errorDetail = totalUartErrors == 0
                    ? "0 (Framing: 0, Parity: 0, Overrun: 0)"
                    : $"{totalUartErrors} ({string.Join(", ", errA.Concat(errB))})";
                AppendText($"  • UART Errors  : {errorDetail}\n");
                AppendText("==========================================================\n\n");
            }
            catch (Exception ex)
            {
                AppendText($"[DIAGNOSTIC ERROR]: {ex.Message}\n");
            }
            finally
            {
                _isRunningDiag = false;
                if (_portA.IsOpen)
                {
                    SetConnectionControlsState(true, _portB.IsOpen, hardwareRts);
                }
            }
        }

        private async void BtnBreak_Click(object? sender, RoutedEventArgs e)
        {
            if (!_portA.IsOpen) return;
            try
            {
                BtnBreak.IsEnabled = false;
                _portA.BreakState = true;
                AppendText($"[SIGNAL] Sending 250ms RS-232 BREAK on {_portA.PortName}...\n");
                await Task.Delay(250);
                if (_portA.IsOpen) _portA.BreakState = false;
            }
            catch (Exception ex)
            {
                AppendText($"[SIGNAL ERROR]: {ex.Message}\n");
            }
            finally
            {
                if (_portA.IsOpen) BtnBreak.IsEnabled = true;
            }
        }

        // --- FLOATING DIAGNOSTIC WINDOWS ---
        private void BtnByteVis_Click(object? sender, RoutedEventArgs e)
        {
            if (_byteVisualizerWindow != null)
            {
                _byteVisualizerWindow.Activate();
                return;
            }

            _byteVisualizerWindow = new ByteVisualizerWindow();
            _byteVisualizerWindow.Closing += (_, _) => _byteVisualizerWindow = null;
            _byteVisualizerWindow.Show();

            if (_lastVisBytes.Length > 0)
            {
                _byteVisualizerWindow.FeedBytes(_lastVisDirection, _lastVisBytes);
            }
        }

        private void BtnPacketMon_Click(object? sender, RoutedEventArgs e)
        {
            if (_packetMonitorWindow != null)
            {
                _packetMonitorWindow.Activate();
                return;
            }

            _packetMonitorWindow = new PacketMonitorWindow();
            _packetMonitorWindow.Closing += (_, _) => _packetMonitorWindow = null;
            _packetMonitorWindow.Show();
        }

        private void BtnScope_Click(object? sender, RoutedEventArgs e)
        {
            if (_scopeWindow != null)
            {
                _scopeWindow.Activate();
                return;
            }

            _scopeWindow = new ScopeWindow(() =>
            {
                if (!_portA.IsOpen) return (false, false, false, false, false, false);
                try
                {
                    return (true, _portA.CtsHolding, _portA.DsrHolding, _portA.CDHolding, _portA.RtsEnable, _portA.DtrEnable);
                }
                catch
                {
                    return (false, false, false, false, false, false);
                }
            });

            _scopeWindow.Closing += (_, _) => _scopeWindow = null;
            _scopeWindow.Show();
        }

        private void BtnPopOut_Click(object? sender, RoutedEventArgs e)
        {
            if (_popOutWindow != null)
            {
                _popOutWindow.Activate();
                return;
            }

            _popOutTextBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = new SolidColorBrush(Color.FromRgb(74, 246, 38)),
                FontFamily = new FontFamily("Consolas, Menlo, Monaco, 'DejaVu Sans Mono', monospace"),
                Text = TxtOutput.Text ?? ""
            };

            _popOutWindow = new Window
            {
                Title = "SerialDebugger — Floating Traffic Monitor",
                Width = 850,
                Height = 520,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Content = _popOutTextBox
            };

            _popOutWindow.Closing += (_, _) =>
            {
                _popOutWindow = null;
                _popOutTextBox = null;
            };

            _popOutWindow.Show();
        }

        private void BtnPinout_Click(object? sender, RoutedEventArgs e)
        {
            if (_pinoutWindow != null)
            {
                _pinoutWindow.Activate();
                return;
            }

            string guideText =
@"DB9 SERIAL WIRING & BENCH TEST CHEAT-SHEET
============================================================

1. RS-232 NULL-MODEM / CROSSOVER (Port A <-> Port B)
   • Pin 2 (RxD)  <---->  Pin 3 (TxD)
   • Pin 3 (TxD)  <---->  Pin 2 (RxD)
   • Pin 5 (GND)  <---->  Pin 5 (GND)
   Optional Hardware Flow Control:
   • Pin 7 (RTS)  <---->  Pin 8 (CTS)
   • Pin 8 (CTS)  <---->  Pin 7 (RTS)

2. RS-232 SINGLE-PORT LOOPBACK PLUG (Port A Self-Test)
   • Jump Pin 2 (RxD) <--> Pin 3 (TxD)
   • Jump Pin 7 (RTS) <--> Pin 8 (CTS)
   • Jump Pin 1 (DCD) <--> Pin 4 (DTR) <--> Pin 6 (DSR)

3. NEOUSYS MEZIO-C180 / C181 — RS-422 FULL-DUPLEX CROSSOVER
   • Pin 2 (TxD+) <---->  Pin 3 (RxD+)
   • Pin 3 (RxD+) <---->  Pin 2 (TxD+)
   • Pin 8 (TxD-) <---->  Pin 4 (RxD-)
   • Pin 4 (RxD-) <---->  Pin 8 (TxD-)
   • Pin 5 (GND)  <---->  Pin 5 (GND)

4. NEOUSYS MEZIO-C180 / C181 — RS-485 HALF-DUPLEX (2-WIRE)
   • Pin 2 (Data+) <----> Pin 2 (Data+)
   • Pin 8 (Data-) <----> Pin 8 (Data-)
   • Pin 5 (GND)   <----> Pin 5 (GND)";

            _pinoutWindow = new Window
            {
                Title = "SerialDebugger — DB9 Wiring & Pinout Guide",
                Width = 600,
                Height = 520,
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                Content = new TextBox
                {
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Avalonia.Thickness(12),
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
                    FontFamily = new FontFamily("Consolas, Menlo, Monaco, 'DejaVu Sans Mono', monospace"),
                    Text = guideText
                }
            };

            _pinoutWindow.Closing += (_, _) => _pinoutWindow = null;
            _pinoutWindow.Show();
        }

        // --- INJECTION, CYCLIC REPEAT & MACRO COMMANDS ---
        private async Task<byte[]?> TryBuildPayloadAsync(string? rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return null;

            try
            {
                byte[] bodyBytes;
                if (ChkInjectHex.IsChecked == true)
                {
                    string cleanHex = rawInput.Replace(" ", "").Replace("-", "").Replace("0x", "").Replace("0X", "");
                    if (cleanHex.Length % 2 != 0)
                    {
                        _repeatTimer.Stop();
                        ChkRepeat.IsChecked = false;
                        await ShowDialogAsync("Invalid HEX", "HEX input must have an even number of characters (e.g., 02 10 FF).");
                        return null;
                    }
                    bodyBytes = Enumerable.Range(0, cleanHex.Length / 2)
                                          .Select(x => Convert.ToByte(cleanHex.Substring(x * 2, 2), 16))
                                          .ToArray();
                }
                else
                {
                    bodyBytes = Encoding.ASCII.GetBytes(rawInput);
                }

                byte[] eolBytes = Encoding.ASCII.GetBytes(GetSelectedEOL());
                byte[] payload = new byte[bodyBytes.Length + eolBytes.Length];
                Buffer.BlockCopy(bodyBytes, 0, payload, 0, bodyBytes.Length);
                Buffer.BlockCopy(eolBytes, 0, payload, bodyBytes.Length, eolBytes.Length);
                return payload;
            }
            catch
            {
                _repeatTimer.Stop();
                ChkRepeat.IsChecked = false;
                await ShowDialogAsync("Invalid HEX", "Invalid HEX characters detected. Use 0-9 and A-F only.");
                return null;
            }
        }

        private async Task SendToPortAAsync(string? rawCommand, string sourceTag)
        {
            if (!_portA.IsOpen) return;
            byte[]? buffer = await TryBuildPayloadAsync(rawCommand);
            if (buffer == null) return;

            _portA.Write(buffer, 0, buffer.Length);
            _portATxCount += buffer.Length;
            UpdateCounters();
            DispatchToVisualizers("A_TX", "[PORT A TX]", buffer);

            if (ChkLocalEcho.IsChecked == true)
            {
                LogTraffic($"[ECHO -> {sourceTag}]: ", buffer);
            }
            else
            {
                LogTraffic($"[{sourceTag} -> PORT A]: ", buffer);
            }
        }

        private async void ChkRepeat_Changed(object? sender, RoutedEventArgs e)
        {
            if (ChkRepeat.IsChecked == true)
            {
                if (!_portA.IsOpen || string.IsNullOrWhiteSpace(TxtInput.Text))
                {
                    ChkRepeat.IsChecked = false;
                    return;
                }

                if (!int.TryParse(TxtRepeatMs.Text, out int intervalMs) || intervalMs < 20)
                {
                    intervalMs = 500;
                    TxtRepeatMs.Text = "500";
                }

                _repeatTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
                _repeatTimer.Start();
                await SendToPortAAsync(TxtInput.Text, "REPEAT_A");
            }
            else
            {
                _repeatTimer.Stop();
            }
        }

        private async Task RepeatTimer_TickAsync()
        {
            if (!_portA.IsOpen || string.IsNullOrWhiteSpace(TxtInput.Text) || _isRunningDiag)
            {
                _repeatTimer.Stop();
                ChkRepeat.IsChecked = false;
                return;
            }

            await SendToPortAAsync(TxtInput.Text, "REPEAT_A");
        }

        private async void BtnSendA_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtInput.Text)) return;
            await SendToPortAAsync(TxtInput.Text, "INJECT_A");
            if (ChkRepeat.IsChecked != true)
                TxtInput.Clear();
        }

        private async void BtnSendB_Click(object? sender, RoutedEventArgs e)
        {
            if (!_portB.IsOpen || string.IsNullOrWhiteSpace(TxtInput.Text)) return;
            byte[]? buffer = await TryBuildPayloadAsync(TxtInput.Text);
            if (buffer == null) return;

            _portB.Write(buffer, 0, buffer.Length);
            DispatchToVisualizers("B_TX", "[PORT B TX]", buffer);
            LogTraffic("[INJECT_B -> PORT B]: ", buffer);
            if (ChkRepeat.IsChecked != true)
                TxtInput.Clear();
        }

        private void TxtInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnSendA.IsEnabled)
            {
                BtnSendA_Click(sender, e);
            }
        }

        private async void BtnMacro_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                string? macroText = btn.Tag.ToString() switch
                {
                    "1" => TxtMacro1.Text,
                    "2" => TxtMacro2.Text,
                    "3" => TxtMacro3.Text,
                    "4" => TxtMacro4.Text,
                    _ => ""
                };
                await SendToPortAAsync(macroText, $"MACRO_{btn.Tag}");
            }
        }

        // --- UTILITIES ---
        private string GetSelectedEOL()
        {
            string selection = (CmbEOL.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CRLF";
            return selection switch
            {
                "CRLF" => "\r\n",
                "CR" => "\r",
                "LF" => "\n",
                _ => ""
            };
        }

        private void LogTraffic(string directionPrefix, byte[] buffer)
        {
            string formattedData = ChkHexView.IsChecked == true
                ? BitConverter.ToString(buffer).Replace("-", " ") + " "
                : Encoding.ASCII.GetString(buffer);

            AppendText($"{directionPrefix}{formattedData}\n");
        }

        private void AppendText(string text)
        {
            string stamp = (ChkTimestamp != null && ChkTimestamp.IsChecked == true)
                ? $"[{DateTime.Now:HH:mm:ss.fff}] "
                : "";

            string entry = $"{stamp}{text}";
            string updated = (TxtOutput.Text ?? "") + entry;

            if (updated.Length > MaxLogChars)
                updated = updated.Substring(updated.Length - MaxLogChars);

            TxtOutput.Text = updated;

            if (ChkAutoScroll != null && ChkAutoScroll.IsChecked == true)
            {
                TxtOutput.CaretIndex = TxtOutput.Text.Length;
            }

            if (_popOutTextBox != null)
            {
                _popOutTextBox.Text = updated;
                if (ChkAutoScroll != null && ChkAutoScroll.IsChecked == true)
                {
                    _popOutTextBox.CaretIndex = _popOutTextBox.Text.Length;
                }
            }
        }

        private void UpdateCounters()
        {
            TxtTxBytes.Text = $"Port A Transmitted: {_portATxCount} bytes";
            TxtRxBytes.Text = $"Port A Received: {_portARxCount} bytes";
        }

        private void BtnResetCounters_Click(object? sender, RoutedEventArgs e)
        {
            _portATxCount = 0;
            _portARxCount = 0;
            UpdateCounters();
        }

        private void BtnClear_Click(object? sender, RoutedEventArgs e)
        {
            TxtOutput.Clear();
            if (_popOutTextBox != null) _popOutTextBox.Clear();
            _portATxCount = 0;
            _portARxCount = 0;
            _rxBufferA = "";
            _rxBufferB = "";
            UpdateCounters();
        }

        private async void BtnSaveLog_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Serial Traffic Log",
                SuggestedFileName = $"SerialDebuggerLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Text File") { Patterns = new[] { "*.txt" } }
                }
            });

            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(TxtOutput.Text ?? "");
                await ShowDialogAsync("Saved", "Traffic log saved successfully.");
            }
        }

        private void ChkLineControl_Changed(object? sender, RoutedEventArgs e)
        {
            if (_portA.IsOpen)
            {
                try
                {
                    if (ChkRTS.IsEnabled)
                        _portA.RtsEnable = ChkRTS.IsChecked == true;
                    _portA.DtrEnable = ChkDTR.IsChecked == true;
                }
                catch { }
            }
        }

        private async Task ShowDialogAsync(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 430,
                Height = 175,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CanResize = false
            };

            var okBtn = new Button
            {
                Content = "OK",
                Width = 85,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            okBtn.Click += (_, _) => dialog.Close();

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White
                    },
                    okBtn
                }
            };

            await dialog.ShowDialog(this);
        }

        private void Window_Closing(object? sender, WindowClosingEventArgs e)
        {
            _pinTimer.Stop();
            _repeatTimer.Stop();
            _popOutWindow?.Close();
            _pinoutWindow?.Close();
            _byteVisualizerWindow?.Close();
            _packetMonitorWindow?.Close();
            _scopeWindow?.Close();
            if (_portA.IsOpen) _portA.Close();
            if (_portB.IsOpen) _portB.Close();
        }
    }
}