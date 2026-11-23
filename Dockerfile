FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ClinicDesk.Domain/ClinicDesk.Domain.csproj src/ClinicDesk.Domain/
COPY src/ClinicDesk.Api/ClinicDesk.Api.csproj src/ClinicDesk.Api/
RUN dotnet restore src/ClinicDesk.Api/ClinicDesk.Api.csproj
COPY src/ src/
RUN dotnet publish src/ClinicDesk.Api/ClinicDesk.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 Urls=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ClinicDesk.Api.dll"]
