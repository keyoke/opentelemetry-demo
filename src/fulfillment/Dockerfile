# Copyright The OpenTelemetry Authors
# SPDX-License-Identifier: Apache-2.0

FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:8.0 AS builder
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["/src/fulfillment/", "Fulfillment/"]
RUN dotnet restore "./Fulfillment/Fulfillment.csproj" -r linux-$TARGETARCH
WORKDIR "/src/Fulfillment"

RUN dotnet build "./Fulfillment.csproj" -r linux-$TARGETARCH -c $BUILD_CONFIGURATION -o /app/build

# -----------------------------------------------------------------------------

FROM builder AS publish
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Fulfillment.csproj" -r linux-$TARGETARCH -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# -----------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/aspnet:8.0
USER app
WORKDIR /app
COPY --from=publish /app/publish .

EXPOSE ${FULFILLMENT_PORT}

ENTRYPOINT ["dotnet", "Fulfillment.dll"]
