using Aer.Ui;
using Avalonia.Input;

namespace Aer.Ui.Tests;

/// <summary>
/// The composer's send-vs-newline rule, whose design citation lives on <see cref="MainWindow.IsSendKeystroke"/>:
/// a bare Enter sends, anything else is a newline. Pins that pure decision without standing up a window.
/// </summary>
public class ChatComposerKeystrokeTests
{
    [Fact]
    public void A_bare_Enter_sends()
        => Assert.True(MainWindow.IsSendKeystroke(Key.Enter, KeyModifiers.None));

    [Fact]
    public void Shift_Enter_inserts_a_newline_rather_than_sending()
        => Assert.False(MainWindow.IsSendKeystroke(Key.Enter, KeyModifiers.Shift));

    [Theory]
    [InlineData(Key.Enter, KeyModifiers.Control)]
    [InlineData(Key.Enter, KeyModifiers.Alt)]
    [InlineData(Key.Enter, KeyModifiers.Meta)]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.Tab, KeyModifiers.None)]
    public void Only_an_unmodified_Enter_sends(Key key, KeyModifiers modifiers)
        => Assert.False(MainWindow.IsSendKeystroke(key, modifiers));
}
