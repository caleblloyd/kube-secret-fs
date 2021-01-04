using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mono.Fuse.NETStandard;

namespace KubeSecretFS
{
    public static class Program
    {
        private static IHostBuilder CreateHostBuilder() =>
            Host.CreateDefaultBuilder()
                .ConfigureServices((_, services) =>
                {
                    services
                        .AddSingleton<AppConfig>()
                        .AddSingleton<KubeAccessor>()
                        .AddSingleton<KubeSecretFS>()
                        .AddSingleton<Logger>()
                        .AddSingleton<Sync>();
                });


        public static async Task Main(string[] args)
        {
            using var host = CreateHostBuilder().Build();
            using var scope = host.Services.CreateScope();
            var terminateCts = new CancellationTokenSource();
            var terminateResetEvent = new ManualResetEventSlim();
            var config = scope.ServiceProvider.GetRequiredService<AppConfig>();
            var fs = scope.ServiceProvider.GetRequiredService<KubeSecretFS>();
            var logger = scope.ServiceProvider.GetRequiredService<Logger>();

            var fuseArgs = new[]
                {
                    "-o",
                    "auto_unmount"
                }
                .Concat(args)
                .ToArray();
            var unhandled = fs.ParseFuseArguments(fuseArgs);
            switch (config.ParseArguments(unhandled))
            {
                case ParseResult.Valid:
                    break;
                case ParseResult.Help:
                    Environment.ExitCode = 0;
                    return;
                case ParseResult.Error:
                    Environment.ExitCode = 1;
                    return;
            }

            fs.MountPoint = config.MountPoint;

            var sync = scope.ServiceProvider.GetRequiredService<Sync>();
            await sync.InitAsync();

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                logger.Debug("setting termination request");
                terminateCts.Cancel();
                terminateResetEvent.Wait(CancellationToken.None);
            };

            var fsTask = Task.Run(() => fs.Start(), CancellationToken.None);
            await Task.WhenAny(fsTask, sync.WriteLoopAsync(terminateCts.Token));
            logger.Debug("program exiting with RC 0");
            Environment.ExitCode = 0;
            terminateResetEvent.Set();
        }
    }
}