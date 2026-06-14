namespace BleSimpleApp;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(LogsPage), typeof(LogsPage));
	}
}
