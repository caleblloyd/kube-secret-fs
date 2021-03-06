// adapted from the following files:
// https://github.com/alhimik45/Mono.Fuse.NETStandard/blob/master/example/RedirectFS-FH/RedirectFS-FH.cs
// https://github.com/jonpryor/mono-fuse/blob/master/example/RedirectFS/RedirectFS-FH.cs
// https://github.com/libfuse/libfuse/blob/fuse_2_9_5/example/fusexmp.c

using System;
using System.Collections.Generic;
using System.Text;
using Mono.Fuse.NETStandard;
using Mono.Unix.Native;

namespace KubeSecretFS
{
    public class KubeSecretFS : FileSystem
    {
        private readonly AppConfig _config;
        private readonly Sync _sync;

        public KubeSecretFS(AppConfig config, Sync sync)
        {
            _config = config;
            _sync = sync;
        }

        protected override Errno OnGetPathStatus(string path, out Stat buf)
        {
            var r = Syscall.lstat(_config.BaseDir + path, out buf);
            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override Errno OnGetHandleStatus(string path, OpenedPathInfo info, out Stat buf)
        {
            var r = Syscall.fstat((int) info.Handle, out buf);
            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override Errno OnAccessPath(string path, AccessModes mask)
        {
            var r = Syscall.access(_config.BaseDir + path, mask);
            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override Errno OnReadSymbolicLink(string path, out string target)
        {
            target = null;
            var buf = new StringBuilder(256);
            do
            {
                var r = Syscall.readlink(_config.BaseDir + path, buf);
                if (r < 0) return Stdlib.GetLastError();

                if (r == buf.Capacity)
                {
                    buf.Capacity *= 2;
                }
                else
                {
                    target = buf.ToString(0, r);
                    return 0;
                }
            } while (true);
        }

        protected override Errno OnOpenDirectory(string path, OpenedPathInfo info)
        {
            var guardOp = info.OpenAccess != 0;
            Func<Errno> operationFn = () =>
            {
                var dp = Syscall.opendir(_config.BaseDir + path);
                if (dp == IntPtr.Zero)
                    return Stdlib.GetLastError();

                info.Handle = dp;
                return 0;
            };
            return guardOp
                ? _sync.OperationAsync($"OnOpenDirectory {path}", false, operationFn)
                    .GetAwaiter()
                    .GetResult()
                : operationFn.Invoke();
        }

        protected override Errno OnReadDirectory(string path, OpenedPathInfo fi,
            out IEnumerable<DirectoryEntry> paths)
        {
            var dp = fi.Handle;
            paths = ReadDirectory(dp);
            return 0;
        }

        private static IEnumerable<DirectoryEntry> ReadDirectory(IntPtr dp)
        {
            Dirent de;
            while ((de = Syscall.readdir(dp)) != null)
            {
                var e = new DirectoryEntry(de.d_name)
                {
                    Stat = {st_ino = de.d_ino, st_mode = (FilePermissions) (de.d_type << 12)}
                };
                yield return e;
            }
        }

        protected override Errno OnReleaseDirectory(string path, OpenedPathInfo info)
        {
            var guardOp = info.OpenAccess != 0;
            Func<Errno> operationFn = () =>
            {
                var dp = info.Handle;
                Syscall.closedir(dp);
                return 0;
            };
            return guardOp
                ? _sync.OperationAsync($"OnReleaseDirectory {path}", true, operationFn)
                    .GetAwaiter()
                    .GetResult()
                : operationFn.Invoke();
        }

        protected override Errno OnSynchronizeDirectory(string directory, OpenedPathInfo info, bool onlyUserData)
        {
            var guardOp = info.OpenAccess != 0;
            Func<Errno> operationFn = () =>
            {
                var r = onlyUserData ? Syscall.fdatasync((int) info.Handle) : Syscall.fsync((int) info.Handle);
                return r == -1 ? Stdlib.GetLastError() : 0;
            };
            return guardOp
                ? _sync.OperationAsync($"OnSynchronizeDirectory {directory}", true, operationFn)
                    .GetAwaiter()
                    .GetResult()
                : operationFn.Invoke();
        }

        protected override Errno OnCreateSpecialFile(string path, FilePermissions mode, ulong rdev)
        {
            return _sync.OperationAsync($"OnCreateSpecialFile {path}", true, () =>
            {
                int r;

                // On Linux, this could just be `mknod(basedir+path, mode, rdev)' but 
                // this is more portable.
                if ((mode & FilePermissions.S_IFMT) == FilePermissions.S_IFREG)
                {
                    r = Syscall.open(_config.BaseDir + path, OpenFlags.O_CREAT | OpenFlags.O_EXCL |
                                                             OpenFlags.O_WRONLY, mode);
                    if (r >= 0)
                        r = Syscall.close(r);
                }
                else if ((mode & FilePermissions.S_IFMT) == FilePermissions.S_IFIFO)
                {
                    r = Syscall.mkfifo(_config.BaseDir + path, mode);
                }
                else
                {
                    r = Syscall.mknod(_config.BaseDir + path, mode, rdev);
                }

                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnCreateDirectory(string path, FilePermissions mode)
        {
            return _sync.OperationAsync($"OnCreateDirectory {path}", true, () =>
            {
                var r = Syscall.mkdir(_config.BaseDir + path, mode);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnRemoveFile(string path)
        {
            return _sync.OperationAsync($"OnRemoveFile {path}", true, () =>
            {
                var r = Syscall.unlink(_config.BaseDir + path);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnRemoveDirectory(string path)
        {
            return _sync.OperationAsync($"OnRemoveDirectory {path}", true, () =>
            {
                var r = Syscall.rmdir(_config.BaseDir + path);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnCreateSymbolicLink(string from, string to)
        {
            return _sync.OperationAsync($"OnCreateSymbolicLink {from} {to}", true, () =>
            {
                var r = Syscall.symlink(from, _config.BaseDir + to);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnRenamePath(string from, string to)
        {
            return _sync.OperationAsync($"OnRenamePath {from} {to}", true, () =>
            {
                var r = Stdlib.rename(_config.BaseDir + from, _config.BaseDir + to);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnCreateHardLink(string from, string to)
        {
            return _sync.OperationAsync($"OnCreateHardLink {from} {to}", true, () =>
            {
                var r = Syscall.link(_config.BaseDir + from, _config.BaseDir + to);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnChangePathPermissions(string path, FilePermissions mode)
        {
            return _sync.OperationAsync($"OnChangePathPermissions {path}", true, () =>
            {
                var r = Syscall.chmod(_config.BaseDir + path, mode);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnChangePathOwner(string path, long uid, long gid)
        {
            return _sync.OperationAsync($"OnChangePathOwner {path}", true, () =>
            {
                var r = Syscall.lchown(_config.BaseDir + path, (uint) uid, (uint) gid);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnTruncateFile(string path, long size)
        {
            return _sync.OperationAsync($"OnTruncateFile {path}", true, () =>
            {
                var r = Syscall.truncate(_config.BaseDir + path, size);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnTruncateHandle(string path, OpenedPathInfo info, long size)
        {
            return _sync.OperationAsync($"OnTruncateHandle {path}", false, () =>
            {
                var r = Syscall.ftruncate((int) info.Handle, size);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnChangePathTimes(string path, ref Utimbuf buf)
        {
            var bufVal = buf;
            var result = _sync.OperationAsync($"OnChangePathTimes {path}", true, () =>
            {
                var r = Syscall.utime(_config.BaseDir + path, ref bufVal);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
            buf = bufVal;
            return result;
        }

        protected override Errno OnCreateHandle(string path, OpenedPathInfo info, FilePermissions mode)
        {
            return _sync.OperationAsync($"OnCreateHandle {path}", false, () =>
            {
                var fd = Syscall.open(_config.BaseDir + path, info.OpenFlags, mode);
                if (fd == -1)
                    return Stdlib.GetLastError();
                info.Handle = (IntPtr) fd;
                return 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnOpenHandle(string path, OpenedPathInfo info)
        {
            var guardOp = info.OpenAccess != 0;
            Func<Errno> operationFn = () =>
            {
                var fd = Syscall.open(_config.BaseDir + path, info.OpenFlags);
                if (fd == -1)
                    return Stdlib.GetLastError();
                info.Handle = (IntPtr) fd;
                return 0;
            };
            return guardOp
                ? _sync.OperationAsync($"OnOpenHandle {path}", false, operationFn)
                    .GetAwaiter()
                    .GetResult()
                : operationFn.Invoke();
        }

        protected override unsafe Errno OnReadHandle(string path, OpenedPathInfo info, byte[] buf,
            long offset, out int bytesRead)
        {
            int r;
            fixed (byte* pb = buf)
            {
                r = bytesRead = (int) Syscall.pread((int) info.Handle,
                    pb, (ulong) buf.Length, offset);
            }

            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override unsafe Errno OnWriteHandle(string path, OpenedPathInfo info,
            byte[] buf, long offset, out int bytesWritten)
        {
            var bytesWrittenVal = 0;
            var result = _sync.OperationAsync($"OnWriteHandle {path}", false, () =>
            {
                int r;
                fixed (byte* pb = buf)
                {
                    r = bytesWrittenVal = (int) Syscall.pwrite((int) info.Handle,
                        pb, (ulong) buf.Length, offset);
                }

                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
            bytesWritten = bytesWrittenVal;
            return result;
        }

        protected override Errno OnGetFileSystemStatus(string path, out Statvfs stbuf)
        {
            var r = Syscall.statvfs(_config.BaseDir + path, out stbuf);
            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override Errno OnFlushHandle(string path, OpenedPathInfo info)
        {
            /* This is called from every close on an open file, so call the
               close on the underlying filesystem.  But since flush may be
               called multiple times for an open file, this must not really
               close the file.  This is important if used on a network
               filesystem like NFS which flush the data/metadata on close() */
            var r = Syscall.close(Syscall.dup((int) info.Handle));
            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override Errno OnReleaseHandle(string path, OpenedPathInfo info)
        {
            var guardOp = info.OpenAccess != 0;
            Func<Errno> operationFn = () =>
            {
                var r = Syscall.close((int) info.Handle);
                return r == -1 ? Stdlib.GetLastError() : 0;
            };
            return guardOp
                ? _sync.OperationAsync($"OnReleaseHandle {path}", true, operationFn)
                    .GetAwaiter()
                    .GetResult()
                : operationFn.Invoke();
        }

        protected override Errno OnSynchronizeHandle(string path, OpenedPathInfo info, bool onlyUserData)
        {
            var guardOp = info.OpenAccess != 0;
            Func<Errno> operationFn = () =>
            {
                var r = onlyUserData ? Syscall.fdatasync((int) info.Handle) : Syscall.fsync((int) info.Handle);
                return r == -1 ? Stdlib.GetLastError() : 0;
            };
            return guardOp
                ? _sync.OperationAsync($"OnSynchronizeHandle {path}", true, operationFn)
                    .GetAwaiter()
                    .GetResult()
                : operationFn.Invoke();
        }

        protected override Errno OnSetPathExtendedAttribute(string path, string name, byte[] value, XattrFlags flags)
        {
            return _sync.OperationAsync($"OnSetPathExtendedAttribute {path}", true, () =>
            {
                var r = Syscall.lsetxattr(_config.BaseDir + path, name, value, (ulong) value.Length, flags);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnGetPathExtendedAttribute(string path, string name, byte[] value,
            out int bytesWritten)
        {
            var r = bytesWritten =
                (int) Syscall.lgetxattr(_config.BaseDir + path, name, value, (ulong) (value?.Length ?? 0));
            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override Errno OnListPathExtendedAttributes(string path, out string[] names)
        {
            var r = (int) Syscall.llistxattr(_config.BaseDir + path, out names);
            return r == -1 ? Stdlib.GetLastError() : 0;
        }

        protected override Errno OnRemovePathExtendedAttribute(string path, string name)
        {
            return _sync.OperationAsync($"OnRemovePathExtendedAttribute {path}", true, () =>
            {
                var r = Syscall.lremovexattr(_config.BaseDir + path, name);
                return r == -1 ? Stdlib.GetLastError() : 0;
            }).GetAwaiter().GetResult();
        }

        protected override Errno OnLockHandle(string file, OpenedPathInfo info, FcntlCommand cmd, ref Flock @lock)
        {
            var r = Syscall.fcntl((int) info.Handle, cmd, ref @lock);
            return r == -1 ? Stdlib.GetLastError() : 0;
        }
    }
}