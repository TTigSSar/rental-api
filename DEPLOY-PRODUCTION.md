# Развёртывание production (dorent.am) на VPS

Пошаговая инструкция по первому production-развёртыванию `dorent.am` на VPS
(Ubuntu, IP `<SERVER_IP>`) через Cloudflare Tunnel. Сервер — 2 ГБ RAM
(Hetzner CPX12), поэтому многие шаги ниже (swap, лимит памяти SQL Server,
порядок `build`/`up`) существуют именно из-за этого ограничения. Домен
`dorent.am` уже подключён к Cloudflare. Входящие порты на сервере
**не открываются** — весь трафик идёт через исходящее (outbound) соединение
`cloudflared`.

> Репозиторий публичный — реальный IP сервера и SSH-логин намеренно не
> хранятся здесь. Подставьте свои значения вместо `<SERVER_IP>` и `<SSH_USER>`.

Файлы стенда: `docker-compose.production.yml`, `.env.production.example` —
оба лежат в корне репозитория `rental-api`.

Это первое production-развёртывание — база данных стартует пустой (в отличие
от staging, dev-сид в Production не запускается). См. раздел **e** про
bootstrap-администратора и раздел **h** про предупреждения перед запуском.

---

## a. Подготовка сервера

Первое подключение (пока ещё под тем пользователем, что дал провайдер, обычно `root`):

```bash
ssh root@<SERVER_IP>
```

### Отдельный sudo-пользователь

Не работать под `root` постоянно — создать пользователя `dorent`:

```bash
adduser dorent
usermod -aG sudo dorent
rsync --archive --chown=dorent:dorent ~/.ssh /home/dorent
```

Открыть **второй, отдельный** SSH-сеанс и убедиться, что вход под новым
пользователем работает, ДО того как отключать что-либо в первом сеансе:

```bash
ssh <SSH_USER>@<SERVER_IP>
sudo whoami   # должно вывести "root" без ошибок
```

### Отключение входа по паролю (только после проверки выше)

Только когда вход под `dorent` по ключу подтверждён работающим во втором
сеансе — отключить парольную аутентификацию SSH:

```bash
sudo nano /etc/ssh/sshd_config
# PasswordAuthentication no
# PermitRootLogin no
sudo systemctl restart ssh
```

Не закрывать первый (root) сеанс, пока не подтверждено, что новый SSH-вход
работает — иначе при ошибке в конфиге можно потерять доступ к серверу.

### Своп-файл (обязателен — сервер на 2 ГБ RAM)

Сборка образов и одновременная работа SQL Server на 2 ГБ RAM без свопа
приводит к OOM. Обязателен своп-файл на 4 ГБ:

```bash
sudo fallocate -l 4G /swapfile
sudo chmod 600 /swapfile
sudo mkswap /swapfile
sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
free -h
```

### Docker + плагин compose

```bash
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
sudo usermod -aG docker $USER
newgrp docker
docker --version
docker compose version
```

Если плагин `docker compose` не установился скриптом (обычно ставится
автоматически как `docker-compose-plugin`):

```bash
sudo apt-get update
sudo apt-get install -y docker-compose-plugin
```

### Ротация логов Docker-демона

Дополнительно к ротации на уровне сервисов в `docker-compose.production.yml`
настроить лимит и на уровне демона (страхует любые контейнеры/логи, не
охваченные compose-файлом):

```bash
sudo nano /etc/docker/daemon.json
```

```json
{
  "log-driver": "json-file",
  "log-opts": {
    "max-size": "10m",
    "max-file": "3"
  }
}
```

```bash
sudo systemctl restart docker
```

### Firewall

Разрешить только SSH — входящие 80/443 не нужны, туннель полностью исходящий:

```bash
sudo ufw allow OpenSSH
sudo ufw enable
sudo ufw status
```

## b. Раскладка и получение кода на сервере

```bash
sudo mkdir -p /opt/dorent
sudo chown dorent:dorent /opt/dorent
mkdir -p /opt/dorent/{rental-api,Rental-Ui,backups,deployment-notes}
```

`ui` собирается из `../Rental-Ui` относительно `docker-compose.production.yml`,
поэтому оба репозитория должны лежать рядом друг с другом (`rental-api` и
`Rental-Ui` — соседние каталоги внутри `/opt/dorent`):

```bash
cd /opt/dorent
git clone <URL_rental-api> rental-api
git clone <URL_Rental-Ui> Rental-Ui
cd rental-api
git checkout main   # production разворачивается с проверенной ветки, не dev
```

Если удалённых репозиториев нет под рукой — можно скопировать код с локальной
машины через `rsync`:

