#!/bin/bash -e

cd "$(dirname "$0")/../../"

docker build -t ksfs -f cicd/release/Dockerfile .
