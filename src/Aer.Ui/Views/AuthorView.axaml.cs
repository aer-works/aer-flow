using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>The Author view (M19 Phase 2, #187): the template and worker-bindings editors, re-homed behavior-preserving — editor state and file I/O live on the editor ViewModels in <c>Aer.Ui.Core</c>; Phase 4 rebuilds this into the guided New Workflow flow.</summary>
public partial class AuthorView : UserControl
{
    public AuthorView() => InitializeComponent();
}
