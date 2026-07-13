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

Автоматизировано скриптом `deploy/backup-production.sh` (версионирован в
репозитории `rental-api`). Работает из любого текущего каталога — сам
находит `docker-compose.production.yml` и `.env` рядом со своим
расположением (`<repo>/deploy/backup-production.sh` → compose-файл в
`<repo>/`).

### Что делает ежедневный запуск (без аргументов)

1. Читает `MSSQL_SA_PASSWORD` из `/opt/dorent/rental-api/.env` (построчным
   парсингом, без `source` — файл `.env` никогда не исполняется как shell-код).
2. `BACKUP DATABASE [RentalPlatformDb] ... WITH COMPRESSION, INIT` внутри
   контейнера `db`. Пароль передаётся через переменную окружения
   `SQLCMDPASSWORD` самого `sqlcmd`-процесса (`-U sa`, без `-P`) — так он не
   попадает в вывод `ps` внутри контейнера.
3. `.bak` копируется наружу через `docker cp` в `/opt/dorent/backups/`, копия
   внутри контейнера удаляется.
4. Загруженные файлы (`rental-api_uploads`, `rental-api_chat-uploads`)
   архивируются в `/opt/dorent/backups/uploads_<штамп>.tgz` одноразовым
   `alpine`-контейнером — **не** вторым SQL Server контейнером: на 2 ГБ RAM
   с уже занятым SQL Server (`MSSQL_MEMORY_LIMIT_MB=1024`) второй такой
   контейнер не поместится.
5. Ротация: `.bak`/`.tgz` в `/opt/dorent/backups` старше 7 дней удаляются.
6. Одна строка результата (время, имена файлов, размеры, OK/FAIL)
   добавляется в `/opt/dorent/backups/backup.log`. Любая ошибка на любом шаге
   → ненулевой код возврата и `FAIL`-строка с причиной в этом же логе.

Ручной запуск:

```bash
/opt/dorent/rental-api/deploy/backup-production.sh
tail -1 /opt/dorent/backups/backup.log
```

### Cron

```bash
crontab -e   # под пользователем dorent
```

```cron
# У cron минимальный PATH (обычно без /usr/local/bin) — скрипт сам добавляет
# /usr/local/bin:/usr/bin:/bin в начало PATH, поэтому вызывать его по
# абсолютному пути достаточно, отдельно чинить PATH в crontab не нужно.
30 3 * * * /opt/dorent/rental-api/deploy/backup-production.sh >> /opt/dorent/backups/backup.log 2>&1
```

(Скрипт и так пишет результат в `backup.log` сам — перенаправление в cron
дополнительно ловит любой вывод/трассировку, которая по какой-то причине
не попала в лог штатным путём, например падение до того, как скрипт
успел сам начать логировать.)

### Проверка восстановления (`--verify`)

**Непроверенный бэкап нельзя считать бэкапом.** `--verify` копирует САМЫЙ
СВЕЖИЙ локальный `.bak` обратно в тот же контейнер `db`, разворачивает его
как отдельную базу `RentalPlatformDb_verify` (уникальные имена `.mdf`/`.ldf`,
вычисленные из `RESTORE FILELISTONLY` самого бэкапа — рядом с боевой базой,
без конфликта имён файлов), выполняет проверочный запрос
(`SELECT COUNT(*) FROM [RentalPlatformDb_verify].dbo.Users`) и печатает
результат, затем **дропает** `RentalPlatformDb_verify` и удаляет временную
копию `.bak` внутри контейнера — в любом случае, даже если что-то по пути
упало (safety-net cleanup).

Специально сделано как отдельная база **внутри существующего контейнера
`db`**, а не второй контейнер SQL Server — на этом сервере (2 ГБ RAM,
SQL Server уже занял отведённый ему 1 ГБ) второй полноценный экземпляр
SQL Server просто не запустится.

```bash
/opt/dorent/rental-api/deploy/backup-production.sh --verify
```

Ожидаемый вывод: `VERIFY PASS: restored <file>.bak as RentalPlatformDb_verify,
dbo.Users COUNT(*) = <N>` — и `N` должно быть правдоподобным (не ноль, если в
базе реально есть пользователи). Запускать периодически (не только один раз
после написания скрипта) — бэкап, который проверялся полгода назад, снова
превращается в непроверенный, если формат/схема успели измениться.

### Off-site копия — пока не сделано

Сейчас `/opt/dorent/backups` — это тот же физический сервер, что и сами
данные: пожар/диск/провайдер убьёт и то, и другое одновременно. Это
осознанный временный пробел, а не забытый пункт. Варианты на выбор, когда
дойдут руки:

- **Backblaze B2 (free tier) через `rclone`** — `rclone sync
  /opt/dorent/backups b2:<bucket>/dorent-backups` отдельной cron-задачей
  после `backup-production.sh`;
- периодический `rsync`/`scp` `/opt/dorent/backups` на локальную машину или
  другой сервер, вне Hetzner.

До тех пор бэкапы защищают только от «сломали руками/багом», а не от отказа
сервера целиком.

## j. Smoke-проверка (`deploy/smoke.sh`)

Скрипт `deploy/smoke.sh` (версионирован в репозитории `rental-api`) прогоняет
полный набор инфраструктурных проверок стенда: состояние контейнеров,
`/health` API, отдачу Angular-приложения, проксирование `/api/` (заодно
доказывает связь API → SQL Server — эндпойнт ходит в базу), оба публичных
домена через туннель, маршрутизацию `/uploads/` и `/hubs/` (регрессия M-008),
плюс предупреждающие проверки: ошибки в логах API, диск, своп, бэкапы,
права на `.env`. Скрипт **только читает** — ничего в стенде не меняет,
секретов не печатает, запускать можно в любой момент и из любого каталога.

**Правило рабочего процесса: деплой не считается завершённым, пока
`smoke.sh` не отработал без `FAIL`.** Запускать после каждого обновления
стенда (см. «Обновление стенда» ниже), а также при любом подозрении, что
что-то не так.

```bash
/opt/dorent/rental-api/deploy/smoke.sh
```

### Как читать вывод

Одна строка на проверку — `PASS`, `WARN` или `FAIL` + идентификатор проверки
и короткая причина (вывод удобно грепать: `smoke.sh | grep -E '^(WARN|FAIL)'`).
В конце — сводка `checks: pass=N warn=N fail=N` и вердикт `overall:`.

- `FAIL` — обязательная (mandatory) проверка провалена: стенд неисправен,
  деплой не завершён, разбираться немедленно.
- `WARN` — не блокирует (код возврата остаётся 0), но требует внимания:
  например, подозрительные строки в логах, диск ≥80 %, бэкап старше 26 часов.
  Заполнение диска ≥90 % — исключение: оно эскалируется до `FAIL`.
- Проверка свежести бэкапа легитимно даёт `WARN` до первого запуска cron
  в 03:30 после (пере)развёртывания.

### Коды возврата

- `0` — все обязательные проверки прошли (предупреждения возможны);
- `1` — хотя бы одна обязательная проверка провалена.

## Обновление стенда

```bash
cd /opt/dorent/rental-api  && git pull
cd /opt/dorent/Rental-Ui   && git pull
cd /opt/dorent/rental-api
docker compose -f docker-compose.production.yml build
docker compose -f docker-compose.production.yml up -d
./deploy/smoke.sh   # деплой завершён, только когда smoke-проверка прошла (раздел j)
```

Миграции EF Core применяются автоматически при старте `api` (`MigrationExtensions`,
работает во всех окружениях) — отдельно накатывать их не нужно, но перед
обновлением production рекомендуется снять бэкап (пункт i).
