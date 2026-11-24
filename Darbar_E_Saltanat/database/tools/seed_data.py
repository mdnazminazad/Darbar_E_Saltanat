#!/usr/bin/env python3
"""Generate mysql-seed.sql, sqlite-seed.sql and the shared permission catalogs
(Node + C#) from one definition. Usage: python3 seed_data.py <bcrypt-hash-of-default-admin-password>"""
import sys, uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
NS = uuid.UUID("6f1d0c52-8a4e-5b7a-9d3e-1b2c3d4e5f60")
def U(name): return str(uuid.uuid5(NS, name))

ADMIN_HASH = sys.argv[1] if len(sys.argv) > 1 else "$2a$12$CHANGE_ME_GENERATE_WITH_NPM_RUN_HASH"
BRANCH = U("branch:MAIN")

PERMISSIONS = [
 ("orders.view","orders","View orders"),("orders.create","orders","Create and edit orders"),
 ("orders.void","orders","Void an order (requires reason)"),("orders.transfer","orders","Transfer/merge/split orders"),
 ("orders.reprint","orders","Reprint bills and KOTs"),("payments.take","payments","Take payments"),
 ("payments.refund","payments","Refund payments"),("discounts.apply","discounts","Apply discounts and coupons"),
 ("discounts.override","discounts","Apply discounts above the configured limit"),
 ("menu.view","menu","View menu"),("menu.manage","menu","Create/edit menu, categories, variants, add-ons"),
 ("tables.manage","tables","Manage tables"),("reservations.manage","reservations","Manage reservations"),
 ("kitchen.view","kitchen","View kitchen display"),("kitchen.update","kitchen","Update kitchen item status"),
 ("inventory.view","inventory","View inventory"),("inventory.manage","inventory","Manage ingredients and recipes"),
 ("stock.adjust","inventory","Adjust stock, record wastage"),("purchases.manage","purchases","Manage purchases and receiving"),
 ("suppliers.manage","purchases","Manage suppliers and supplier payments"),
 ("customers.view","customers","View customers"),("customers.manage","customers","Manage customers and loyalty"),
 ("employees.manage","employees","Manage employees, shifts and salaries"),
 ("attendance.manage","employees","Manage attendance for all staff"),("attendance.self","employees","Check in/out for self"),
 ("cash.manage","accounts","Open/close cash session, cash in/out"),("expenses.manage","accounts","Manage expenses"),
 ("reports.view","reports","View reports"),("reports.export","reports","Export reports (PDF/Excel)"),
 ("coupons.manage","discounts","Manage coupons and discounts"),
 ("users.manage","admin","Manage users"),("roles.manage","admin","Manage roles and permissions"),
 ("branches.manage","admin","Manage branches"),("settings.manage","admin","Manage settings"),
 ("audit.view","admin","View audit logs"),("sync.view","sync","View sync status and devices"),
 ("sync.manage","sync","Manage devices, trigger sync"),("sync.resolve","sync","Resolve sync conflicts"),
 ("backup.manage","admin","Backup and restore local database"),
]
ALL = [p[0] for p in PERMISSIONS]
def excl(*prefixes): return [p for p in ALL if not any(p.startswith(x) for x in prefixes)]

ROLES = {
 "SUPER_ADMIN": ("Super Admin","Full access to everything", ALL),
 "MANAGER": ("Manager","Runs the restaurant day to day", [p for p in ALL if p not in ("roles.manage","branches.manage","backup.manage")]),
 "CASHIER": ("Cashier","POS, payments and cash drawer", ["orders.view","orders.create","orders.transfer","orders.reprint","payments.take","discounts.apply","menu.view","tables.manage","reservations.manage","customers.view","customers.manage","cash.manage","attendance.self","kitchen.view","reports.view"]),
 "WAITER": ("Waiter","Takes orders at the table", ["orders.view","orders.create","orders.transfer","orders.reprint","menu.view","tables.manage","reservations.manage","customers.view","attendance.self","kitchen.view"]),
 "KITCHEN": ("Kitchen","Kitchen display", ["kitchen.view","kitchen.update","orders.view","menu.view","attendance.self"]),
 "INVENTORY_MANAGER": ("Inventory Manager","Stock, recipes and purchasing", ["inventory.view","inventory.manage","stock.adjust","purchases.manage","suppliers.manage","menu.view","reports.view","attendance.self"]),
 "ACCOUNTANT": ("Accountant","Accounts, expenses and reports", ["cash.manage","expenses.manage","reports.view","reports.export","orders.view","payments.refund","purchases.manage","suppliers.manage","employees.manage","audit.view","attendance.self"]),
}

