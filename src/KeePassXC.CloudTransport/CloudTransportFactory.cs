namespace KeePassXC.CloudTransport;

public static class CloudTransportFactory
{
    public static ICloudTransport Create(CloudConnectionOptions options, CloudTransportServices services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        if (options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromHours(1))
            throw new CloudTransportException("CONFIGURATION", CloudFailureKind.Configuration, "Timeout must be between 1 second and 1 hour");
        _ = CloudPath.Normalize(options.RemotePrefix, true);
        return options.Provider switch
        {
            CloudProvider.GoogleDrive => new GoogleDriveTransport(options, services),
            CloudProvider.ProtonDrive => new ProtonDriveTransport(options, services),
            CloudProvider.S3 or CloudProvider.CloudflareR2 => new S3Transport(options, services),
            CloudProvider.WebDav => new WebDavTransport(options, services),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unsupported cloud provider")
        };
    }
}
