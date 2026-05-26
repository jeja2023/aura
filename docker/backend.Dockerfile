# SDK 镜像与仓库根目录 global.json 中 sdk.version 对齐；ASP.NET 运行时镜像使用官方运行时标签。
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.201
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY Directory.Build.props global.json Aura.sln ./
COPY backend/Aura.Api/Aura.Api.csproj backend/Aura.Api/
COPY backend/Aura.DbMigrator/Aura.DbMigrator.csproj backend/Aura.DbMigrator/
RUN dotnet restore backend/Aura.Api/Aura.Api.csproj \
    && dotnet restore backend/Aura.DbMigrator/Aura.DbMigrator.csproj

COPY backend/Aura.Api/ backend/Aura.Api/
COPY backend/Aura.DbMigrator/ backend/Aura.DbMigrator/
WORKDIR /src/backend/Aura.Api
RUN dotnet publish -c Release -o /app/publish --no-restore
WORKDIR /src/backend/Aura.DbMigrator
RUN dotnet publish -c Release -o /app/migrator --no-restore

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app

RUN groupadd --system aura && useradd --system --gid aura --no-create-home --shell /usr/sbin/nologin aura

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
EXPOSE 5000

COPY --from=build --chown=aura:aura /app/publish ./
COPY --from=build --chown=aura:aura /app/migrator ./migrator/
COPY --chown=aura:aura Aura.sln ./
COPY --chown=aura:aura database ./database/

USER aura

ENTRYPOINT ["dotnet", "Aura.Api.dll"]
