# dotnet-sdk base image
FROM mcr.microsoft.com/dotnet/sdk:5.0-buster-slim AS sdk

COPY /KubeSecretFS.csproj /build/KubeSecretFS.csproj
WORKDIR /build
RUN dotnet restore

COPY / /build
RUN dotnet publish -o /app

# final image
FROM mcr.microsoft.com/dotnet/runtime-deps:5.0-buster-slim

RUN apt update \
    && apt install -y \
        fuse \
        libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /mnt/test /mnt/redir
WORKDIR /mnt/test

COPY --from=sdk /app /app
CMD ["/app/KubeSecretFS", "/mnt/test", "/mnt/redir"]
