using System.Windows;

namespace Spit.App;

/// `Theme.xaml` with a code-behind class, so windows construct it directly instead of resolving a
/// pack URI (which needs an `Application` to exist first).
public partial class ThemeDictionary : ResourceDictionary
{
    public ThemeDictionary()
    {
        InitializeComponent();
    }
}
