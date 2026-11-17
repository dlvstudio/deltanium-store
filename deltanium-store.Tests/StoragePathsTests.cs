using DeltaniumStore.Security;
using Xunit;

namespace DeltaniumStore.Tests;

public class StoragePathsTests
{
    [Fact]
    public void Configured_path_wins()
    {
        var p = StoragePaths.ResolveBasePath("/data/blocks", "/sites/store-1.example", "/nowhere");
        Assert.Equal(Path.GetFullPath("/data/blocks"), p);
    }

    [Fact]
    public void Uses_site_program_data_folder_when_it_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "dlt-pd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "store-1.example"));
        try
        {
            var p = StoragePaths.ResolveBasePath(null, "/sites/store-1.example", root);
            Assert.Equal(Path.Combine(root, "store-1.example", "FileStorage"), p);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Falls_back_to_content_root_for_local_runs()
    {
        var p = StoragePaths.ResolveBasePath("", "/dev/store", "/nowhere-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.Combine("/dev/store", "FileStorage"), p);
    }
}
