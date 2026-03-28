using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.GoogleDrive;
using Xunit;

namespace Rag.NET.DataProviders.GoogleDrive.Tests;

public sealed class GoogleDriveDataProviderTests
{
    private static DriveService MakeDriveService()
        => new(new BaseClientService.Initializer { ApplicationName = "test" });

    [Fact]
    public void Constructor_NullDrive_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveDataProvider(null!));
    }

    [Fact]
    public void Constructor_WithOptions_Succeeds()
    {
        var drive = MakeDriveService();
        var opts = new GoogleDriveOptions { FolderId = "folder-1", Extensions = [".md"] };

        var sut = new GoogleDriveDataProvider(drive, opts);

        Assert.NotNull(sut);
    }

    [Fact]
    public void AddGoogleDriveDataProvider_DriveService_RegistersIFileContentProvider()
    {
        var services = new ServiceCollection();
        var drive = MakeDriveService();

        services.AddGoogleDriveDataProvider(drive);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileContentProvider>();
        Assert.IsType<GoogleDriveDataProvider>(provider);
    }

    [Fact]
    public void AddGoogleDriveDataProvider_NullDriveService_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddGoogleDriveDataProvider((DriveService)null!));
    }
}
