using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Exceptions;
using Plugin.BLE.Abstractions.Extensions;
using System.Collections.Concurrent;
using System.Text;

namespace BleSimpleApp.Services;

public sealed class BleService
{
    private const int PreferredMtu = 247;
    private const int MaxReconnectAttempts = 3;

    public static readonly Guid TargetCharacteristicUuid =
        Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb");

    public static readonly Guid KnownHc08DeviceId =
        Guid.Parse("00000000-0000-0000-0000-9c1d589554e2");

    private static readonly Guid[] KnownNotificationCharacteristicUuids =
    [
        TargetCharacteristicUuid,
        Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000ffe2-0000-1000-8000-00805f9b34fb")
    ];

    private readonly IBluetoothLE _bluetoothLe;
    private readonly IAdapter _adapter;
    private readonly ILogger<BleService> _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _gattLock = new(1, 1);
    private readonly SemaphoreSlim _permissionLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _intentionalDisconnects = new();

    private CancellationTokenSource? _reconnectCancellationTokenSource;
    private ScanSession? _activeScan;
    private IDevice? _connectedDevice;
    private ICharacteristic? _notificationCharacteristic;
    private EventHandler<CharacteristicUpdatedEventArgs>? _notificationHandler;
    private Guid _notificationCharacteristicUuid = TargetCharacteristicUuid;
    private int _bluetoothOffCleanupInProgress;
    private int _reconnectInProgress;
    private volatile bool _intentionalDisconnect;
    private volatile bool _isAppSleeping;
    private bool _resumeNotifications;

    public BleService(ILogger<BleService> logger)
    {
        _logger = logger;
        _bluetoothLe = CrossBluetoothLE.Current;
        _adapter = _bluetoothLe.Adapter;

        _bluetoothLe.StateChanged += OnBluetoothStateChanged;
        _adapter.DeviceConnectionLost += OnDeviceConnectionLost;
        _adapter.DeviceDisconnected += OnDeviceDisconnected;
    }

    public event EventHandler<BleDataReceivedEventArgs>? DataReceived;

    public event EventHandler<BleStatusEventArgs>? StatusChanged;

    public bool IsBluetoothOn => _bluetoothLe.State == BluetoothState.On;

    public bool IsConnected =>
        _connectedDevice?.State == DeviceState.Connected;

    public IDevice? ConnectedDevice => _connectedDevice;

    public async Task<BlePrerequisiteState> GetPrerequisiteStateAsync()
    {
#if ANDROID
        var bluetoothPermission =
            await Permissions.CheckStatusAsync<Permissions.Bluetooth>();

        if (bluetoothPermission != PermissionStatus.Granted)
        {
            return BlePrerequisiteState.BluetoothPermissionRequired;
        }

        var locationPermission =
            await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        if (locationPermission != PermissionStatus.Granted)
        {
            return BlePrerequisiteState.LocationPermissionRequired;
        }

        if (_bluetoothLe.State != BluetoothState.On)
        {
            return BlePrerequisiteState.BluetoothDisabled;
        }

        var locationManager =
            Android.App.Application.Context.GetSystemService(
                Android.Content.Context.LocationService)
            as Android.Locations.LocationManager;

        if (locationManager?.IsLocationEnabled != true)
        {
            return BlePrerequisiteState.LocationServicesDisabled;
        }
#else
        if (_bluetoothLe.State != BluetoothState.On)
        {
            return BlePrerequisiteState.BluetoothDisabled;
        }
#endif

        return BlePrerequisiteState.Ready;
    }

    public void OpenBluetoothSettings()
    {
#if ANDROID
        OpenAndroidSettings(Android.Provider.Settings.ActionBluetoothSettings);
#endif
    }

    public void OpenLocationSettings()
    {
#if ANDROID
        OpenAndroidSettings(
            Android.Provider.Settings.ActionLocationSourceSettings);
#endif
    }

    public void OpenApplicationSettings()
    {
        AppInfo.ShowSettingsUI();
    }

#if ANDROID
    private static void OpenAndroidSettings(string action)
    {
        var intent = new Android.Content.Intent(action);
        var activity = Platform.CurrentActivity;

        if (activity is not null)
        {
            activity.StartActivity(intent);
            return;
        }

        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
    }
#endif

