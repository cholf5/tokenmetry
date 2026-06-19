namespace CCUsage.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private string _title = "AI Coding Cost Monitor";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public DashboardViewModel Dashboard { get; }
    public ReportsViewModel Reports { get; }
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel(DashboardViewModel dashboard, ReportsViewModel reports, SettingsViewModel settings)
    {
        Dashboard = dashboard;
        Reports = reports;
        Settings = settings;
    }
}
