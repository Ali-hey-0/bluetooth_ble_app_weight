using BleSimpleApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Plugin.BLE.Abstractions.Contracts;
using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace BleSimpleApp;

public partial class MainPage : ContentPage
{
    private const int MaxReceiveMessageBytes = 4096;
    private const int ReceiveMessageIdleMilliseconds = 1200;
    private const string BluetoothCsvHeader =
        TerminalLogStore.BluetoothCsvHeader;

    private readonly BleService _bleService;
    private readonly TerminalLogStore _terminalStore;
    private readonly List<byte> _receiveBuffer = new(MaxReceiveMessageBytes);
    private readonly StringBuilder _terminalBuffer = new();

    private CancellationTokenSource? _receiveFlushCancellationTokenSource;
    private ReceiveDisplayMode _receiveDisplayMode = ReceiveDisplayMode.Ascii;
    private bool _eventsSubscribed;
    private bool _invalidAsciiWarningShown;
    private bool _prerequisiteCheckInProgress;
    private string? _lastAcceptedCsvRow;
    private int _terminalRenderRequested;
    private int _scrollRequested;

    public MainPage()
    {
        InitializeComponent();

        _bleService =
            IPlatformApplication.Current?.Services
                .GetRequiredService<BleService>()
            ?? throw new InvalidOperationException(
                "BleService is unavailable / سرویس بلوتوث در دسترس نیست.");

        _terminalStore =
            IPlatformApplication.Current.Services
                .GetRequiredService<TerminalLogStore>();

        BindingContext = this;
    }

    public ObservableCollection<DeviceListItem> Devices { get; } = [];

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (!_eventsSubscribed)
        {
            _bleService.DataReceived += OnBleDataReceived;
            _bleService.StatusChanged += OnBleStatusChanged;
            _terminalStore.Changed += OnTerminalStoreChanged;
            _eventsSubscribed = true;
        }

