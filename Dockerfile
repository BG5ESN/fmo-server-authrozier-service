# syntax=docker/dockerfile:1

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY src/Sas.csproj src/
RUN dotnet restore src/Sas.csproj

COPY src/ src/

RUN dotnet publish src/Sas.csproj \
    -c Release \
    -o /out \
    --no-restore \
    -p:PublishSingleFile=false \
    -p:SelfContained=false \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

WORKDIR /app

COPY --from=build --chown=app:app /out/ ./

ENV HOME=/home/app
ENV DOTNET_RUNNING_IN_CONTAINER=true

RUN mkdir -p /home/app/.sas/roots \
    && chown -R app:app /home/app/.sas

USER app

VOLUME ["/home/app/.sas"]

EXPOSE 8080

ENTRYPOINT ["dotnet", "sas.dll"]