CATEGORIES = [("Biryani & Polao",1,"Main Kitchen"),("Kebab & Grill",2,"Grill"),("Curries",3,"Main Kitchen"),("Breads",4,"Tandoor"),("Desserts",5,"Dessert"),("Beverages",6,"Bar"),("Sides",7,"Main Kitchen")]
COUPONS = [("WELCOME10","10% off first visit (min ৳500, max ৳200)","PERCENT",10,500,200,None,1),("FLAT100","৳100 off orders above ৳1500","FIXED",100,1500,None,500,None)]
DISCOUNTS = [("Happy Hour 10%","PERCENT",10,0,None,0),("Staff Meal 20%","PERCENT",20,0,None,1),("Loyal Guest ৳50","FIXED",50,500,None,0)]
# (category, name, bn, price, cost, sku, station)
ITEMS = [
 ("Biryani & Polao","Kacchi Biryani","কাচ্চি বিরিয়ানি",450,260,"BIR-001","Main"),
 ("Biryani & Polao","Morog Polao","মোরগ পোলাও",380,210,"BIR-002","Main"),
 ("Biryani & Polao","Beef Tehari","বিফ তেহারি",320,180,"BIR-003","Main"),
 ("Biryani & Polao","Plain Polao","সাদা পোলাও",120,55,"BIR-004","Main"),
 ("Kebab & Grill","Chicken Roast","চিকেন রোস্ট",220,120,"KEB-001","Grill"),
 ("Kebab & Grill","Seekh Kebab","শিক কাবাব",180,95,"KEB-002","Grill"),
 ("Kebab & Grill","Chicken Tikka","চিকেন টিক্কা",260,140,"KEB-003","Grill"),
 ("Kebab & Grill","Jali Kabab","জালি কাবাব",150,80,"KEB-004","Grill"),
 ("Curries","Beef Rezala","বিফ রেজালা",340,190,"CUR-001","Main"),
 ("Curries","Mutton Kosha","মাটন কষা",420,250,"CUR-002","Main"),
 ("Curries","Chicken Bhuna","চিকেন ভুনা",260,140,"CUR-003","Main"),
 ("Curries","Dal Tarka","ডাল তড়কা",110,45,"CUR-004","Main"),
 ("Breads","Butter Naan","বাটার নান",50,18,"BRD-001","Tandoor"),
 ("Breads","Plain Naan","প্লেইন নান",40,14,"BRD-002","Tandoor"),
 ("Breads","Paratha","পরোটা",25,9,"BRD-003","Tandoor"),
 ("Desserts","Firni","ফিরনি",90,35,"DES-001","Dessert"),
 ("Desserts","Jorda","জর্দা",100,40,"DES-002","Dessert"),
 ("Beverages","Borhani","বোরহানি",60,20,"BEV-001","Bar"),
 ("Beverages","Sweet Lassi","মিষ্টি লাচ্ছি",110,45,"BEV-002","Bar"),
 ("Beverages","Doodh Cha","দুধ চা",40,12,"BEV-003","Bar"),
 ("Beverages","Soft Drink 250ml","সফট ড্রিংক",40,28,"BEV-004","Bar"),
 ("Beverages","Mineral Water 500ml","মিনারেল ওয়াটার",30,18,"BEV-005","Bar"),
 ("Sides","Green Salad","স্যালাড",50,15,"SID-001","Main"),
 ("Sides","Raita","রায়তা",40,15,"SID-002","Main"),
]
VARIANTS = {"Kacchi Biryani":[("Half",450,260,1),("Full",850,500,0)],"Morog Polao":[("Half",380,210,1),("Full",720,400,0)],"Soft Drink 250ml":[("250ml",40,28,1),("500ml",60,42,0)]}
ADDONS = [("Extra Egg","Extras",20),("Extra Borhani","Extras",30),("Extra Salad","Extras",25),("Extra Gravy","Extras",30)]
ADDON_MAP = {"Kacchi Biryani":["Extra Egg","Extra Borhani","Extra Salad"],"Morog Polao":["Extra Egg","Extra Salad"],"Beef Tehari":["Extra Egg","Extra Salad"],"Beef Rezala":["Extra Gravy"]}
INGREDIENTS = [("Basmati Rice","ING-001","KG",120,30,140),("Chicken","ING-002","KG",60,15,230),("Beef","ING-003","KG",50,12,780),("Mutton","ING-004","KG",30,8,1150),
 ("Onion","ING-005","KG",40,10,70),("Garlic","ING-006","KG",8,2,220),("Ginger","ING-007","KG",8,2,200),("Cooking Oil","ING-008","L",60,15,170),("Ghee","ING-009","KG",15,4,900),
 ("Yogurt","ING-010","KG",25,6,160),("Milk","ING-011","L",40,10,95),("Flour (Maida)","ING-012","KG",50,12,70),("Sugar","ING-013","KG",30,8,125),("Salt","ING-014","KG",20,5,40),
 ("Eggs","ING-015","PCS",300,60,14),("Potato","ING-016","KG",40,10,55),("Borhani Mix","ING-017","PACKET",40,10,35),("Soft Drink 250ml","ING-018","PCS",200,48,26)]
