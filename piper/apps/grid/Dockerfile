# The grid app's image: the app compiled by the piper compiler in this repository, on the ASP.NET Core runtime (the
# remote actors plugin's gRPC runs on its server). Build it from the piper folder:
#
#   docker build -f apps/grid/Dockerfile -t grid .
#
# The container takes the app's command (coordinator or worker) and options; see k8s/grid.yaml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The compiler, with the plugins that come with it; then the app, which brings in Proto.Actor's packages.
RUN dotnet build src/Piper.Cli -c Release \
 && dotnet src/Piper.Cli/bin/Release/net10.0/piper.dll build apps/grid --exe -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8000 8090
ENTRYPOINT ["dotnet", "grid.dll"]
