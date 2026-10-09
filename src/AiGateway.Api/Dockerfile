# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json AiGateway.slnx ./
COPY src/ src/
RUN dotnet publish src/AiGateway.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Non-root; keyring directory must be a persistent private volume.
RUN mkdir -p /var/lib/aigateway/keyring && chown -R app:app /var/lib/aigateway
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiGateway.Api.dll"]
