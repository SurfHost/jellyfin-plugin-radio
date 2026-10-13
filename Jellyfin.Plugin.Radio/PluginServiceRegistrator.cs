using System;
using System.Net.Http;
using Jellyfin.Plugin.Radio.Api;
using Jellyfin.Plugin.Radio.Library;
using Jellyfin.Plugin.Radio.Playback;
using Jellyfin.Plugin.Radio.Stations;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Radio;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        // Since Jellyfin 10.9 channels are found through dependency injection only.
        // Without this line the plugin installs and loads, and the channel never appears,
        // with nothing in the log.
        serviceCollection.AddSingleton<IChannel, RadioChannel>();

        // The Radio channel has no folders; refuse requests for one (see ChannelFolderGuard).
        serviceCollection.AddSingleton<ChannelFolderGuard>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<ChannelFolderGuard>());

        // Named clients are shared by every plugin in the server, hence the prefix. The
        // probe follows redirects itself to report them.
        serviceCollection.AddHttpClient(StreamProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

        serviceCollection.AddSingleton<StationStateStore>();
        serviceCollection.AddSingleton<StreamProbe>();
        serviceCollection.AddSingleton<UserAudit>();
        serviceCollection.AddSingleton<RadioSync>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<RadioSync>());
    }
}
