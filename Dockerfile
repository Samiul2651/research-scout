# Research Scout API — multi-stage build.
# Build:  docker build -t research-scout .
# Run:    docker run -p 8080:8080 --env-file .env research-scout

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached until a project file changes).
COPY global.json Directory.Build.props ResearchScout.slnx ./
COPY src/ResearchScout.Harness/ResearchScout.Harness.csproj src/ResearchScout.Harness/
COPY src/ResearchScout.Llm/ResearchScout.Llm.csproj         src/ResearchScout.Llm/
COPY src/ResearchScout.Infra/ResearchScout.Infra.csproj     src/ResearchScout.Infra/
COPY src/ResearchScout.Papers/ResearchScout.Papers.csproj   src/ResearchScout.Papers/
COPY src/ResearchScout.Scout/ResearchScout.Scout.csproj     src/ResearchScout.Scout/
COPY src/ResearchScout.Api/ResearchScout.Api.csproj         src/ResearchScout.Api/
RUN dotnet restore src/ResearchScout.Api/ResearchScout.Api.csproj

COPY .editorconfig profile.md ./
COPY src/ src/
RUN dotnet publish src/ResearchScout.Api/ResearchScout.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---------- run ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
COPY --from=build /app ./

# Render sets PORT; 8080 is the default otherwise. Runs as the image's non-root user.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimitPercent=75
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "ResearchScout.Api.dll"]
