# NounoursAutoRegie (régie + moteur) en conteneur Docker.
# Construction depuis la racine du dépôt :
#   docker build -f docker/Dockerfile -t nounoursautoregie .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/NAR.Server/NAR.Server.csproj -c Release -o /app \
 && dotnet publish src/NAR.Engine/NAR.Engine.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# /data : base, vignettes, HLS, journaux  —  /media : vos fichiers (à ajouter dans la médiathèque)
VOLUME ["/data", "/media"]
EXPOSE 8090
ENV Nar__DataDirectory=/data \
    Nar__HttpPort=8090 \
    Nar__EnginePath=/app/NAR.Engine.dll
ENTRYPOINT ["dotnet", "NAR.Server.dll"]
