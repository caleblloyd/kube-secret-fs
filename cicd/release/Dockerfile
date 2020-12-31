# dotnet-sdk base image
FROM mcr.microsoft.com/dotnet/sdk:5.0-buster-slim AS sdk

COPY /KubeSecretFS.csproj /build/KubeSecretFS.csproj
WORKDIR /build
RUN dotnet restore

COPY /src /build/src
RUN dotnet publish -c Release -o /app

# final image
FROM mcr.microsoft.com/dotnet/runtime-deps:5.0-buster-slim

RUN apt update \
    && apt install -y --no-install-recommends \
        fuse \
        libglib2.0-0 \
        tini \
    && rm -rf /var/lib/apt/lists/*

COPY --from=sdk /app /app
ENTRYPOINT ["tini", "-g", "--", "/app/KubeSecretFS"]
