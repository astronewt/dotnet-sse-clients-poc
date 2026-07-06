# Sse SSE server — ASP.NET Core Minimal API. Cleartext HTTP; the edge terminates TLS/HTTP2.
# Build context = repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Sse.Server/Sse.Server.csproj src/Sse.Server/
RUN dotnet restore src/Sse.Server/Sse.Server.csproj
COPY src/ src/
RUN dotnet publish src/Sse.Server/Sse.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sse.Server.dll"]