    public Task<bool> CheckAndRequestPermissionsAsync()
    {
#if ANDROID
        return CheckAndRequestAndroidPermissionsAsync();
#else
        return Task.FromResult(true);
#endif
    }

#if ANDROID
    private async Task<bool> CheckAndRequestAndroidPermissionsAsync()
    {
        await _permissionLock.WaitAsync();

        try
        {
            var bluetoothStatus =
                await Permissions.CheckStatusAsync<Permissions.Bluetooth>();

            if (bluetoothStatus != PermissionStatus.Granted)
            {
                bluetoothStatus = await MainThread.InvokeOnMainThreadAsync(
                    Permissions.RequestAsync<Permissions.Bluetooth>);
            }

            if (bluetoothStatus != PermissionStatus.Granted)
            {
                EmitStatus(
                    BleConnectionState.PermissionDenied,
                    "Bluetooth permission denied / مجوز بلوتوث رد شد");
                return false;
            }

            var locationStatus =
                await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

            if (locationStatus != PermissionStatus.Granted)
            {
                locationStatus = await MainThread.InvokeOnMainThreadAsync(
                    Permissions.RequestAsync<Permissions.LocationWhenInUse>);
            }

            if (locationStatus != PermissionStatus.Granted)
            {
                EmitStatus(
                    BleConnectionState.PermissionDenied,
                    "Location permission denied / مجوز موقعیت مکانی رد شد");
                return false;
            }

            return true;
        }
        catch (PermissionException exception)
        {
            _logger.LogError(
                exception,
                "BLE permission configuration failed / پیکربندی مجوزهای بلوتوث ناموفق بود.");

            EmitStatus(
                BleConnectionState.PermissionDenied,
                "BLE permission configuration failed / پیکربندی مجوزهای بلوتوث ناموفق بود");
            return false;
        }
        finally
        {
            _permissionLock.Release();
        }
    }
#endif

