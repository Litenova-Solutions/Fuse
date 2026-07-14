using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Fuse.Cli.Rpc;

namespace Fuse.Cli.Tests.Host;

// R7 pipe ACL: FUSE_HOST_RESTRICT_PIPE=1 restricts the Windows named pipe to the current user. Default-off
// behavior must stay unchanged on every platform.
public sealed class FuseHostPipeSecurityTests
{
    [Fact]
    public void RestrictPipe_IsOffByDefault()
    {
        var prior = Environment.GetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, null);
            Assert.False(HostPipeSecurity.RestrictToCurrentUserOptIn());

            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, "0");
            Assert.False(HostPipeSecurity.RestrictToCurrentUserOptIn());

            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, "false");
            Assert.False(HostPipeSecurity.RestrictToCurrentUserOptIn());
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, prior);
        }
    }

    [Fact]
    public async Task UnrestrictedPipe_AcceptsClientConnection_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var prior = Environment.GetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable);
        var pipeName = "fuse-host-unrestricted-" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, null);
            Assert.False(HostPipeSecurity.RestrictToCurrentUserOptIn());

            await using var server = HostPipeSecurity.CreateServerStream(pipeName);
            var connectTask = server.WaitForConnectionAsync(CancellationToken.None);
            await using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, CancellationToken.None);
            await connectTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, prior);
        }
    }

    [Fact]
    [Trait(RequiresSdkIntegration.TraitName, RequiresSdkIntegration.TraitValue)]
    // The body reads Windows-only pipe ACLs (SecurityIdentifier, AccessControlType); the analyzer does not treat
    // the early-throw guard below as a platform check, so mark the method Windows-only to satisfy CA1416.
    [SupportedOSPlatform("windows")]
    public void RestrictedPipe_AclGrantsOnlyCurrentUser_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Windows-only pipe ACL test.");

        var prior = Environment.GetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable);
        var pipeName = "fuse-host-restricted-" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, "1");
            Assert.True(HostPipeSecurity.RestrictToCurrentUserOptIn());

            using var server = HostPipeSecurity.CreateServerStream(pipeName);
            var security = server.GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current Windows identity has no user SID.");

            var allowRules = security
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow)
                .ToList();

            Assert.NotEmpty(allowRules);
            Assert.All(allowRules, rule =>
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                Assert.Equal(currentUser, sid);
            });

            foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                Assert.False(sid.IsWellKnown(WellKnownSidType.WorldSid));
                Assert.False(sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostPipeSecurity.EnvironmentVariable, prior);
        }
    }
}
