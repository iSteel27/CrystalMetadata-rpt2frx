using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrystalMetadata.Client;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the client stack: options, process host, client, batch orchestrator and a null converter.</summary>
    public static IServiceCollection AddCrystalMetadataClient(this IServiceCollection services, IConfiguration? configuration = null, Action<ClientOptions>? configure = null)
    {
        var options = new ClientOptions();
        configuration?.GetSection("Client").Bind(options);
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(sp => new ExtractorProcessHost(sp.GetRequiredService<ClientOptions>(), sp.GetService<ILogger<ExtractorProcessHost>>()));
        services.AddSingleton<ICrystalMetadataClient>(sp => new CrystalMetadataClient(sp.GetRequiredService<ExtractorProcessHost>(), sp.GetRequiredService<ClientOptions>(), sp.GetService<ILoggerFactory>()));
        services.AddSingleton(sp => new BatchOrchestrator(sp.GetRequiredService<ICrystalMetadataClient>(), sp.GetService<ILogger<BatchOrchestrator>>()));
        services.AddSingleton<IReportConverter, NullReportConverter>();
        return services;
    }
}
