ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY tools/Aura.MediaAnalysis.ProviderSimulator/Aura.MediaAnalysis.ProviderSimulator.csproj tools/Aura.MediaAnalysis.ProviderSimulator/
RUN dotnet restore tools/Aura.MediaAnalysis.ProviderSimulator/Aura.MediaAnalysis.ProviderSimulator.csproj
COPY tools/Aura.MediaAnalysis.ProviderSimulator/ tools/Aura.MediaAnalysis.ProviderSimulator/
RUN dotnet publish tools/Aura.MediaAnalysis.ProviderSimulator/Aura.MediaAnalysis.ProviderSimulator.csproj -c Release -o /app --no-restore

FROM ${DOTNET_ASPNET_IMAGE}
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Aura.MediaAnalysis.ProviderSimulator.dll"]
