using BleSimpleApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Text;

namespace BleSimpleApp;

public partial class LogsPage : ContentPage
{
    private readonly BleService _bleService;
    private readonly TerminalLogStore _terminalStore;

    private bool _followLatest = true;
    private bool _isSubscribed;
    private int _renderRequested;
    private int _selectedRecordIndex = -1;
    private uint? _selectedRecordSequence;

    public LogsPage()
    {
        InitializeComponent();

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException(
                "Application services are unavailable / سرویس‌های برنامه در دسترس نیستند.");

        _bleService = services.GetRequiredService<BleService>();
        _terminalStore =
            services.GetRequiredService<TerminalLogStore>();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (!_isSubscribed)
        {
            _terminalStore.Changed += OnTerminalStoreChanged;
            _bleService.StatusChanged += OnBleStatusChanged;
            _isSubscribed = true;
        }

        _followLatest = true;
        _selectedRecordIndex = -1;
        _selectedRecordSequence = null;
        UpdateConnectionStatus();
        RenderSelectedRecord();
    }

    protected override void OnDisappearing()
    {
        if (_isSubscribed)
        {
            _terminalStore.Changed -= OnTerminalStoreChanged;
            _bleService.StatusChanged -= OnBleStatusChanged;
            _isSubscribed = false;
        }

        base.OnDisappearing();
    }

    private void OnTerminalStoreChanged(
        object? sender,
        EventArgs e)
    {
        RequestRender();
    }

