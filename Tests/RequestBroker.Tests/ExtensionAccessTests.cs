using System.Collections;
using valheimCLI.Extensions;
using Xunit;

public class ExtensionAccessTests
{
    private static IEnumerator Done(ExtensionContext context) { context.Succeed(); yield break; }
    private static string? Check(bool readOnly = false, ExtensionRole role = ExtensionRole.Any, bool world = true,
        bool server = false, bool dedicated = false, bool console = true, bool devcommands = true, bool optIn = true, bool network = true) =>
        ExtensionAccess.Refusal(new ExtensionCommand("test", "test", Done, readOnly, role, needsWorld: true), world, network, server, dedicated, console, devcommands, optIn);
    [Fact] public void JoinedClientCanMutateWithBothExplicitPermissions() => Assert.Null(Check(server: false, devcommands: true, optIn: true));
    [Fact] public void JoinedClientRequiresOptIn() => Assert.NotNull(Check(optIn: false));
    [Fact] public void JoinedClientAlsoRequiresDevcommands() => Assert.NotNull(Check(devcommands: false));
    [Fact] public void LocalHostDoesNotNeedClientOptIn() => Assert.Null(Check(server: true, optIn: false));
    [Fact] public void HostStillRequiresDevcommands() => Assert.NotNull(Check(server: true, devcommands: false));
    [Fact] public void ReadOnlyDoesNotNeedMutationPermissions() => Assert.Null(Check(readOnly: true, console: false, devcommands: false, optIn: false));
    [Fact] public void OptInCannotWaiveWorldRequirement() => Assert.NotNull(Check(world: false));
    [Fact] public void OptInCannotWaiveServerRole() => Assert.NotNull(Check(role: ExtensionRole.Server));
    [Fact] public void DedicatedServerCannotRunPlayerCommand() => Assert.NotNull(Check(role: ExtensionRole.Client, server: true, dedicated: true));
    [Fact] public void ClientRoleNeedsNetwork() => Assert.NotNull(Check(role: ExtensionRole.Client, network: false));
}
