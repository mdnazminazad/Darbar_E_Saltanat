"""
Single source of truth for the Darbar_E_Saltanat data model.

gen_schema.py reads this spec and emits:
  - database/mysql-schema.sql
  - database/sqlite-schema.sql
  - web/server/src/sync/entityRegistry.json   (used by the generic sync engine)

Column mini-language (one per line):
    name type [null] [unique] [=default]
Types: sN (VARCHAR N), charN, text, int, bigint, bool, money (DECIMAL 12,2),
       qty (DECIMAL 14,3), pct (DECIMAL 5,2), dt (UTC datetime), date, time,
       json, enum(A|B|C)
Directives:
    @fk <name> <ref_table> [null]   -> MySQL `<name>_id` BIGINT FK, SQLite/sync `<name>_uuid`
    @idx a,b                        -> index (names may be fk names)
    @uniq a,b                       -> unique index
Flags on a table:
    sync    - standard sync columns + participates in the sync engine
    global  - branch_id is nullable (shared across branches)
    append  - append-only (never updated): conflicts are impossible, duplicates are no-ops
    fin     - financial/inventory record: conflicts are never auto-resolved
    server  - exists only on the MySQL server (not in SQLite, not synced)
"""

TABLES = []


def T(name, flags, cols, comment=""):
    TABLES.append(dict(name=name, flags=set(flags.split()), cols=cols.strip(), comment=comment))


# ----------------------------------------------------------------- identity & access
T("branches", "sync global", """
code s20 unique
name s150
address text null
phone s30 null
email s150 null
timezone s40 ='Asia/Dhaka'
currency s10 ='BDT'
is_active bool =1
""", "Restaurant branches. branches.branch_id is always NULL (the row IS the branch).")

T("roles", "sync global", """
code s40 unique
name s80
description s255 null
is_system bool =0
""")

T("permissions", "sync global", """
code s80 unique
module s40
description s255 null
""")

T("role_permissions", "sync global", """
@fk role roles
@fk permission permissions
@uniq role,permission
""")

T("users", "sync global", """
username s60 unique
email s150 null unique
password_hash s255
pin_hash s255 null
full_name s150
phone s30 null
is_active bool =1
must_change_password bool =0
failed_login_count int =0
locked_until dt null
last_login_at dt null
""", "Staff accounts. Hashes are bcrypt; they are synced to desktops so cashiers can log in offline.")

T("user_roles", "sync global", """
@fk user users
@fk role roles
@uniq user,role
""")

# ----------------------------------------------------------------- customers
T("customers", "sync global", """
name s150
phone s30
email s150 null
password_hash s255 null
address text null
notes text null
loyalty_points int =0
credit_balance money =0
credit_limit money =0
is_active bool =1
@idx phone
@idx name
""", "password_hash is only set for customers who registered on the website.")

T("customer_addresses", "sync global", """
@fk customer customers
label s60
address text
area s100 null
is_default bool =0
""")

# ----------------------------------------------------------------- suppliers & menu
T("suppliers", "sync", """
name s150
contact_person s120 null
phone s30 null
email s150 null
address text null
opening_balance money =0
is_active bool =1
@idx name
""")

T("categories", "sync", """
@fk parent categories null
name s120
description text null
image_url s500 null
kitchen_station s60 null
sort_order int =0
is_active bool =1
@idx name
""")

T("menu_items", "sync", """
@fk category categories
name s150
name_bn s150 null
description text null
sku s60 null
barcode s60 null
image_url s500 null
cost_price money =0
selling_price money
tax_rate pct =0
prep_time_minutes int =15
sort_order int =0
is_available bool =1
is_active bool =1
@idx name
@idx sku
@idx barcode
@idx category,is_available
""")

T("item_variants", "sync", """
@fk menu_item menu_items
name s60
sku s60 null
price money
cost_price money =0
is_default bool =0
is_active bool =1
""")

T("item_addons", "sync", """
name s100
group_name s60 null
price money =0
is_active bool =1
""")

T("item_addon_mappings", "sync", """
@fk menu_item menu_items
@fk addon item_addons
max_qty int =1
@uniq menu_item,addon
""")

