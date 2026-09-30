using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     The build id and where the engine runs from. The native client reports the module id of the fuse.dll it starts,
///     and finds that fuse.dll beside it, or on Windows through the tools directory's .store folder, where dotnet tool
///     install puts the package when it copies the native client into the tools directory as the fuse command.
/// </summary>
public class EngineVersionTests
{
    [Fact]
    public void The_module_id_read_from_a_file_is_the_loaded_assembly_s()
    {
        var assembly = typeof(EngineVersion).Assembly;

        Assert.Equal(assembly.ManifestModule.ModuleVersionId, EngineVersion.ModuleIdOf(assembly.Location));
    }

    [Fact]
    public void A_managed_client_s_build_is_its_version_and_module_id_and_it_runs_the_engine_from_its_own_directory()
    {
        Assert.False(EngineVersion.IsNativeClient);
        Assert.Equal($"{EngineVersion.Product}/{typeof(EngineVersion).Assembly.ManifestModule.ModuleVersionId:N}", EngineVersion.Build);
        Assert.Equal(AppContext.BaseDirectory, EngineVersion.ManagedDirectory);
    }

    [Fact]
    public void A_managed_client_runs_the_engine_from_its_own_directory_whatever_is_there() =>
        InTempDirectory(directory => Assert.Equal(directory, EngineVersion.FindManagedDirectory(directory, isNativeClient: false, "win-x64", "5.2.0")));

    [Fact]
    public void A_native_client_with_fuse_dll_beside_it_runs_the_engine_from_there() =>
        InTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, EngineVersion.ManagedAssembly), "");

            Assert.Equal(directory, EngineVersion.FindManagedDirectory(directory, isNativeClient: true, "linux-x64", "5.2.0"));
        });

    [Fact]
    public void A_native_client_installed_as_a_tool_on_windows_runs_the_engine_from_its_package_in_the_store() =>
        InTempDirectory(directory =>
        {
            var package = Path.Combine(directory, ".store", "fuse", "5.2.0", "litenova.fuse.win-x64", "5.2.0", "tools", "net10.0", "win-x64");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, EngineVersion.ManagedAssembly), "");

            Assert.Equal(package, EngineVersion.FindManagedDirectory(directory, isNativeClient: true, "win-x64", "5.2.0"));
        });

    [Fact]
    public void A_native_client_finds_fuse_dll_anywhere_in_its_version_s_store_folder() =>
        InTempDirectory(directory =>
        {
            var package = Path.Combine(directory, ".store", "fuse", "5.2.0", "fuse", "5.2.0", "tools", "net10.0", "any");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, EngineVersion.ManagedAssembly), "");
            var otherVersion = Path.Combine(directory, ".store", "fuse", "5.1.0", "fuse", "5.1.0", "tools", "net10.0", "any");
            Directory.CreateDirectory(otherVersion);
            File.WriteAllText(Path.Combine(otherVersion, EngineVersion.ManagedAssembly), "");

            Assert.Equal(package, EngineVersion.FindManagedDirectory(directory, isNativeClient: true, "win-x64", "5.2.0"));
        });

    [Fact]
    public void A_native_client_with_no_fuse_dll_has_no_engine_to_run() =>
        InTempDirectory(directory =>
        {
            Directory.CreateDirectory(Path.Combine(directory, ".store", "fuse", "5.1.0"));

            Assert.Null(EngineVersion.FindManagedDirectory(directory, isNativeClient: true, "win-x64", "5.2.0"));
        });

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-tests", "engine-version-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
