using System.Windows;

namespace Spit.App;

/// Closing every window never quits (Mac rule 4): the app lives in the tray until Quit Spit.
public partial class App : Application
{
}
