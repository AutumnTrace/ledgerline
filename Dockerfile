# syntax=docker/dockerfile:1

# The SDK stage always runs on the build machine's architecture and cross-compiles for the target,
# so multi-arch images (linux/amd64, linux/arm64) need no emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first so the dependency layer is cached between code changes.
COPY global.json Directory.Build.props Directory.Packages.props Ledgerline.slnx .editorconfig ./
COPY src/Ledgerline.Domain/Ledgerline.Domain.csproj src/Ledgerline.Domain/
COPY src/Ledgerline.Application/Ledgerline.Application.csproj src/Ledgerline.Application/
COPY src/Ledgerline.Infrastructure/Ledgerline.Infrastructure.csproj src/Ledgerline.Infrastructure/
COPY src/Ledgerline.Api/Ledgerline.Api.csproj src/Ledgerline.Api/
RUN dotnet restore src/Ledgerline.Api/Ledgerline.Api.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/Ledgerline.Api/Ledgerline.Api.csproj -c Release -a $TARGETARCH --no-self-contained --no-restore -o /app \
    && mkdir -p /data

# Chiseled Ubuntu: no shell, no package manager, non-root by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
# A writable directory for the SQLite file when the image runs without PostgreSQL.
COPY --from=build --chown=1654:1654 /data /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Ledger="Data Source=/data/ledgerline.db" \
    Serilog__WriteTo__0__Name=Console \
    Serilog__WriteTo__0__Args__formatter="Serilog.Formatting.Compact.RenderedCompactJsonFormatter, Serilog.Formatting.Compact"

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Ledgerline.Api.dll"]