# recipe: item -> [(ingredient, qty)]
RECIPES = {"Kacchi Biryani":[("Basmati Rice",0.25),("Mutton",0.3),("Yogurt",0.08),("Onion",0.1),("Ghee",0.03),("Potato",0.1)],
 "Morog Polao":[("Basmati Rice",0.22),("Chicken",0.25),("Ghee",0.03),("Onion",0.08)],
 "Chicken Roast":[("Chicken",0.3),("Onion",0.05),("Cooking Oil",0.03)],
 "Butter Naan":[("Flour (Maida)",0.1),("Ghee",0.01)],"Borhani":[("Borhani Mix",0.1),("Yogurt",0.1)],"Soft Drink 250ml":[("Soft Drink 250ml",1)]}
TABLES = [(f"T{i}", 4 if i<=6 else 6, "Main Hall") for i in range(1,11)] + [(f"F{i}",6,"Family Cabin") for i in range(1,4)] + [("V1",10,"VIP Room"),("V2",8,"VIP Room")]
EXP_CATS = ["Utilities","Rent","Salaries & Wages","Maintenance","Marketing","Packaging","Transport","Miscellaneous"]
SETTINGS = [
 ("restaurant_name","Darbar_E_Saltanat","STRING","general"),("restaurant_address","Dhaka, Bangladesh","STRING","general"),("restaurant_phone","","STRING","general"),
 ("currency","BDT","STRING","general"),("currency_symbol","৳","STRING","general"),("timezone","Asia/Dhaka","STRING","general"),
 ("vat_rate","5","NUMBER","tax"),("vat_inclusive","false","BOOL","tax"),("service_charge_rate","10","NUMBER","tax"),("service_charge_dine_in_only","true","BOOL","tax"),
 ("max_discount_percent_without_permission","10","NUMBER","pos"),("order_no_prefix","ORD","STRING","pos"),
 ("receipt_header","Welcome to Darbar_E_Saltanat","STRING","receipt"),("receipt_footer","Thank you! Please visit again.","STRING","receipt"),("receipt_width_mm","80","NUMBER","receipt"),
 ("auto_lock_minutes","5","NUMBER","security"),("sync_interval_seconds","30","NUMBER","sync"),("sync_batch_size","100","NUMBER","sync"),
 ("loyalty_points_per_100_bdt","1","NUMBER","loyalty"),("loyalty_point_value_bdt","1","NUMBER","loyalty"),("low_stock_alerts_enabled","true","BOOL","inventory"),
 ("theme","dark","STRING","ui"),("round_total_to_taka","true","BOOL","pos"),("mobile_banking_providers","bKash,Nagad,Rocket,Upay","STRING","pos"),
 ("vat_applies_to_service_charge","true","BOOL","tax"),("delivery_charge_default","60","NUMBER","pos"),
]

def q(v):
    if v is None: return "NULL"
    return "'" + str(v).replace("\\","\\\\").replace("'","''") + "'"

class Out:
    def __init__(self, dialect): self.d=dialect; self.lines=[]
    def ins(self, table, row, refs=None):
        """row: dict of column->value (already uuid for refs). refs: {col: (table, uuid)} resolved per dialect."""
        cols = dict(row)
        if self.d=="mysql":
            vals=[]; names=[]
            for k,v in cols.items():
                names.append(k); vals.append(q(v))
            for col,(rt,ru) in (refs or {}).items():
                names.append(col+"_id"); vals.append("(SELECT `id` FROM `%s` WHERE `uuid`=%s)"%(rt,q(ru)) if ru else "NULL")
            self.lines.append("INSERT INTO `%s` (%s, `device_id`) VALUES (%s, NULL);"%(table,",".join("`%s`"%n for n in names),",".join(vals)))
        else:
            names=list(cols.keys()); vals=[q(cols[k]) for k in names]
            for col,(rt,ru) in (refs or {}).items():
                names.append(col+"_uuid"); vals.append(q(ru))
            now="'2026-01-01T00:00:00.000Z'"
            names+=["created_at","updated_at","sync_version","sync_status"]; vals+=[now,now,"1","'SYNCED'"]
            self.lines.append("INSERT INTO %s (%s) VALUES (%s);"%(table,",".join(names),",".join(vals)))

