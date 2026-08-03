# Развёртывание production (dorent.am) на VPS

Пошаговая инструкция по первому production-развёртыванию `dorent.am` на VPS
(Ubuntu, IP `<SERVER_IP>`) через Cloudflare Tunnel. Сервер — 2 ГБ RAM
(Hetzner CPX12), поэтому многие шаги ниже (swap, лимит памяти SQL Server,
порядок `build`/`up`) существуют именно из-за этого ограничения. Домен
`dorent.am` уже подключён к Cloudflare. Входящие порты на сервере
**не открываются** — весь трафик идёт через исходящее (outbound) соединение
`cloudflared`.

> Репозиторий публичный — реальный IP сервера, SSH-логин и email владельца
> намеренно не хранятся здесь. Подставьте свои значения вместо `<SERVER_IP>`,
> `<SSH_USER>` и `<OWNER_EMAIL>`.

> **Стенд не открыт публике.** `dorent.am` и `www.dorent.am` закрыты Cloudflare
> Access: пускает только владельца, по одноразовому коду на email. Это гейт на
> краю Cloudflare, перед туннелем — стек о нём не знает. См. раздел **k**
> (включая откат в публичное состояние) и ADR-009.
>
> ⚠️ **СТАТУС на 2026-07-31: раздел k описан, но на живом стенде НЕ применён.**
> Проверено при деплое `b8ff848`: анонимный `curl https://dorent.am/` отдаёт
> `200` (а не `302` на страницу логина Cloudflare), и в `/opt/dorent/rental-api/.env`
> нет ни `CF_ACCESS_CLIENT_ID`, ни `CF_ACCESS_CLIENT_SECRET`. Как следствие
> `smoke.sh` работает в режиме `access mode: PUBLIC`, а проверки `access-gate-*`
> не выполняются вообще. **Сайт сейчас доступен любому, кто знает адрес.**
> Код и smoke-инструментарий к гейту готовы (коммит `729ebe9`) — не сделаны
> шаги k.1–k.3 в панели Cloudflare Zero Trust, они требуют владельца аккаунта
> (политика по email + одноразовый показ Client Secret сервисного токена).
> Снять это предупреждение только после того, как `smoke.sh` напечатает
> `access mode: ENFORCED` и `PASS access-gate-dorent.am`.

Файлы стенда: `docker-compose.production.yml`, `.env.production.example` —
оба лежат в корне репозитория `rental-api`.

Это первое production-развёртывание — база данных стартует пустой (dev-сид
гейтится на `IsDevelopment()` и в Production не запускается; засеянные данные
есть только в локальном `docker-compose.yml`). См. раздел **e** про
bootstrap-администратора и раздел **h** про предупреждения перед запуском.

> На VPS работает **только** этот production-стек. Staging-стенда не существует:
> `test.dorent.am` не резолвится, `docker-compose.staging.yml` никогда не
> запускался. См. ADR-004 в `knowledge/decisions.md`.

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

Заполнить значения (правила и генерация — см. комментарии в самом файле):

- `MSSQL_SA_PASSWORD` — пароль SA для SQL Server (мин. 8 симв., 3 из 4 классов символов).
- `JWT_SECRET_KEY` — секрет JWT, минимум 32 символа, `openssl rand -base64 48`.
- `CLOUDFLARE_TUNNEL_TOKEN` — токен туннеля, см. пункт d.
- `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` — учётные данные первого
  администратора, см. пункт e.
- `BOOTSTRAP_DEMO_CONTENT_ENABLED` / `BOOTSTRAP_DEMO_OWNER_EMAIL` /
  `BOOTSTRAP_DEMO_OWNER_PASSWORD` — начальный каталог, см. пункт e. Необязательные:
  если оставить пустыми или вовсе не задавать — каталог просто не создаётся,
  остальной стек работает как обычно.

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

### Начальный каталог (demo-контент)

Та же логика, но для витрины: на пустой базе нет ни одного объявления, и
посетителю не на что смотреть. Три переменные в `.env`:

- `BOOTSTRAP_DEMO_CONTENT_ENABLED` — включает механизм. Допустимые значения:
  `true`, `false`, либо пусто/не задано (тогда compose подставит `false` —
  в `docker-compose.production.yml` стоит `${BOOTSTRAP_DEMO_CONTENT_ENABLED:-false}`).
  **Другие написания (`yes`, `1`, `on`) недопустимы** — приложение читает это
  значение как `bool` и на любом другом тексте падает при старте с
  `InvalidOperationException`, а вместе с api не поднимутся ui и cloudflared
  (они ждут `service_healthy` у api). Писать только `true` или `false`.
- `BOOTSTRAP_DEMO_OWNER_EMAIL` / `BOOTSTRAP_DEMO_OWNER_PASSWORD` — учётные данные
  аккаунта-владельца витрины.

Если механизм включён и оба значения владельца заданы, при старте (после миграций)
приложение создаёт один аккаунт-владельца и его каталог Approved-объявлений с
картинками (`DemoContentBootstrapRunner`). Если выключен или значения пустые —
тихий no-op, как и у админ-пары выше.

**Идемпотентно и только на добавление.** Объявления привязаны к фиксированным
GUID: повторный старт с включённым флагом ничего не продублирует и не перезапишет —
в том числе правки, которые владелец витрины позже внёс через обычный UI. Как только
контент создан, механизм становится no-op, поэтому флаг можно спокойно оставить
включённым навсегда. Никаких других аккаунтов, бронирований, отзывов и чатов он не
создаёт.

**Владелец витрины — настоящий живой аккаунт на публичном сайте, а не «демо-логин».**
Под этим email можно войти и управлять объявлениями. Пароль должен быть длинным,
случайным и уникальным (`openssl rand -base64 24`) — общие/«демонстрационные»
пароли здесь недопустимы, требования те же, что к bootstrap-администратору из
пункта e.

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

### Бэкфилл координат объявлений

**Проверяется автоматически** — проверка `geo-map-pins` в `smoke.sh`
(обязательная, влияет на вердикт и код возврата). Руками после гео-деплоя
делать ничего не нужно: достаточно того, что `smoke.sh` прошёл без `FAIL`.
Запросы ниже нужны, только если `geo-map-pins` **упала** — они локализуют
причину.

`Program.cs` на старте выполняет `ApplyMigrationsAsync()`, а сразу за ним
`BackfillListingLocationsAsync()` — обе до `app.Run()`. Второй шаг
(`ListingLocationBackfillRunner`) заполняет `PublicLatitude`/`PublicLongitude`/
`DistrictId` у всех объявлений, у которых есть точные `Latitude`/`Longitude`,
но производные значения пусты. Именно на этот шаг рассчитывает миграция
`20260725165816_InvalidatePublicCoordinatesForGeohashPrecisionUpgrade`: она
обнуляет публичные координаты, а пересчитывает их не она, а бэкфилл.

Если бэкфилл не отработал, объявления остаются с `PublicLatitude IS NULL` и
**молча исчезают** из `GET /api/listings/map-pins` и из фильтра по радиусу —
сами объявления при этом целы, поэтому ни на главной странице, ни в остальных
проверках стенда поломка не видна. Ровно это и ловит `geo-map-pins`: она
сравнивает число пинов с `totalCount` одобренного каталога, и «в каталоге есть
объявления, а пинов ноль» — это `FAIL`. Пустой каталог (`totalCount = 0`)
пинов и не должен давать, поэтому там проверка осознанно молчит.

Диагностика, когда `geo-map-pins` упала:

```bash
# 1) строка-итог в логах старта API
docker compose -f docker-compose.production.yml logs --no-color api \
  | grep -i "Public coordinates filled"
# ожидается: Public coordinates filled: N, districts assigned: M (of K candidate(s) examined)

# 2) факт в данных: ни одного объявления с точными координатами и пустыми публичными
#    (пароль — только через SQLCMDPASSWORD, никогда через -P в командной строке)
docker exec -e SQLCMDPASSWORD="$SQLCMDPASSWORD" rental-api-db-1 \
  /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b -h -1 -W -d RentalPlatformDb \
  -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM Listings
      WHERE Latitude IS NOT NULL AND Longitude IS NOT NULL
        AND (PublicLatitude IS NULL OR PublicLongitude IS NULL);"
# ожидается: 0
```

Откат этих миграций — **только восстановлением из `.bak`**. `Down()` у обеих
не проверялся, а у миграции точности он намеренно не «восстанавливает»
прежние значения (публичные координаты — производный кэш, а не источник
истины). Схемная миграция `AddDistrictsAndListingLocationFields` аддитивна
(только `ADD COLUMN`/`CREATE TABLE`/`CREATE INDEX`), поэтому предыдущий образ
API совместим с новой схемой — откат кода без отката схемы допустим.

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
домена через туннель, маршрутизацию `/uploads/` и `/hubs/` (регрессия M-008) —
и по петле `127.0.0.1`, и **через публичные домены**, потому что край Cloudflare
эти маршруты видит иначе, чем nginx (см. «Публичные проверки и Cloudflare
Access» ниже), плюс предупреждающие проверки: ошибки в логах API, диск, своп, бэкапы,
права на `.env`. Скрипт **только читает** — ничего в стенде не меняет,
секретов не печатает, запускать можно в любой момент и из любого каталога.

Особняком стоит `geo-map-pins`: это единственная проверка, которая смотрит не
«отвечает ли компонент», а **на сами данные**. Гео-поверхность умеет ломаться
молча — публичные координаты объявлений (`PublicLatitude`/`PublicLongitude`)
это производный кэш, который заполняет `ListingLocationBackfillRunner` на
старте API. Если он не отработал, объявления живы и все остальные проверки
зелёные, но карта и фильтр по радиусу пусты. Проверка сравнивает число пинов
`/api/listings/map-pins` с `totalCount` одобренного каталога и падает, когда
объявления есть, а пинов нет (подробнее — «Бэкфилл координат объявлений» в
разделе g). Равенства «пинов ровно столько же, сколько объявлений» она
намеренно не требует: координаты у объявления необязательны, так что
объявление без точки на карте — нормальное объявление.

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

### Публичные проверки и Cloudflare Access

Часть проверок (`domain-*`, `public-*`, `access-gate-*`) ходит на стенд **через
публичные домены**, то есть через край Cloudflare. Пока стенд закрыт Access
(раздел k), анонимный запрос туда получает редирект на страницу логина, а не
приложение, поэтому скрипт умеет ходить с **service-токеном** и сам определяет
режим по наличию `CF_ACCESS_CLIENT_ID` / `CF_ACCESS_CLIENT_SECRET`:

| Что в `.env` | Режим | Поведение |
|---|---|---|
| обе переменные | `ENFORCED` | публичные проверки идут с токеном и должны получить приложение; дополнительно **анонимная** проверка обоих хостов должна приложение НЕ получить |
| ни одной | `PUBLIC` | публичные проверки идут анонимно (поведение до Access) |
| только одна | `MISCONFIGURED` | сразу `FAIL access-token` — половина токена это опечатка, а не режим |

Режим печатается строкой `access mode:` в шапке вывода. Если Access включён, а
токена нет, проверки не «падают молча»: они дают `FAIL` с прямым указанием, какие
переменные и куда прописать.

Разовый запуск без правки `.env` (значения не попадают ни в историю shell при
запуске через `env`, ни в `ps` — скрипт передаёт их curl через конфиг-файл 600,
а не аргументом командной строки):

```bash
CF_ACCESS_CLIENT_ID=... CF_ACCESS_CLIENT_SECRET=... /opt/dorent/rental-api/deploy/smoke.sh
```

## k. Закрытый доступ: Cloudflare Access (Zero Trust)