# ----------------------------------------------------------------- inventory
T("ingredients", "sync", """
@fk supplier suppliers null
name s150
sku s60 null
unit enum(KG|G|L|ML|PCS|PACKET)
current_stock qty =0
reorder_level qty =0
cost_per_unit money =0
is_active bool =1
@idx name
""", "current_stock is a cached balance; stock_movements is the authoritative ledger.")

T("recipes", "sync", """
@fk menu_item menu_items
@fk variant item_variants null
yield_qty qty =1
notes text null
""")

T("recipe_ingredients", "sync", """
@fk recipe recipes
@fk ingredient ingredients
quantity qty
wastage_pct pct =0
""")

T("stock_movements", "sync append fin", """
@fk ingredient ingredients
type enum(PURCHASE|SALE|WASTAGE|ADJUSTMENT|STOCK_IN|STOCK_OUT|RETURN)
quantity qty
balance_after qty null
unit_cost money =0
reference_type s40 null
reference_uuid char36 null
reason s255 null
@fk created_by users null
occurred_at dt
@idx ingredient,occurred_at
@idx reference_uuid
""", "Signed quantity (+in / -out). Append-only so two devices can never overwrite each other's stock changes.")

T("stock_adjustments", "sync append fin", """
@fk ingredient ingredients
system_qty qty
counted_qty qty
difference_qty qty
reason s255
@fk adjusted_by users null
@fk approved_by users null
adjusted_at dt
""")

# ----------------------------------------------------------------- tables & reservations
T("restaurant_tables", "sync", """
name s30
capacity int =4
section s60 null
status enum(AVAILABLE|RESERVED|OCCUPIED|BILLING|CLEANING) =AVAILABLE
is_active bool =1
@uniq branch,name
@idx status
""")

T("reservations", "sync", """
@fk table restaurant_tables null
@fk customer customers null
customer_name s150
phone s30
party_size int =2
reserved_for dt
duration_minutes int =90
status enum(PENDING|CONFIRMED|SEATED|COMPLETED|CANCELLED|NO_SHOW) =PENDING
source enum(DESKTOP|WEB|PHONE) =DESKTOP
notes text null
@idx reserved_for
@idx phone
@idx status
""")

# ----------------------------------------------------------------- promotions
T("discounts", "sync", """
name s100
type enum(PERCENT|FIXED)
value money
min_order money =0
max_discount money null
requires_permission bool =0
is_active bool =1
""")

T("coupons", "sync", """
code s40 unique
description s255 null
type enum(PERCENT|FIXED)
value money
min_order money =0
max_discount money null
usage_limit int null
per_customer_limit int null
used_count int =0
starts_at dt null
ends_at dt null
is_active bool =1
""")

# ----------------------------------------------------------------- cash
T("cash_sessions", "sync fin", """
@fk opened_by users
@fk closed_by users null
opened_at dt
closed_at dt null
opening_amount money =0
expected_closing money null
counted_closing money null
difference money null
status enum(OPEN|CLOSED) =OPEN
notes text null
@idx status
""")

T("cash_movements", "sync append fin", """
@fk cash_session cash_sessions
type enum(CASH_IN|CASH_OUT)
amount money
reason s255
@fk created_by users null
occurred_at dt
""")

# ----------------------------------------------------------------- orders
T("orders", "sync fin", """
order_no s40
order_type enum(DINE_IN|TAKEAWAY|DELIVERY)
source enum(POS|WEB|PHONE) =POS
status enum(DRAFT|HELD|OPEN|IN_KITCHEN|READY|SERVED|BILLED|COMPLETED|VOIDED|CANCELLED) =OPEN
payment_status enum(UNPAID|PARTIAL|PAID|REFUNDED|PARTIAL_REFUND) =UNPAID
@fk table restaurant_tables null
@fk customer customers null
@fk waiter users null
@fk cashier users null
@fk coupon coupons null
@fk parent_order orders null
@fk voided_by users null
guest_count int =1
subtotal money =0
discount_type enum(NONE|PERCENT|FIXED|COUPON) =NONE
discount_value money =0
discount_amount money =0
service_charge_rate pct =0
service_charge_amount money =0
vat_amount money =0
delivery_charge money =0
round_off money =0
total money =0
paid_amount money =0
due_amount money =0
notes text null
delivery_address text null
delivery_phone s30 null
void_reason text null
voided_at dt null
order_date dt
completed_at dt null
@uniq branch,order_no
@idx order_date
@idx status
@idx payment_status
@idx customer
@idx table
""", "order_no embeds a per-device code (e.g. DES1-20261120-0007) so offline devices never collide.")

