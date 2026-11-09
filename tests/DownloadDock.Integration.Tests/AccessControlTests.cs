using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DownloadDock.Integration.Tests;

/// <summary>Windows-only: the pipe's access control (who may connect) and squatting protection.</summary>
public class AccessControlTests
{
    [SkippableFact]
    public void Pipe_acl_allows_only_this_logon_session_and_denies_network_clients()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Pipe ACLs are Windows-only");
#pragma warning disable CA1416
        var security = PipeSecurityFactory.Create();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.True(security.AreAccessRulesProtected, "must not inherit broader permissions");

        var deny = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Deny);
        Assert.Equal(PipeSecurityFactory.NetworkSid, deny.IdentityReference);

        var allow = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Allow);
        using var me = WindowsIdentity.GetCurrent();
        var expected = PipeSecurityFactory.LogonSid(me) ?? me.User!;
        Assert.Equal(expected, allow.IdentityReference);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545"); // Everyone, Authenticated Users, Users
#pragma warning restore CA1416
    }

    [SkippableFact]
    public async Task A_second_server_cannot_take_over_an_owned_pipe_name()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "FirstPipeInstance is Windows-only");
        var pipe = ServerClientTests.UniquePipe();
        await using var legit = ServerClientTests.NewServer(pipe);
        Assert.True(await ServerClientTests.WaitFor(() => legit.IsListening));
        await using var squatter = ServerClientTests.NewServer(pipe);
        Assert.True(await ServerClientTests.WaitFor(() => squatter.StartupProblem is not null), "second server should detect the name is taken");
        Assert.False(squatter.IsListening);
        // Clients still reach the legitimate server.
        await using var client = await ServerClientTests.Connect(pipe);
        Assert.Equal("TestServer", client.Server!.ServerName);
    }

    [SkippableFact]
    public async Task Live_pipe_carries_the_restricted_acl()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only");
        var pipe = ServerClientTests.UniquePipe();
        await using var server = ServerClientTests.NewServer(pipe);
        await using var client = await ServerClientTests.Connect(pipe);
#pragma warning disable CA1416
        // Open a second raw handle and read the DACL Windows actually applied to the pipe object.
        using var probe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None);
        await probe.ConnectAsync(2000);
        var acl = probe.GetAccessControl();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(PipeSecurityFactory.NetworkSid));
        Assert.DoesNotContain(rules, r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545");
#pragma warning restore CA1416
    }
}
