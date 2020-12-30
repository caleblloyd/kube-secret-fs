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
        public bool Debug { get; private set; }
        public string MountPoint { get; private set; }
        public string SecretBaseName { get; private set; }
        public string SecretNamespace { get; private set; }

        public int MaxBytesPerSecret { get; } = 524288;
        public int MaxSecrets { get; } = 20;
        public int KubeApiTimeoutSeconds { get; } = 5;

        private static readonly string NamespacePath = Path.Combine(
            $"{Path.DirectorySeparatorChar}var",
            "run",
            "secrets",
            "kubernetes.io",
            "serviceaccount",
            "namespace");

        [SuppressMessage("ReSharper", "AssignmentInConditionalExpression")]
        public ParseResult ParseArguments(IEnumerable<string> args)
        {
            var error = false;
            var showHelp = false;

            // Parse Args
            var optionsSet = new OptionSet
            {
                {
                    "debug", "enable debug output                               " +
                             "env var KUBE_SECRET_FS_DEBUG set to '1' or 'true'",
                    v => Debug = v != null
                },
                {
                    "baseDir=", "base dir used for filesystem caching             " +
                                "defaults to <temp dir>/kube-secret-fs            " +
                                "env var KUBE_SECRET_FS_BASE_DIR                  ",
                    v => BaseDir = v
                },
                {
                    "h|help", "show this message and exit",
                    v => showHelp = v != null
                },
                {
                    "secretBaseName=", "kubernetes secret base name to store data in  " +
                                       "env var KUBE_SECRET_FS_SECRET_BASE_NAME       " +
                                       "defaults to 'kube-secret-fs'",
                    v => SecretBaseName = v
                },
                {
                    "secretNamespace=", "kubernetes secret namespace                   " +
                                        "env var KUBE_SECRET_FS_SECRET_NAMESPACE       " +
                                        "defaults to same namespace as pod",
                    v => SecretNamespace = v
                },
            };

            var extraArgs = optionsSet.Parse(args);
            foreach (var extraArg in extraArgs)
            {
                if (!extraArg.StartsWith("-") && MountPoint == null)
                    MountPoint = extraArg;
                else
                    WriteWarning($"unknown arg: '{extraArg}'");
            }

            if (showHelp)
            {
                WriteHelp(optionsSet);
                return ParseResult.Help;
            }

            // Parse Env Vars
            BaseDir = Environment.GetEnvironmentVariable("KUBE_SECRET_FS_BASE_DIR") ?? BaseDir;
            Debug = Debug
                    || Environment.GetEnvironmentVariable("KUBE_SECRET_FS_DEBUG") == "1"
                    || Environment.GetEnvironmentVariable("KUBE_SECRET_FS_DEBUG") == "true";
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
                WriteError("missing mountPoint arg");

            if (error |= SecretNamespace == null)
                WriteError("unable to determine default for --secretNamespace; must be set explicitly");

            WriteDebug($"Options:");
            WriteDebug($"BaseDir: {BaseDir}");
            WriteDebug($"MountPoint: {MountPoint}");
            WriteDebug($"SecretBaseName: {SecretBaseName}");
            WriteDebug($"SecretNamespace: {SecretNamespace}");

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

        public void WriteError(string message)
        {
            Console.Error.WriteLine("kube-secret-fs: error: {0}", message);
        }

        public void WriteWarning(string message)
        {
            Console.Error.WriteLine("kube-secret-fs: warning: {0}", message);
        }

        public void WriteDebug(string message)
        {
            if (Debug)
            {
                Console.Error.WriteLine("kube-secret-fs: debug: {0}", message);
            }
        }
    }

    public enum ParseResult
    {
        Valid,
        Help,
        Error
    }
}