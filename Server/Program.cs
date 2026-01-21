using Microsoft.Extensions.DependencyInjection;
using UltimateServer.Servers;
using UltimateServer.Services;
using UltimateServer.Events;
using UltimateServer.Models;
using Server.Services;

namespace UltimateServer
{
    class Program
    {
        private static int tcpPort = 11001;
        private static int httpPort = 11002;
        private static int udpPort = 11003;
        private static int sftpPort = 11004;
        private static CancellationTokenSource cts = new();
        private const string JwtSecret = "your-super-secret-jwt-key-change-this-in-production-32-chars-min";

        static async Task Main(string[] args)
        {
            // Stating the app with args if you want custom ports to use:
            // Start Command:  ./Server 11001 11002 11003 11004 
            //                    args: arg-1 arg-2 arg-3 arg-4

            // Use the Default command for building the project so there will be no need of DotNet to run the app!
            // Default build command: dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=true -p:TrimMode=partial -p:InvariantGlobalization=true -p:DebugType=None -p:DebugSymbols=false -p:EnableDiagnostics=false -p:PublishReadyToRun=false -p:TieredCompilation=false -p:EventSourceSupport=false -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=false


            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
                tcpPort = parsedPort;
            if (args.Length > 1 && int.TryParse(args[1], out int parsedPortWeb))
                httpPort = parsedPortWeb;
            if (args.Length > 2 && int.TryParse(args[2], out int parsedVoicePort))
                udpPort = parsedVoicePort;
            if (args.Length > 3 && int.TryParse(args[3], out int parsedSftpPort))
                sftpPort = parsedSftpPort;
            var services = new ServiceCollection();

            services.AddSingleton<Logger>();
            services.AddSingleton(provider => new ConfigManager(logger: provider.GetRequiredService<Logger>()));
            services.AddSingleton(provider => new EmailService(config: provider.GetRequiredService<ConfigManager>().Config));
            services.AddSingleton(provider => provider.GetRequiredService<ConfigManager>().Config);
            services.AddSingleton<FilePaths>();
            services.AddSingleton(provider => new ServerSettings { tcpPort = tcpPort, httpPort = httpPort, udpPort = udpPort, sftpPort = sftpPort });
            services.AddSingleton<FeatherDatabase>();
            services.AddSingleton<CacheService>();
            services.AddSingleton<IEventBus, InMemoryEventBus>();
            services.AddSingleton<Services.EventHandler>();
            services.AddSingleton<PluginManager>();
            services.AddSingleton<SitePress>();
            services.AddSingleton<SftpServer>();
            services.AddSingleton<DataBox>();
            services.AddSingleton<MiniDB>();
            services.AddSingleton<DataBackuper>();
            services.AddSingleton<WebSocketServer>();
            services.AddSingleton<UserService>();
            services.AddSingleton<HttpServer>();
            services.AddSingleton<TcpServer>();
            services.AddSingleton<UdpServer>();
            


            services.AddScoped(provider =>
                new AuthenticationService(
                    JwtSecret,
                    provider.GetRequiredService<ServerConfig>(),
                    provider.GetRequiredService<Logger>()));

            services.AddScoped<ValidationService>();
            services.AddScoped<VideoService>();
            services.AddScoped<CommandHandler>();
            services.AddScoped<Nginx>();
            services.AddScoped<PackageManager>();

            var serviceProvider = services.BuildServiceProvider();

            var logger = serviceProvider.GetRequiredService<Logger>();
            logger.PrepareLogs();

            var eventBus = serviceProvider.GetRequiredService<IEventBus>();
            var eventHandler = serviceProvider.GetRequiredService<Services.EventHandler>();

            eventBus.Subscribe<UserRegisteredEvent>(eventHandler);
            eventBus.Subscribe<VideoUploadedEvent>(eventHandler);
            eventBus.Subscribe<PluginLoadedEvent>(eventHandler);


            var userService = serviceProvider.GetRequiredService<UserService>();
            var videoService = serviceProvider.GetRequiredService<VideoService>();
            var commandHandler = serviceProvider.GetRequiredService<CommandHandler>();
            var httpServer = serviceProvider.GetRequiredService<HttpServer>();
            var tcpServer = serviceProvider.GetRequiredService<TcpServer>();
            var udpServer = serviceProvider.GetRequiredService<UdpServer>();
            var sitePress = serviceProvider.GetRequiredService<SitePress>();
            var sftpServer = serviceProvider.GetRequiredService<SftpServer>();
            var dataBox = serviceProvider.GetRequiredService<DataBox>();
            var miniDB = serviceProvider.GetRequiredService<MiniDB>();
            var dataBackuper = serviceProvider.GetRequiredService<DataBackuper>();
            var webSocketServer = serviceProvider.GetRequiredService<WebSocketServer>();
            var pluginManager = serviceProvider.GetRequiredService<PluginManager>();

            await miniDB.Start();
            await dataBox.Start();

            await httpServer.Start();
            await tcpServer.Start();
            await udpServer.Start();
            await sitePress.Start();
            await sftpServer.Start();
            await dataBackuper.Start();
            await webSocketServer.Start();

            if (Directory.Exists(Path.Combine("plugins", ".plugin_temp")))
                Directory.Delete(Path.Combine("plugins", ".plugin_temp"), true);
            pluginManager._serviceProvider = serviceProvider;
            await pluginManager.LoadPluginsAsync();

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);
                    await dataBox.Save();
                }
            });

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                    await pluginManager.UpdateLoadedPluginsAsync(cts.Token);
                }
            });
            logger.Log($"🚀 Server started successfully!");


            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                ShutdownApplication(serviceProvider, logger, cts).GetAwaiter().GetResult();
            };
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                ShutdownApplication(serviceProvider, logger, cts).GetAwaiter().GetResult();
            };


            static async Task ShutdownApplication(ServiceProvider serviceProvider, Logger logger, CancellationTokenSource cts)
            {
                if (cts.IsCancellationRequested) return;

                cts.Cancel();
                logger.Log("🛑 Shutdown requested...");

                logger.Log("🛑 Stopping servers...");
                var httpServer = serviceProvider.GetRequiredService<HttpServer>();
                var tcpServer = serviceProvider.GetRequiredService<TcpServer>();
                var udpServer = serviceProvider.GetRequiredService<UdpServer>();
                var pluginManager = serviceProvider.GetRequiredService<PluginManager>();
                var sitePressService = serviceProvider.GetRequiredService<SitePress>();
                var sftpServerService = serviceProvider.GetRequiredService<SftpServer>();
                var webSocketServer = serviceProvider.GetRequiredService<WebSocketServer>();
                var dataBox = serviceProvider.GetRequiredService<DataBox>();
                var miniDB = serviceProvider.GetRequiredService<MiniDB>();

                await pluginManager.UnloadAllPluginsAsync();

                await httpServer.StopAsync();
                await tcpServer.StopAsync();
                await udpServer.StopAsync();
                await sitePressService.StopAsync();
                await sftpServerService.StopAsync();
                await webSocketServer.Stop();

                await dataBox.Stop();
                await miniDB.Stop();

                logger.Log("✅ Shutdown complete.");
                Environment.Exit(0);
            }

            await Task.Delay(Timeout.Infinite);
        }
    }
}
