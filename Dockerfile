# Один образ: сайт собирается и кладётся рядом с API, который отдаёт его сам.
# Так у сервиса один адрес: не нужен CORS, поток событий и вход работают без настройки доменов.
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# CRM команды продукта — отдельное приложение по адресу /crm/.
FROM node:22-alpine AS crm
WORKDIR /crm
COPY crm/package*.json ./
RUN npm ci
COPY crm/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY backend/src/Ostatok.Api/ ./
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
COPY --from=crm /crm/dist ./wwwroot/crm
ENV ASPNETCORE_ENVIRONMENT=Production
# Хостинг сообщает порт переменной PORT; локально — 8080.
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet Ostatok.Api.dll"]
