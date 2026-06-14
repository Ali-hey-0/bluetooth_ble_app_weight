using System.Globalization;
using System.Text;

namespace BleSimpleApp.Services;

public sealed class TerminalLogStore
{
    public const int MaxEntries = 500;

    public const string BluetoothCsvHeader =
        "seq,t_ms,adc_raw,hx_raw,hx_net,hx_g_x1000,hx_filt_g_x1000,status";

    private readonly object _syncRoot = new();
    private readonly List<string> _entries = [];
    private readonly List<BleLogRecord> _records = [];

    private long _bytes;
    private long _corruptRecords;
    private long _packets;
    private long _validRecords;

    public event EventHandler? Changed;

    public TerminalLogSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return new TerminalLogSnapshot(
                _entries.ToArray(),
                _records.ToArray(),
                _packets,
                _bytes,
                _validRecords,
                _corruptRecords);
        }
    }

    public void Append(string entry)
    {
        lock (_syncRoot)
        {
            AddCapped(_entries, entry);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordPacket(int byteCount)
    {
        lock (_syncRoot)
        {
            _packets++;
            _bytes += byteCount;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordValidCsv(BleLogRecord record)
    {
        lock (_syncRoot)
        {
            _validRecords++;
            AddCapped(_records, record);
            AddCapped(_entries, record.FormattedText);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordCorrupt()
    {
        lock (_syncRoot)
        {
            _corruptRecords++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
            _records.Clear();
            _packets = 0;
            _bytes = 0;
            _validRecords = 0;
            _corruptRecords = 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string CreateCsvExportText()
    {
        var snapshot = GetSnapshot();
        var builder = new StringBuilder()
            .AppendLine(BluetoothCsvHeader);

        foreach (var record in snapshot.Records)
        {
            builder.AppendLine(record.RawCsv);
        }

        return builder.ToString();
    }

    public string CreateExportText(Guid targetUuid)
    {
        var snapshot = GetSnapshot();
        var timestamp = DateTime.Now;
        var builder = new StringBuilder()
            .AppendLine("BLE Manager Log Export / خروجی لاگ مدیریت بلوتوث")
            .AppendLine(
                $"Exported: {timestamp:yyyy-MM-dd HH:mm:ss} / زمان خروجی: {timestamp:yyyy-MM-dd HH:mm:ss}")
            .AppendLine(
                $"Records: {snapshot.Records.Count} / تعداد رکوردها: {snapshot.Records.Count}")
            .AppendLine($"Target UUID: {targetUuid}")
            .AppendLine()
            .AppendLine(BluetoothCsvHeader);

        foreach (var record in snapshot.Records)
        {
            builder.AppendLine(record.RawCsv);
        }

        return builder
            .AppendLine()
            .AppendLine(new string('-', 72))
            .AppendLine("Application Terminal / ترمینال برنامه")
            .AppendLine(
                string.Join(
                    $"{Environment.NewLine}{Environment.NewLine}",
                    snapshot.Entries))
            .ToString();
    }

    private static void AddCapped<T>(
        List<T> target,
        T value)
    {
        target.Add(value);

        while (target.Count > MaxEntries)
        {
            target.RemoveAt(0);
        }
    }
}

public sealed record BleLogRecord(
    DateTime ReceivedAt,
    string RawCsv,
    string FormattedText,
    uint Sequence,
    uint TickMilliseconds,
    ushort AdcRaw,
    int HxRaw,
    int HxNet,
    int HxGramsX1000,
    int HxFilteredGramsX1000,
    uint Status)
{
    public string WeightText =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{HxFilteredGramsX1000 / 1000m:0.###} g");

    public string RawWeightText =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{HxGramsX1000 / 1000m:0.###} g");

    public string FullDetails =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Received / دریافت: {ReceivedAt:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
            $"Sequence / شماره: {Sequence}{Environment.NewLine}" +
            $"Device time / زمان دستگاه: {TickMilliseconds} ms{Environment.NewLine}" +
            $"ADC raw: {AdcRaw}{Environment.NewLine}" +
            $"HX raw: {HxRaw}{Environment.NewLine}" +
            $"HX net: {HxNet}{Environment.NewLine}" +
            $"Weight / وزن: {RawWeightText}{Environment.NewLine}" +
            $"Filtered weight / وزن فیلترشده: {WeightText}{Environment.NewLine}" +
            $"Status / وضعیت: 0x{Status:X8}{Environment.NewLine}{Environment.NewLine}" +
            $"Raw CSV / CSV خام:{Environment.NewLine}{RawCsv}");
}

public sealed record TerminalLogSnapshot(
    IReadOnlyList<string> Entries,
    IReadOnlyList<BleLogRecord> Records,
    long Packets,
    long Bytes,
    long ValidRecords,
    long CorruptRecords);