    private void OnBleStatusChanged(
        object? sender,
        BleStatusEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ConnectionLabel.Text =
                $"Status / وضعیت: {e.Message}";
        });
    }

    private void UpdateConnectionStatus()
    {
        ConnectionLabel.Text = _bleService.IsConnected
            ? "Status: Connected and receiving / وضعیت: متصل و در حال دریافت"
            : "Status: Disconnected / وضعیت: قطع اتصال";
    }

    private void RequestRender()
    {
        if (Interlocked.Exchange(ref _renderRequested, 1) != 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(80);
            Interlocked.Exchange(ref _renderRequested, 0);
            RenderSelectedRecord();
        });
    }

    private void RenderSelectedRecord()
    {
        var snapshot = _terminalStore.GetSnapshot();
        var recordCount = snapshot.Records.Count;

        EntryCountLabel.Text =
            $"Records / رکوردها: {recordCount:N0}";

        StatisticsLabel.Text =
            $"Packets: {snapshot.Packets:N0} • Bytes: {snapshot.Bytes:N0} • " +
            $"Valid: {snapshot.ValidRecords:N0} • Corrupt: {snapshot.CorruptRecords:N0}";

        if (recordCount == 0)
        {
            _selectedRecordIndex = -1;
            _selectedRecordSequence = null;
            _followLatest = true;
            AdcValueLabel.Text = "--";
            WeightValueLabel.Text = "--";
            RawWeightLabel.Text = "Raw / خام: --";
            RecordPositionLabel.Text = "No record / بدون رکورد";
            RecordTimeLabel.Text = "--:--:--";
            RecordDetailsLabel.Text =
                "Waiting for a complete CSV record... / در انتظار یک رکورد کامل CSV...";
            LiveModeLabel.Text = "LIVE / زنده";
            PreviousButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            LatestButton.IsEnabled = false;
            return;
        }

        if (_followLatest)
        {
            _selectedRecordIndex = recordCount - 1;
            _selectedRecordSequence =
                snapshot.Records[_selectedRecordIndex].Sequence;
        }
        else if (_selectedRecordSequence.HasValue)
        {
            _selectedRecordIndex = FindRecordIndex(
                snapshot.Records,
                _selectedRecordSequence.Value);

            if (_selectedRecordIndex < 0)
            {
                _selectedRecordIndex = 0;
                _selectedRecordSequence =
                    snapshot.Records[0].Sequence;
            }
        }
        else
        {
            _selectedRecordIndex = Math.Clamp(
                _selectedRecordIndex,
                0,
                recordCount - 1);
            _selectedRecordSequence =
                snapshot.Records[_selectedRecordIndex].Sequence;
        }

        var record = snapshot.Records[_selectedRecordIndex];
        AdcValueLabel.Text = record.AdcRaw.ToString("N0");
        WeightValueLabel.Text = record.WeightText;
        RawWeightLabel.Text =
            $"Raw / خام: {record.RawWeightText}";
        RecordPositionLabel.Text =
            $"Record {_selectedRecordIndex + 1:N0} of {recordCount:N0} / رکورد {_selectedRecordIndex + 1:N0} از {recordCount:N0}";
        RecordTimeLabel.Text =
            record.ReceivedAt.ToString("yyyy-MM-dd  HH:mm:ss.fff");
        RecordDetailsLabel.Text = record.FullDetails;

        PreviousButton.IsEnabled = _selectedRecordIndex > 0;
        NextButton.IsEnabled = _selectedRecordIndex < recordCount - 1;
        LatestButton.IsEnabled = !_followLatest;
        LiveModeLabel.Text = _followLatest
            ? "LIVE / زنده"
            : "HISTORY / تاریخچه";
        LiveModeLabel.TextColor = _followLatest
            ? Color.FromArgb("#37C978")
            : Color.FromArgb("#F2B84B");

        _ = RecordScrollView.ScrollToAsync(
            0,
            0,
            animated: false);
    }

    private void OnPreviousClicked(
        object? sender,
        EventArgs e)
    {
        var records = _terminalStore.GetSnapshot().Records;
        var currentIndex = _followLatest
            ? records.Count - 1
            : _selectedRecordSequence.HasValue
                ? FindRecordIndex(
                    records,
                    _selectedRecordSequence.Value)
                : _selectedRecordIndex;

        if (currentIndex <= 0)
        {
            return;
        }

        _followLatest = false;
        _selectedRecordIndex = currentIndex - 1;
        _selectedRecordSequence =
            records[_selectedRecordIndex].Sequence;
        RenderSelectedRecord();
    }

    private void OnNextClicked(
        object? sender,
        EventArgs e)
    {
        var records = _terminalStore.GetSnapshot().Records;
        var currentIndex = _followLatest
            ? records.Count - 1
            : _selectedRecordSequence.HasValue
                ? FindRecordIndex(
                    records,
                    _selectedRecordSequence.Value)
                : _selectedRecordIndex;

        if (currentIndex < 0 ||
            currentIndex >= records.Count - 1)
        {
            return;
        }

        _selectedRecordIndex = currentIndex + 1;
        _followLatest =
            _selectedRecordIndex == records.Count - 1;
        _selectedRecordSequence =
            records[_selectedRecordIndex].Sequence;
        RenderSelectedRecord();
    }

    private void OnLatestClicked(
        object? sender,
        EventArgs e)
    {
        _followLatest = true;
        _selectedRecordIndex = -1;
        _selectedRecordSequence = null;
        RenderSelectedRecord();
    }

    private async void OnClearClicked(
        object? sender,
        EventArgs e)
    {
        var confirmed = await DisplayAlert(
            "Clear records / پاک کردن رکوردها",
            "Delete all received records and counters? / همه رکوردها و شمارنده‌ها پاک شوند؟",
            "Clear / پاک کردن",
            "Cancel / انصراف");

        if (!confirmed)
        {
            return;
        }

        _terminalStore.Clear();
        _followLatest = true;
        _selectedRecordIndex = -1;
        _selectedRecordSequence = null;
        RenderSelectedRecord();
    }

    private static int FindRecordIndex(
        IReadOnlyList<BleLogRecord> records,
        uint sequence)
    {
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index].Sequence == sequence)
            {
                return index;
            }
        }

        return -1;
    }

    private async void OnExportClicked(
        object? sender,
        EventArgs e)
    {
        var snapshot = _terminalStore.GetSnapshot();

        if (snapshot.Records.Count == 0)
        {
            await DisplayAlert(
                "No records / بدون رکورد",
                "No measurement records are available to export. / رکورد اندازه‌گیری برای خروجی وجود ندارد.",
                "OK / تأیید");
            return;
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var fileName = $"BLE-measurements-{timestamp}.csv";
            var filePath = Path.Combine(
                FileSystem.CacheDirectory,
                fileName);

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
        }
        catch (Exception exception)
        {
            await DisplayAlert(
                "Export failed / خروجی ناموفق",
                $"Could not create the CSV file: {exception.Message} / فایل CSV ساخته نشد.",
                "OK / تأیید");
        }
    }
}
