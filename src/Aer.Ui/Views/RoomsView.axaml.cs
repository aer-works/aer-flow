using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>Rooms (M24 Phase 5, #278): a thin Avalonia skin over <c>MainWindowViewModel.Rooms</c>; wired to the shell under the same contract <c>ChatView</c> documents.</summary>
public partial class RoomsView : UserControl
{
    public RoomsView() => InitializeComponent();
}
