FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/ArchiveImageLabler.csproj ./src/
COPY servicedefaults/ArchiveImageLabler.ServiceDefaults.csproj ./servicedefaults/
RUN dotnet restore ./src/ArchiveImageLabler.csproj

COPY . ./
RUN dotnet publish ./src/ArchiveImageLabler.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /library /app/data \
    && chown -R "$APP_UID":0 /app /library

COPY --from=build /app/publish ./

EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "ArchiveImageLabler.dll"]
