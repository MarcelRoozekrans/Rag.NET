using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GoogleDrive;

/// <summary>DI registration extensions for <see cref="GoogleDriveDataProvider"/>.</summary>
public static class GoogleDriveDataProviderExtensions
{
    /// <summary>Registers using a service account JSON key file path.</summary>
    public static IServiceCollection AddGoogleDriveDataProvider(
        this IServiceCollection services,
        string serviceAccountKeyPath,
        Action<GoogleDriveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccountKeyPath);

        ServiceAccountCredential credential;
        using (var stream = File.OpenRead(serviceAccountKeyPath))
        {
            var parameters = Google.Apis.Auth.OAuth2.ServiceAccountCredential.FromServiceAccountData(stream);
            credential = new ServiceAccountCredential(
                new ServiceAccountCredential.Initializer(parameters.Id)
                {
                    Scopes = [DriveService.Scope.DriveReadonly],
                    Key    = parameters.Key,
                });
        }

        var drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = "Rag.NET",
        });

        var opts = new GoogleDriveOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new GoogleDriveDataProvider(drive, opts));
    }

    /// <summary>Registers using an existing <see cref="DriveService"/> instance.</summary>
    public static IServiceCollection AddGoogleDriveDataProvider(
        this IServiceCollection services,
        DriveService driveService,
        Action<GoogleDriveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(driveService);

        var opts = new GoogleDriveOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new GoogleDriveDataProvider(driveService, opts));
    }
}
