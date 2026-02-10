FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["proto/", "proto/"]
COPY ["src/Andy.Containers/Andy.Containers.csproj", "src/Andy.Containers/"]
COPY ["src/Andy.Containers.Api/Andy.Containers.Api.csproj", "src/Andy.Containers.Api/"]
COPY ["src/Andy.Containers.Infrastructure/Andy.Containers.Infrastructure.csproj", "src/Andy.Containers.Infrastructure/"]
RUN dotnet restore "src/Andy.Containers.Api/Andy.Containers.Api.csproj"
COPY . .
WORKDIR "/src/src/Andy.Containers.Api"
RUN dotnet build "Andy.Containers.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Andy.Containers.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Andy.Containers.Api.dll"]