Стенд поднят, но публика на него пускаться не должна: `dorent.am` и
`www.dorent.am` открываются только у владельца, с любого устройства и из любой
сети. Механизм — **Cloudflare Access** с аутентификацией по одноразовому коду на
email. Решение и отклонённая альтернатива (IP-allowlist) — ADR-009 в
`knowledge/decisions.md`.

Важное свойство: Access работает **на краю Cloudflare, перед туннелем**. Ни
`docker-compose.production.yml`, ни `nginx.conf`, ни DNS, ни сам туннель для
этого не меняются — контейнеры вообще не знают, что перед ними появился гейт.
Поэтому и откат (ниже) не требует ни пересборки, ни рестарта.

### k.1. Приложение Access

Cloudflare Zero Trust → **Access → Applications → Add an application →
Self-hosted**:

| Поле | Значение |
|---|---|
| Application name | `dorent-production` |
| Session Duration | **1 month** |
| Public hostname #1 | Subdomain: *(пусто)* · Domain: `dorent.am` · Path: *(пусто)* |
| Public hostname #2 | Subdomain: `www` · Domain: `dorent.am` · Path: *(пусто)* |

Пустой Path — это и есть «весь сайт», `/*`: SPA, `/api/`, `/uploads/`, `/hubs/`.
Оба хоста должны быть в **одном** приложении, иначе политику придётся вести в
двух местах и они разъедутся.

**Почему именно 1 month.** Это максимум, который Cloudflare даёт выбрать
(дальше только «No duration» — сессия умирает сразу). Каждое пересоздание сессии
= письмо с кодом и ручной ввод, в том числе на телефоне, поэтому короткий срок
здесь оплачивается не безопасностью, а тем, что владелец начнёт искать обходной
путь. Цена длинной сессии названа честно: **потерянный или чужой разлогиненный
телефон сохраняет доступ до месяца**. Страховка на этот случай —
Zero Trust → **My Team → Users** → нужный пользователь → **Revoke user
sessions**: обрывает все активные сессии немедленно, на всех устройствах.
Замечание: cookie `CF_Authorization` ставится на конкретный хост, так что
`dorent.am` и `www.dorent.am` логинятся по отдельности — открывать один и тот же
адрес удобнее.

### k.2. Политики

У приложения должно быть **две** политики, именно в этом порядке.

**1. `smoke-service-token`** (первая в списке)

| Поле | Значение |
|---|---|
| Policy name | `smoke-service-token` |
| Action | **Service Auth** |
| Include | Selector **Service Token** → `dorent-smoke` |

**2. `owner-email`**

| Поле | Значение |
|---|---|
| Policy name | `owner-email` |
| Action | **Allow** |
| Include | Selector **Emails** → `<OWNER_EMAIL>` |

Порядок значим: политики оцениваются сверху вниз, и `Service Auth` должна
встретиться раньше, чем `Allow`, иначе запрос без человеческой identity успевает
уехать на страницу логина до того, как будет рассмотрен его сервисный токен.
Именно этот случай `smoke.sh` подсказывает в тексте своего `FAIL`.

Метод входа должен быть включён: Zero Trust → **Settings → Authentication →
Login methods** → **One-time PIN** (в новых аккаунтах включён по умолчанию).
Никакого IdP заводить не нужно — код приходит письмом на адрес из политики.

### k.3. Service-токен для smoke-проверки

Zero Trust → **Access → Service auth → Service Tokens → Create Service Token**:

| Поле | Значение |
|---|---|
| Name | `dorent-smoke` |
| Service Token Duration | `1 year` |

**Client Secret показывается ровно один раз.** Сразу вписать обе половины на
сервере (значения не набирать в чатах, тикетах и коммитах):

```bash
# на сервере, под пользователем <SSH_USER>
nano /opt/dorent/rental-api/.env      # добавить CF_ACCESS_CLIENT_ID и CF_ACCESS_CLIENT_SECRET
chmod 600 /opt/dorent/rental-api/.env # права должны остаться 600
```

