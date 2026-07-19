using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>Remote (M21 Phase 3, #234): a thin Avalonia skin over <c>MainWindowViewModel.Remote</c> — all state and daemon calls live in <c>Aer.Ui.Core</c>; button wiring stays with the shell (<c>MainWindow</c>), which owns the <c>TaskSession</c> this view's actions need.</summary>
public partial class RemoteView : UserControl
{
    public RemoteView() => InitializeComponent();
}
