using System.Windows.Controls;
using CloudSSD.Games;

namespace CloudSSD.Desktop.Views;
public partial class CompletionActionPicker : UserControl
{
    public CompletionActionPicker() => InitializeComponent();
    public CompletionAction Action => (CompletionAction)Math.Max(0, Choice.SelectedIndex);
}