Рестарт стека для этого **не нужен** — файл читает `smoke.sh`, а не контейнеры
(`docker compose` подставляет из `.env` только те переменные, которые упомянуты
в compose-файле; этих двух там нет).

Через год токен истечёт, и smoke начнёт давать `FAIL domain-*` с текстом «the
service token was REJECTED» — лечится выпуском нового токена и заменой двух
строк в `.env`.

### k.4. Проверка

После k.1–k.3 — проверять именно в таком порядке.

**Доступ есть (ПК):** открыть `https://dorent.am` в **обычном** окне. Cloudflare
перебрасывает на страницу входа, там ввести email из политики → на почту
приходит 6-значный код → приложение открывается. Внутри сессии обязательно
пройти: навигацию по SPA и перезагрузку страницы на внутреннем маршруте (F5 не
должен давать 404), карточку объявления с картинками (`/uploads/`), и **чат** —
он держит WebSocket через `/hubs/`; сообщения должны уходить и приходить без
перезагрузки. Чат — самая чувствительная к гейту часть (регрессия M-008).

**Доступ есть (телефон):** тот же адрес в мобильном браузере, **обязательно с
мобильных данных, а не с домашнего Wi-Fi** — иначе проверка не отличает работу
Access от совпадения IP. Логин тот же: email → код из письма. После входа зайти
в чат и убедиться, что realtime работает и там.

**Доступа нет у посторонних:** открыть `https://dorent.am` в окне
инкогнито/приватном (в нём нет cookie `CF_Authorization`) — должна показаться
страница входа Cloudflare, а не сайт. То же самое повторить для
`https://www.dorent.am`. Дополнительно, без браузера:

```bash
curl -s -o /dev/null -w '%{http_code} -> %{redirect_url}\n' https://dorent.am/
# ожидается: 302 -> https://<team>.cloudflareaccess.com/cdn-cgi/access/login/dorent.am?...
# 200 означает, что гейта нет
```

**Стенд в целом:** прописать service-токен в `.env` (k.3) и запустить

```bash
/opt/dorent/rental-api/deploy/smoke.sh
```

В шапке должно быть `access mode: ENFORCED`, а среди проверок —
`PASS access-gate-dorent.am` и `PASS access-gate-www.dorent.am`: это и есть
машинная версия проверки «посторонний не войдёт», она выполняется при каждом
деплое.

### k.5. Откат — вернуть сайт в публичное состояние

Порядок обратный, каждый шаг обратим, простоя нет.

1. **Быстро (секунды, без удаления настроек):** Zero Trust → Access →
   Applications → `dorent-production` → **Configure → Policies** → у политики
   `owner-email` сменить Action на **Bypass**, Include оставить **Everyone**
   (либо добавить отдельную политику `Bypass / Everyone` первой в списке).
   Сайт сразу становится публичным, приложение и токен остаются на месте —
   вернуть гейт можно тем же переключателем.
2. **Полностью:** удалить приложение `dorent-production` (Applications → … →
   Delete). Ни DNS, ни туннель, ни контейнеры это не затрагивает.
3. **Вернуть smoke в публичный режим:** удалить (или закомментировать) строки
   `CF_ACCESS_CLIENT_ID` и `CF_ACCESS_CLIENT_SECRET` в
   `/opt/dorent/rental-api/.env`. Скрипт сам переключится в `PUBLIC` и перестанет
   требовать закрытого гейта. Убрать **обе** строки: одна оставшаяся даст
   `FAIL access-token`.
4. Удалить сервисный токен `dorent-smoke` (Access → Service auth) — необязательно,
   но без приложения он ничего не открывает и висит зря.

Проверка отката: `curl -s -o /dev/null -w '%{http_code}\n' https://dorent.am/`
даёт `200`, и `smoke.sh` печатает `access mode: PUBLIC` и `overall: PASS`.

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
