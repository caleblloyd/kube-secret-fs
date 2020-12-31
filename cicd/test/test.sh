#!/bin/sh -e

cd "$(dirname "$0")"

../../compose.sh up

docker exec ksfs-dotnet /test/initial.sh
docker stop ksfs-dotnet
docker start ksfs-dotnet
docker exec ksfs-dotnet /test/compare.sh
