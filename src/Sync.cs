using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
        private readonly Kubernetes _kubeClient;
        private bool _mdSecretExists;
        private readonly SemaphoreSlim _semaphore = new(1);
        private readonly ConcurrentDictionary<string, string> _secretNameGenerationDict = new();

        public Sync(AppConfig config, Kubernetes kubeClient)
        {
            _config = config;
            _kubeClient = kubeClient;
        }

        public async Task InitAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.KubeApiTimeoutSeconds));
                Utils.CleanFolder(_config.BaseDir);

                var mdSecret = (
                    await _kubeClient.ListNamespacedSecretAsync(
                        _config.SecretNamespace,
                        fieldSelector: $"metadata.name={_config.SecretBaseName}",
                        cancellationToken: cts.Token)
                ).Items.FirstOrDefault();

                string mdVersion = null;
                string mdGeneration = null;

                if (mdSecret == default)
                {
                    _config.WriteDebug("metadata secret is empty");
                }
                else
                {
                    _mdSecretExists = true;
                    if (mdSecret.Metadata.Labels.TryGetValue("version", out mdVersion))
                    {
                        _config.WriteDebug($"metadata secret version: {mdVersion}");
                    }

                    if (mdSecret.Metadata.Labels.TryGetValue("generation", out mdGeneration))
                    {
                        _config.WriteDebug($"metadata secret generation: {mdGeneration}");
                    }
                }

                var sl = new SortedList<int, byte[]>();
                var secrets = await _kubeClient.ListNamespacedSecretAsync(
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
                    _config.WriteDebug("tar called");
                    // ReSharper disable once UseAwaitUsing
                    using (var stdinStream = process!.StandardInput.BaseStream)
                    {
                        foreach (var bytes in sl.Values)
                        {
                            await stdinStream.WriteAsync(bytes.AsMemory(0, bytes.Length), cts.Token);
                        }
                    }

                    await process.WaitForExitAsync(cts.Token);
                    _config.WriteDebug("tar done");
                    if (process.ExitCode != 0)
                    {
                        var stderr = await process.StandardError.ReadToEndAsync();
                        _config.WriteError($"tar error: {stderr}");
                    }

                    await CleanupOldSecrets(mdGeneration ?? "none", cts.Token);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<Errno> WriteAsync()
        {
            _config.WriteDebug("sync called");
            await _semaphore.WaitAsync();
            _config.WriteDebug("semaphore acquired");
            try
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.KubeApiTimeoutSeconds));
                var generation = Multibase.Encode(
                    MultibaseEncoding.Base32Lower,
                    Utils.Utf8EncodingNoBom.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
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
                _config.WriteDebug("tar called");
                var stdoutStream = process!.StandardOutput.BaseStream;

                var i = 0;
                var tasks = new List<Task>();

                Errno FlushToSecret(byte[] bytes)
                {
                    _config.WriteDebug(bytes.Length.ToString());
                    if (i >= _config.MaxSecrets)
                    {
                        _config.WriteError("unable to write; " +
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
                        await _kubeClient.CreateNamespacedSecretAsync(secret, _config.SecretNamespace,
                            cancellationToken: cts.Token);
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
                           cts.Token))
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

                await process.WaitForExitAsync(cts.Token);
                _config.WriteDebug("tar done");
                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    _config.WriteError($"tar error: {stderr}");
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
                    await _kubeClient.ReplaceNamespacedSecretAsync(mdSecret, _config.SecretBaseName,
                        _config.SecretNamespace, cancellationToken: cts.Token);
                }
                else
                {
                    await _kubeClient.CreateNamespacedSecretAsync(mdSecret, _config.SecretNamespace,
                        cancellationToken: cts.Token);
                    _mdSecretExists = true;
                }

                await CleanupOldSecrets(generation, cts.Token);

                return 0;
            }
            catch (Exception e)
            {
                _config.WriteError(e.Message);
                _config.WriteDebug(e.StackTrace);
                return Errno.ECOMM;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task CleanupOldSecrets(string currentGeneration, CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();
            tasks.AddRange(_secretNameGenerationDict
                .Where(kvp => kvp.Value != currentGeneration)
                .Select(kvp =>
                    new Func<string, Task>(async (secretName) =>
                    {
                        await _kubeClient.DeleteNamespacedSecretAsync(secretName, _config.SecretNamespace,
                            cancellationToken: cancellationToken);
                        _secretNameGenerationDict.Remove(secretName, out _);
                    }).Invoke(kvp.Key)
                ));

            await Task.WhenAll(tasks);
        }
    }
}