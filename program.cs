using System.Reflection;
using Microsoft.Extensions.Hosting;
using NetDaemon.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.Extensions.Tts;
using NetDaemon.Runtime;
using NetDaemon.Extensions.MqttEntityManager;
using NetDeamon;

// Add next line if using code generator
//using HomeAssistantGenerated;

#pragma warning disable CA1812

try
{
    await Host.CreateDefaultBuilder(args)
        .UseNetDaemonAppSettings()
        .UseNetDaemonDefaultLogging()
        .UseNetDaemonRuntime()
        .UseNetDaemonTextToSpeech()
        .UseNetDaemonMqttEntityManagement()
        .ConfigureServices((_, services) =>
            services
                .AddAppsFromAssembly(Assembly.GetExecutingAssembly())
                // App plugins: apps/plugins/<Name>/<Name>.dll, and folders given with --plugins <folder>
                .AddAppPlugins(args)
                .AddNetDaemonStateManager()
                .AddNetDaemonScheduler()
        // Add next line if using code generator
        // .AddHomeAssistantGenerated()
        )
        .Build()
        .RunAsync()
        .ConfigureAwait(false);
}
catch (Exception e)
{
    Console.WriteLine($"Failed to start host... {e}");
    throw;
}