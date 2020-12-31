#!/bin/sh -e

# wait for mount
while [ "$(findmnt -no FSTYPE "/mnt")" != "fuse.KubeSecretFS.dll" ]; do sleep 0.1; done

cd "$(dirname "$0")"

# extract to test directory
rm -rf /tmp/test
mkdir /tmp/test
tar -xzf data/test.tar.gz -C /tmp/test
dd if=/dev/urandom of=/tmp/test/random bs=1M count=2

# extract to mount
find /mnt -xdev -mindepth 1 -maxdepth 1 -exec rm -rf {} \;
tar -xzf data/test.tar.gz -C /mnt
cp /tmp/test/random /mnt/random
sync
