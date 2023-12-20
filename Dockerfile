# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS build
ARG TARGETARCH

COPY . /source

WORKDIR /source

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish -a ${TARGETARCH/amd64/x64} --use-current-runtime --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine AS final
WORKDIR /app

COPY --from=build /app .

EXPOSE 1234:1234
VOLUME worker-app:/app

ENTRYPOINT ["dotnet", "WorkerService1.dll"]
