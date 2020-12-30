using System.IO;
using System.Text;

namespace KubeSecretFS
{
    public static class Utils
    {
        public static readonly Encoding Utf8EncodingNoBom = new UTF8Encoding(false);

        // https://stackoverflow.com/a/2766718
        public static void CleanFolder(string dirName)
        {
            var dir = new DirectoryInfo(dirName);
            foreach (var file in dir.GetFiles())
            {
                file.Delete();
            }

            foreach (var subDir in dir.GetDirectories())
            {
                CleanFolder(subDir.FullName);
                subDir.Delete();
            }
        }
    }
}