T("order_items", "sync fin", """
@fk order orders
@fk menu_item menu_items
@fk variant item_variants null
item_name s150
variant_name s60 null
quantity int
unit_price money
addons_total money =0
discount_amount money =0
tax_rate pct =0
tax_amount money =0
line_total money
status enum(NEW|SENT|PREPARING|READY|SERVED|CANCELLED) =NEW
notes text null
cancel_reason s255 null
@idx order
""")

T("order_item_addons", "sync fin", """
@fk order_item order_items
@fk addon item_addons
addon_name s100
quantity int =1
unit_price money
""")

T("order_tables", "sync", """
@fk order orders
@fk table restaurant_tables
@uniq order,table
""", "Links one order to several tables (merged tables).")

T("kots", "sync", """
@fk order orders
kot_no s40
station s60 null
status enum(NEW|PREPARING|READY|SERVED|CANCELLED) =NEW
notes text null
printed_at dt null
started_at dt null
ready_at dt null
@idx status
@idx order
""")

T("kot_items", "sync", """
@fk kot kots
@fk order_item order_items
item_name s150
quantity int
status enum(NEW|PREPARING|READY|SERVED|CANCELLED) =NEW
""")

T("payments", "sync append fin", """
@fk order orders
@fk received_by users null
@fk cash_session cash_sessions null
method enum(CASH|CARD|MOBILE_BANKING|CREDIT|OTHER)
provider s40 null
amount money
tendered money null
change_amount money =0
reference s100 null
status enum(COMPLETED|VOIDED) =COMPLETED
paid_at dt
@idx order
@idx paid_at
@idx method
""")

T("refunds", "sync append fin", """
@fk order orders
@fk payment payments null
@fk approved_by users null
amount money
method enum(CASH|CARD|MOBILE_BANKING|CREDIT|OTHER)
reason text
restock bool =0
refunded_at dt
@idx order
""")

T("coupon_usages", "sync append fin", """
@fk coupon coupons
@fk order orders
@fk customer customers null
discount_amount money
used_at dt
@idx coupon
""")

T("loyalty_transactions", "sync global append fin", """
@fk customer customers
@fk order orders null
type enum(EARN|REDEEM|ADJUST)
points int
note s255 null
occurred_at dt
@idx customer
""")

# ----------------------------------------------------------------- purchasing
T("purchases", "sync fin", """
@fk supplier suppliers
@fk created_by users null
purchase_no s40
status enum(DRAFT|ORDERED|PARTIALLY_RECEIVED|RECEIVED|CANCELLED) =DRAFT
payment_status enum(UNPAID|PARTIAL|PAID) =UNPAID
ordered_at dt null
expected_at dt null
received_at dt null
subtotal money =0
discount money =0
tax money =0
total money =0
paid_amount money =0
notes text null
@uniq branch,purchase_no
@idx status
""")

T("purchase_items", "sync fin", """
@fk purchase purchases
@fk ingredient ingredients
quantity qty
received_quantity qty =0
unit_cost money
line_total money
""")

T("supplier_payments", "sync append fin", """
@fk supplier suppliers
@fk purchase purchases null
@fk cash_session cash_sessions null
@fk paid_by users null
amount money
method enum(CASH|CARD|MOBILE_BANKING|BANK|OTHER)
reference s100 null
paid_at dt
""")

# ----------------------------------------------------------------- staff
T("shifts", "sync", """
name s60
start_time time
end_time time
grace_minutes int =10
""")