```bash
rsync -avz --exclude .git rental-api/  <SSH_USER>@<SERVER_IP>:/opt/dorent/rental-api/
rsync -avz --exclude .git Rental-Ui/   <SSH_USER>@<SERVER_IP>:/opt/dorent/Rental-Ui/
```

`deployment-notes/` — свободный каталог для заметок о конкретных
развёртываниях (какая версия/тег когда выкатывалась и т.п.), `backups/` —
см. раздел i.

## c. Файл окружения

В каталоге `/opt/dorent/rental-api`:

```bash
cp .env.production.example .env
nano .env
```

Заполнить пять значений (правила и генерация — см. комментарии в самом файле):

- `MSSQL_SA_PASSWORD` — пароль SA для SQL Server (мин. 8 симв., 3 из 4 классов символов).
- `JWT_SECRET_KEY` — секрет JWT, минимум 32 символа, `openssl rand -base64 48`.
- `CLOUDFLARE_TUNNEL_TOKEN` — токен туннеля, см. пункт d.
- `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` — учётные данные первого
  администратора, см. пункт e.

`.env` не коммитится (уже в `.gitignore`).

## d. Настройка Cloudflare Tunnel

1. Зайти в **Cloudflare Zero Trust → Networks → Tunnels**.
2. **Create a tunnel** → тип коннектора **Cloudflared**.
3. Дать имя `dorent-production`.
4. На шаге установки коннектора Cloudflare покажет команду вида
   `docker run cloudflare/cloudflared:latest tunnel run --token <TOKEN>` —
   скопировать только `<TOKEN>` в `CLOUDFLARE_TUNNEL_TOKEN` в `.env`
   (контейнер `cloudflared` в compose-файле запустится сам, отдельно руками
   его запускать не нужно).
5. Перейти на вкладку **Public Hostnames** этого туннеля и добавить:
   - `dorent.am` → Service: `HTTP` → `ui:80`
   - `www.dorent.am` → Service: `HTTP` → `ui:80`
6. DNS-записи в зоне `dorent.am` Cloudflare создаст автоматически при
   добавлении public hostname — руками ничего прописывать не нужно.

## e. Bootstrap-администратор

В Production dev-сид не запускается (`ASPNETCORE_ENVIRONMENT=Production`
отключает и его, и Swagger — см. `Program.cs`), поэтому на пустой базе нет
ни одного пользователя, включая администратора. Механизм: при старте, после
применения миграций, если в `.env` заданы ОБА значения
`BOOTSTRAP_ADMIN_EMAIL` и `BOOTSTRAP_ADMIN_PASSWORD` и пользователя с таким
email ещё нет — приложение само создаёт одного Admin-пользователя с этим
email и паролем (пароль хешируется тем же BCrypt-хешером, что и обычная
регистрация). Если пользователь уже существует — ничего не делает. Если
значения не заданы — ничего не делает.

Это первый (и на момент написания единственный) логин в свежей production
базе — см. предупреждение в разделе h.

## f. Порядок запуска (важно на 2 ГБ RAM)

Собрать образы ДО того, как поднимать SQL Server — сборка (`dotnet publish`,
`npm run build`) и SQL Server одновременно легко упираются в память на 2 ГБ:

```bash
cd /opt/dorent/rental-api
docker compose -f docker-compose.production.yml build
docker compose -f docker-compose.production.yml up -d
```

(`up --build` собирает и поднимает всё одной командой, но на этом сервере
надёжнее развести сборку и запуск на два шага, как выше.)

Первый запуск занимает несколько минут — инициализация SQL Server внутри
контейнера, применение миграций EF Core и bootstrap администратора (см.
пункт e) при старте API.

## g. Проверка

```bash
docker compose -f docker-compose.production.yml ps
docker compose -f docker-compose.production.yml logs -f api
```

Все сервисы (`db`, `api`, `ui`, `cloudflared`) должны быть в состоянии
`running`, а `db`/`api`/`ui` — `healthy`.

Проверки с самого сервера (через loopback-порты, без похода через туннель):

```bash
# API отвечает на liveness-проверку (тот же путь, что использует docker healthcheck)
curl -s http://127.0.0.1:8080/health
# ожидается: {"status":"ok"}

# UI отдаёт страницу
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:4200/
# ожидается: 200
```

После того как DNS туннеля применился — проверки через публичный домен:

