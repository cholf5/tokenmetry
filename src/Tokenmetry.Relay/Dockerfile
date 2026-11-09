# Tokenmetry Relay 自建镜像（docs/selfhost.md）：多阶段构建，发布单文件。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Tokenmetry.Relay -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# 编排存活检查需要 curl（aspnet 运行时镜像不带），HEALTHCHECK 打匿名 /health。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

# 数据卷：库文件默认落 /data（TOKENMETRY_RELAY_DB 可覆盖）。
ENV TOKENMETRY_RELAY_DB=/data/relay.db
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["./Tokenmetry.Relay"]
