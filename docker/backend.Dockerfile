ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-preview

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY backend/Aura.Api/Aura.Api.csproj backend/Aura.Api/
RUN dotnet restore backend/Aura.Api/Aura.Api.csproj

COPY backend/Aura.Api/ backend/Aura.Api/
WORKDIR /src/backend/Aura.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
EXPOSE 5000

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "Aura.Api.dll"]
