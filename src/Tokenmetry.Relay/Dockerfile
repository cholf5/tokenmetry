# Tokenmetry Relay 自建镜像（docs/selfhost.md）：多阶段构建，发布单文件。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Tokenmetry.Relay -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# 数据卷：库文件默认落 /data（TOKENMETRY_RELAY_DB 可覆盖）。
ENV TOKENMETRY_RELAY_DB=/data/relay.db
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["./Tokenmetry.Relay"]
