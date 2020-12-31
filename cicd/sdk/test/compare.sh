#!/bin/sh -e

cd "$(dirname "$0")"

# wait for mount
echo "waiting for FUSE mount to start" >&2
while ! findmnt -no FSTYPE "/mnt" | grep -q '^fuse\.KubeSecretFS'; do sleep 0.1; done
echo "FUSE mount started" >&2

# diff directories
diff -qr /mnt /tmp/test
