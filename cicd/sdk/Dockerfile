FROM boxboat/kubectl:1.20.0 AS kubectl

# dotnet-sdk base image
FROM mcr.microsoft.com/dotnet/sdk:5.0-buster-slim AS sdk

# propcs needed due to
# https://github.com/dotnet/dotnet-docker/issues/2396#issuecomment-728243108
RUN apt update \
    && apt install -y \
        fuse \
        libglib2.0-0 \
        procps \
    && rm -rf /var/lib/apt/lists/*

COPY --from=kubectl /usr/local/bin/kubectl /usr/local/bin/kubectl