```bash
# Главная страница SPA
curl -s -o /dev/null -w "%{http_code}\n" https://dorent.am/
# ожидается: 200

# SPA fallback — несуществующий фронтенд-маршрут тоже должен отдать index.html (200),
# а не 404, иначе прямые ссылки/обновление страницы в браузере будут ломаться
curl -s -o /dev/null -w "%{http_code}\n" https://dorent.am/some/deep/spa/route
# ожидается: 200

# API через прокси nginx -> api:8080
curl -s -o /dev/null -w "%{http_code}\n" https://dorent.am/api/categories
# ожидается: 200

# Раздача загруженных файлов через прокси /uploads/
curl -s -o /dev/null -w "%{http_code}\n" https://dorent.am/uploads/listings/does-not-exist.jpg
# ожидается: 404 (не 502/503 — значит проксирование до api работает, файла просто нет)

# SignalR negotiate — ключевая регрессия M-008: если этот путь провалится в SPA
# fallback вместо nginx location /hubs/, здесь будет 405 вместо 200/401
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://dorent.am/hubs/chat/negotiate
# ожидается: 200 (анонимный negotiate) или 401 (если хаб требует авторизации) — НЕ 405
```

В браузере открыть `https://dorent.am`:

- войти под учётными данными bootstrap-администратора (пункт e) — это первый
  логин в системе;
- убедиться, что изображения объявлений загружаются;
- открыть чат и проверить, что сообщения приходят в реальном времени
  (WebSocket через `/hubs/`, проксируется туннелем).

### Данные переживают перезапуск без `-v`

```bash
docker compose -f docker-compose.production.yml down
docker compose -f docker-compose.production.yml up -d
```

После этого данные (пользователи, объявления, загруженные файлы) должны быть
на месте — том `sqldata`/`uploads`/`chat-uploads` не удалялся, потому что
флаг `-v` не передавался (см. предупреждение ниже).

## h. Важные предупреждения

- **Никогда не выполнять `down -v` на этом стенде.** Флаг `-v` удаляет
  named volumes — это база данных и все загруженные файлы. `down` без `-v`
  безопасен (контейнеры пересоздаются, данные остаются). Есть только в
  DEPLOY-STAGING.md как способ снести тестовый стенд — здесь этого раздела
  нет намеренно.
- **В Production нет ни Swagger, ни dev-сида.** Оба гейтятся на
  `IsDevelopment()` в `Program.cs`. Если что-то нужно продебажить через
  Swagger — это не сюда, это для staging/local.
- **Первый логин — bootstrap-администратор** (пункт e). Других учётных
  записей на свежей базе не существует.

## i. Резервное копирование

Минимальный вариант: ежедневный бэкап через `sqlcmd` внутри контейнера `db`
в bind-mount `/opt/dorent/backups`, плюс архив томов с загруженными файлами.

```bash
mkdir -p /opt/dorent/backups
cd /opt/dorent/rental-api

# Бэкап базы (sqlcmd уже есть в образе mssql/server)
docker compose -f docker-compose.production.yml exec -T db \
  /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q \
  "BACKUP DATABASE RentalPlatformDb TO DISK = N'/var/opt/mssql/backup_$(date +%Y%m%d).bak'"

# .bak остаётся внутри тома db — скопировать его наружу, в bind-mount:
docker compose -f docker-compose.production.yml cp \
  db:/var/opt/mssql/backup_$(date +%Y%m%d).bak \
  /opt/dorent/backups/backup_$(date +%Y%m%d).bak

# Загруженные файлы — архивом из именованных томов
docker run --rm \
  -v rental-api_uploads:/uploads \
  -v rental-api_chat-uploads:/chat-uploads \
  -v /opt/dorent/backups:/backup \
  alpine tar czf /backup/uploads_$(date +%Y%m%d).tar.gz /uploads /chat-uploads

# Хранить последние 7 дней, удалять более старые
find /opt/dorent/backups -name '*.bak' -mtime +7 -delete
find /opt/dorent/backups -name '*.tar.gz' -mtime +7 -delete
```

Оформить это как ежедневную cron-задачу (`crontab -e` под `dorent`), либо
как systemd timer — выбор оставлен на усмотрение того, кто разворачивает.

**Восстановление обязательно проверить, прежде чем полагаться на бэкап** —
непроверенный бэкап нельзя считать бэкапом. Проверка на отдельном стенде
(например staging), не на production:

```bash
# Скопировать .bak на стенд, затем внутри контейнера db:
docker compose -f docker-compose.staging.yml exec -T db \
  /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q \
  "RESTORE DATABASE RentalPlatformDb FROM DISK = N'/var/opt/mssql/backup_YYYYMMDD.bak' WITH REPLACE"
```

## Обновление стенда

```bash
cd /opt/dorent/rental-api  && git pull
cd /opt/dorent/Rental-Ui   && git pull
cd /opt/dorent/rental-api
docker compose -f docker-compose.production.yml build
docker compose -f docker-compose.production.yml up -d
```

Миграции EF Core применяются автоматически при старте `api` (`MigrationExtensions`,
работает во всех окружениях) — отдельно накатывать их не нужно, но перед
обновлением production рекомендуется снять бэкап (пункт i).
