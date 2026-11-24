# Deployment on the existing PHP server

The Node API runs beside PHP; Nginx (or Apache) terminates TLS and routes `/api` to Node. MySQL 8 is shared.

## 1. Server prerequisites
```bash
# Node 20 LTS + PM2
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash - && sudo apt-get install -y nodejs
sudo npm i -g pm2
```
Create a MySQL user with privileges only on `darbar_e_saltanat` (see setup.md). Never expose port 3306 publicly.

## 2. Deploy code
```bash
sudo mkdir -p /var/www/darbar && sudo chown $USER /var/www/darbar
cd /var/www/darbar && git clone <repo> . 
cd web/server && npm ci --omit=dev && cp .env.example .env && nano .env   # NODE_ENV=production, DB_*, JWT secret, CORS_ORIGINS, REQUIRE_HTTPS=true
npm run migrate -- --seed            # first time only (seed is skipped when data exists)
pm2 start src/server.js --name darbar-api -i 2 && pm2 save && pm2 startup
cd ../client && npm ci && npm run build   # outputs web/client/dist
```

## 3. Nginx (recommended)
```nginx
server {
    listen 443 ssl http2;
    server_name darbaresaltanat.com;
    ssl_certificate     /etc/letsencrypt/live/darbaresaltanat.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/darbaresaltanat.com/privkey.pem;
    client_max_body_size 6m;

    location /api/ {
        proxy_pass http://127.0.0.1:4000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 60s;
    }
    location /uploads/ { alias /var/www/darbar/web/server/uploads/; expires 30d; add_header X-Content-Type-Options nosniff; }

    root /var/www/darbar/web/client/dist;
    location / { try_files $uri /index.html; }
    location /assets/ { expires 1y; add_header Cache-Control "public, immutable"; }
}
server { listen 80; server_name darbaresaltanat.com; return 301 https://$host$request_uri; }
```
If PHP sites share the host, keep their `server` blocks unchanged; this block only claims the restaurant domain (or use a subdomain such as `pos.example.com`).

## 4. Apache alternative
```apache
<VirtualHost *:443>
  ServerName darbaresaltanat.com
  SSLEngine on
  SSLCertificateFile /etc/letsencrypt/live/darbaresaltanat.com/fullchain.pem
  SSLCertificateKeyFile /etc/letsencrypt/live/darbaresaltanat.com/privkey.pem
  ProxyPreserveHost On
  RequestHeader set X-Forwarded-Proto "https"
  ProxyPass /api/ http://127.0.0.1:4000/api/
  ProxyPassReverse /api/ http://127.0.0.1:4000/api/
  DocumentRoot /var/www/darbar/web/client/dist
  <Directory /var/www/darbar/web/client/dist>
    FallbackResource /index.html
    Require all granted
  </Directory>
</VirtualHost>
```
Enable: `a2enmod proxy proxy_http headers ssl rewrite`.

Shared hosting without root/SSH (cPanel): use cPanel "Setup Node.js App" (Passenger) pointing at `web/server/src/server.js`, and upload `web/client/dist` to `public_html`. Confirm Node 20 is offered first.

## 5. TLS & hardening
* `certbot --nginx -d darbaresaltanat.com` (auto-renew).
* `REQUIRE_HTTPS=true`, strong `JWT_ACCESS_SECRET`, `CORS_ORIGINS` limited to your domains.
* Firewall: allow 22/80/443 only. MySQL bound to 127.0.0.1.
* `/api/docs` is disabled when `NODE_ENV=production`.

## 6. Backups
```bash
# nightly 02:30 Asia/Dhaka, keep 14 days
30 2 * * * mysqldump --single-transaction --routines -u darbar_backup darbar_e_saltanat | gzip > /var/backups/darbar/db-$(date +\%F).sql.gz && find /var/backups/darbar -mtime +14 -delete
```
Copy backups off the server (e.g. rclone to Google Drive). Desktop PCs keep their own SQLite database; Phase 6 adds one-click local backup/restore.

## 7. Updating
```bash
cd /var/www/darbar && git pull
cd web/server && npm ci --omit=dev && npm run migrate && pm2 reload darbar-api
cd ../client && npm ci && npm run build
```
Desktop updates: publish with `dotnet publish DarbarESaltanat.Desktop -c Release -r win-x64 --self-contained` and install over the old version; the local database is kept in `%LOCALAPPDATA%`.
