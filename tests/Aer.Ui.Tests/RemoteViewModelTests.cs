using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// Issue #384 (eval B-03): plain unit-test coverage, no Avalonia headless session or live daemon
/// needed, for the three pairing-presentation fixes in <see cref="RemoteViewModel"/> — advertising
/// the LAN address that actually pairs instead of the unreachable tailnet one, showing the
/// "traffic isn't encrypted" warning exactly when remote access is on, and gating the pairing
/// block on <see cref="RemoteViewModel.IsRemoteEnabled"/> rather than host availability alone.
/// </summary>
public class RemoteViewModelTests
{
    [Fact]
    public void EffectivePairingHost_prefers_the_LAN_address_over_the_tailnet_address_when_both_exist()
    {
        var viewModel = new RemoteViewModel
        {
            Host = "192.168.1.72:5000",
            Port = 5000,
            SidecarTailscaleIp = "100.69.70.109",
        };

        Assert.Equal("192.168.1.72:5000", viewModel.EffectivePairingHost);
        Assert.False(viewModel.IsPairingOverTailnet);
    }

    [Fact]
    public void EffectivePairingHost_falls_back_to_the_tailnet_address_when_there_is_no_LAN_address()
    {
        var viewModel = new RemoteViewModel
        {
            Host = null,
            Port = 5000,
            SidecarTailscaleIp = "100.69.70.109",
        };

        Assert.Equal("100.69.70.109:5000", viewModel.EffectivePairingHost);
        Assert.True(viewModel.IsPairingOverTailnet);
    }

    [Fact]
    public void ShowLanEncryptionWarning_tracks_IsRemoteEnabled_even_while_pairing_over_the_tailnet()
    {
        var viewModel = new RemoteViewModel { IsRemoteEnabled = true, Host = null, Port = 5000, SidecarTailscaleIp = "100.69.70.109" };

        Assert.True(viewModel.IsPairingOverTailnet);
        Assert.True(viewModel.ShowLanEncryptionWarning);

        viewModel.IsRemoteEnabled = false;

        Assert.False(viewModel.ShowLanEncryptionWarning);
    }

    [Fact]
    public void ShowPairingBlock_is_false_while_remote_is_off_even_if_a_host_is_already_populated()
    {
        var viewModel = new RemoteViewModel { IsRemoteEnabled = false, Host = "192.168.1.72:5000", Port = 5000 };

        Assert.True(viewModel.HasEffectivePairingHost);
        Assert.False(viewModel.ShowPairingBlock);
    }

    [Fact]
    public void ShowPairingBlock_and_ShowNoPairingHostMessage_are_mutually_exclusive_while_remote_is_on()
    {
        var withHost = new RemoteViewModel { IsRemoteEnabled = true, Host = "192.168.1.72:5000", Port = 5000 };
        Assert.True(withHost.ShowPairingBlock);
        Assert.False(withHost.ShowNoPairingHostMessage);

        var withoutHost = new RemoteViewModel { IsRemoteEnabled = true, Host = null, Port = null };
        Assert.False(withoutHost.ShowPairingBlock);
        Assert.True(withoutHost.ShowNoPairingHostMessage);
    }
}
