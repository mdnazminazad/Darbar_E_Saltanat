# Developer setup

## Prerequisites
* Node.js 20+ and npm · MySQL 8 · Python 3.10+ (only to regenerate schema/seed)
* Windows 10/11 with .NET 8 SDK and Visual Studio 2022 17.8+ (desktop)

## 1. Database
```bash
mysql -u root -p -e "CREATE USER 'darbar_app'@'localhost' IDENTIFIED BY 'strong-password';
  CREATE DATABASE darbar_e_saltanat CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  GRANT ALL ON darbar_e_saltanat.* TO 'darbar_app'@'localhost';"
cd web/server && cp .env.example .env    # edit DB_* and JWT_ACCESS_SECRET (openssl rand -hex 32)
npm install
npm run migrate -- --seed                 # applies database/mysql-schema.sql + seed
```
Default login: **admin / ChangeMe@123** — you are forced to change it at first login.

## 2. API
```bash
cd web/server
npm run dev          # http://localhost:4000/api/v1/health · docs at /api/docs
npm test             # integration tests (in-memory SQLite, no MySQL needed)
```

### Try the API without MySQL
```bash
cd web/server && npm run dev:sqlite     # in-memory SQLite + production seed, prints a registered device id + token
```
Point the web client (`npm run dev`) or a desktop till (Settings → Server sync, `http://127.0.0.1:4000/api/v1`) at it. Data is lost when it stops — development only.

## 3. Web client
```bash
cd web/client && npm install
npm run dev          # http://localhost:5173 (proxies /api to :4000)
npm run build        # → web/client/dist
```

## 4. Desktop (Windows)
```powershell
cd desktop
dotnet restore
dotnet test DarbarESaltanat.Tests
dotnet run --project DarbarESaltanat.Desktop
```
First run creates `%LOCALAPPDATA%\DarbarESaltanat\darbar.db` from the embedded schema + seed (works offline). Logs: `%LOCALAPPDATA%\DarbarESaltanat\logs`.
To connect a PC to the server: portal → Devices & Sync → Register device → paste Device ID + token in desktop Settings → Server sync.

## 5. Changing the schema
Edit `database/tools/schema_spec.py` (and `seed_data.py` for seed/permissions), then:
```bash
python3 database/tools/gen_schema.py
python3 database/tools/seed_data.py "$(cd web/server && node -e "import('bcryptjs').then(m=>console.log(m.default.hashSync('ChangeMe@123',12)))")"
```
For a database already in production, also add an incremental `database/migrations/NNN_description.sql` (applied in order by `npm run migrate`) and bump `DatabaseInitializer.CurrentSchemaVersion` with a matching SQLite migration step.
