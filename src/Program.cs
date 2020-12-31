using System;
using System.Diagnostics;
using System.Linq;
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


        public static async Task<int> Main(string[] args)
        {
            using var host = CreateHostBuilder().Build();
            using var scope = host.Services.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<AppConfig>();
            var fs = scope.ServiceProvider.GetRequiredService<KubeSecretFS>();

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
                    return 0;
                case ParseResult.Error:
                    return 1;
            }

            fs.MountPoint = config.MountPoint;

            var sync = scope.ServiceProvider.GetRequiredService<Sync>();
            await sync.InitAsync();

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                sync.PrepareToStop();
                // ReSharper disable once AccessToDisposedClosure
                fs.Stop();
            };

            fs.Start();
            return 0;
        }
    }
}