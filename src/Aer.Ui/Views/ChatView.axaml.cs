using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>Chat (M24 Phase 1, #262): a thin Avalonia skin over <c>MainWindowViewModel.Chat</c> — all state and daemon calls live in <c>Aer.Ui.Core</c>; button wiring stays with the shell (<c>MainWindow</c>), which owns the <c>TaskSession</c> this view's actions need.</summary>
public partial class ChatView : UserControl
{
    public ChatView() => InitializeComponent();
}
