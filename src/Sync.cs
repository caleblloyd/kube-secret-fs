using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Mono.Unix.Native;
using Multiformats.Base;

namespace KubeSecretFS
{
    public class Sync
    {
        private const string Version = "0.1";

        private readonly AppConfig _config;
        private readonly Logger _logger;
        private readonly KubeAccessor _kubeAccessor;
        private bool _mdSecretExists;
        private readonly ConcurrentDictionary<string, string> _secretNameGenerationDict = new();

        public Sync(AppConfig config, KubeAccessor kubeAccessor, Logger logger)
        {
            _config = config;
            _kubeAccessor = kubeAccessor;
            _logger = logger;
        }

        public async Task InitAsync()
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.KubeApiTimeoutSeconds));
            Utils.CleanFolder(_config.BaseDir);

            var mdSecret = (
                await _kubeAccessor.Client.ListNamespacedSecretAsync(
                    _config.SecretNamespace,
                    fieldSelector: $"metadata.name={_config.SecretBaseName}",
                    cancellationToken: cts.Token)
            ).Items.FirstOrDefault();

            string mdVersion = null;
            string mdGeneration = null;

            if (mdSecret == default)
            {
                _logger.Debug("metadata secret is empty");
            }
            else
            {
                _mdSecretExists = true;
                if (mdSecret.Metadata.Labels.TryGetValue("version", out mdVersion))
                {
                    _logger.Debug($"metadata secret version: {mdVersion}");
                }

                if (mdSecret.Metadata.Labels.TryGetValue("generation", out mdGeneration))
                {
                    _logger.Debug($"metadata secret generation: {mdGeneration}");
                }
            }

            var sl = new SortedList<int, byte[]>();
            var secrets = await _kubeAccessor.Client.ListNamespacedSecretAsync(
                _config.SecretNamespace,
                labelSelector: $"owner=kube-secret-fs,parent={_config.SecretBaseName}",
                cancellationToken: cts.Token);
            foreach (var secret in secrets.Items)
            {
                _secretNameGenerationDict.TryAdd(secret.Metadata.Name,
                    secret.Metadata.Labels.TryGetValue("generation", out var generation)
                        ? generation
                        : "unknown");
                secret.Metadata.Labels.TryGetValue("order", out var orderStr);
                secret.Metadata.Labels.TryGetValue("version", out var version);

                if (mdVersion != null
                    && mdGeneration != null
                    && version == mdVersion
                    && generation == mdGeneration
                    && orderStr != null
                    && int.TryParse(orderStr, out var order)
                    && secret.Data.TryGetValue("data.tar.gz", out var data))
                    sl.Add(order, data);
            }

            if (sl.Count > 0)
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "tar",
                    ArgumentList =
                    {
                        "-xz"
                    },
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = _config.BaseDir
                };
                var process = Process.Start(processStartInfo);
                // ReSharper disable once UseAwaitUsing
                using (var stdinStream = process!.StandardInput.BaseStream)
                {
                    foreach (var bytes in sl.Values)
                    {
                        await stdinStream.WriteAsync(bytes.AsMemory(0, bytes.Length), cts.Token);
                    }
                }

                await process.WaitForExitAsync(cts.Token);
                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    _logger.Error($"tar error: {stderr}");
                }

                await CleanupOldSecrets(mdGeneration ?? "none", cts.Token);
            }
        }

        private readonly Channel<Tuple<string, bool, Func<Errno>, Channel<Errno>>> _operationCh =
            Channel.CreateUnbounded<Tuple<string, bool, Func<Errno>, Channel<Errno>>>();

        public async Task<Errno> OperationAsync(string debugInfo, bool commit, Func<Errno> operationFn)
        {
            var resultCh = Channel.CreateBounded<Errno>(1);
            await _operationCh.Writer.WriteAsync(
                new Tuple<string, bool, Func<Errno>, Channel<Errno>>(
                    debugInfo, commit, operationFn, resultCh));
            _logger.Debug($"{debugInfo} queued");
            var result = await resultCh.Reader.ReadAsync();
            _logger.Debug($"{debugInfo} result: {result}");
            return result;
        }

        public async Task WriteLoopAsync(CancellationToken terminateToken)
        {
            while (true)
            {
                var resultChs = new List<Channel<Errno>>();

                // read operations
                try
                {
                    await _operationCh.Reader.WaitToReadAsync(terminateToken);
                    while (_operationCh.Reader.TryRead(out var operation))
                    {
                        var (debugInfo, commit, operationFn, resultCh) = operation;
                        _logger.Debug($"{debugInfo} running");
                        var result = operationFn.Invoke();
                        if (result != 0 || !commit)
                        {
                            await resultCh.Writer.WriteAsync(result, CancellationToken.None);
                            continue;
                        }

                        resultChs.Add(resultCh);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Debug("terminate cts received");
                    return;
                }
                catch (Exception e)
                {
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }

                // continue if no operations
                if (!resultChs.Any())
                    continue;
                
                // write secret
                Errno errno;
                var generation = Multibase.Encode(
                    MultibaseEncoding.Base32Lower,
                    Utils.Utf8EncodingNoBom.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.KubeApiTimeoutSeconds));
                    errno = await WriteAsync(generation, cts.Token);
                }
                catch (Exception e)
                {
                    errno = Errno.ECOMM;
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }

                // notify senders of result
                try
                {
                    foreach (var resultCh in resultChs)
                    {
                        await resultCh.Writer.WriteAsync(errno, CancellationToken.None);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }

                // cleanup old secrets
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.KubeApiTimeoutSeconds));
                    await CleanupOldSecrets(generation, cts.Token);
                }
                catch (Exception e)
                {
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }
        }

        private async Task<Errno> WriteAsync(string generation, CancellationToken cancellationToken)
        {
            _logger.Debug($"write generation: {generation}");
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                ArgumentList =
                {
                    "-cz",
                    "."
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _config.BaseDir
            };
            var process = Process.Start(processStartInfo);
            var stdoutStream = process!.StandardOutput.BaseStream;

            var i = 0;
            var tasks = new List<Task>();

            Errno FlushToSecret(byte[] bytes)
            {
                if (i >= _config.MaxSecrets)
                {
                    _logger.Error("unable to write; " +
                                  $"filesystem is larger {_config.MaxBytesPerSecret * _config.MaxSecrets} bytes");
                    return Errno.ENOSPC;
                }

                var secretNumber = $"{i}".PadLeft(5, '0');
                var secretName = $"{_config.SecretBaseName}-{generation}-{secretNumber}";

                var secret = new V1Secret
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = secretName,
                        Labels = new Dictionary<string, string>
                        {
                            {"generation", generation},
                            {"order", i.ToString()},
                            {"owner", "kube-secret-fs"},
                            {"parent", _config.SecretBaseName},
                            {"version", Version}
                        }
                    },
                    Data = new Dictionary<string, byte[]>
                    {
                        {"data.tar.gz", bytes}
                    }
                };

                tasks.Add(new Func<Task>(async () =>
                {
                    await _kubeAccessor.Client.CreateNamespacedSecretAsync(secret, _config.SecretNamespace,
                        cancellationToken: cancellationToken);
                    _logger.Debug($"wrote secret: {secretName} size: {bytes.Length} bytes");
                    _secretNameGenerationDict.TryAdd(secretName, generation);
                }).Invoke());
                i++;

                return 0;
            }

            var buffer = new byte[_config.MaxBytesPerSecret];
            int bytesRead;
            var bytesTotal = 0;
            while ((bytesRead = await stdoutStream.ReadAsync(
                       buffer.AsMemory(bytesTotal, buffer.Length - bytesTotal),
                       cancellationToken))
                   > 0)
            {
                bytesTotal += bytesRead;
                if (bytesTotal < buffer.Length)
                    continue;

                var bytes = new byte[bytesTotal];
                Buffer.BlockCopy(buffer, 0, bytes, 0, bytesTotal);
                var err = FlushToSecret(bytes);
                if (err != 0)
                    return err;
                bytesTotal = 0;
            }

            if (bytesTotal > 0)
            {
                var bytes = new byte[bytesTotal];
                Buffer.BlockCopy(buffer, 0, bytes, 0, bytesTotal);
                var err = FlushToSecret(bytes);
                if (err != 0)
                    return err;
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                _logger.Error($"tar error: {stderr}");
                return Errno.EPROTO;
            }

            await Task.WhenAll(tasks);

            var mdSecret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"{_config.SecretBaseName}",
                    Labels = new Dictionary<string, string>
                    {
                        {"generation", generation},
                        {"owner", "kube-secret-fs"},
                        {"version", Version}
                    }
                }
            };

            if (_mdSecretExists)
            {
                await _kubeAccessor.Client.ReplaceNamespacedSecretAsync(mdSecret, _config.SecretBaseName,
                    _config.SecretNamespace, cancellationToken: cancellationToken);
            }
            else
            {
                await _kubeAccessor.Client.CreateNamespacedSecretAsync(mdSecret, _config.SecretNamespace,
                    cancellationToken: cancellationToken);
                _mdSecretExists = true;
            }

            return 0;
        }

        private async Task CleanupOldSecrets(string currentGeneration, CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();
            tasks.AddRange(_secretNameGenerationDict
                .Where(kvp => kvp.Value != currentGeneration)
                .Select(kvp =>
                    new Func<string, Task>(async (secretName) =>
                    {
                        await _kubeAccessor.Client.DeleteNamespacedSecretAsync(secretName, _config.SecretNamespace,
                            cancellationToken: cancellationToken);
                        _secretNameGenerationDict.Remove(secretName, out _);
                    }).Invoke(kvp.Key)
                ));

            await Task.WhenAll(tasks);
        }
    }
}