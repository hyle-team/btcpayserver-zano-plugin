# Zano BTCPay Server Plugin

Accept [Zano](https://zano.org) payments in BTCPay Server. Privacy-focused cryptocurrency with confidential transactions and integrated addresses.

> [!WARNING]
> This plugin shares a single Zano wallet across all stores in the BTCPay Server instance. Use this plugin only if you are not sharing your instance.

## How it works

When a customer creates an invoice, the plugin generates a unique **integrated address** (containing an embedded payment ID) for each payment. A background poller checks the wallet every 15 seconds for incoming payments matching pending invoices, tracks confirmations, and settles invoices based on your configured threshold.

## Configuration

| Environment variable | Required | Description | Example |
| --- | --- | --- | --- |
| `BTCPAY_ZANO_DAEMON_URI` | Yes | URI of the zanod RPC interface | `http://127.0.0.1:11211` |
| `BTCPAY_ZANO_WALLET_DAEMON_URI` | Yes | URI of the simplewallet RPC interface | `http://127.0.0.1:11212` |
| `BTCPAY_ZANO_WALLET_DAEMON_WALLETDIR` | No | Directory where wallet files are stored (for auto-loading on startup) | `/wallet` |
| `BTCPAY_ZANO_EXTRA_ASSETS` | No | Confidential Assets to accept in addition to native ZANO. See [Confidential Assets](#confidential-assets) below. | `USDZ\|<asset_id>\|6\|USD Zano\|pegged_usd` |

## Confidential Assets

Beyond native ZANO, the plugin can accept any Zano **Confidential Asset** (USDz, BTCz, custom tokens, etc.). Each extra asset is registered as its own BTCPay payment method (e.g. `ZANOUSDZ`) sharing the daemon and wallet configured above.

Extra assets are declared via the `BTCPAY_ZANO_EXTRA_ASSETS` environment variable. Multiple assets are separated by `;`; fields within each asset are separated by `|`:

```
TICKER|asset_id|decimals|DisplayName|rate_mode|rate_param
```

- **`TICKER`** — short symbol (uppercase in the final crypto code; e.g. `USDZ` → payment method `ZANOUSDZ`). Must not be `ZANO`.
- **`asset_id`** — 64-hex Zano asset ID. Must not equal the native ZANO asset ID.
- **`decimals`** — atomic-unit divisibility (integer, 0–18).
- **`DisplayName`** *(optional)* — shown in the store wallet nav and checkout. Falls back to the ticker.
- **`rate_mode`** *(optional)* — how BTCPay converts invoice currency → asset amount. One of:
  - `none` *(default)* — no automatic rate; configure manually in BTCPay's rate rules.
  - `pegged_usd` — `<CODE>_X = USD_X` (1:1 USD stablecoin).
  - `fixed_usd` — fixed price in USD (`rate_param` = USD price, e.g. `0.50`).
  - `coingecko` — *(accepted but currently a no-op)* BTCPay's rate-rule parser rejects hyphenated coingecko ids; until that's worked around, this mode records the coin id but emits no default rule. Merchants must configure the rate manually in BTCPay's UI.
- **`rate_param`** *(required for `fixed_usd` and `coingecko`)* — see above.

### Example

```
BTCPAY_ZANO_EXTRA_ASSETS=USDZ|0123...cdef|6|USD Zano|pegged_usd;;BTCZ|fedc...4567|12|Wrapped BTC|coingecko|wrapped-bitcoin
```

Registers two extra payment methods in addition to native ZANO:

- `ZANOUSDZ` — "USD Zano", 6 decimals, pegged 1:1 to USD.
- `ZANOBTCZ` — "Wrapped BTC", 12 decimals, priced via CoinGecko's `wrapped-bitcoin` feed.

Each extra asset appears under **Store Settings > Zano** with its own enable/disable toggle and confirmation-threshold setting. The checkout badge shows the asset ticker, and the payment URI includes `&asset_id=…` so customer wallets can pre-select the correct asset.

### Notes & limits

- Assets share the single daemon/wallet configured via `BTCPAY_ZANO_DAEMON_URI` / `BTCPAY_ZANO_WALLET_DAEMON_URI`. One wallet, many assets.
- Adding, removing, or changing an extra asset requires a BTCPay Server restart (payment-method registration is performed once at startup).
- If `BTCPAY_ZANO_EXTRA_ASSETS` is malformed, only the extras are disabled — native ZANO continues to work.

## Setup

### 1. Run Zano daemon

```bash
zanod --rpc-bind-ip=0.0.0.0 --rpc-bind-port=11211
```

### 2. Run Zano wallet in RPC mode

Create or open a wallet, then start simplewallet with RPC enabled:

```bash
simplewallet --wallet-file /path/to/wallet \
  --password "your-password" \
  --daemon-address 127.0.0.1:11211 \
  --rpc-bind-port 11212
```

For receive-only setups, create a watch-only wallet first:

```bash
simplewallet --generate-new-wallet /path/to/wallet
# Then from the wallet prompt:
save_watch_only /path/to/watch-only-wallet password
```

### 3. Install the plugin

In BTCPay Server, go to **Server Settings > Plugins** and install the Zano plugin. Then configure the environment variables above and restart.

### 4. Enable Zano for your store

Go to **Store Settings > Zano** to enable it and set your preferred confirmation threshold.

## Docker

A Docker image with zanod and simplewallet is available:

```bash
docker pull pavelravaga/zano:2.2.0.455
```

Run the daemon:
```bash
docker run -d --name zanod \
  -p 11211:11211 \
  -v zano_data:/data \
  pavelravaga/zano:2.2.0.455
```

Run the wallet:
```bash
docker run -d --name zano_wallet \
  -p 11212:11212 \
  -v zano_wallet:/wallet \
  --entrypoint simplewallet \
  pavelravaga/zano:2.2.0.455 \
  --rpc-bind-ip=0.0.0.0 --rpc-bind-port=11212 \
  --daemon-address=zanod:11211 \
  --wallet-file=/wallet/wallet --password=""
```

## Development

### Requirements

- .NET 8.0 SDK
- Git
- Docker and Docker Compose

### Clone and build

```bash
git clone --recurse-submodules https://github.com/hyle-team/btcpayserver-zano-plugin
cd btcpayserver-zano-plugin
dotnet build btcpay-zano-plugin.sln
```

### Run unit tests

```bash
dotnet test BTCPayServer.Plugins.UnitTests
```

### Run integration tests

```bash
docker compose -f BTCPayServer.Plugins.IntegrationTests/docker-compose.yml run tests
```

### Local development

Start the dev dependencies:

```bash
cd BTCPayServer.Plugins.IntegrationTests/
docker compose up -d dev
```

Create `appsettings.dev.json` in `btcpayserver/BTCPayServer`:

```json
{
  "DEBUG_PLUGINS": "../../Plugins/Zano/bin/Debug/net8.0/BTCPayServer.Plugins.Zano.dll",
  "ZANO_DAEMON_URI": "http://127.0.0.1:11211",
  "ZANO_WALLET_DAEMON_URI": "http://127.0.0.1:11212"
}
```

Then run BTCPay Server with the plugin loaded.

## Technical details

- **Address generation**: Integrated addresses with random 8-byte payment IDs (unique per invoice)
- **Payment detection**: Polls `get_bulk_payments` every 15 seconds
- **Fee**: Fixed 0.01 ZANO per transaction
- **Divisibility**: 12 decimal places
- **Rate source**: CoinGecko (`ZANO_BTC`)
- **Confirmations**: Configurable (0, 1, 10, or custom)

## License

[MIT](LICENSE.md)
