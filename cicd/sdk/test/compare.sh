#!/bin/sh -e

while [ "$(findmnt -no FSTYPE "/mnt")" != "fuse.KubeSecretFS.dll" ]; do sleep 0.1; done

cd "$(dirname "$0")"
diff -qr /mnt /tmp/test