    public async Task StartScanningAsync(
        Action<IDevice> onDeviceFound,
        int timeoutMs = 10_000)
    {
        ArgumentNullException.ThrowIfNull(onDeviceFound);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        if (!await CheckAndRequestPermissionsAsync())
        {
            throw new UnauthorizedAccessException(
                "Bluetooth and location permissions are required / مجوز بلوتوث و موقعیت مکانی لازم است.");
        }

        ScanSession session;
        Task scanTask;

        await _operationLock.WaitAsync();

        try
        {
            await StopScanningCoreAsync();

            if (_bluetoothLe.State != BluetoothState.On)
            {
                throw new InvalidOperationException(
                    "Bluetooth is not enabled / بلوتوث روشن نیست.");
            }

            _adapter.ScanTimeout = timeoutMs;

            EventHandler<DeviceEventArgs> handler = (_, args) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        onDeviceFound(args.Device);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(
                            exception,
                            "Device discovery callback failed / پردازش دستگاه پیدا شده ناموفق بود.");
                    }
                });
            };

            session = new ScanSession(handler);
            _activeScan = session;
            _adapter.DeviceDiscovered += session.Handler;

            try
            {
                scanTask = _adapter.StartScanningForDevicesAsync(session.Token);
            }
            catch
            {
                CleanupScanSession(session);
                throw;
            }

            EmitStatus(
                BleConnectionState.Scanning,
                "BLE scan started / اسکن بلوتوث شروع شد");
        }
        finally
        {
            _operationLock.Release();
        }

        try
        {
            await scanTask;
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            EmitStatus(
                BleConnectionState.ScanStopped,
                "BLE scan stopped / اسکن بلوتوث متوقف شد");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "BLE scanning failed / اسکن بلوتوث ناموفق بود.");

            EmitStatus(
                BleConnectionState.Error,
                $"BLE scanning failed: {exception.Message} / اسکن بلوتوث ناموفق بود");
            throw;
        }
        finally
        {
            await _operationLock.WaitAsync();

            try
            {
                CleanupScanSession(session);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        EmitStatus(
            BleConnectionState.ScanCompleted,
            "BLE scan completed / اسکن بلوتوث کامل شد");
    }

    public async Task StopScanningAsync()
    {
        await _operationLock.WaitAsync();

        try
        {
            await StopScanningCoreAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Stopping BLE scan failed / توقف اسکن بلوتوث ناموفق بود.");

            EmitStatus(
                BleConnectionState.Error,
                "Stopping BLE scan failed / توقف اسکن بلوتوث ناموفق بود");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> ConnectToDeviceAsync(
        IDevice? device,
        CancellationToken cancellationToken = default)
    {
        if (device is null)
        {
            EmitStatus(
                BleConnectionState.Error,
                "No BLE device selected / هیچ دستگاه بلوتوثی انتخاب نشده است");
            return false;
        }

        if (!await CheckAndRequestPermissionsAsync())
        {
            return false;
        }

        CancelReconnect();
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            _intentionalDisconnect = false;
            await StopScanningCoreAsync();

            if (_bluetoothLe.State != BluetoothState.On)
            {
                EmitStatus(
                    BleConnectionState.BluetoothOff,
                    "Bluetooth is off / بلوتوث خاموش است");
                return false;
            }

            if (_connectedDevice is not null)
            {
                await DisconnectPhysicalDeviceCoreAsync(
                    _connectedDevice,
                    cancellationToken);
            }

            EmitStatus(
                BleConnectionState.Connecting,
                $"Connecting to {GetDeviceName(device)} / در حال اتصال به {GetDeviceName(device)}");

            await _adapter.ConnectToDeviceAsync(
                device,
                cancellationToken: cancellationToken);

            _connectedDevice = device;
            await RequestPreferredMtuAsync(device, cancellationToken);

            var notificationsStarted =
                await StartCharacteristicNotificationsAsync(
                    TargetCharacteristicUuid,
                    cancellationToken);

            if (!notificationsStarted)
            {
                await DisconnectPhysicalDeviceCoreAsync(
                    device,
                    cancellationToken);

                EmitStatus(
                    BleConnectionState.Error,
                    "Target notification characteristic is unavailable / مشخصه اعلان موردنظر در دسترس نیست");
                return false;
            }

            EmitStatus(
                BleConnectionState.Connected,
                $"Connected to {GetDeviceName(device)} / اتصال به {GetDeviceName(device)} برقرار شد");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DeviceConnectionException exception)
        {
            _connectedDevice = null;
            _logger.LogWarning(
                exception,
                "BLE connection failed / اتصال بلوتوث ناموفق بود.");

            EmitStatus(
                BleConnectionState.Error,
                $"BLE connection failed: {exception.Message} / اتصال بلوتوث ناموفق بود");
            return false;
        }
        catch (Exception exception)
        {
            _connectedDevice = null;
            _logger.LogError(
                exception,
                "Unexpected BLE connection error / خطای پیش‌بینی‌نشده اتصال بلوتوث.");

            EmitStatus(
                BleConnectionState.Error,
                $"Connection error: {exception.Message} / خطای اتصال بلوتوث");
            return false;
        }
        finally
        {
            _intentionalDisconnect = false;
            _operationLock.Release();
        }
    }

    public async Task<bool> ConnectToKnownHc08Async(
        CancellationToken cancellationToken = default)
    {
        if (!await CheckAndRequestPermissionsAsync())
        {
            return false;
        }

        CancelReconnect();
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            _intentionalDisconnect = false;
            await StopScanningCoreAsync();

            if (_bluetoothLe.State != BluetoothState.On)
            {
                EmitStatus(
                    BleConnectionState.BluetoothOff,
                    "Bluetooth is off / بلوتوث خاموش است");
                return false;
            }

            if (_connectedDevice is not null)
            {
                await DisconnectPhysicalDeviceCoreAsync(
                    _connectedDevice,
                    cancellationToken);
            }

            EmitStatus(
                BleConnectionState.Connecting,
                "Connecting directly to HC-08 / اتصال مستقیم به HC-08");

            var device = await _adapter.ConnectToKnownDeviceAsync(
                KnownHc08DeviceId,
                cancellationToken: cancellationToken);

            _connectedDevice = device;
            await RequestPreferredMtuAsync(device, cancellationToken);

            var notificationsStarted =
                await StartCharacteristicNotificationsAsync(
                    TargetCharacteristicUuid,
                    cancellationToken);

            if (!notificationsStarted)
            {
                await DisconnectPhysicalDeviceCoreAsync(
                    device,
                    cancellationToken);

                EmitStatus(
                    BleConnectionState.Error,
                    "HC-08 notification characteristic is unavailable / مشخصه اعلان HC-08 در دسترس نیست");
                return false;
            }

            EmitStatus(
                BleConnectionState.Connected,
                "Connected directly to HC-08 / اتصال مستقیم به HC-08 برقرار شد");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DeviceConnectionException exception)
        {
            _connectedDevice = null;
            _logger.LogWarning(
                exception,
                "Direct HC-08 connection failed / اتصال مستقیم HC-08 ناموفق بود.");

            EmitStatus(
                BleConnectionState.Error,
                $"Direct HC-08 connection failed: {exception.Message} / اتصال مستقیم HC-08 ناموفق بود");
            return false;
        }
        catch (Exception exception)
        {
            _connectedDevice = null;
            _logger.LogError(
                exception,
                "Unexpected direct HC-08 connection error / خطای پیش‌بینی‌نشده اتصال مستقیم HC-08.");

            EmitStatus(
                BleConnectionState.Error,
                $"HC-08 connection error: {exception.Message} / خطای اتصال HC-08");
            return false;
        }
        finally
        {
            _intentionalDisconnect = false;
            _operationLock.Release();
        }
    }

    public async Task<bool> StartCharacteristicNotificationsAsync(
        Guid? characteristicUuid = null,
        CancellationToken cancellationToken = default)
    {
        var device = _connectedDevice;
        if (device is null || device.State != DeviceState.Connected)
        {
            EmitStatus(
                BleConnectionState.Error,
                "Notification setup requires an active connection / برای فعال‌سازی اعلان، اتصال فعال لازم است");
            return false;
        }

        var targetUuid = characteristicUuid ?? TargetCharacteristicUuid;
        _notificationCharacteristicUuid = targetUuid;

        await _gattLock.WaitAsync(cancellationToken);

        try
        {
            await StopCharacteristicNotificationsCoreAsync(
                cancellationToken,
                callNativeStop: true);

            var services = await device.GetServicesAsync(cancellationToken);
            var candidates = new List<GattCharacteristicCandidate>();

            foreach (var service in services)
            {
                var characteristics =
                    await service.GetCharacteristicsAsync(cancellationToken);

                foreach (var characteristic in characteristics)
                {
                    candidates.Add(
                        new GattCharacteristicCandidate(
                            service.Id,
                            characteristic));
                }
            }

            var profileSummary = candidates.Count == 0
                ? "(empty / خالی)"
                : string.Join(
                    "; ",
                    candidates.Select(candidate =>
                        $"{FormatUuid(candidate.ServiceUuid)}/{FormatUuid(candidate.Characteristic.Id)} " +
                        $"[R:{candidate.Characteristic.CanRead}, W:{candidate.Characteristic.CanWrite}, N:{candidate.Characteristic.CanUpdate}]"));

            EmitStatus(
                BleConnectionState.GattDiscovered,
                $"GATT profile: {profileSummary} / پروفایل GATT کشف شد");

            var selectedCandidate = candidates
                .Where(candidate => candidate.Characteristic.CanUpdate)
                .OrderBy(candidate =>
                    GetNotificationCharacteristicPriority(
                        candidate.Characteristic.Id,
                        targetUuid))
                .FirstOrDefault();

            if (selectedCandidate is null)
            {
                _logger.LogWarning(
                    "No notification characteristic was found. Preferred UUID: {PreferredUuid} / هیچ مشخصه قابل اعلان پیدا نشد. UUID ترجیحی: {PreferredUuidFa}",
                    targetUuid,
                    targetUuid);

                EmitStatus(
                    BleConnectionState.Error,
                    $"No Notify/Indicate characteristic found. GATT: {profileSummary} / هیچ مشخصه اعلان پیدا نشد");
                return false;
            }

            var targetCharacteristic = selectedCandidate.Characteristic;
            _notificationCharacteristicUuid = targetCharacteristic.Id;

            EventHandler<CharacteristicUpdatedEventArgs> handler = (_, args) =>
            {
                if (_isAppSleeping)
                {
                    return;
                }

                var bytes = args.Characteristic.Value?.ToArray() ?? [];

                EmitData(BleDataReceivedEventArgs.FromBytes(bytes));
            };

            _notificationCharacteristic = targetCharacteristic;
            _notificationHandler = handler;
            targetCharacteristic.ValueUpdated += handler;

            try
            {
                await targetCharacteristic.StartUpdatesAsync(cancellationToken);
                _resumeNotifications = true;

                EmitStatus(
                    BleConnectionState.NotificationsReady,
                    $"Notifications active: service {selectedCandidate.ServiceUuid}, characteristic {targetCharacteristic.Id} / اعلان‌ها فعال شدند");
                return true;
            }
            catch
            {
                targetCharacteristic.ValueUpdated -= handler;
                _notificationCharacteristic = null;
                _notificationHandler = null;
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Starting BLE notifications failed / فعال‌سازی اعلان‌های بلوتوث ناموفق بود.");
            return false;
        }
        finally
        {
            _gattLock.Release();
        }
    }

    public async Task StopCharacteristicNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gattLock.WaitAsync(cancellationToken);

        try
        {
            await StopCharacteristicNotificationsCoreAsync(
                cancellationToken,
                callNativeStop: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Stopping BLE notifications failed / توقف اعلان‌های بلوتوث ناموفق بود.");
        }
        finally
        {
            _gattLock.Release();
        }
    }

    public async Task<bool> DisconnectDeviceAsync(
        CancellationToken cancellationToken = default)
    {
        _intentionalDisconnect = true;
        CancelReconnect();

        var operationLockAcquired = false;

        try
        {
            await _operationLock.WaitAsync(cancellationToken);
            operationLockAcquired = true;

            await StopCharacteristicNotificationsAsync(cancellationToken);

            if (_connectedDevice is null)
            {
                EmitStatus(
                    BleConnectionState.Disconnected,
                    "BLE device is already disconnected / دستگاه بلوتوث از قبل قطع است");
                return true;
            }

            var device = _connectedDevice;
            await DisconnectPhysicalDeviceCoreAsync(device, cancellationToken);

            EmitStatus(
                BleConnectionState.Disconnected,
                $"Disconnected from {GetDeviceName(device)} / اتصال از {GetDeviceName(device)} قطع شد");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Disconnecting BLE device failed / قطع اتصال دستگاه بلوتوث ناموفق بود.");

            EmitStatus(
                BleConnectionState.Error,
                $"Disconnect failed: {exception.Message} / قطع اتصال ناموفق بود");
            return false;
        }
        finally
        {
            _intentionalDisconnect = false;

            if (operationLockAcquired)
            {
                _operationLock.Release();
            }
        }
    }

    public async Task HandleAppSleepAsync()
    {
        _isAppSleeping = true;
        await StopScanningAsync();

        if (_notificationCharacteristic is not null)
        {
            _resumeNotifications = true;
            await StopCharacteristicNotificationsAsync();
        }

        EmitStatus(
            BleConnectionState.AppSleeping,
            "App entered background; notifications paused safely / برنامه به پس‌زمینه رفت؛ اعلان‌ها با ایمنی متوقف شدند");
    }

    public async Task HandleAppResumeAsync()
    {
        _isAppSleeping = false;

        if (_bluetoothLe.State != BluetoothState.On)
        {
            EmitStatus(
                BleConnectionState.BluetoothOff,
                "Bluetooth turned off globally! / بلوتوث سیستم‌عامل خاموش شد!");
            return;
        }

        if (_connectedDevice?.State == DeviceState.Connected)
        {
            if (_resumeNotifications && _notificationCharacteristic is null)
            {
                var notificationsRestored =
                    await StartCharacteristicNotificationsAsync(
                        _notificationCharacteristicUuid);

                EmitStatus(
                    BleConnectionState.AppResumedConnected,
                    notificationsRestored
                        ? "App resumed; BLE connection and notifications are active / برنامه بازگشت؛ اتصال و اعلان‌های بلوتوث فعال هستند"
                        : "App resumed; BLE connected but notifications could not restart / برنامه بازگشت؛ بلوتوث متصل است اما اعلان‌ها فعال نشدند");
            }
            else
            {
                EmitStatus(
                    BleConnectionState.AppResumedConnected,
                    "App resumed; BLE device is connected / برنامه بازگشت؛ دستگاه بلوتوث متصل است");
            }

            return;
        }

        if (_connectedDevice is not null)
        {
            QueueReconnect(_connectedDevice);
        }

        EmitStatus(
            BleConnectionState.AppResumedDisconnected,
            "App resumed; BLE device is disconnected / برنامه بازگشت؛ دستگاه بلوتوث قطع است");
    }

    private async Task RequestPreferredMtuAsync(
        IDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            var negotiatedMtu = await device.RequestMtuAsync(
                PreferredMtu,
                cancellationToken);

            EmitStatus(
                BleConnectionState.MtuNegotiated,
                $"MTU negotiated: {negotiatedMtu} / اندازه MTU توافق‌شده: {negotiatedMtu}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "MTU request was rejected; using default MTU / درخواست MTU رد شد؛ مقدار پیش‌فرض استفاده می‌شود.");

            EmitStatus(
                BleConnectionState.MtuFallback,
                "MTU request rejected; using default / درخواست MTU رد شد؛ استفاده از مقدار پیش‌فرض");
        }
    }

    private void OnDeviceConnectionLost(
        object? sender,
        DeviceErrorEventArgs args)
    {
        if (_intentionalDisconnects.TryRemove(args.Device.Id, out _))
        {
            return;
        }

        QueueReconnect(args.Device);
    }

    private void OnDeviceDisconnected(
        object? sender,
        DeviceEventArgs args)
    {
        if (_intentionalDisconnects.TryRemove(args.Device.Id, out _))
        {
            return;
        }

        QueueReconnect(args.Device);
    }

    private void QueueReconnect(IDevice device)
    {
        var connectedDevice = _connectedDevice;

        if (_intentionalDisconnect ||
            _isAppSleeping ||
            _bluetoothLe.State != BluetoothState.On ||
            connectedDevice is null ||
            connectedDevice.Id != device.Id ||
            Interlocked.CompareExchange(
                ref _reconnectInProgress,
                1,
                0) != 0)
        {
            return;
        }

        _ = ReconnectDeviceAsync(device);
    }

    private async Task ReconnectDeviceAsync(IDevice device)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var previousCancellationTokenSource = Interlocked.Exchange(
            ref _reconnectCancellationTokenSource,
            cancellationTokenSource);

        previousCancellationTokenSource?.Cancel();

        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            EmitStatus(
                BleConnectionState.ConnectionLost,
                "Connection lost. Reconnecting... / اتصال قطع شد. در حال تلاش مجدد...");

            await StopCharacteristicNotificationsCoreWithLockAsync(
                cancellationToken,
                callNativeStop: false);

            for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EmitStatus(
                    BleConnectionState.Reconnecting,
                    $"Reconnect attempt {attempt} of {MaxReconnectAttempts} / تلاش اتصال مجدد {attempt} از {MaxReconnectAttempts}");

                try
                {
                    await _operationLock.WaitAsync(cancellationToken);

                    try
                    {
                        if (_intentionalDisconnect ||
                            _bluetoothLe.State != BluetoothState.On)
                        {
                            return;
                        }

                        await _adapter.ConnectToDeviceAsync(
                            device,
                            cancellationToken: cancellationToken);

                        _connectedDevice = device;
                        await RequestPreferredMtuAsync(
                            device,
                            cancellationToken);

                        var notificationsStarted =
                            await StartCharacteristicNotificationsAsync(
                                _notificationCharacteristicUuid,
                                cancellationToken);

                        if (!notificationsStarted)
                        {
                            throw new InvalidOperationException(
                                "Notification characteristic could not be restored.");
                        }
                    }
                    finally
                    {
                        _operationLock.Release();
                    }

                    EmitStatus(
                        BleConnectionState.Reconnected,
                        "Reconnected. Notifications restored / اتصال مجدد برقرار شد. اعلان‌ها بازیابی شدند");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Reconnect attempt {Attempt} failed / تلاش اتصال مجدد {AttemptFa} ناموفق بود.",
                        attempt,
                        attempt);

                    EmitStatus(
                        BleConnectionState.Reconnecting,
                        $"Reconnect attempt {attempt} failed / تلاش اتصال مجدد {attempt} ناموفق بود");

                    if (attempt < MaxReconnectAttempts)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(2),
                            cancellationToken);
                    }
                }
            }

            _connectedDevice = null;
            EmitStatus(
                BleConnectionState.ReconnectFailed,
                "Reconnect failed after 3 attempts / اتصال مجدد پس از ۳ تلاش ناموفق بود");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _connectedDevice = null;
            _logger.LogError(
                exception,
                "Automatic reconnect failed / اتصال مجدد خودکار ناموفق بود.");

            EmitStatus(
                BleConnectionState.ReconnectFailed,
                "Automatic reconnect failed / اتصال مجدد خودکار ناموفق بود");
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _reconnectCancellationTokenSource,
                null,
                cancellationTokenSource);

            cancellationTokenSource.Dispose();
            Interlocked.Exchange(ref _reconnectInProgress, 0);
        }
    }

    private void OnBluetoothStateChanged(
        object? sender,
        BluetoothStateChangedArgs args)
    {
        if ((args.NewState is BluetoothState.Off or BluetoothState.Unavailable) &&
            Interlocked.CompareExchange(
                ref _bluetoothOffCleanupInProgress,
                1,
                0) == 0)
        {
            _ = HandleBluetoothTurnedOffAsync();
        }
    }

    private async Task HandleBluetoothTurnedOffAsync()
    {
        _intentionalDisconnect = true;
        CancelReconnect();

        await _operationLock.WaitAsync();

        try
        {
            await StopScanningCoreAsync();
            await StopCharacteristicNotificationsCoreWithLockAsync(
                CancellationToken.None,
                callNativeStop: false);

            var device = _connectedDevice;
            _connectedDevice = null;
            _resumeNotifications = false;

            if (device is not null)
            {
                _intentionalDisconnects.TryAdd(device.Id, 0);

                try
                {
                    await _adapter.DisconnectDeviceAsync(device);
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(
                        exception,
                        "Native disconnect was unavailable after Bluetooth turned off / پس از خاموش شدن بلوتوث، قطع اتصال سیستمی در دسترس نبود.");
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Bluetooth shutdown cleanup failed / پاک‌سازی پس از خاموش شدن بلوتوث ناموفق بود.");
        }
        finally
        {
            _intentionalDisconnect = false;
            Interlocked.Exchange(ref _bluetoothOffCleanupInProgress, 0);
            _operationLock.Release();

            EmitStatus(
                BleConnectionState.BluetoothOff,
                "Bluetooth turned off globally! / بلوتوث سیستم‌عامل خاموش شد!");
        }
    }

    private async Task DisconnectPhysicalDeviceCoreAsync(
        IDevice device,
        CancellationToken cancellationToken)
    {
        _intentionalDisconnects.TryAdd(device.Id, 0);
        _intentionalDisconnect = true;

        try
        {
            await StopCharacteristicNotificationsCoreWithLockAsync(
                cancellationToken,
                callNativeStop: true);

            await _adapter.DisconnectDeviceAsync(
                device,
                cancellationToken);
        }
        finally
        {
            if (_connectedDevice?.Id == device.Id)
            {
                _connectedDevice = null;
            }

            _resumeNotifications = false;
        }
    }

    private async Task StopCharacteristicNotificationsCoreWithLockAsync(
        CancellationToken cancellationToken,
        bool callNativeStop)
    {
        await _gattLock.WaitAsync(cancellationToken);

        try
        {
            await StopCharacteristicNotificationsCoreAsync(
                cancellationToken,
                callNativeStop);
        }
        finally
        {
            _gattLock.Release();
        }
    }

    private async Task StopCharacteristicNotificationsCoreAsync(
        CancellationToken cancellationToken,
        bool callNativeStop)
    {
        var characteristic = _notificationCharacteristic;
        var handler = _notificationHandler;

        _notificationCharacteristic = null;
        _notificationHandler = null;

        if (characteristic is null)
        {
            return;
        }

        if (handler is not null)
        {
            characteristic.ValueUpdated -= handler;
        }

        if (callNativeStop)
        {
            try
            {
                await characteristic.StopUpdatesAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Native notification stop failed / توقف سیستمی اعلان‌ها ناموفق بود.");
            }
        }
    }

    private async Task StopScanningCoreAsync()
    {
        var session = _activeScan;
        _activeScan = null;

        if (session is not null)
        {
            _adapter.DeviceDiscovered -= session.Handler;
            session.Cancel();
        }

        if (_adapter.IsScanning)
        {
            await _adapter.StopScanningForDevicesAsync();
        }
    }

    private void CleanupScanSession(ScanSession session)
    {
        _adapter.DeviceDiscovered -= session.Handler;

        if (ReferenceEquals(_activeScan, session))
        {
            _activeScan = null;
        }

        session.Dispose();
    }

    private void CancelReconnect()
    {
        Volatile.Read(ref _reconnectCancellationTokenSource)?.Cancel();
    }

    private void EmitData(BleDataReceivedEventArgs data)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                DataReceived?.Invoke(this, data);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "BLE data event failed / رویداد داده بلوتوث ناموفق بود.");
            }
        });
    }

    private void EmitStatus(
        BleConnectionState state,
        string message)
    {
        if (state is BleConnectionState.Error or
            BleConnectionState.BluetoothOff or
            BleConnectionState.ConnectionLost or
            BleConnectionState.ReconnectFailed)
        {
            _logger.LogWarning("{Message}", message);
        }
        else
        {
            _logger.LogInformation("{Message}", message);
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                StatusChanged?.Invoke(
                    this,
                    new BleStatusEventArgs(state, message));
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "BLE status event failed / رویداد وضعیت بلوتوث ناموفق بود.");
            }
        });
    }

    private static string GetDeviceName(IDevice device)
    {
        return string.IsNullOrWhiteSpace(device.Name)
            ? device.Id.ToString()
            : device.Name;
    }

    private static int GetNotificationCharacteristicPriority(
        Guid characteristicUuid,
        Guid preferredUuid)
    {
        if (characteristicUuid == preferredUuid)
        {
            return 0;
        }

        var knownIndex = Array.IndexOf(
            KnownNotificationCharacteristicUuids,
            characteristicUuid);

        return knownIndex >= 0
            ? knownIndex + 1
            : 100;
    }

    private static string FormatUuid(Guid uuid)
    {
        var value = uuid.ToString();
        const string bluetoothBaseSuffix =
            "-0000-1000-8000-00805f9b34fb";

        return value.EndsWith(
            bluetoothBaseSuffix,
            StringComparison.OrdinalIgnoreCase)
                ? value[4..8].ToUpperInvariant()
                : value;
    }

    private sealed record GattCharacteristicCandidate(
        Guid ServiceUuid,
        ICharacteristic Characteristic);

    private sealed class ScanSession : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private int _disposed;

        public ScanSession(EventHandler<DeviceEventArgs> handler)
        {
            Handler = handler;
        }

        public EventHandler<DeviceEventArgs> Handler { get; }

        public CancellationToken Token => _cancellationTokenSource.Token;

        public bool IsCancellationRequested =>
            _cancellationTokenSource.IsCancellationRequested;

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellationTokenSource.Dispose();
            }
        }
    }
}