def build(d):
    o=Out(d); branch_ref=lambda: {"branch":("branches",BRANCH)}
    if d=="mysql": o.lines.append("SET NAMES utf8mb4;")
    # branch
    row={"uuid":BRANCH,"code":"MAIN","name":"Darbar_E_Saltanat — Main Branch","address":"Dhaka, Bangladesh","timezone":"Asia/Dhaka","currency":"BDT","is_active":1}
    if d=="mysql":
        o.lines.append("INSERT INTO `branches` (`uuid`,`code`,`name`,`address`,`timezone`,`currency`,`is_active`) VALUES (%s,%s,%s,%s,%s,%s,1);"%tuple(q(row[k]) for k in ["uuid","code","name","address","timezone","currency"]))
    else:
        o.ins("branches",row)
    B=branch_ref
    for code,desc_mod,desc in [(p[0],p[1],p[2]) for p in PERMISSIONS]:
        o.ins("permissions",{"uuid":U("perm:"+code),"code":code,"module":desc_mod,"description":desc},B())
    for rc,(name,desc,perms) in ROLES.items():
        o.ins("roles",{"uuid":U("role:"+rc),"code":rc,"name":name,"description":desc,"is_system":1},B())
        for p in perms:
            o.ins("role_permissions",{"uuid":U("rp:%s:%s"%(rc,p))},{**B(),"role":("roles",U("role:"+rc)),"permission":("permissions",U("perm:"+p))})
    # super admin: default password must be changed at first login
    o.ins("users",{"uuid":U("user:admin"),"username":"admin","email":"admin@darbar-e-saltanat.example","password_hash":ADMIN_HASH,"full_name":"System Administrator","is_active":1,"must_change_password":1},B())
    o.ins("user_roles",{"uuid":U("ur:admin")},{**B(),"user":("users",U("user:admin")),"role":("roles",U("role:SUPER_ADMIN"))})
    for k,v,t,g in SETTINGS:
        o.ins("settings",{"uuid":U("set:"+k),"setting_key":k,"setting_value":v,"value_type":t,"group_name":g},B())
    cat_uuid={}
    for name,order,station in CATEGORIES:
        cat_uuid[name]=U("cat:"+name)
        o.ins("categories",{"uuid":cat_uuid[name],"name":name,"kitchen_station":station,"sort_order":order,"is_active":1},B())
    item_uuid={}
    for cat,name,bn,price,cost,sku,station in ITEMS:
        item_uuid[name]=U("item:"+name)
        o.ins("menu_items",{"uuid":item_uuid[name],"name":name,"name_bn":bn,"sku":sku,"selling_price":"%.2f"%price,"cost_price":"%.2f"%cost,"tax_rate":"0.00","is_available":1,"is_active":1},{**B(),"category":("categories",cat_uuid[cat])})
    for item,vs in VARIANTS.items():
        for vn,price,cost,dflt in vs:
            o.ins("item_variants",{"uuid":U("var:%s:%s"%(item,vn)),"name":vn,"price":"%.2f"%price,"cost_price":"%.2f"%cost,"is_default":dflt,"is_active":1},{**B(),"menu_item":("menu_items",item_uuid[item])})
    for name,grp,price in ADDONS:
        o.ins("item_addons",{"uuid":U("addon:"+name),"name":name,"group_name":grp,"price":"%.2f"%price,"is_active":1},B())
    for item,adds in ADDON_MAP.items():
        for a in adds:
            o.ins("item_addon_mappings",{"uuid":U("am:%s:%s"%(item,a)),"max_qty":3},{**B(),"menu_item":("menu_items",item_uuid[item]),"addon":("item_addons",U("addon:"+a))})
    ing_uuid={}
    for name,sku,unit,stock,reorder,cost in INGREDIENTS:
        ing_uuid[name]=U("ing:"+name)
        o.ins("ingredients",{"uuid":ing_uuid[name],"name":name,"sku":sku,"unit":unit,"current_stock":"%.3f"%stock,"reorder_level":"%.3f"%reorder,"cost_per_unit":"%.2f"%cost,"is_active":1},B())
    for item,lines in RECIPES.items():
        ru=U("recipe:"+item)
        o.ins("recipes",{"uuid":ru,"yield_qty":"1.000"},{**B(),"menu_item":("menu_items",item_uuid[item])})
        for ing,qty in lines:
            o.ins("recipe_ingredients",{"uuid":U("ri:%s:%s"%(item,ing)),"quantity":"%.3f"%qty,"wastage_pct":"0.00"},{**B(),"recipe":("recipes",ru),"ingredient":("ingredients",ing_uuid[ing])})
    for name,cap,section in TABLES:
        o.ins("restaurant_tables",{"uuid":U("table:"+name),"name":name,"capacity":cap,"section":section,"status":"AVAILABLE","is_active":1},B())
    for code,desc,typ,val,minord,maxd,limit,percust in COUPONS:
        o.ins("coupons",{"uuid":U("coupon:"+code),"code":code,"description":desc,"type":typ,"value":"%.2f"%val,"min_order":"%.2f"%minord,"max_discount":("%.2f"%maxd) if maxd else None,"usage_limit":limit,"per_customer_limit":percust,"used_count":0,"is_active":1},B())
    for name,typ,val,minord,maxd,perm in DISCOUNTS:
        o.ins("discounts",{"uuid":U("disc:"+name),"name":name,"type":typ,"value":"%.2f"%val,"min_order":"%.2f"%minord,"max_discount":maxd,"requires_permission":perm,"is_active":1},B())
    for n in EXP_CATS:
        o.ins("expense_categories",{"uuid":U("expcat:"+n),"name":n,"is_active":1},B())
    o.ins("shifts",{"uuid":U("shift:day"),"name":"Day Shift","start_time":"10:00:00","end_time":"18:00:00","grace_minutes":10},B())
    o.ins("shifts",{"uuid":U("shift:night"),"name":"Evening Shift","start_time":"16:00:00","end_time":"23:59:00","grace_minutes":10},B())
    o.ins("suppliers",{"uuid":U("sup:demo"),"name":"Demo Supplier (edit or delete)","phone":"","opening_balance":"0.00","is_active":1},B())
    hdr="-- Darbar_E_Saltanat %s seed (GENERATED by database/tools/seed_data.py)\n-- Default login: admin / ChangeMe@123  (must_change_password=1 — change on first login!)\n"%d.upper()
    if d=="sqlite": o.lines.insert(0,"PRAGMA foreign_keys=ON;")
    return hdr+"\n".join(o.lines)+"\n"

