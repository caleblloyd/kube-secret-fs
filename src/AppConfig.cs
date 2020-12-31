using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Mono.Fuse.NETStandard;
using Mono.Options;

namespace KubeSecretFS
{
    public class AppConfig
    {
        public string BaseDir { get; private set; }
        public int KubeApiTimeoutSeconds { get; private set; } = 10;
        public int MaxBytesPerSecret { get; private set; } = 524288;
        public int MaxSecrets { get; private set; } = 20;
        public string MountPoint { get; private set; }
        public string SecretBaseName { get; private set; }
        public string SecretNamespace { get; private set; }

        private static readonly string NamespacePath = Path.Combine(
            $"{Path.DirectorySeparatorChar}var",
            "run",
            "secrets",
            "kubernetes.io",
            "serviceaccount",
            "namespace");

        private readonly Logger _logger;

        public AppConfig(Logger logger)
        {
            _logger = logger;
        }

        [SuppressMessage("ReSharper", "AssignmentInConditionalExpression")]
        public ParseResult ParseArguments(IEnumerable<string> args)
        {
            var error = false;
            var showHelp = false;

            // Parse Args
            var optionsSet = new OptionSet
            {
                {
                    "debug", "enable debug output\n" +
                             "env var KUBE_SECRET_FS_DEBUG set to '1' or 'true'",
                    v => _logger.LogLeveDebug = v != null
                },
                {
                    "baseDir=", "base dir used for filesystem caching\n" +
                                "defaults to <temp dir>/kube-secret-fs\n" +
                                "env var KUBE_SECRET_FS_BASE_DIR",
                    v => BaseDir = v
                },
                {
                    "h|help", "show this message and exit",
                    v => showHelp = v != null
                },
                {
                    "kubeApiTimeoutSeconds=", "timeout for calling kubernetes API\n" +
                                              "defaults to 10s\n" +
                                              "env var KUBE_SECRET_FS_KUBE_API_TIMEOUT_SECONDS",
                    v =>
                    {
                        if (v != null && int.TryParse(v, out var num)) KubeApiTimeoutSeconds = num;
                    }
                },
                {
                    "maxBytesPerSecret=", "maximum number of bytes stored in each secret\n" +
                                          "defaults to 524288 (512 KiB)\n" +
                                          "env var KUBE_SECRET_FS_MAX_BYTES_PER_SECRET",
                    v =>
                    {
                        if (v != null && int.TryParse(v, out var num)) MaxBytesPerSecret = num;
                    }
                },
                {
                    "maxSecrets=", "maximum number of secrets to store\n" +
                                   "defaults to 20\n" +
                                   "env var KUBE_SECRET_FS_MAX_SECRETS",
                    v =>
                    {
                        if (v != null && int.TryParse(v, out var num)) MaxSecrets = num;
                    }
                },
                {
                    "secretBaseName=", "kubernetes secret base name to store data in\n" +
                                       "defaults to kube-secret-fs\n" +
                                       "env var KUBE_SECRET_FS_SECRET_BASE_NAME",
                    v => SecretBaseName = v
                },
                {
                    "secretNamespace=", "kubernetes secret namespace\n" +
                                        "defaults to same namespace as pod\n" +
                                        "env var KUBE_SECRET_FS_SECRET_NAMESPACE",
                    v => SecretNamespace = v
                },
            };

            var extraArgs = optionsSet.Parse(args);
            foreach (var extraArg in extraArgs)
            {
                if (!extraArg.StartsWith("-") && MountPoint == null)
                    MountPoint = extraArg;
                else
                    _logger.Warning($"unknown arg: '{extraArg}'");
            }

            if (showHelp)
            {
                WriteHelp(optionsSet);
                return ParseResult.Help;
            }

            // Parse Env Vars
            int? EnvVarToInt(string name)
            {
                return Environment.GetEnvironmentVariable(name) != null
                       && int.TryParse(Environment.GetEnvironmentVariable(name), out var num)
                    ? num
                    : null;
            }

            BaseDir = Environment.GetEnvironmentVariable("KUBE_SECRET_FS_BASE_DIR") ?? BaseDir;
            _logger.LogLeveDebug = _logger.LogLeveDebug
                                   || Environment.GetEnvironmentVariable("KUBE_SECRET_FS_DEBUG") == "1"
                                   || Environment.GetEnvironmentVariable("KUBE_SECRET_FS_DEBUG") == "true";
            KubeApiTimeoutSeconds = EnvVarToInt("KUBE_SECRET_FS_KUBE_API_TIMEOUT_SECONDS") ?? KubeApiTimeoutSeconds;
            MaxBytesPerSecret = EnvVarToInt("KUBE_SECRET_FS_MAX_BYTES_PER_SECRET") ?? MaxBytesPerSecret;
            MaxSecrets = EnvVarToInt("KUBE_SECRET_FS_MAX_SECRETS") ?? MaxSecrets;
            MountPoint = Environment.GetEnvironmentVariable("KUBE_SECRET_FS_MOUNT_POINT") ?? MountPoint;
            SecretBaseName = Environment.GetEnvironmentVariable("KUBE_SECRET_FS_SECRET_BASE_NAME") ?? SecretBaseName;
            SecretNamespace = Environment.GetEnvironmentVariable("KUBE_SECRET_FS_SECRET_NAMESPACE") ?? SecretNamespace;

            // Set Defaults
            if (BaseDir == null)
            {
                BaseDir = Path.Combine(Path.GetTempPath(), "kube-secret-fs");
                Directory.CreateDirectory(BaseDir!);
            }

            SecretBaseName ??= "kube-secret-fs";

            if (SecretNamespace == null && File.Exists(NamespacePath))
                SecretNamespace = File.ReadAllText(NamespacePath, Utils.Utf8EncodingNoBom).Trim();

            // Error Checking
            if (error |= MountPoint == null)
                _logger.Error("missing mountPoint arg");

            if (error |= SecretNamespace == null)
                _logger.Error("unable to determine default for --secretNamespace; must be set explicitly");

            // Logging
            _logger.Debug("Args:");
            _logger.Debug($"MountPoint:            {MountPoint}");
            _logger.Debug("Options:");
            _logger.Debug($"BaseDir:               {BaseDir}");
            _logger.Debug($"MountPoint:            {MountPoint}");
            _logger.Debug($"KubeApiTimeoutSeconds: {KubeApiTimeoutSeconds}");
            _logger.Debug($"MaxBytesPerSecret:     {MaxBytesPerSecret}");
            _logger.Debug($"MaxSecrets:            {MaxSecrets}");
            _logger.Debug($"SecretBaseName:        {SecretBaseName}");
            _logger.Debug($"SecretNamespace:       {SecretNamespace}");

            return error ? ParseResult.Error : ParseResult.Valid;
        }

        private static void WriteHelp(OptionSet p)
        {
            Console.Error.WriteLine("usage: kube-secret-fs [options] mountPoint");
            FileSystem.ShowFuseHelp("kube-secret-fs");
            Console.Error.WriteLine();
            Console.Error.WriteLine("kube-secret-fs args:");
            Console.Error.WriteLine("  mountPoint                 mount point of filesystem");
            Console.Error.WriteLine("                               env var KUBE_SECRET_FS_MOUNT_POINT");
            Console.Error.WriteLine();
            Console.Error.WriteLine("kube-secret-fs options:");
            p.WriteOptionDescriptions(Console.Error);
        }
    }

    public enum ParseResult
    {
        Valid,
        Help,
        Error
    }
}