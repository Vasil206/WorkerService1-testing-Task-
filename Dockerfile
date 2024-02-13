FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS build

COPY . /source

WORKDIR /source

RUN dotnet publish -c Release --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine AS final
WORKDIR /app

COPY --from=build /app .

EXPOSE 1234

ENTRYPOINT ["dotnet", "WorkerService1.dll"]
