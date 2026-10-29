# BTCPay Server Zano Plugin — Testing Guide

## What is this?

A BTCPay Server plugin that lets merchants accept Zano (ZANO) payments. When a customer pays an invoice, the plugin:

1. Generates a unique **integrated address** (embeds a payment ID so each invoice gets its own address)
2. Shows a checkout page with QR code, address, and "Pay in wallet" button
3. Polls the Zano wallet every 15 seconds via `get_recent_txs_and_info2` RPC to detect incoming payments
4. Tracks confirmations and settles the invoice based on the merchant's threshold setting

Built by forking the Monero BTCPay plugin and adapting it for Zano's RPC API.

## QA Instance

- **URL**: https://testnet.btcpay.zano.me
- **Server**: obscura (157.180.1.140)
- **Daemon**: Zano testnet remote node (37.27.100.59:10505)
- **Wallet**: dedicated testnet receiving wallet configured for QA

---

## Manual Testing (QA Engineer)

### Prerequisites

- A Zano testnet wallet with some balance (get testnet ZANO from https://faucet.testnet.zano.org)
- A browser

### Test 1: Setup & Enable Zano

1. Go to https://testnet.btcpay.zano.me
2. Sign in with your QA account. Register an administrator only on a fresh disposable instance.
3. Create a store (any name)
4. In the left sidebar under **Wallets**, click **Zano**
5. **Verify**: The settings page shows:
   - Node available: True
   - Wallet RPC available: True
   - Synced: True (with current block height)
6. Check **Enabled**
7. Set "Consider the invoice settled when the payment transaction..." to **Zero Confirmation**
8. Click **Save**

**Expected**: Green success message, Zano stays enabled on page reload.

### Test 2: Create Invoice with Zano

1. Go to **Invoices** in the sidebar
2. Click **Create Invoice**
3. Set Amount: `1`, Currency: `ZANO`. For a USD-priced test, configure a working ZANO/USD rate source first.
4. Click **Create**
5. Click the invoice link to open the checkout page

**Expected**:
- Shows amount: `1.000000000000 ZANO`. The sender wallet pays its network fee separately; the plugin adds no payment-method fee.
- Shows an integrated address starting with `iZ`
- QR code is displayed
- "Pay in wallet" button is present (opens `zano:` URI)
- "View Details" shows the full address and amount breakdown

### Test 3: Pay an Invoice

1. Create an invoice as above
2. Copy the integrated address from the checkout page
3. From your testnet wallet, send the exact amount shown using the correct asset. For 1 ZANO, this is 1000000000000 atomic units. Keep enough native testnet ZANO for the wallet's separate network fee.
4. Wait up to 30 seconds on the checkout page

**Expected**:
- Checkout page updates to show "Processing" or "Paid"
- With Zero Confirmation setting, invoice should settle almost immediately
- Invoice details page shows payment with tx hash, confirmations count

### Test 4: Confirmation Tracking

1. Set confirmation threshold to **At Least One** in Zano settings
2. Create and pay a new invoice
3. Watch the invoice status

**Expected**:
- Invoice shows "Processing" immediately after payment detected
- After 1 block confirmation (~1 minute on testnet), status changes to "Settled"
- Confirmation count updates on each poll (every 15 seconds)

### Test 5: Multiple Invoices

1. Create 3 invoices with different amounts
2. Pay one of them
3. Check that only the paid invoice changes status

**Expected**: Each invoice has a unique integrated address. Payment to one doesn't affect the others.

### Test 6: Underpayment

1. Create an invoice for 2 ZANO
2. Send only 1 ZANO to the integrated address

**Expected**: Invoice remains in "New" or shows partial payment, doesn't settle.

### Test 7: Settings Persistence

1. Enable Zano, set Custom confirmation threshold to 5
2. Save, navigate away, come back to Zano settings

**Expected**: Settings are preserved (Enabled, Custom, value 5).

---

## Automated tests

Run the repository's unit tests with .NET SDK 10.0.400 or later:

```bash
dotnet test --project BTCPayServer.Plugins.UnitTests/BTCPayServer.Plugins.UnitTests.csproj -c Release
```

Run the integration suite in its disposable Docker environment:

```bash
docker compose -f BTCPayServer.Plugins.IntegrationTests/docker-compose.yml run tests
docker compose -f BTCPayServer.Plugins.IntegrationTests/docker-compose.yml down -v
```

See the repository README for the integration suite's node and wallet requirements. Use the manual browser cases above to verify the deployed checkout. Automated source tests do not replace a funded testnet payment or installation test of the exact package.

---

## What to look for (common issues)

| Symptom | Likely cause |
|---------|-------------|
| Zano not in sidebar | Plugin not loaded — check BTCPay logs for "Plugins.Zano" |
| "Node available: False" | Daemon RPC unreachable — check BTCPAY_ZANO_DAEMON_URI |
| "Wallet RPC available: False" | simplewallet not running or wrong port |
| "Synced: False" | Daemon still syncing — wait or use a synced remote node |
| Invoice shows no Zano option | Zano not enabled for this store |
| Payment not detected | Check wallet sync, paged get_recent_txs_and_info2 results, and matching payment ID and asset |
| Wrong amount displayed | Divisibility issue (should be 12 decimal places) |
| Address doesn't start with iZ | Integrated address generation failed — check make_integrated_address RPC |

## Architecture

```
Customer → BTCPay checkout page → shows integrated address + QR
                                     ↓
                              Payment sent to address
                                     ↓
BTCPay plugin (every 15s) → get_recent_txs_and_info2 → wallet RPC
                                     ↓
                              Payment matched by payment_id
                                     ↓
                              Invoice status: Processing → Settled
```

## RPC calls used

| Method | Target | Purpose |
|--------|--------|---------|
| `getinfo` | Daemon | Sync status, block height |
| `get_wallet_info` | Wallet | Wallet height, address |
| `make_integrated_address` | Wallet | Generate unique address per invoice |
| `get_recent_txs_and_info2` | Wallet | Discover payments by payment ID and asset |
| `get_tx_details` | Daemon | Check placement or absence of recorded receipts |
| `get_bulk_payments` | Wallet | Support reconciliation of recorded payments |
