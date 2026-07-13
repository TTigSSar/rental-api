# Развёртывание тестового стенда (staging) на VPS

Пошаговая инструкция по развёртыванию `test.dorent.am` на дешёвом VPS
(Ubuntu, IP `<SERVER_IP>`, пользователь `<SSH_USER>`) через Cloudflare Tunnel.
Домен `dorent.am` уже подключён к Cloudflare. Входящие порты на сервере
**не открываются** — весь трафик идёт через исходящее (outbound) соединение
`cloudflared`.

> Репозиторий публичный — реальные IP сервера и SSH-логин намеренно не
> хранятся здесь. Подставьте свои значения вместо `<SERVER_IP>` и `<SSH_USER>`.

Файлы стенда: `docker-compose.staging.yml`, `.env.staging.example` — оба
лежат в корне репозитория `rental-api`.

---

## a. Подготовка сервера

Подключиться по SSH:

```bash
ssh <SSH_USER>@<SERVER_IP>
```

Установить Docker (официальный скрипт) и плагин compose:

```bash
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
sudo usermod -aG docker $USER
# перелогиниться (или newgrp docker), чтобы группа docker применилась
newgrp docker
docker --version
docker compose version
```

Плагин `docker compose` устанавливается скриптом get.docker.com автоматически
(`docker-compose-plugin`). Если его нет — поставить отдельно:

```bash
sudo apt-get update
sudo apt-get install -y docker-compose-plugin
```

Настроить firewall — разрешить только SSH, входящие 80/443 не нужны,
т.к. туннель полностью исходящий:

```bash
sudo ufw allow OpenSSH
sudo ufw enable
sudo ufw status
```

Стенд рассчитан на VPS с 2 ГБ RAM (Hetzner CPX12) — этого мало для
одновременной сборки образов и работы SQL Server, поэтому обязателен файл
подкачки (swap) на 4 ГБ:

```bash
sudo fallocate -l 4G /swapfile
sudo chmod 600 /swapfile
sudo mkswap /swapfile
sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
free -h
```

## b. Получение кода на сервере

`ui` собирается из `../Rental-Ui` относительно `docker-compose.staging.yml`,
поэтому оба репозитория должны лежать рядом:

```bash
mkdir -p ~/apps/rental
cd ~/apps/rental
git clone <URL_rental-api> rental-api
git clone <URL_Rental-Ui> Rental-Ui
cd rental-api
git checkout dev   # или нужную ветку/тег для стенда
```

Если удалённых репозиториев нет под рукой — можно скопировать код с локальной
машины через `scp`/`rsync`:

```bash
rsync -avz --exclude .git rental-api/  <SSH_USER>@<SERVER_IP>:~/apps/rental/rental-api/
rsync -avz --exclude .git Rental-Ui/   <SSH_USER>@<SERVER_IP>:~/apps/rental/Rental-Ui/
```

## c. Файл окружения

В каталоге `~/apps/rental/rental-api`:

```bash
cp .env.staging.example .env
nano .env   # или vim/etc.
```

Заполнить три значения (правила и генерация — см. комментарии в самом файле):

- `MSSQL_SA_PASSWORD` — пароль SA для SQL Server (мин. 8 симв., 3 из 4 классов символов).
- `JWT_SECRET_KEY` — секрет JWT, минимум 32 символа, `openssl rand -base64 48`.
- `CLOUDFLARE_TUNNEL_TOKEN` — токен туннеля, см. пункт d.

`.env` не коммитится (уже в `.gitignore`).

## d. Настройка Cloudflare Tunnel

1. Зайти в **Cloudflare Zero Trust → Networks → Tunnels**.
2. **Create a tunnel** → тип коннектора **Cloudflared**.
3. Дать имя, например `dorent-staging`.
4. На шаге установки коннектора Cloudflare покажет команду вида
   `docker run cloudflare/cloudflared:latest tunnel run --token <TOKEN>` —
   скопировать только `<TOKEN>` в `CLOUDFLARE_TUNNEL_TOKEN` в `.env`
   (сам контейнер запустит `docker compose`, отдельно руками его запускать не нужно).
5. Перейти на вкладку **Public Hostnames** этого туннеля и добавить:
   - `test.dorent.am` → Service: `HTTP` → `ui:80`
   - (опционально) `api.dorent.am` → Service: `HTTP` → `api:8080` — удобно для Swagger.
6. DNS-записи в зоне `dorent.am` Cloudflare создаст автоматически при добавлении
   public hostname — руками ничего прописывать не нужно.

## e. Запуск стенда

```bash
cd ~/apps/rental/rental-api
docker compose -f docker-compose.staging.yml up --build -d
```

Первый запуск занимает несколько минут — инициализация SQL Server внутри
контейнера, применение миграций EF Core и наполнение dev-сида при старте API.

## f. Проверка

```bash
docker compose -f docker-compose.staging.yml ps
docker compose -f docker-compose.staging.yml logs -f api
```

Все сервисы (`db`, `api`, `ui`, `cloudflared`) должны быть в состоянии `running`
(`db` — `healthy`).

Открыть `https://test.dorent.am` в браузере и:

- войти под демо-аккаунтом `renter@rental.local` / `Demo1234`;
- убедиться, что изображения объявлений загружаются (проверяет прокси `/uploads/`);
- открыть чат и проверить, что сообщения приходят в реальном времени
  (проверяет проксирование WebSocket `/hubs/` через туннель).

## g. Заметки

- **Google Sign-In**: чтобы вход через Google работал на `test.dorent.am`,
  нужно добавить `https://test.dorent.am` в Authorized JavaScript origins
  соответствующего OAuth-клиента в Google Cloud Console (client id
  оканчивается на `…oupt.apps.googleusercontent.com`).
- **SQL Server Developer edition** используется намеренно — только для
  тестового стенда, не для продакшена (лицензионные ограничения Developer
  edition это допускают только для непродакшен-сред).
- **`ASPNETCORE_ENVIRONMENT=Development`** в `docker-compose.staging.yml` —
  осознанное решение для тестового стенда (нужны dev-сид и Swagger). Когда
  стенд станет чем-то большим, чем тестовым — переключить на `Production`
  и пересмотреть остальные dev-настройки.
- **Обновление стенда** до новой версии кода:

  ```bash
  cd ~/apps/rental/rental-api  && git pull
  cd ~/apps/rental/Rental-Ui   && git pull
  cd ~/apps/rental/rental-api
  docker compose -f docker-compose.staging.yml up --build -d
  ```

- **Полный снос стенда** (включая данные БД и загруженные файлы):

  ```bash
  docker compose -f docker-compose.staging.yml down -v
  ```
- **Память**: стенд рассчитан на 2 ГБ RAM (Hetzner CPX12), поэтому на сервере
  обязателен файл подкачки (swap) на 4 ГБ — настраивается на этапе подготовки
  сервера (пункт a). SQL Server ограничен 1 ГБ через переменную
  `MSSQL_MEMORY_LIMIT_MB` в `docker-compose.staging.yml`. Первый запуск
  `docker compose ... up --build` на таком объёме памяти медленный
  (10–20+ минут) — это ожидаемо, не зависание.
