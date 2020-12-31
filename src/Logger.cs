using System;

namespace KubeSecretFS
{
    public class Logger
    {
        public bool LogLeveDebug { get; set; }

        public void Error(string message)
        {
            Console.Error.WriteLine("kube-secret-fs: error: {0}", message);
        }

        public void Warning(string message)
        {
            Console.Error.WriteLine("kube-secret-fs: warning: {0}", message);
        }

        public void Debug(string message)
        {
            if (LogLeveDebug)
            {
                Console.Error.WriteLine("kube-secret-fs: debug: {0}", message);
            }
        }
    }
}