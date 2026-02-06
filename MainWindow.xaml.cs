using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ADC_Rec
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private Services.SerialService _serialService;
        private Services.Parser _parser;
        private Managers.PlotManager _plotManager;
        private volatile bool _running = false;

        // Incoming packet queue and timer for batched UI updates
        private readonly System.Collections.Concurrent.ConcurrentQueue<Models.Packet> _packetQueue = new System.Collections.Concurrent.ConcurrentQueue<Models.Packet>();
        private volatile int _pendingPacketCount = 0;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private System.Threading.Timer? _uiTimer = null;
        private System.Threading.Timer? _counterTimer = null; // updates bytes-per-channel UI every 200ms
        private double _cpuFreq = 240000000.0;
        private double _rampSlope = 1.0;
        private int _displayWindowSamples = 48000; // default 1s
        private int _sampleRate = 48000; // default sample rate
        private int _plotBits = 16; // default plot bits (user-selectable)
        private bool _fitToData = true; // if true, autoscale to observed samples instead of full bit range
        private const int MaxPacketBatchPerTick = 64;
        private const int MaxPacketQueue = 4096; // if exceeded, drop oldest packets to keep memory bounded
        private int _lastLogFlushTick = Environment.TickCount;
        private const int LogFlushMs = 500;

        // Background drain and recording state
        private System.Threading.CancellationTokenSource? _drainCts = null;
        private System.Threading.Tasks.Task? _drainTask = null;
        private readonly object _recordLock = new object();
        private System.IO.BinaryWriter? _recordWriter = null;
        private bool _recording = false;
        private bool _replaying = false;
        private long _droppedPacketCount = 0;
        private long _dropLogAccumulator = 0;
        private int _lastDropLogTick = Environment.TickCount;
        private long _processedPacketCount = 0;
        private long _drainLoopIterations = 0;
        private int _lastDrainBatchCount = 0;
        private int _lastDrainTick = 0;
        private bool _drainStartedLogged = false;
        private bool _drainIdleLogged = false;
        private bool _reverseBytes = false; // when true, reverse 3-byte order when interpreting samples
        private const int DrainBatchSize = 512;
        private const int DrainIdleMs = 5;

        // Reusable display buffers to avoid allocations
        private float[][] _displayBuffers = new float[Models.Packet.NumChannels][];
        private uint[][] _displayRawBuffers = new uint[Models.Packet.NumChannels][]; // raw 24-bit samples for hover
        private long[] _bytesPerChannel = new long[Models.Packet.NumChannels]; // parsed bytes per channel (cumulative)

        public MainWindow()
        {
            InitializeComponent();

            _serialService = new Services.SerialService();
            _serialService.DataReceived += SerialService_DataReceived;
            _serialService.LogMessage += (s) => AddLog(s);

            _parser = new Services.Parser();
            _parser.PacketParsed += Parser_PacketParsed;
            _parser.DebugLine += (s) => { if (_parser.Verbose) AddLog("[DBG] " + s); };

            _plotManager = new Managers.PlotManager(48000); // 1 s history default

            // initialize UI-driven conversion parameters
            double.TryParse(CpuFreqTextBox.Text, out _cpuFreq);
            double.TryParse(RampSlopeTextBox.Text, out _rampSlope);
            CpuFreqTextBox.TextChanged += (s, e) => { double.TryParse(CpuFreqTextBox.Text, out _cpuFreq); };
            RampSlopeTextBox.TextChanged += (s, e) => { double.TryParse(RampSlopeTextBox.Text, out _rampSlope); };

            // prepare display buffers
            for (int ch = 0; ch < Models.Packet.NumChannels; ch++) _displayBuffers[ch] = new float[_displayWindowSamples];
            // prepare raw display buffers used by hover tooltips
            for (int ch = 0; ch < Models.Packet.NumChannels; ch++) _displayRawBuffers[ch] = new uint[_displayWindowSamples];
            // initialize counters and counter timer (stopped until capture starts)
            for (int ch = 0; ch < Models.Packet.NumChannels; ch++) _bytesPerChannel[ch] = 0;
            _counterTimer = new System.Threading.Timer(_ => UpdateBytesUi(), null, Timeout.Infinite, 200);
            // Plot bits NumberBox initialized below (spinner control)
            if (PlotBitsNumberBox != null) PlotBitsNumberBox.Value = _plotBits;

            // Fit-to-data checkbox hookup (if exists)
            if (FitToDataCheck != null)
            {
                FitToDataCheck.IsChecked = _fitToData;
                FitToDataCheck.Checked += (s, e) => { _fitToData = true; _logQueue.Enqueue("Fit to data: ON"); };
                FitToDataCheck.Unchecked += (s, e) => { _fitToData = false; _logQueue.Enqueue("Fit to data: OFF"); };
            }

            // Verbose checkbox hookup (if exists)
            if (VerboseCheckbox != null)
            {
                VerboseCheckbox.IsChecked = false;
                VerboseCheckbox.Checked += (s, e) => { _parser.Verbose = true; _logQueue.Enqueue("Verbose ON"); };
                VerboseCheckbox.Unchecked += (s, e) => { _parser.Verbose = false; _logQueue.Enqueue("Verbose OFF"); };
            }

            // PlotBits numberbox and ReverseBytes checkbox hookup (if exists)
            if (PlotBitsNumberBox != null)
            {
                PlotBitsNumberBox.Value = _plotBits;
                // ValueChanged is wired in XAML; additional hookup not required here.
            }
            if (ReverseBytesCheck != null)
            {
                ReverseBytesCheck.IsChecked = _reverseBytes;
                ReverseBytesCheck.Checked += (s, e) => { _reverseBytes = true; _logQueue.Enqueue("Reverse bytes: ON"); ForceRedraw(); };
                ReverseBytesCheck.Unchecked += (s, e) => { _reverseBytes = false; _logQueue.Enqueue("Reverse bytes: OFF"); ForceRedraw(); };
            }

            // Show hover/last-value labels all the time (initialize)
            try
            {
                if (HoverText0 != null) { HoverText0.Visibility = Microsoft.UI.Xaml.Visibility.Visible; HoverText0.Text = "<no data>"; }
                if (HoverText1 != null) { HoverText1.Visibility = Microsoft.UI.Xaml.Visibility.Visible; HoverText1.Text = "<no data>"; }
                if (HoverText2 != null) { HoverText2.Visibility = Microsoft.UI.Xaml.Visibility.Visible; HoverText2.Text = "<no data>"; }
                if (HoverText3 != null) { HoverText3.Visibility = Microsoft.UI.Xaml.Visibility.Visible; HoverText3.Text = "<no data>"; }
            }
            catch { }

            this.Activated += MainWindow_Activated;
            this.Closed += MainWindow_Closed;
        }

        private void ForceRedraw()
        {
            _ = DispatcherQueue.TryEnqueue(() => {
                for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                {
                    int n = _plotManager.FillChannelSnapshot(ch, _displayBuffers[ch], _displayWindowSamples);
                    var canvas = ch == 0 ? WaveCanvas0 : ch == 1 ? WaveCanvas1 : ch == 2 ? WaveCanvas2 : WaveCanvas3;
                    DrawChannel(canvas, _displayBuffers[ch], n);
                }
            });
        }

        private int ConvertRawToUnsigned(uint raw)
        {
            return (int)(raw & 0x00FFFFFFu);
        }

        private void MainWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
        {
            // Activated may be called multiple times; refresh ports when activated initially
            RefreshPorts();
        }

        private void MainWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        {
            _serialService?.Dispose();
            try { _counterTimer?.Change(Timeout.Infinite, Timeout.Infinite); _counterTimer?.Dispose(); _counterTimer = null; } catch { }
        }

        private void RefreshPorts()
        {
            try
            {
                var ports = _serialService.GetPortNames();
                PortComboBox.ItemsSource = ports;
                if (ports.Length > 0) PortComboBox.SelectedIndex = 0;
                AddLog($"Found ports: {string.Join(",", ports)}");
            }
            catch (Exception ex)
            {
                AddLog("Error listing ports: " + ex.Message);
            }
        }

        private void AddLog(string s)
        {
            // Queue log lines and let UI flush them periodically to avoid flooding the dispatcher
            _logQueue.Enqueue(s);
        }

        private void SerialService_DataReceived(byte[] data)
        {
            if (!_running) return;
            _parser.Feed(data);
        }



        private void Parser_PacketParsed(Models.Packet pkt)
        {
            // Enqueue packets quickly for batch processing by background drain
            _packetQueue.Enqueue(pkt);
            System.Threading.Interlocked.Increment(ref _pendingPacketCount);

            // Track parsed bytes per channel (each sample is 3 bytes)
            for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
            {
                System.Threading.Interlocked.Add(ref _bytesPerChannel[ch], Models.Packet.BufferLen * 3);
            }

            // Enforce bounded queue immediately to avoid unbounded memory growth when parsing is faster than drain
            try
            {
                int qCount = System.Threading.Interlocked.Add(ref _pendingPacketCount, 0);
                if (qCount > MaxPacketQueue)
                {
                    int toDrop = qCount - MaxPacketQueue;
                    int dropped = 0;
                    for (int i = 0; i < toDrop; i++)
                    {
                        if (_packetQueue.TryDequeue(out _))
                        {
                            System.Threading.Interlocked.Decrement(ref _pendingPacketCount);
                            dropped++;
                        }
                        else break;
                    }
                    if (dropped > 0)
                    {
                        System.Threading.Interlocked.Add(ref _droppedPacketCount, dropped);
                        System.Threading.Interlocked.Add(ref _dropLogAccumulator, dropped);
                        int now = Environment.TickCount;
                        if (now - _lastDropLogTick >= 10_000)
                        {
                            long pendingDrops = System.Threading.Interlocked.Exchange(ref _dropLogAccumulator, 0);
                            _lastDropLogTick = now;
                            if (pendingDrops > 0)
                            {
                                _logQueue.Enqueue($"Packet queue full, dropped {pendingDrops} packets (last 10s)");
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void ProcessPendingPackets()
        {
            // UI refresh only; background drain feeds the plot manager at full speed
            if (!_running) return;



            // trigger single UI update (fill display buffers then draw) and flush logs occasionally
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                bool anyDrawn = false;
                for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                {
                    int n = _plotManager.FillChannelSnapshot(ch, _displayBuffers[ch], _displayWindowSamples);
                    var canvas = ch == 0 ? WaveCanvas0 : ch == 1 ? WaveCanvas1 : ch == 2 ? WaveCanvas2 : WaveCanvas3;
                    DrawChannel(canvas, _displayBuffers[ch], n);
                    if (n > 0) anyDrawn = true;
                }

                // Update per-channel latest raw sample display (always show most recent sample as hex)
                for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                {
                    var tmpRaw = new uint[1];
                    int a = _plotManager.FillChannelRawSnapshot(ch, tmpRaw, 1);
                    string txt = a == 1 ? $"0x{tmpRaw[0]:X6} ({ConvertRawToUnsigned(tmpRaw[0])})" : "<no data>";
                    if (ch == 0) HoverText0.Text = txt;
                    else if (ch == 1) HoverText1.Text = txt;
                    else if (ch == 2) HoverText2.Text = txt;
                    else HoverText3.Text = txt;
                }

                if (!anyDrawn)
                {
                    // Avoid spamming logs. Update on-screen diagnostics so you can inspect counts without flooding the log.
                    UpdateBytesUi();
                }

                if (Environment.TickCount - _lastLogFlushTick >= LogFlushMs) FlushLogsAndUpdateQueueStatus();
            });
        }

        private void FlushLogsAndUpdateQueueStatus()
        {
            int now = Environment.TickCount;
            // Only flush logs at most every LogFlushMs
            if (now - _lastLogFlushTick < LogFlushMs) return;

            const int MaxFlush = 500;
            var sb = new System.Text.StringBuilder();
            int flushed = 0;
            for (int i = 0; i < MaxFlush; i++)
            {
                if (_logQueue.TryDequeue(out var s))
                {
                    sb.AppendLine(s);
                    flushed++;
                }
                else break;
            }

            if (flushed > 0)
            {
                const int MaxChars = 200_000;
                string add = sb.ToString();
                string t = (LogTextBox.Text ?? string.Empty) + add;
                if (t.Length > MaxChars) t = t.Substring(t.Length - MaxChars);
                LogTextBox.Text = t;
                LogTextBox.SelectionStart = t.Length;
                LogTextBox.SelectionLength = 0;
            }

            // update queue count display
            try { QueueTextBlock.Text = $"Queue: {System.Threading.Interlocked.Add(ref _pendingPacketCount, 0)}"; } catch { }

            _lastLogFlushTick = now;
        }

        private void DisplayWindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (DisplayWindowCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int ms))
                {
                    _displayWindowSamples = Math.Max(16, ms * _sampleRate / 1000);
                    // resize display buffers if needed
                    for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                    {
                        if (_displayBuffers[ch] == null || _displayBuffers[ch].Length < _displayWindowSamples)
                        {
                            _displayBuffers[ch] = new float[_displayWindowSamples];
                        }
                        if (_displayRawBuffers[ch] == null || _displayRawBuffers[ch].Length < _displayWindowSamples)
                        {
                            _displayRawBuffers[ch] = new uint[_displayWindowSamples];
                        }
                    }
                    _logQueue.Enqueue($"Display window set to {ms} ms ({_displayWindowSamples} samples)");
                }
            }
            catch { }
        }

        // Combo-based PlotBits handler left for backward compatibility if control returns; no-op when control missing.
        private void PlotBitsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // no-op: we now use the single PlotBitsNumberBox for full control.
        }

        // Old PlotBitsTextBox LostFocus handler removed; using NumberBox.ValueChanged instead.

        private void StartUiTimer()
        {
            if (_uiTimer != null) return; // already running
            _uiTimer = new Timer(_ => ProcessPendingPackets(), null, 0, 33); // ~30Hz
        }

        private void StopUiTimer()
        {
            if (_uiTimer == null) return;
            _uiTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _uiTimer.Dispose();
            _uiTimer = null;
        }

        private void StartBackgroundDrain()
        {
            // Log previous task state (if any)
            if (_drainTask != null)
            {
                _logQueue.Enqueue($"Previous drain task state: {_drainTask.Status}");

                if (!_drainTask.IsCompleted)
                {
                    _logQueue.Enqueue("Drain loop already running");
                    return;
                }

                if (_drainTask.IsFaulted)
                {
                    _logQueue.Enqueue($"Previous drain faulted: {_drainTask.Exception}");
                }
            }

            // Fresh CTS for this run
            _drainCts = new CancellationTokenSource();

            // Start on a dedicated background thread (WinUI-safe)
            _drainTask = Task.Factory.StartNew(
                async () =>
                {
                    try
                    {
                        _logQueue.Enqueue("DrainLoop delegate entered");
                        await DrainLoop(_drainCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logQueue.Enqueue("DrainLoop canceled");
                    }
                    catch (Exception ex)
                    {
                        _logQueue.Enqueue($"DrainLoop crashed: {ex}");
                        throw;
                    }
                },
                _drainCts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            ).Unwrap();

            _logQueue.Enqueue("Background drain started");
        }

        private async System.Threading.Tasks.Task DrainLoop(System.Threading.CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!_drainStartedLogged)
                    {
                        _drainStartedLogged = true;
                        _logQueue.Enqueue("Drain loop started");
                    }
                    if (!_running)
                    {
                        if (!_drainIdleLogged)
                        {
                            _drainIdleLogged = true;
                            _logQueue.Enqueue("Drain loop idle (capture not running)");
                        }
                        await System.Threading.Tasks.Task.Delay(DrainIdleMs, token).ConfigureAwait(false);
                        continue;
                    }
                    _drainIdleLogged = false;
                    System.Threading.Interlocked.Increment(ref _drainLoopIterations);
                    var batch = new List<Models.Packet>(DrainBatchSize);
                    for (int i = 0; i < DrainBatchSize; i++)
                    {
                        if (_packetQueue.TryDequeue(out var p))
                        {
                            batch.Add(p);
                            System.Threading.Interlocked.Decrement(ref _pendingPacketCount);
                        }
                        else break;
                    }
                    _lastDrainBatchCount = batch.Count;
                    _lastDrainTick = Environment.TickCount;
                    if (batch.Count == 0)
                    {
                        await System.Threading.Tasks.Task.Delay(DrainIdleMs, token).ConfigureAwait(false);
                        continue;
                    }
                    // Use the latest configured conversion parameters (updated by UI events)
                    float voltsPerCycle = (float)(_rampSlope / Math.Max(1.0, _cpuFreq));
                    _plotManager.AddPacketsBatch(batch, voltsPerCycle, _plotBits);
                    // Track processed packets for diagnostics
                    System.Threading.Interlocked.Add(ref _processedPacketCount, batch.Count);

                    // If recording, write raw packets to disk (header + payload)
                    if (_recording)
                    {
                        lock (_recordLock)
                        {
                            if (_recordWriter != null)
                            {
                                foreach (var pkt in batch)
                                {
                                    try
                                    {
                                        _recordWriter.Write((byte)0x55);
                                        _recordWriter.Write((byte)0xAA);
                                        for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                                        {
                                            for (int i = 0; i < Models.Packet.BufferLen; i++)
                                            {
                                                uint v = pkt.Samples[ch, i] & 0x00FFFFFFu;
                                                byte b0 = (byte)(v & 0xFF);
                                                byte b1 = (byte)((v >> 8) & 0xFF);
                                                byte b2 = (byte)((v >> 16) & 0xFF);
                                                _recordWriter.Write(b0);
                                                _recordWriter.Write(b1);
                                                _recordWriter.Write(b2);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logQueue.Enqueue("Record write error: " + ex.Message);
                                    }
                                }
                                try { _recordWriter.Flush(); } catch { }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logQueue.Enqueue("Drain error: " + ex.Message);
            }
            _logQueue.Enqueue("Background drain stopped");
        }

        private void StopBackgroundDrain()
        {
            try
            {
                if (_drainCts != null)
                {
                    _drainCts.Cancel();
                    _drainCts.Dispose();
                    _drainCts = null;
                }
                _drainTask = null;
            }
            catch { }
        }

        private void DrawChannel(Canvas canvas, float[] samples, int length)
        {
            try
            {
                canvas.Children.Clear();
                if (samples == null || length <= 0) return;

                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                if (w <= 0) w = canvas.Width > 0 ? canvas.Width : 600;
                if (h <= 0) h = canvas.Height > 0 ? canvas.Height : 140;

                float min, max;
                float mid = 0f;
                int bits = Math.Max(1, Math.Min(24, _plotBits));
                int maxValue = (1 << bits) - 1;
                if (_fitToData)
                {
                    // Autoscale to snapshot data range using unsigned values, then center around midpoint (AC-coupled)
                    min = float.MaxValue; max = float.MinValue;
                    for (int i = 0; i < length; i++)
                    {
                        uint raw = (uint)samples[i] & 0x00FFFFFFu;
                        if (_reverseBytes) raw = ADC_Rec.PlotUtils.ReverseBytes24(raw);
                        int vUnsigned = ConvertRawToUnsigned(raw);
                        float v = (float)vUnsigned;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    if (min == float.MaxValue || max == float.MinValue) { min = 0f; max = maxValue; }
                    if (Math.Abs(max - min) < 1e-6f) { max = min + 1f; }
                    mid = (min + max) / 2f;
                    float maxAbs = Math.Max(Math.Abs(max - mid), Math.Abs(min - mid));
                    min = -maxAbs;
                    max = maxAbs;
                }
                else
                {
                    // Use a fixed plotting range based on selected bit depth: [0 .. 2^bits-1]
                    min = 0f;
                    max = maxValue;
                }
                float range = max - min;
                if (range <= 0f) range = 1f; // fall back to sensible range to avoid div0

                // Decimate to canvas width using min/max per bucket
                int n = length;
                int pixelWidth = (int)w;
                if (pixelWidth < 16) pixelWidth = 16;
                int buckets = Math.Min(pixelWidth, n);
                double bucketSize = (double)n / buckets;

                var poly = new Microsoft.UI.Xaml.Shapes.Polyline
                {
                    Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Lime),
                    StrokeThickness = 1
                };
                var pts = new Microsoft.UI.Xaml.Media.PointCollection();

                for (int b = 0; b < buckets; b++)
                {
                    int s = (int)Math.Floor(b * bucketSize);
                    int e = (int)Math.Floor((b + 1) * bucketSize) - 1;
                    if (e < s) e = s;
                    float bmin = float.MaxValue, bmax = float.MinValue;
                    for (int k = s; k <= e && k < n; k++)
                    {
                        // Convert raw sample to unsigned plotted value respecting plot bits and byte order
                        uint raw = (uint)samples[k] & 0x00FFFFFFu;
                        if (_reverseBytes) raw = ADC_Rec.PlotUtils.ReverseBytes24(raw);
                        int vUnsigned = ConvertRawToUnsigned(raw);
                        float v = (float)vUnsigned;
                        if (_fitToData) v -= mid;
                        if (v < bmin) bmin = v;
                        if (v > bmax) bmax = v;
                    }
                    if (bmin == float.MaxValue || bmax == float.MinValue) { bmin = min; bmax = min; }
                    double x = (double)b / (buckets - 1) * w;
                    double y1 = h - ((bmin - min) / range) * h;
                    double y2 = h - ((bmax - min) / range) * h;
                    pts.Add(new Windows.Foundation.Point(x, y1));
                    pts.Add(new Windows.Foundation.Point(x, y2));
                }

                poly.Points = pts;

                // Draw zero baseline if zero lies within visible range (helps to see offset)
                if (min <= 0 && max >= 0)
                {
                    double y0 = h - ((0 - min) / range) * h;
                    var baseline = new Microsoft.UI.Xaml.Shapes.Line
                    {
                        X1 = 0,
                        Y1 = y0,
                        X2 = w,
                        Y2 = y0,
                        Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DimGray),
                        StrokeThickness = 1
                    };
                    canvas.Children.Add(baseline);
                }

                canvas.Children.Add(poly);

                // Draw a small red dot at the most recent sample (helps detect scrolling)
                if (length > 0)
                {
                    // Convert last sample to signed plotted value
                    uint rawLast = (uint)samples[length - 1] & 0x00FFFFFFu;
                    if (_reverseBytes) rawLast = ADC_Rec.PlotUtils.ReverseBytes24(rawLast);
                    int vLast = ConvertRawToUnsigned(rawLast);
                    float last = (float)vLast;
                    if (_fitToData) last -= mid;
                    double xLast = w; // most recent sample drawn at right edge
                    double yLast = h - ((last - min) / range) * h;
                    var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 4,
                        Height = 4,
                        Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red)
                    };
                    Canvas.SetLeft(dot, Math.Max(0, xLast - 2));
                    Canvas.SetTop(dot, Math.Max(0, yLast - 2));
                    canvas.Children.Add(dot);
                }
            }
            catch (Exception ex)
            {
                AddLog("Draw error: " + ex.Message);
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (PortComboBox.SelectedItem == null)
            {
                AddLog("No port selected");
                return;
            }
            string? port = PortComboBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(port)) { AddLog("Invalid port selected"); return; }
            bool ok = _serialService.Connect(port);
            _ = DispatcherQueue.TryEnqueue(() => { StatusTextBlock.Text = ok ? $"Connected {port}" : $"Connect failed {port}"; });
        }

        private void DumpButton_Click(object sender, RoutedEventArgs e)
        {
            // If there's a pending parsed packet, dequeue it and dump its raw bytes (header + payload)
            if (_packetQueue.TryDequeue(out var pkt))
            {
                System.Threading.Interlocked.Decrement(ref _pendingPacketCount);
                int payloadLen = Models.Packet.NumChannels * Models.Packet.BufferLen * 3;
                int totalLen = 2 + payloadLen;
                _logQueue.Enqueue($"Packet dump (raw {totalLen} bytes):");
                // Header bytes on their own line
                _logQueue.Enqueue("0x55,0xAA,");
                // Per-channel samples (3 bytes LSB-first) with integer value shown
                for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                {
                    for (int i = 0; i < Models.Packet.BufferLen; i++)
                    {
                        uint v = pkt.Samples[ch, i] & 0x00FFFFFFu;
                        byte b0 = (byte)(v & 0xFF);
                        byte b1 = (byte)((v >> 8) & 0xFF);
                        byte b2 = (byte)((v >> 16) & 0xFF);
                        uint vrev = ADC_Rec.PlotUtils.ReverseBytes24(v);
                        if (_reverseBytes)
                        {
                            // Show reversed sample as decimal integer (no hex) for easier numeric inspection
                            _logQueue.Enqueue($"0x{b0:X2},0x{b1:X2},0x{b2:X2}, = CH{ch}, {v}, rev={vrev}");
                        }
                        else
                        {
                            _logQueue.Enqueue($"0x{b0:X2},0x{b1:X2},0x{b2:X2}, = CH{ch}, {v}");
                        }
                    }
                    // separator line between channels for readability
                    _logQueue.Enqueue(string.Empty);
                }
                return;
            }

            // Fallback: Dump a compact textual snapshot of recent samples for each channel
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                int dumpSamples = Math.Min(128, _displayWindowSamples);
                AddLog($"Dumping last {dumpSamples} samples per channel (most recent last):");
                for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                {
                    var tmp = new float[dumpSamples];
                    int avail = _plotManager.FillChannelSnapshot(ch, tmp, dumpSamples);
                    if (avail == 0) { _logQueue.Enqueue($"ch{ch}: <no data>"); continue; }
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"ch{ch}: ");
                    for (int i = 0; i < avail; i++)
                    {
                        sb.Append(tmp[i].ToString("G6"));
                        if (i + 1 < avail) sb.Append(',');
                    }
                    _logQueue.Enqueue(sb.ToString());
                }
            });
        }

        private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (LogTextBox == null) return;
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(LogTextBox.Text ?? string.Empty);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                AddLog("Logs copied to clipboard");
            }
            catch (Exception ex)
            {
                AddLog("Copy error: " + ex.Message);
            }
        }

        private void CopyDiagButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string txt = string.Empty;
                if (DiagTextBlock != null) txt = DiagTextBlock.Text ?? string.Empty;
                // Include basic hover/latest labels and counts too
                txt += "\r\n" + (HoverText0?.Text ?? "");
                txt += "\r\n" + (HoverText1?.Text ?? "");
                txt += "\r\n" + (HoverText2?.Text ?? "");
                txt += "\r\n" + (HoverText3?.Text ?? "");
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(txt);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                AddLog("Diagnostics copied to clipboard");
            }
            catch (Exception ex)
            {
                AddLog("Copy diag error: " + ex.Message);
            }
        }

        private void PlotBitsNumberBox_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
        {
            try
            {
                int bits = (int)Math.Round(e.NewValue);
                bits = Math.Max(8, Math.Min(24, bits));
                if (bits == _plotBits) return;
                _plotBits = bits;
                _logQueue.Enqueue($"Plot bits set to {_plotBits} (spinner)");
                // If Fit-to-data is enabled, autoscale will hide the effect of changing bit depth. Disable it so the change is visible.
                try
                {
                    if (FitToDataCheck != null && FitToDataCheck.IsChecked == true)
                    {
                        FitToDataCheck.IsChecked = false;
                        _fitToData = false;
                        _logQueue.Enqueue("Fit to data: OFF (disabled to apply plot bits)");
                    }
                }
                catch { }
                _plotManager?.RescaleBuffers(_plotBits);
                ForceRedraw();
            }
            catch (Exception ex) { _logQueue.Enqueue("PlotBits number change error: " + ex.Message); }
        }

        private void UpdateBytesUi()
        {
            try
            {
                long parsedPkts = _parser?.ParsedPacketCount ?? 0;
                long invalidPkts = _parser?.InvalidPacketCount ?? 0;
                long trimmedBytes = _parser?.TrimmedBytesCount ?? 0;
                long drainIters = System.Threading.Interlocked.Read(ref _drainLoopIterations);
                int lastBatch = System.Threading.Interlocked.CompareExchange(ref _lastDrainBatchCount, 0, 0);
                int lastTick = System.Threading.Interlocked.CompareExchange(ref _lastDrainTick, 0, 0);
                int ageMs = lastTick == 0 ? -1 : unchecked(Environment.TickCount - lastTick);
                var sb = new System.Text.StringBuilder();
                for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                {
                    long v = System.Threading.Interlocked.Add(ref _bytesPerChannel[ch], 0);
                    sb.Append($" ch{ch}={v}");
                }
                long dropped = System.Threading.Interlocked.Add(ref _droppedPacketCount, 0);
                long processed = System.Threading.Interlocked.Add(ref _processedPacketCount, 0);
                int queueCount = System.Threading.Interlocked.Add(ref _pendingPacketCount, 0);
                // collect per-channel snapshot counts and maximums for diagnostics
                var sbDiag = new System.Text.StringBuilder();
                for (int ch = 0; ch < Models.Packet.NumChannels; ch++)
                {
                    int avail = _plotManager.GetAvailableSamples(ch);
                    uint maxRaw = _plotManager.GetMaxRaw(ch);
                    sbDiag.Append($"ch{ch}: samples={avail} max=0x{maxRaw:X6}  ");
                }
                string drainInfo = ageMs < 0 ? "drainAge=NA" : $"drainAgeMs={ageMs}";
                string diag = $"proc={processed} parsed={parsedPkts} invalid={invalidPkts} dropped={dropped} queue={queueCount} trimmedBytes={trimmedBytes} drainIters={drainIters} lastBatch={lastBatch} {drainInfo} {sbDiag.ToString()}";
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    BytesTextBlock.Text = "Bytes:" + sb.ToString();
                    QueueTextBlock.Text = $"Queue: {System.Threading.Interlocked.Add(ref _pendingPacketCount, 0)}";
                    DroppedTextBlock.Text = $"Dropped: {dropped}";
                    DiagTextBlock.Text = diag;
                });
            }
            catch { }
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_recording)
                {
                    string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string path = System.IO.Path.Combine(folder, $"ADCRec_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
                    var fs = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.Read);
                    lock (_recordLock) { _recordWriter = new System.IO.BinaryWriter(fs); }
                    _recording = true;
                    if (RecordButton != null) RecordButton.Content = "Stop Record";
                    AddLog($"Recording to {path}");
                }
                else
                {
                    lock (_recordLock) { try { _recordWriter?.Dispose(); } catch { } _recordWriter = null; }
                    _recording = false;
                    if (RecordButton != null) RecordButton.Content = "Start Record";
                    AddLog("Recording stopped");
                }
            }
            catch (Exception ex)
            {
                AddLog("Record error: " + ex.Message);
            }
        }

        private void ReplayButton_Click(object sender, RoutedEventArgs e)
        {
            // Find most recent recording in Documents and replay it into the parser (offline)
            try
            {
                string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var files = System.IO.Directory.GetFiles(folder, "ADCRec_*.bin");
                if (files == null || files.Length == 0) { AddLog("No recordings found in Documents"); return; }
                var path = files.OrderByDescending(f => f).First();
                AddLog($"Replaying {path}");
                _replaying = true;
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        int pktLen = 2 + Models.Packet.NumChannels * Models.Packet.BufferLen * 3;
                        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                        var buf = new byte[pktLen];
                        while (true)
                        {
                            int read = await fs.ReadAsync(buf, 0, pktLen).ConfigureAwait(false);
                            if (read < pktLen) break;
                            _parser.Feed(buf);
                            // approximate real-time pacing based on sample rate and buffer length
                            int pps = Math.Max(1, _sampleRate / Models.Packet.BufferLen);
                            await System.Threading.Tasks.Task.Delay(1000 / pps).ConfigureAwait(false);
                        }
                        _logQueue.Enqueue($"Replay finished: {path}");
                    }
                    catch (Exception ex)
                    {
                        _logQueue.Enqueue("Replay error: " + ex.Message);
                    }
                    finally
                    {
                        _replaying = false;
                    }
                });
            }
            catch (Exception ex)
            {
                AddLog("Replay error: " + ex.Message);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear plotted data, queues, logs, and counters
            _plotManager.Clear();

            // clear pending packet queue and counters
            while (_packetQueue.TryDequeue(out _)) { }
            System.Threading.Interlocked.Exchange(ref _pendingPacketCount, 0);

            // reset bytes counters
            for (int ch = 0; ch < Models.Packet.NumChannels; ch++) System.Threading.Interlocked.Exchange(ref _bytesPerChannel[ch], 0);

            // clear logs queue and UI
            while (_logQueue.TryDequeue(out _)) { }
            _ = DispatcherQueue.TryEnqueue(() => { LogTextBox.Text = string.Empty; QueueTextBlock.Text = "Queue: 0"; BytesTextBlock.Text = "Bytes: ch0=0 ch1=0 ch2=0 ch3=0"; WaveCanvas0.Children.Clear(); WaveCanvas1.Children.Clear(); WaveCanvas2.Children.Clear(); WaveCanvas3.Children.Clear(); });
            AddLog("Cleared display, logs, and counters");
        }

        private void StartCountersTimer()
        {
            _counterTimer?.Change(0, 200);
        }

        private void StopCountersTimer()
        {
            _counterTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void WaveCanvas_PointerEnter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // no-op: we always display the latest sample value in the HoverText controls
        }

        private void WaveCanvas_PointerExit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // no-op: keep hover labels visible and showing the latest value
        }

        private void WaveCanvas_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {            // no-op: we don't use pointer location to inspect historical samples any more
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _serialService.Disconnect();
            _ = DispatcherQueue.TryEnqueue(() => { StatusTextBlock.Text = "Disconnected"; });
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            _running = true;
            System.Threading.Interlocked.Exchange(ref _drainLoopIterations, 0);
            System.Threading.Interlocked.Exchange(ref _lastDrainBatchCount, 0);
            System.Threading.Interlocked.Exchange(ref _lastDrainTick, 0);
            _drainStartedLogged = false;
            _drainIdleLogged = false;
            StartBackgroundDrain();
            StartUiTimer();
            StartCountersTimer();
            AddLog("Capture started");
            StatusTextBlock.Text = "Running";
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _running = false;
            StopBackgroundDrain();
            StopUiTimer();
            StopCountersTimer();
            // stop recording if active
            if (_recording) {
                lock (_recordLock) { try { _recordWriter?.Dispose(); } catch { } _recordWriter = null; }
                _recording = false;
                if (RecordButton != null) RecordButton.Content = "Start Record";
                AddLog("Recording stopped (capture stopped)");
            }
            AddLog("Capture stopped");
            StatusTextBlock.Text = "Stopped";
        }
    }
}
