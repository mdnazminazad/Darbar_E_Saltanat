# User manual (Phases 1–3)

## Desktop POS (Windows)

**Signing in**
* Username/email + password, or **Cashier PIN login (F2)**: pick your name, type your 4–6 digit PIN on the keypad or keyboard.
* Works without internet — staff accounts are stored securely on the computer.
* 5 wrong attempts lock the account for 15 minutes.
* The first admin login uses `admin / ChangeMe@123`; change it in the web portal immediately.

**Locking** — press **F12** (or Ctrl+L) or the Lock button. The terminal also locks itself after the auto-lock time (Settings). Unlock with your password or PIN, or choose *Switch user*.

**Status bar** — shows sync state: *Offline mode* (not connected to a server yet), *Pending changes*, *All changes synced*, *Sync failed*, *Conflicts found*. Your data is always saved on the computer first.

**Shortcuts** — F1 Dashboard · F2 POS · F3 Tables · F4 Orders · F5 Menu · F9 Settings · Shift+F9 Sync · F12 Lock.

### Connecting a till to the server (once per computer)
1. Web portal → **Devices & Sync** → **+ Register device** → name it (e.g. *Counter PC 1*) and choose the branch. Copy the **Device ID** and **token** (shown once).
2. On the till: **Settings → Server sync** → API address (e.g. `https://darbaresaltanat.com/api/v1`), Device ID, token → **Save sync settings**. The till checks the connection, adopts its branch and starts syncing.

### Sync (Shift+F9)
* The till never waits for the internet: everything is saved on the computer first and uploads automatically every 30 seconds when online, and immediately when the network comes back.
* Status bar: *Online · all changes synced*, *Online · N pending*, *Offline · N waiting*, *Conflicts found*, *Sync failed*, *Device not authorised*.
* The Sync page shows pending / failed / conflict counts, the last successful sync, changes that need attention (with the reason and the next automatic retry) and the sync log. **Sync now** forces a sync; **Retry failed** retries failed changes immediately.
* A **conflict** means the same record was changed here and on the server. The till keeps its version until a manager decides in the web portal; then the decision arrives automatically. Payments and stock movements can never conflict.
* Menu prices, users, settings, coupons and orders from other tills arrive automatically. Changes to settings take effect at once.

### POS (F2)
* **Start an order**: tap any item to start a takeaway order, or use **+ Dine-in** (pick a table and guests) / **+ Delivery** (customer mobile and address). Open orders appear as chips at the top — tap one to switch.
* **Add items**: tap a tile, or type in the search box (name, বাংলা name, SKU) and press Enter. A barcode scanner works the same way. Items with **⋯** ask for size, add-ons, quantity and a kitchen note. Out-of-stock items are greyed out.
* **Change the cart**: **+ / −** change quantity of items not yet sent to the kitchen. Once the kitchen has an item, − or *remove* cancels it — this needs a manager (orders.void) and a reason, and prints a cancellation ticket.
* **Send to kitchen (F6)**: prints one KOT per kitchen station (e.g. Main Kitchen, Grill, Tandoor, Bar — set per category in Menu).
* **Discount (F7)**: quick discounts, manual % or ৳, or a coupon code. Discounts above the limit in Settings (default 10%) and locked presets 🔒 need a manager.
* **Bill (F11)**: prints the bill; the table turns gold (*Billing*). Adding more items puts it back to *Occupied*.
* **Pay (F8)**: Cash (with quick “Exact / ৳500 / ৳1000” buttons and change), Card, Mobile banking (bKash/Nagad/Rocket/Upay + TrxID) or Customer credit. Pay part with one method and the rest with another — the payment window reopens until nothing is due. The receipt prints and the cash drawer opens for cash.
* **Other actions**: Customer (find/create by mobile; Ctrl+U), Note / guests, Hold & Resume (Ctrl+H), Move table, Merge (another open order joins this one), Split bill (choose quantities for a new bill), Void (manager + reason; refund any payment first), Reprint (Ctrl+P).
* Totals follow Settings: service charge (dine-in), VAT (per item or restaurant rate; inclusive or exclusive), rounding to whole taka.

### Tables (F3)
Colour shows status — green available, blue reserved, red occupied, gold bill printed, grey cleaning. Tap a table to open its order or seat guests; **Status…** reserves a table or marks it clean. **+ Add table** creates tables.

### Orders (F4)
Today / yesterday / last 7 or 30 days, filter by status, search by order no, customer or table. Select an order to see items, payments and refunds; **Open in POS**, **Reprint**, **Refund** (amount, method, reason — manager) or **Void**.

### Menu (F5, Managers)
Categories (with kitchen station and sort order), items (English + বাংলা name, price, cost, SKU, barcode, VAT %, prep time, available/active), sizes/variants with one default, and add-ons with a maximum quantity per item. Tap an item's status to mark it out of stock instantly. **Import/Export CSV** (opens in Excel): columns `sku, name, name_bn, category, selling_price, cost_price, tax_rate, barcode, prep_time_minutes, is_available, description`; rows are matched by SKU (else name), new categories are created, and nothing is imported if any row has an error.

### Printers (Settings → Printers)
Choose the Windows printer for receipts and for the kitchen, press **Test**, and tick *Open the cash drawer* if the drawer is connected to the receipt printer. Thermal printers must be installed in Windows (use the maker's driver or “Generic / Text Only”). With no printer chosen, receipts and KOTs appear on screen. Receipts print in English with “Tk” because thermal printers cannot print Bangla script.

**Settings** (Managers/Super Admin)
* Restaurant name, address, phone, receipt header/footer, printer width (58/80 mm), theme.
* VAT %, prices-include-VAT, service charge %, dine-in-only service charge, max discount without override, auto-lock minutes, sync interval.
* *Server sync*: API URL, Device ID and token from the web portal. The token is encrypted for the Windows user.

## Web portal (`https://<domain>/admin`)
* **Overview** — API/database health, desktop devices online, pending desktop changes.
* **Users** — create staff, assign roles, set POS PINs, reset passwords, disable/delete. New users must change their password at first login.
* **Roles & Permissions** — tick what each role may do; create custom roles (e.g. Host).
* **Branches** — branch details (multi-branch ready).
* **Devices & Sync** — *Devices*: register each POS computer (copy the one-time token), last heartbeat / push / pull, pending changes, revoke lost PCs. *Conflicts*: review a field-by-field comparison and choose **Keep server version** or **Use till version** (needs *Resolve sync conflicts*). *Failed changes*: changes the server refused and why (tills retry automatically).
* **Settings** — the same restaurant/tax/receipt settings; desktops receive changes on their next sync.
* **Audit Logs** — who did what and when (logins, failed logins, user/role/settings changes).

## Default roles
| Role | Can |
|---|---|
| Super Admin | Everything |
| Manager | Everything except role management, branches, backups |
| Cashier | POS, payments, discounts, tables, reservations, customers, cash drawer |
| Waiter | Orders, tables, reservations, customer lookup |
| Kitchen | Kitchen display, item status |
| Inventory Manager | Ingredients, recipes, stock, purchases, suppliers |
| Accountant | Cash, expenses, refunds, reports & exports, salaries, audit |
