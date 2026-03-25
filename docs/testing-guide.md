# BTCPay Server Zano Plugin — Testing Guide

## What is this?

A BTCPay Server plugin that lets merchants accept Zano (ZANO) payments. When a customer pays an invoice, the plugin:

1. Generates a unique **integrated address** (embeds a payment ID so each invoice gets its own address)
2. Shows a checkout page with QR code, address, and "Pay in wallet" button
3. Polls the Zano wallet every 15 seconds via `get_bulk_payments` RPC to detect incoming payments
4. Tracks confirmations and settles the invoice based on the merchant's threshold setting

Built by forking the Monero BTCPay plugin and adapting it for Zano's RPC API.

## QA Instance

- **URL**: https://btcpay.zano.me
- **Server**: obscura (157.180.1.140)
- **Daemon**: Zano testnet remote node (37.27.100.59:10505)
- **Wallet**: freshly generated testnet wallet inside Docker

---

## Manual Testing (QA Engineer)

### Prerequisites

- A Zano testnet wallet with some balance (get testnet ZANO from https://faucet.testnet.zano.org)
- A browser

### Test 1: Setup & Enable Zano

1. Go to https://btcpay.zano.me
2. Register a new admin account (first user becomes admin)
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
3. Set Amount: `1`, Currency: `ZANO` (USD won't work without a rate source configured)
4. Click **Create**
5. Click the invoice link to open the checkout page

**Expected**:
- Shows amount: `1.010000000000 ZANO` (1 ZANO + 0.01 network fee)
- Shows an integrated address starting with `iZ`
- QR code is displayed
- "Pay in wallet" button is present (opens `zano:` URI)
- "View Details" shows the full address and amount breakdown

### Test 3: Pay an Invoice

1. Create an invoice as above
2. Copy the integrated address from the checkout page
3. From your testnet wallet, send the exact amount shown to that address:
   ```
   transfer <integrated_address> <amount_in_atomic_units> 10
   ```
   For 1.01 ZANO: amount = 1010000000000 atomic units
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

## Automated E2E Testing (Playwright CLI)

### Prerequisites

- Node.js with `npx tsx` available
- Playwright installed (`npm install playwright` in `~/.claude/scripts/`)
- BTCPay Server running locally or accessible URL
- Zano wallet RPC accessible

### Running the test

```bash
# Against local instance
npx tsx ~/.claude/scripts/btcpay-zano-e2e.ts

# Against QA instance (edit BASE_URL in the script first)
BASE_URL=https://btcpay.zano.me npx tsx ~/.claude/scripts/btcpay-zano-e2e.ts
```

### What the script tests

| Step | Action | Verification |
|------|--------|-------------|
| 1 | Register admin account | Redirects to dashboard |
| 2 | Create store "Test Store" | Store appears in nav |
| 3 | Navigate to Zano settings | Shows Node/Wallet status |
| 4 | Enable Zano + Zero Confirmation | Settings saved successfully |
| 5 | Create 1 ZANO invoice | Invoice created, ID assigned |
| 6 | Open checkout page | Integrated address (iZ...) displayed, QR code rendered |
| 7 | Attempt payment via wallet RPC | Transfer call made (may fail if wallet has no funds) |
| 8 | Check invoice status | Status page accessible |

### Screenshots

Saved to `/tmp/btcpay-zano-e2e/`:
- `01-landing.png` — Initial page
- `06-zano-settings.png` — Zano settings with node status
- `07-zano-settings-configured.png` — Enabled + threshold set
- `12-checkout-page.png` — Checkout with QR code and address
- `14-invoice-details.png` — Invoice admin view

### Adapting the script

Key variables at the top of `btcpay-zano-e2e.ts`:

```typescript
const BASE_URL = 'http://127.0.0.1:23002';  // BTCPay Server URL
const WALLET_RPC = 'http://127.0.0.1:11212/json_rpc';  // Zano wallet RPC
const EMAIL = 'admin@test.com';
const PASSWORD = 'Test1234!Test1234!';
```

---

## What to look for (common issues)

| Symptom | Likely cause |
|---------|-------------|
| Zano not in sidebar | Plugin not loaded — check BTCPay logs for "Plugins.Zano" |
| "Node available: False" | Daemon RPC unreachable — check BTCPAY_ZANO_DAEMON_URI |
| "Wallet RPC available: False" | simplewallet not running or wrong port |
| "Synced: False" | Daemon still syncing — wait or use a synced remote node |
| Invoice shows no Zano option | Zano not enabled for this store |
| Payment not detected | Check wallet is connected to same daemon, check get_bulk_payments response |
| Wrong amount displayed | Divisibility issue (should be 12 decimal places) |
| Address doesn't start with iZ | Integrated address generation failed — check make_integrated_address RPC |

## Architecture

```
Customer → BTCPay checkout page → shows integrated address + QR
                                     ↓
                              Payment sent to address
                                     ↓
BTCPay plugin (every 15s) → get_bulk_payments(payment_ids) → wallet RPC
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
| `get_bulk_payments` | Wallet | Detect payments by payment_id |
| `getbalance` | Wallet | Balance check |