        RefreshDeviceActions();
        RenderTerminal();
        _ = _bleService.HandleAppResumeAsync();
        _ = CheckPrerequisitesOnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
    }

    private void OnTerminalStoreChanged(
        object? sender,
        EventArgs e)
    {
        RequestTerminalRender();
    }

    private async void OnScanClicked(object? sender, EventArgs e)
    {
        if (!await EnsureBlePrerequisitesAsync())
        {
            return;
        }

        SetScanControls(isScanning: true);
        Devices.Clear();

        try
        {
            SetStatus(
                "Status: Scanning... / وضعیت: در حال اسکن...");

            await _bleService.StartScanningAsync(AddOrUpdateDevice);

            SetStatus(
                $"Status: Scan completed ({Devices.Count}) / وضعیت: اسکن کامل شد ({Devices.Count})");
        }
        catch (UnauthorizedAccessException exception)
        {
            SetStatus(
                "Status: Permission denied / وضعیت: مجوز رد شد");
            AppendTerminal(
                $"Permission error: {exception.Message} / خطای مجوز");
        }
        catch (InvalidOperationException exception)
        {
            SetStatus(
                "Status: Bluetooth unavailable / وضعیت: بلوتوث در دسترس نیست");
            AppendTerminal(
                $"Bluetooth error: {exception.Message} / خطای بلوتوث");
        }
        catch (Exception exception)
        {
            SetStatus(
                "Status: Scan failed / وضعیت: اسکن ناموفق بود");
            AppendTerminal(
                $"Scan error: {exception.Message} / خطای اسکن");
        }
        finally
        {
            SetScanControls(isScanning: false);
        }
    }

    private async void OnStopScanClicked(object? sender, EventArgs e)
    {
        StopScanButton.IsEnabled = false;

        try
        {
            await _bleService.StopScanningAsync();
            SetStatus(
                "Status: Scan stopped / وضعیت: اسکن متوقف شد");
        }
        catch (Exception exception)
        {
            AppendTerminal(
                $"Stop scan error: {exception.Message} / خطای توقف اسکن");
        }
        finally
        {
            ScanButton.IsEnabled = true;
            Hc08QuickConnectButton.IsEnabled =
                !_bleService.IsConnected;
            StopScanButton.IsEnabled = false;
        }
    }

    private async void OnConnectKnownHc08Clicked(
        object? sender,
        EventArgs e)
    {
        if (!await EnsureBlePrerequisitesAsync())
        {
            return;
        }

        SetDeviceActionsEnabled(false);
        ScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = false;
        Hc08QuickConnectButton.IsEnabled = false;

        try
        {
            SetStatus(
                "Status: Connecting directly to HC-08... / وضعیت: اتصال مستقیم به HC-08...");

            var connected =
                await _bleService.ConnectToKnownHc08Async();

            if (connected)
            {
                MarkConnectedFromService();
                SetStatus(
                    "Status: Connected to HC-08 / وضعیت: متصل به HC-08");
            }
            else
            {
                SetStatus(
                    "Status: HC-08 connection failed / وضعیت: اتصال HC-08 ناموفق بود");
            }
        }
        catch (Exception exception)
        {
            SetStatus(
                "Status: HC-08 connection failed / وضعیت: اتصال HC-08 ناموفق بود");
            AppendTerminal(
                $"HC-08 connection error: {exception.Message} / خطای اتصال HC-08");
        }
        finally
        {
            ScanButton.IsEnabled = !_bleService.IsConnected;
            Hc08QuickConnectButton.IsEnabled =
                !_bleService.IsConnected;
            StopScanButton.IsEnabled = false;
            SetDeviceActionsEnabled(true);
            RefreshDeviceActions();
        }
    }

    private async void OnDeviceConnectClicked(
        object? sender,
        EventArgs e)
    {
        if (sender is not Button button ||
            button.CommandParameter is not DeviceListItem item)
        {
            return;
        }

        if (!item.IsConnected &&
            !await EnsureBlePrerequisitesAsync())
        {
            return;
        }

        SetDeviceActionsEnabled(false);
        ScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = false;

        try
        {
            if (item.IsConnected)
            {
                SetStatus(
                    "Status: Disconnecting... / وضعیت: در حال قطع اتصال...");

                var disconnected =
                    await _bleService.DisconnectDeviceAsync();

                if (disconnected)
                {
                    MarkConnectedDevice(null);
                    SetStatus(
                        "Status: Disconnected / وضعیت: اتصال قطع شد");
                }

                return;
            }

            SetStatus(
                $"Status: Connecting to {item.DisplayName} / وضعیت: در حال اتصال به {item.DisplayName}");

            var connected = await _bleService.ConnectToDeviceAsync(
                item.Device);

            if (connected)
            {
                MarkConnectedDevice(item);
                SetStatus(
                    $"Status: Connected to {item.DisplayName} / وضعیت: متصل به {item.DisplayName}");
            }
            else
            {
                SetStatus(
                    "Status: Connection failed / وضعیت: اتصال ناموفق بود");
            }
        }
        catch (Exception exception)
        {
            SetStatus(
                "Status: BLE operation failed / وضعیت: عملیات بلوتوث ناموفق بود");
            AppendTerminal(
                $"BLE operation error: {exception.Message} / خطای عملیات بلوتوث");
        }
        finally
        {
            ScanButton.IsEnabled = !_bleService.IsConnected;
            StopScanButton.IsEnabled = false;
            SetDeviceActionsEnabled(true);
            RefreshDeviceActions();
        }
    }

    private void OnBleDataReceived(
        object? sender,
        BleDataReceivedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ProcessReceivedBytes(e.Data);
        });
    }

    private void OnBleStatusChanged(
        object? sender,
        BleStatusEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (e.State is BleConnectionState.Disconnected or
                BleConnectionState.ConnectionLost or
                BleConnectionState.ReconnectFailed or
                BleConnectionState.BluetoothOff)
            {
                CompletePendingMessage();
                _lastAcceptedCsvRow = null;
            }

            AppendTerminalCore(
                $"{DateTime.Now:HH:mm:ss.fff} - {e.Message}");

            SetStatus($"Status / وضعیت: {e.Message}");
            ApplyConnectionState(e.State);
        });
    }

    private void ApplyConnectionState(BleConnectionState state)
    {
        switch (state)
        {
            case BleConnectionState.Connected:
            case BleConnectionState.Reconnected:
            case BleConnectionState.AppResumedConnected:
                ScanButton.IsEnabled = false;
                Hc08QuickConnectButton.IsEnabled = false;
                MarkConnectedFromService();
                break;

            case BleConnectionState.ConnectionLost:
            case BleConnectionState.Reconnecting:
                ScanButton.IsEnabled = false;
                Hc08QuickConnectButton.IsEnabled = false;
                SetDeviceActionsEnabled(false);
                break;

            case BleConnectionState.Disconnected:
            case BleConnectionState.ReconnectFailed:
            case BleConnectionState.BluetoothOff:
            case BleConnectionState.AppResumedDisconnected:
                ScanButton.IsEnabled = true;
                Hc08QuickConnectButton.IsEnabled = true;
                MarkConnectedDevice(null);
                SetDeviceActionsEnabled(true);
                break;

            case BleConnectionState.Scanning:
                SetScanControls(isScanning: true);
                break;

            case BleConnectionState.ScanStopped:
            case BleConnectionState.ScanCompleted:
                SetScanControls(isScanning: false);
                break;
        }
    }

    private void AddOrUpdateDevice(IDevice device)
    {
        var existing = Devices.FirstOrDefault(
            item => item.Device?.Id == device.Id);

        if (existing is null)
        {
            InsertDeviceSorted(new DeviceListItem(device));
            return;
        }

        var previousSortKey = existing.SortKey;
        existing.Update(device);

        if (!string.Equals(
                previousSortKey,
                existing.SortKey,
                StringComparison.OrdinalIgnoreCase))
        {
            Devices.Remove(existing);
            InsertDeviceSorted(existing);
        }
    }

    private void InsertDeviceSorted(DeviceListItem item)
    {
        var index = 0;

        while (index < Devices.Count &&
               string.Compare(
                   Devices[index].SortKey,
                   item.SortKey,
                   StringComparison.OrdinalIgnoreCase) <= 0)
        {
            index++;
        }

        Devices.Insert(index, item);
    }

    private void MarkConnectedFromService()
    {
        var connectedDevice = _bleService.ConnectedDevice;
        if (connectedDevice is null)
        {
            MarkConnectedDevice(null);
            return;
        }

        var connectedItem = Devices.FirstOrDefault(
            item => item.Device?.Id == connectedDevice.Id);

        if (connectedItem is null)
        {
            connectedItem = new DeviceListItem(connectedDevice);
            InsertDeviceSorted(connectedItem);
        }

        MarkConnectedDevice(connectedItem);
    }

    private void MarkConnectedDevice(DeviceListItem? connectedItem)
    {
        foreach (var item in Devices)
        {
            item.IsConnected = ReferenceEquals(item, connectedItem);
        }
    }

    private void SetDeviceActionsEnabled(bool enabled)
    {
        foreach (var item in Devices)
        {
            item.IsActionEnabled = enabled;
        }
    }

    private void RefreshDeviceActions()
    {
        foreach (var item in Devices)
        {
            item.IsActionEnabled = true;
            item.RefreshActionText();
        }
    }

    private void SetScanControls(bool isScanning)
    {
        ScanButton.IsEnabled =
            !isScanning && !_bleService.IsConnected;
        Hc08QuickConnectButton.IsEnabled =
            !isScanning && !_bleService.IsConnected;
        StopScanButton.IsEnabled = isScanning;
    }

    private void SetStatus(string status)
    {
        StatusLabel.Text = status;
    }

    private void AppendTerminal(string entry)
    {
        if (MainThread.IsMainThread)
        {
            AppendTerminalCore(entry);
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
            AppendTerminalCore(entry));
    }

    private void AppendTerminalCore(string entry)
    {
        _terminalStore.Append(entry);
    }

    private void ProcessReceivedBytes(byte[] data)
    {
        _terminalStore.RecordPacket(data.Length);

        foreach (var value in data)
        {
            if (value is 0x00 or (byte)'\r' or (byte)'\n')
            {
                CompletePendingMessage();
                continue;
            }

            _receiveBuffer.Add(value);

            if (_receiveBuffer.Count >= MaxReceiveMessageBytes)
            {
                CompletePendingMessage();
            }
        }

        UpdateReceiveStatistics();
        ScheduleReceiveFrameFlush();
    }

    private void ScheduleReceiveFrameFlush()
    {
        CancelReceiveFrameFlush();

        if (_receiveBuffer.Count == 0)
        {
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _receiveFlushCancellationTokenSource = cancellationTokenSource;
        _ = FlushReceiveFrameAfterIdleAsync(cancellationTokenSource);
    }

    private async Task FlushReceiveFrameAfterIdleAsync(
        CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await Task.Delay(
                ReceiveMessageIdleMilliseconds,
                cancellationTokenSource.Token);

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (ReferenceEquals(
                        _receiveFlushCancellationTokenSource,
                        cancellationTokenSource))
                {
                    _receiveFlushCancellationTokenSource = null;
                    CompletePendingMessage();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }

    private void CancelReceiveFrameFlush()
    {
        var cancellationTokenSource = Interlocked.Exchange(
            ref _receiveFlushCancellationTokenSource,
            null);

        cancellationTokenSource?.Cancel();
    }

    private void CompletePendingMessage()
    {
        CancelReceiveFrameFlush();

        if (_receiveBuffer.Count == 0)
        {
            return;
        }

        var frame = _receiveBuffer.ToArray();
        _receiveBuffer.Clear();

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var utf8 = DecodeUtf8ForDisplay(frame);
        var ascii = DecodeAsciiLine(frame);

        string? decodedText;

        switch (_receiveDisplayMode)
        {
            case ReceiveDisplayMode.Utf8:
                if (utf8.IsExact && utf8.HasReadableText)
                {
                    decodedText = utf8.Text;
                }
                else
                {
                    RegisterInvalidTextFrame(
                        frame.Length,
                        ascii.PrintablePercentage);
                    return;
                }

                break;

            case ReceiveDisplayMode.Ascii:
                if (!ascii.IsValid)
                {
                    RegisterInvalidTextFrame(
                        frame.Length,
                        ascii.PrintablePercentage);
                    return;
                }

                decodedText = ascii.Text;
                break;

            default:
                if (ascii.IsValid)
                {
                    decodedText = ascii.Text;
                }
                else if (utf8.IsExact && utf8.HasReadableText)
                {
                    decodedText = utf8.Text;
                }
                else
                {
                    RegisterInvalidTextFrame(
                        frame.Length,
                        ascii.PrintablePercentage);
                    return;
                }

                break;
        }

        var normalizedText = decodedText.Trim().TrimStart('\uFEFF');

        if (IsBluetoothCsvHeader(normalizedText))
        {
            _invalidAsciiWarningShown = false;
            AppendTerminalCore(
                $"{timestamp}  CSV stream header received / هدر جریان CSV دریافت شد");
            return;
        }

        if (!TryParseBluetoothCsvRecord(
                normalizedText,
                out var record))
        {
            RegisterInvalidTextFrame(
                frame.Length,
                ascii.PrintablePercentage);
            return;
        }

        _invalidAsciiWarningShown = false;

        if (string.Equals(
                _lastAcceptedCsvRow,
                normalizedText,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastAcceptedCsvRow = normalizedText;
        var receivedAt = DateTime.Now;
        _terminalStore.RecordValidCsv(
            new BleLogRecord(
                receivedAt,
                normalizedText,
                FormatBluetoothCsvRecord(timestamp, record),
                record.Sequence,
                record.TickMilliseconds,
                record.AdcRaw,
                record.HxRaw,
                record.HxNet,
                record.HxGramsX1000,
                record.HxFilteredGramsX1000,
                record.Status));
    }

    private static bool IsBluetoothCsvHeader(string text)
    {
        return string.Equals(
            text,
            BluetoothCsvHeader,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBluetoothCsvRecord(
        string text,
        out BluetoothCsvRecord record)
    {
        record = default;

        var columns = text.Split(
            ',',
            StringSplitOptions.TrimEntries);

        if (columns.Length != 8 ||
            !uint.TryParse(
                columns[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence) ||
            !uint.TryParse(
                columns[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var tickMilliseconds) ||
            !ushort.TryParse(
                columns[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var adcRaw) ||
            !int.TryParse(
                columns[3],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var hxRaw) ||
            !int.TryParse(
                columns[4],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var hxNet) ||
            !int.TryParse(
                columns[5],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var hxGramsX1000) ||
            !int.TryParse(
                columns[6],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var hxFilteredGramsX1000) ||
            !TryParseStatus(columns[7], out var status))
        {
            return false;
        }

        record = new BluetoothCsvRecord(
            sequence,
            tickMilliseconds,
            adcRaw,
            hxRaw,
            hxNet,
            hxGramsX1000,
            hxFilteredGramsX1000,
            status);
        return true;
    }

    private static bool TryParseStatus(
        string text,
        out uint status)
    {
        status = 0;

        var value = text.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase)
            ? text[2..]
            : text;

        return value.Length is > 0 and <= 8 &&
               uint.TryParse(
                   value,
                   NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture,
                   out status);
    }

    private static string FormatBluetoothCsvRecord(
        string timestamp,
        BluetoothCsvRecord record)
    {
        var weightGrams = record.HxGramsX1000 / 1000m;
        var filteredWeightGrams =
            record.HxFilteredGramsX1000 / 1000m;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{timestamp}  #{record.Sequence} | t={record.TickMilliseconds} ms | " +
            $"ADC={record.AdcRaw} | HX raw={record.HxRaw} | net={record.HxNet} | " +
            $"weight={weightGrams:0.###} g | filtered={filteredWeightGrams:0.###} g | " +
            $"status=0x{record.Status:X8}");
    }

    private static Utf8DisplayResult DecodeUtf8ForDisplay(byte[] data)
    {
        var length = data.Length;

        while (length > 0 &&
               data[length - 1] is 0x00 or (byte)'\r' or (byte)'\n')
        {
            length--;
        }

        if (length == 0)
        {
            return new Utf8DisplayResult(
                string.Empty,
                IsExact: true,
                HasReadableText: false);
        }

        var source = data.AsSpan(0, length);
        var text = new StringBuilder(length);
        var hasReadableText = false;

        while (!source.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(
                source,
                out var rune,
                out var bytesConsumed);

            if (status == OperationStatus.Done)
            {
                AppendRuneForDisplay(
                    text,
                    rune,
                    ref hasReadableText);
                source = source[bytesConsumed..];
                continue;
            }

            return new Utf8DisplayResult(
                string.Empty,
                IsExact: false,
                HasReadableText: false);
        }

        return new Utf8DisplayResult(
            text.ToString(),
            IsExact: true,
            HasReadableText: hasReadableText);
    }

    private static void AppendRuneForDisplay(
        StringBuilder text,
        Rune rune,
        ref bool hasReadableText)
    {
        var value = rune.Value;

        if (value == '\t')
        {
            text.Append('\t');
            return;
        }

        if (value < 0x20 || value == 0x7F)
        {
            text.Append(@"\u");
            text.Append(value.ToString("X4"));
            return;
        }

        text.Append(rune.ToString());

        if (value > 0x20)
        {
            hasReadableText = true;
        }
    }

    private static AsciiLineResult DecodeAsciiLine(byte[] data)
    {
        var text = new StringBuilder(data.Length);
        var printableBytes = 0;
        var invalidBytes = 0;

        foreach (var value in data)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                text.Append((char)value);
                printableBytes++;
            }
            else if (value == (byte)'\t')
            {
                text.Append('\t');
                printableBytes++;
            }
            else
            {
                invalidBytes++;
            }
        }

        var printablePercentage = data.Length == 0
            ? 0
            : (int)Math.Round(
                printableBytes * 100d / data.Length);

        var decoded = text.ToString().Trim();

        return new AsciiLineResult(
            decoded,
            IsValid:
                invalidBytes == 0 &&
                !string.IsNullOrWhiteSpace(decoded),
            PrintablePercentage: printablePercentage);
    }

    private void RegisterInvalidTextFrame(
        int byteCount,
        int printablePercentage)
    {
        _terminalStore.RecordCorrupt();
        UpdateReceiveStatistics();

        if (_invalidAsciiWarningShown)
        {
            return;
        }

        _invalidAsciiWarningShown = true;
        AppendTerminalCore(
            $"{DateTime.Now:HH:mm:ss.fff}  Invalid Bluetooth CSV record " +
            $"({byteCount} B, {printablePercentage}% ASCII). Android receives these bytes unchanged from HC-08. " +
            "Expected 8 columns ending with CR/LF: seq,t_ms,adc_raw,hx_raw,hx_net,hx_g_x1000,hx_filt_g_x1000,status. / " +
            "رکورد CSV بلوتوث معتبر نیست؛ ۸ ستون با پایان CR/LF انتظار می‌رود. " +
            "اندروید بایت‌ها را بدون تغییر از HC-08 می‌گیرد؛ سرعت UART پردازنده و HC-08 را یکسان کنید " +
            "(معمولاً 9600 با قالب 8N1) و انتهای هر رکورد CR/LF بفرستید.");
    }

    private void UpdateReceiveStatistics()
    {
        var snapshot = _terminalStore.GetSnapshot();

        ReceiveStatsLabel.Text =
            $"Packets: {snapshot.Packets:N0} • Bytes: {snapshot.Bytes:N0} • " +
            $"Valid records: {snapshot.ValidRecords:N0} • Corrupt: {snapshot.CorruptRecords:N0}";
    }

    private void RequestTerminalRender()
    {
        if (Interlocked.Exchange(ref _terminalRenderRequested, 1) != 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(100);

                RenderTerminal();
                RequestTerminalScrollToBottom();
            }
            finally
            {
                Interlocked.Exchange(ref _terminalRenderRequested, 0);
            }
        });
    }

    private void RenderTerminal()
    {
        var snapshot = _terminalStore.GetSnapshot();
        _terminalBuffer.Clear();

        foreach (var entry in snapshot.Entries.TakeLast(8))
        {
            if (_terminalBuffer.Length > 0)
            {
                _terminalBuffer.AppendLine();
            }

            _terminalBuffer.Append(entry);
        }

        TerminalLabel.Text = snapshot.Entries.Count == 0
            ? "Waiting for data... / در انتظار داده..."
            : _terminalBuffer.ToString();

        TerminalCountLabel.Text =
            $"Records / رکوردها: {snapshot.Records.Count:N0}";

        UpdateReceiveStatistics();
    }

    private void RequestTerminalScrollToBottom()
    {
        if (Interlocked.Exchange(ref _scrollRequested, 1) != 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(40);

                var bottomOffset = Math.Max(
                    0,
                    TerminalLabel.Height - TerminalScrollView.Height);

                await TerminalScrollView.ScrollToAsync(
                    0,
                    bottomOffset,
                    animated: false);
            }
            finally
            {
                Interlocked.Exchange(ref _scrollRequested, 0);
            }
        });
    }

    private void OnClearTerminalClicked(
        object? sender,
        EventArgs e)
    {
        CancelReceiveFrameFlush();
        _receiveBuffer.Clear();
        _terminalBuffer.Clear();
        _terminalStore.Clear();
        _invalidAsciiWarningShown = false;
        _lastAcceptedCsvRow = null;
        RenderTerminal();
    }

    private async void OnExportLogsClicked(
        object? sender,
        EventArgs e)
    {
        var snapshot = _terminalStore.GetSnapshot();

        if (snapshot.Records.Count == 0)
        {
            AppendTerminal(
                "No logs to export / لاگی برای خروجی وجود ندارد");
            return;
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var filePath = Path.Combine(
                FileSystem.CacheDirectory,
                $"BLE-measurements-{timestamp}.csv");

            await File.WriteAllTextAsync(
                filePath,
                _terminalStore.CreateCsvExportText(),
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            await Share.Default.RequestAsync(
                new ShareFileRequest
                {
                    Title =
                        "Save or share BLE CSV / ذخیره یا اشتراک CSV بلوتوث",
                    File = new ShareFile(
                        filePath,
                        "text/csv")
                });

            AppendTerminal(
                "Native sharing sheet opened / پنجره اشتراک‌گذاری باز شد");
        }
        catch (Exception exception)
        {
            AppendTerminal(
                $"Log export failed: {exception.Message} / خروجی لاگ ناموفق بود");
        }
    }

    private async void OnOpenLogsClicked(
        object? sender,
        EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(LogsPage));
    }

    private async Task CheckPrerequisitesOnAppearingAsync()
    {
        await Task.Delay(350);

        if (IsVisible)
        {
            await EnsureBlePrerequisitesAsync();
        }
    }

    private async Task<bool> EnsureBlePrerequisitesAsync()
    {
        if (_prerequisiteCheckInProgress)
        {
            return false;
        }

        _prerequisiteCheckInProgress = true;

        try
        {
            for (var check = 0; check < 4; check++)
            {
                var state =
                    await _bleService.GetPrerequisiteStateAsync();

                switch (state)
                {
                    case BlePrerequisiteState.Ready:
                        return true;

                    case BlePrerequisiteState.BluetoothPermissionRequired:
                    case BlePrerequisiteState.LocationPermissionRequired:
                    {
                        var grantPermissions = await DisplayAlert(
                            "Permissions required / مجوزها لازم است",
                            "Nearby devices and Location permissions are required to scan and connect to HC-08. / " +
                            "برای اسکن و اتصال به HC-08، مجوز دستگاه‌های اطراف و موقعیت مکانی لازم است.",
                            "Grant / اعطای مجوز",
                            "Cancel / انصراف");

                        if (!grantPermissions)
                        {
                            SetStatus(
                                "Status: Required permissions are missing / وضعیت: مجوزهای لازم داده نشده‌اند");
                            return false;
                        }

                        if (await _bleService.CheckAndRequestPermissionsAsync())
                        {
                            continue;
                        }

                        var openAppSettings = await DisplayAlert(
                            "Permission denied / مجوز رد شد",
                            "Permission was denied. Open App Settings and allow Nearby devices and Location. / " +
                            "مجوز رد شده است. در تنظیمات برنامه، دسترسی دستگاه‌های اطراف و موقعیت مکانی را فعال کنید.",
                            "Open Settings / باز کردن تنظیمات",
                            "Cancel / انصراف");

                        if (openAppSettings)
                        {
                            _bleService.OpenApplicationSettings();
                        }

                        return false;
                    }

                    case BlePrerequisiteState.BluetoothDisabled:
                    {
                        SetStatus(
                            "Status: Bluetooth is off / وضعیت: بلوتوث خاموش است");

                        var openBluetoothSettings = await DisplayAlert(
                            "Bluetooth is off / بلوتوث خاموش است",
                            "Turn on Bluetooth to scan, connect and receive data from HC-08. / " +
                            "برای اسکن، اتصال و دریافت داده از HC-08 بلوتوث را روشن کنید.",
                            "Open Bluetooth Settings / تنظیمات بلوتوث",
                            "Cancel / انصراف");

                        if (openBluetoothSettings)
                        {
                            _bleService.OpenBluetoothSettings();
                        }

                        return false;
                    }

                    case BlePrerequisiteState.LocationServicesDisabled:
                    {
                        SetStatus(
                            "Status: Location service is off / وضعیت: سرویس موقعیت مکانی خاموش است");

                        var openLocationSettings = await DisplayAlert(
                            "Location is off / موقعیت مکانی خاموش است",
                            "Android requires Location services for reliable BLE scanning on this device. / " +
                            "برای اسکن مطمئن BLE در این دستگاه، سرویس موقعیت مکانی را روشن کنید.",
                            "Open Location Settings / تنظیمات موقعیت",
                            "Cancel / انصراف");

                        if (openLocationSettings)
                        {
                            _bleService.OpenLocationSettings();
                        }

                        return false;
                    }
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            AppendTerminal(
                $"Prerequisite check failed: {exception.Message} / بررسی پیش‌نیازها ناموفق بود");
            return false;
        }
        finally
        {
            _prerequisiteCheckInProgress = false;
        }
    }

    public sealed class DeviceListItem : INotifyPropertyChanged
    {
        private const string KnownHc08AddressSuffix = "9c1d589554e2";

        private IDevice? _device;
        private bool _isConnected;
        private bool _isActionEnabled = true;

        public DeviceListItem(IDevice device)
        {
            _device = device;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public IDevice? Device => _device;

        public string DisplayName =>
            IsHc08
                ? "HC-08"
                : string.IsNullOrWhiteSpace(_device?.Name)
                ? "Unnamed device / دستگاه بدون نام"
                : _device.Name;

        public string DeviceId =>
            _device?.Id.ToString() ?? string.Empty;

        private bool IsHc08 =>
            string.Equals(
                _device?.Name,
                "HC-08",
                StringComparison.OrdinalIgnoreCase) ||
            DeviceId.Replace("-", string.Empty)
                .EndsWith(
                    KnownHc08AddressSuffix,
                    StringComparison.OrdinalIgnoreCase);

        public string SortKey
        {
            get
            {
                var name = _device?.Name;

                if (IsHc08)
                {
                    return "0|HC-08";
                }

                return string.IsNullOrWhiteSpace(name)
                    ? $"2|{DeviceId}"
                    : $"1|{name}";
            }
        }

        public string RssiText =>
            $"RSSI: {_device?.Rssi ?? 0} dBm";

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected == value)
                {
                    return;
                }

                _isConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionText));
            }
        }

        public bool IsActionEnabled
        {
            get => _isActionEnabled;
            set
            {
                if (_isActionEnabled == value)
                {
                    return;
                }

                _isActionEnabled = value;
                OnPropertyChanged();
            }
        }

        public string ActionText =>
            IsConnected
                ? "Disconnect / قطع اتصال"
                : "Connect / اتصال";

        public void Update(IDevice device)
        {
            _device = device;
            OnPropertyChanged(nameof(Device));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DeviceId));
            OnPropertyChanged(nameof(RssiText));
            OnPropertyChanged(nameof(SortKey));
        }

        public void RefreshActionText()
        {
            OnPropertyChanged(nameof(ActionText));
        }

        private void OnPropertyChanged(
            [CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }

    private enum ReceiveDisplayMode
    {
        Auto,
        Utf8,
        Ascii
    }

    private readonly record struct Utf8DisplayResult(
        string Text,
        bool IsExact,
        bool HasReadableText);

    private readonly record struct AsciiLineResult(
        string Text,
        bool IsValid,
        int PrintablePercentage);

    private readonly record struct BluetoothCsvRecord(
        uint Sequence,
        uint TickMilliseconds,
        ushort AdcRaw,
        int HxRaw,
        int HxNet,
        int HxGramsX1000,
        int HxFilteredGramsX1000,
        uint Status);
}
