using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var storageConn = config["AzureWebJobsStorage"] ?? "UseDevelopmentStorage=true";

        services.AddSingleton(new BlobServiceClient(storageConn));
        services.AddSingleton(new QueueServiceClient(storageConn));
    })
    .Build();

host.Run();
