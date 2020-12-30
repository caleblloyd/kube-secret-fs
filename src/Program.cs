using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace KubeSecretFS
{
    public static class Program
    {
        public static Task Main(string[] args)
        {
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
                process.WaitForExit();
                // Console.WriteLine(stdout);
                // Console.WriteLine(stderr);
            }

            using var fs = new KubeSecretFS();
            var unhandled = fs.ParseFuseArguments(args);
            if (!fs.ParseArguments(unhandled))
                return Task.CompletedTask;
            
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                // ReSharper disable once AccessToDisposedClosure
                fs.Stop();
            };

            fs.Start();
            return Task.CompletedTask;
        }
    }
}