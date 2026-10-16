FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json NuGet.Config ./
COPY src/DiscordMusicBot/DiscordMusicBot.csproj src/DiscordMusicBot/packages.lock.json ./src/DiscordMusicBot/
RUN dotnet restore src/DiscordMusicBot/DiscordMusicBot.csproj --locked-mode
COPY src/DiscordMusicBot/ ./src/DiscordMusicBot/
RUN dotnet publish src/DiscordMusicBot/DiscordMusicBot.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "DiscordMusicBot.dll"]
