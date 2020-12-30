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
                    services
                        .AddSingleton<AppConfig>()
                        .AddSingleton<KubeSecretFS>()
                        .AddSingleton(new Kubernetes(KubernetesClientConfiguration.InClusterConfig()))
                );


        public static async Task<int> Main(string[] args)
        {
            await Task.Delay(1);
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = "--help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var process = Process.Start(processStartInfo);
            if (process != null)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                await process.WaitForExitAsync();
                // Console.WriteLine(stdout);
                // Console.WriteLine(stderr);
            }

            using var host = CreateHostBuilder().Build();
            using var scope = host.Services.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<AppConfig>();
            var fs = scope.ServiceProvider.GetRequiredService<KubeSecretFS>();

            var unhandled = fs.ParseFuseArguments(args);
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

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                // ReSharper disable once AccessToDisposedClosure
                fs.Stop();
            };

            fs.Start();
            return 0;
        }
    }
}