FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble

WORKDIR /source
COPY . .

ARG CONFIGURATION_NAME=Release
RUN dotnet restore btcpay-zano-plugin.sln \
    && dotnet build BTCPayServer.Plugins.IntegrationTests -c ${CONFIGURATION_NAME} --no-restore \
    && dotnet build Plugins/Zano/BTCPayServer.Plugins.Zano.csproj -c ${CONFIGURATION_NAME} --no-restore \
       --output /root/.btcpayserver/Plugins/BTCPayServer.Plugins.Zano
RUN pwsh BTCPayServer.Plugins.IntegrationTests/bin/${CONFIGURATION_NAME}/net10.0/playwright.ps1 install chromium --with-deps

WORKDIR /source/BTCPayServer.Plugins.IntegrationTests
ENV CONFIGURATION_NAME=${CONFIGURATION_NAME}
ENTRYPOINT ["./docker-entrypoint.sh"]