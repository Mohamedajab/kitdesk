FROM node:24-alpine AS web
WORKDIR /src/web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/KitDesk/KitDesk.csproj src/KitDesk/packages.lock.json src/KitDesk/
RUN dotnet restore src/KitDesk --locked-mode
COPY src/ src/
COPY --from=web /src/src/KitDesk/wwwroot src/KitDesk/wwwroot
RUN dotnet publish src/KitDesk -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["sh", "-c", "dotnet KitDesk.dll --migrate && exec dotnet KitDesk.dll"]