def node_perms():
    perms = "export const PERMISSIONS = " + "{\n" + ",\n".join('  %s: "%s"'%(p.upper().replace(".","_"),p) for p in ALL) + "\n};\n"
    roles = "export const ROLE_CODES = Object.freeze({\n" + ",\n".join('  %s: "%s"'%(r,r) for r in ROLES) + "\n});\n"
    import json
    rp = "export const ROLE_PERMISSIONS = " + json.dumps({k:v[2] for k,v in ROLES.items()}, indent=2) + ";\n"
    rm = "export const ROLE_META = " + json.dumps({k:{"name":v[0],"description":v[1]} for k,v in ROLES.items()}, indent=2, ensure_ascii=False) + ";\n"
    return "// GENERATED by database/tools/seed_data.py — do not edit\n"+perms+roles+rp+rm

def cs_perms():
    body="\n".join('    public const string %s = "%s";'%("".join(w.capitalize() for w in p.replace(".","_").split("_")),p) for p in ALL)
    allp="\n".join('        %s.%s,'%("Permissions","".join(w.capitalize() for w in p.replace(".","_").split("_"))) for p in ALL)
    return ("// <auto-generated> by database/tools/seed_data.py — do not edit </auto-generated>\nnamespace DarbarESaltanat.Domain.Security;\n\n"
            "public static class Permissions\n{\n"+body+"\n\n    public static readonly string[] All =\n    [\n"+allp+"\n    ];\n}\n")

if __name__=="__main__":
    (ROOT/"database"/"mysql-seed.sql").write_text(build("mysql"),encoding="utf-8")
    (ROOT/"database"/"sqlite-seed.sql").write_text(build("sqlite"),encoding="utf-8")
    sec=ROOT/"web"/"server"/"src"/"security"; sec.mkdir(parents=True,exist_ok=True)
    (sec/"permissions.js").write_text(node_perms(),encoding="utf-8")
    d=ROOT/"desktop"/"DarbarESaltanat.Domain"/"Security"; d.mkdir(parents=True,exist_ok=True)
    (d/"Permissions.cs").write_text(cs_perms(),encoding="utf-8")
    print("seed generated;", len(PERMISSIONS),"permissions;",len(ROLES),"roles")
