namespace FGOPatcher.Maui;
// App.xaml.cs
public partial class App : Application
{
    public App(IServiceProvider sp)
    {
        InitializeComponent();
        MainPage = new NavigationPage(sp.GetRequiredService<MainPage>());
    }
}