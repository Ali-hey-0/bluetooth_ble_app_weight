using BleSimpleApp.Services;

namespace BleSimpleApp;

public partial class App : Application
{
    private readonly BleService _bleService;

    public App(BleService bleService)
    {
        InitializeComponent();
        _bleService = bleService;
    }

    protected override Window CreateWindow(
        IActivationState? activationState)
    {
        var window = new Window(new AppShell());

        window.Stopped += OnSleep;
        window.Resumed += OnResume;
        window.Destroying += OnDestroying;

        return window;
    }

    private async void OnSleep(
        object? sender,
        EventArgs e)
    {
        try
        {
            await _bleService.HandleAppSleepAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"BLE sleep handling failed / مدیریت پس‌زمینه بلوتوث ناموفق بود: {exception}");
        }
    }

    private async void OnResume(
        object? sender,
        EventArgs e)
    {
        try
        {
            await _bleService.HandleAppResumeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"BLE resume handling failed / مدیریت بازگشت بلوتوث ناموفق بود: {exception}");
        }
    }

    private void OnDestroying(
        object? sender,
        EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Stopped -= OnSleep;
        window.Resumed -= OnResume;
        window.Destroying -= OnDestroying;
    }
}