T("employees", "sync", """
@fk user users null
@fk shift shifts null
employee_code s30
full_name s150
phone s30 null
email s150 null
designation s80 null
hire_date date null
salary_type enum(MONTHLY|DAILY|HOURLY) =MONTHLY
base_salary money =0
overtime_rate money =0
is_active bool =1
@uniq branch,employee_code
""")

T("attendance", "sync", """
@fk employee employees
work_date date
check_in dt null
check_out dt null
status enum(PRESENT|ABSENT|LATE|LEAVE|HALF_DAY) =PRESENT
overtime_minutes int =0
notes s255 null
@uniq employee,work_date
@idx work_date
""")

T("salaries", "sync fin", """
@fk employee employees
kind enum(SALARY|ADVANCE) =SALARY
period_month char7
base_amount money =0
overtime_amount money =0
bonus money =0
deductions money =0
advance_deduction money =0
net_amount money =0
status enum(DRAFT|APPROVED|PAID) =DRAFT
paid_at dt null
notes s255 null
@idx employee,period_month
""")

# ----------------------------------------------------------------- accounts
T("expense_categories", "sync", """
name s100
description s255 null
is_active bool =1
""")

T("expenses", "sync fin", """
@fk category expense_categories
@fk supplier suppliers null
@fk cash_session cash_sessions null
@fk created_by users null
amount money
expense_date date
payment_method enum(CASH|CARD|MOBILE_BANKING|BANK|OTHER) =CASH
description s255
reference s100 null
attachment_url s500 null
@idx expense_date
""")

# ----------------------------------------------------------------- settings & audit
T("settings", "sync global", """
setting_key s100
setting_value text null
value_type enum(STRING|NUMBER|BOOL|JSON) =STRING
group_name s50 =general
@uniq branch,setting_key
""", "branch_id NULL = chain-wide default; a branch row overrides it.")

T("audit_logs", "sync global append", """
@fk user users null
action s80
entity_type s60 null
entity_uuid char36 null
old_values json null
new_values json null
ip_address s45 null
user_agent s255 null
occurred_at dt
@idx occurred_at
@idx action
@idx entity_type,entity_uuid
""")

# ----------------------------------------------------------------- server-only
T("devices", "server", """
@fk branch branches
name s100
platform s40 =WINDOWS
app_version s30 null
api_key_hash s255
status enum(ACTIVE|REVOKED) =ACTIVE
last_heartbeat_at dt null
last_pull_at dt null
last_push_at dt null
pending_count int =0
last_ip s45 null
@idx status
""", "Desktop installations. device_id in every syncable table = devices.uuid.")

T("sync_batches", "server", """
batch_id char36 unique
@fk device devices
status enum(PROCESSING|COMPLETED|FAILED) =PROCESSING
change_count int =0
success_count int =0
conflict_count int =0
failure_count int =0
response_json json null
error_message text null
received_at dt
processed_at dt null
@idx status
""", "One row per client batch_id. Used for idempotency: a repeated batch returns response_json.")

T("sync_records", "server", """
@fk batch sync_batches
entity s60
entity_uuid char36
operation enum(CREATE|UPDATE|DELETE)
client_version int
server_version int null
status enum(SYNCED|CONFLICT|FAILED|SKIPPED)
reason s500 null
@idx entity,entity_uuid
@idx status
""")

T("sync_conflicts", "server", """
@fk device devices null
entity s60
entity_uuid char36
local_data json
server_data json
local_version int
server_version int
status enum(OPEN|RESOLVED_LOCAL|RESOLVED_SERVER|RESOLVED_MERGED) =OPEN
@fk resolved_by users null
resolution_note s255 null
detected_at dt
resolved_at dt null
@idx status
@idx entity,entity_uuid
""")

T("notifications", "server", """
@fk branch branches null
@fk user users null
type s40
title s150
body text null
data json null
read_at dt null
@idx user,read_at
""")

T("refresh_tokens", "server", """
@fk user users
token_hash char64 unique
expires_at dt
revoked_at dt null
replaced_by_hash char64 null
user_agent s255 null
ip_address s45 null
@idx expires_at
""")

# ----------------------------------------------------------------- SQLite-only tables are
# written by hand in gen_schema.py (sync_outbox, sync_log, sync_state, local conflicts).