public sealed class BleDataReceivedEventArgs : EventArgs
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private BleDataReceivedEventArgs(
        byte[] data,
        string text,
        string ascii)
    {
        Data = data;
        Text = text;
        Ascii = ascii;
    }

    public byte[] Data { get; }

    public string Text { get; }

    public string Ascii { get; }

    public string DisplayValue
    {
        get
        {
            const string source = "[BLE] / [بلوتوث]";

            return string.IsNullOrEmpty(Text)
                ? $"{source}{Environment.NewLine}ASCII: {Ascii}"
                : $"{source}{Environment.NewLine}Text / متن: {Text}{Environment.NewLine}ASCII: {Ascii}";
        }
    }

    public static BleDataReceivedEventArgs FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var safeCopy = data.ToArray();
        var asciiBuilder = new StringBuilder(safeCopy.Length);

        foreach (var value in safeCopy)
        {
            asciiBuilder.Append(
                value is >= 0x20 and <= 0x7E
                    ? (char)value
                    : '.');
        }

        var ascii = asciiBuilder.Length == 0
            ? "(empty / خالی)"
            : asciiBuilder.ToString();

        var text = string.Empty;

        try
        {
            var decoded = StrictUtf8
                .GetString(safeCopy)
                .TrimEnd('\0', '\r', '\n');

            if (decoded.Length > 0 &&
                decoded.All(character =>
                    !char.IsControl(character) ||
                    character is '\r' or '\n' or '\t'))
            {
                text = decoded;
            }
        }
        catch (DecoderFallbackException)
        {
            // Non-UTF-8 payloads remain available as ASCII.
        }

        return new BleDataReceivedEventArgs(
            safeCopy,
            text,
            ascii);
    }
}

public enum BleConnectionState
{
    Ready,
    Scanning,
    ScanStopped,
    ScanCompleted,
    Connecting,
    Connected,
    Disconnected,
    ConnectionLost,
    Reconnecting,
    Reconnected,
    ReconnectFailed,
    BluetoothOff,
    PermissionDenied,
    MtuNegotiated,
    MtuFallback,
    GattDiscovered,
    NotificationsReady,
    AppSleeping,
    AppResumedConnected,
    AppResumedDisconnected,
    Error
}

public enum BlePrerequisiteState
{
    Ready,
    BluetoothPermissionRequired,
    LocationPermissionRequired,
    BluetoothDisabled,
    LocationServicesDisabled
}

public sealed class BleStatusEventArgs : EventArgs
{
    public BleStatusEventArgs(
        BleConnectionState state,
        string message)
    {
        State = state;
        Message = message;
    }

    public BleConnectionState State { get; }

    public string Message { get; }
}
