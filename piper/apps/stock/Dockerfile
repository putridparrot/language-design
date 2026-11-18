# The stock app's image: the app compiled by the piper compiler in this repository, on the ASP.NET Core runtime (the
# remote actors plugin's nodes talk over HTTP/2 on its Kestrel). Build it from the piper folder:
#
#   docker build -f apps/stock/Dockerfile -t stock .
#
# The container takes the app's options: --host (the name other nodes reach it by), --nodes (all of them), and
# --port and --http (8000 and 8080 by default).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The compiler, with the plugins that come with it; then the app.
RUN dotnet build src/Piper.Cli -c Release \
 && dotnet src/Piper.Cli/bin/Release/net10.0/piper.dll build apps/stock --exe -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8000 8080
ENTRYPOINT ["dotnet", "stock.dll"]
