using System.Windows;

namespace NewsDesk.App;

public partial class App : System.Windows.Application
{
    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }
}
