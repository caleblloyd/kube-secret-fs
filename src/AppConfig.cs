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
                                "env var KUBE_SECRET_FS_BASE_DIR                  " +
                                "defaults to <temp dir>/kube-secret-fs",
                    v => BaseDir = v
                },
                {
                    "h|help", "show this message and exit",
                    v => showHelp = v != null
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

            // Set Defaults
            if (BaseDir == null)
            {
                BaseDir = Path.Combine(Path.GetTempPath(), "kube-secret-fs");
                Directory.CreateDirectory(BaseDir!);
            }

            if (error |= MountPoint == null) WriteError("missing mountPoint arg");
            
            WriteDebug($"Options:");
            WriteDebug($"BaseDir: {BaseDir}");
            WriteDebug($"MountPoint: {MountPoint}");

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

        private static void WriteError(string message)
        {
            Console.Error.WriteLine("kube-secret-fs: error: {0}", message);
        }

        private static void WriteWarning(string message)
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