# Dev Notes

Краткий журнал ключевых изменений проекта WebApplication1.

## Журнал изменений

<!-- Новые записи добавлять сверху -->

### 2026-10-22 — Завершение задачи при недоступном RabbitMQ

**Область:** Backend

- `CompleteTask`: задача завершается и сохраняется даже если публикация в RabbitMQ не удалась. Ошибка пишется в лог (`LogError`, категория `WebApplication1.DTO.TaskItem`) и ответ остаётся 200.
- Таймаут публикации `PublishTimeout` = 5 с. Без него при остановленном брокере `Publish` висел до таймаута клиента (~100 с). Отмена самого запроса по-прежнему пробрасывается.
- Проверено вручную: при остановленном `rabbitmq` `PUT complete` даёт 200 за ~5 с, `IsCompleted` сохраняется, запись в логе есть.
- Ограничение: событие теряется навсегда (без Outbox и повторной отправки).

### 2026-10-22 — Конкурентное завершение задач (409)

**Область:** Backend, Data

- `AppDbContext`: токен конкурентности на системном столбце PostgreSQL `xmin` (`Property<uint>("xmin")` + `IsConcurrencyToken`).
- Миграция `AddTaskConcurrencyToken` без DDL: `xmin` уже есть в каждой таблице, миграция фиксирует только изменение модели.
- `CompleteTask`: при `DbUpdateConcurrencyException` возвращается 409, событие не публикуется. Повторное завершение уже завершённой задачи по-прежнему даёт 200 без повторной публикации.
- Проверено вручную на `TestDb`: 30 задач × 10 параллельных `PUT complete` дают 158×200 и 142×409, сообщений в `task.completed` ровно 30.
- Валидация `Title` (пустая строка, пробелы, отсутствие значения) даёт 400, `CompletedAt` заполняется при завершении, публикация идёт после `SaveChangesAsync`.

### 2026-10-22 — Инфраструктура Docker и интеграционный тест

**Область:** Infrastructure, Backend, Tests

- `docker-compose.yml`: PostgreSQL 17 (база `TestDb`, порт хоста 5433) и RabbitMQ 3 management (5672, 15672).
- `docker/rabbitmq/definitions.json`: обменник `task.events` (topic), очередь `task.completed`, биндинг по ключу `task.completed`, пользователь `guest`. В `rabbitmq.conf` разрешён вход `guest` через проброс портов.
- Важно: при загрузке `definitions.json` брокер не создаёт дефолтного `guest`, поэтому пользователь и его права описаны в файле.
- `tests/WebApplication1.IntegrationTests`: тест `CompletingTaskViaApi_DeliversTaskCompletedMessageToRabbitMq` (POST → PUT complete → сообщение в `task.completed` через реальный подписчик). Запуск: `docker compose up -d`, затем `dotnet test tests/WebApplication1.IntegrationTests`.
- `tests/` исключен из сборки основного проекта (`DefaultItemExcludes`). В тестовом проекте EF Core закреплён на 9.0.11, иначе транзитивная 9.0.1 даёт конфликт сборки.
- `Program.cs`: `public partial class Program;` для `WebApplicationFactory`.

### 2026-10-22 — Graceful shutdown: ожидание публикаций и остановка RabbitMQ

**Область:** Backend

- `Infrastructure/InFlightOperations.cs`: счётчик активных операций публикации. После начала остановки новые операции отклоняются, `StopAndWait` ждёт завершения активных не дольше 5 с.
- `Endpoints/TaskEndpoints.cs`: `PUT /tasks/{id}/complete` регистрирует операцию на всё время работы (чтение, сохранение, публикация). При остановке возвращает 503 до начала изменений.
- `Program.cs`: `HostOptions.ShutdownTimeout` = 10 с; `MassTransitHostOptions.StopTimeout` = 5 с; `ApplicationStopping` вызывает ожидание до остановки шины и веб-сервера.
- Почему ожидание в `ApplicationStopping`: хостовые сервисы останавливаются в обратном порядке регистрации, MassTransit регистрируется после веб-сервера и останавливается раньше него. Без явного ожидания запрос, который публикует в момент остановки, упал бы на закрытой шине.
- Закрытие канала и соединения RabbitMQ выполняет MassTransit при остановке шины.
- Ограничение: Windows не передаёт SIGTERM в процесс, поэтому сценарий проверен только для Ctrl+C. Ожидание в процессе с брокером не проверено (RabbitMQ в сессии не запускался).


### 2026-10-22 — Валидация задач и публикация TaskCompleted в task.events

**Область:** Backend

**Что изменилось:**
- `CreateTaskRequest`: DataAnnotations для `Title` (обязателен, не только пробелы, до 200 символов) и `Priority` (допустимое значение enum). Title обрезается пробелами до валидации. Ошибки возвращаются как 400 ValidationProblem.
- Событие `TaskCompleted` публикуется в обменник `task.events` (topic) с routing key `task.completed`.
- API принимает и отдаёт `Priority` строкой (Low, Medium, High).
- Тело события — plain JSON в camelCase: `taskId`, `title`, `completedAt`, `priority` (строкой). Включён `UseRawJsonSerializer` для шины.

**Затронутые пути:**
- `DTO/CreateTaskRequest.cs`
- `Contracts/TaskCompleted.cs`
- `Endpoints/TaskEndpoints.cs`
- `Program.cs`

**Зачем / контекст:** Контракт события задан внешним потребителям, поэтому конверт MassTransit не используется. Raw JSON serializer включён для всей шины, пока других сообщений нет.

### 2026-10-22 — API задач (Minimal API, EF Core, MassTransit)

**Область:** Backend

**Что изменилось:**
- Эндпоинты `/tasks`: `POST` (создание, по умолчанию Priority=Medium), `GET` (все задачи без пагинации), `PUT /{id}/complete` (завершение + событие `TaskCompleted` в RabbitMQ), `DELETE /{id}` (жёсткое удаление).
- `AppDbContext`: `DbSet<TaskItem> Tasks`, таблица `Tasks`, `Title` обязательный до 200 символов, `Priority` хранится как строка.
- Миграция `InitialCreate`.
- Контракт события `Contracts/TaskCompleted`.

**Затронутые пути:**
- `Endpoints/TaskEndpoints.cs`
- `DTO/CreateTaskRequest.cs`
- `Contracts/TaskCompleted.cs`
- `Data/AppDbContext.cs`
- `Migrations/`
- `Program.cs`

**Зачем / контекст:** Повторное завершение задачи не меняет данные и не публикует событие заново. Публикация идёт после `SaveChangesAsync` без Outbox: при сбое RabbitMQ событие может быть потеряно.

### 2026-10-22 — Сущность TaskItem и enum Priority

**Область:** Backend

**Что изменилось:**
- Добавлена сущность `TaskItem` (Id, Title, IsCompleted, CreatedAt, CompletedAt, Priority).
- Добавлен enum `Priority` (Low, Medium, High).

**Затронутые пути:**
- `DTO/TaskItem.cs`
- `DTO/Priority.cs`

**Зачем / контекст:** Модель задач для будущего API и хранения в PostgreSQL. Ограничения Title (не пусто, до 200 символов) пока не выражены в коде, их нужно задать в EF-конфигурации или валидации.

### 2026-10-22 — Конфигурация проекта: Minimal API, PostgreSQL (EF Core), RabbitMQ (MassTransit)

**Область:** Infra

**Что изменилось:**
- Целевая платформа: .NET 9 (`net9.0`), ASP.NET Core Minimal API.
- Подключены `Npgsql.EntityFrameworkCore.PostgreSQL` 9.0.4 и `Microsoft.EntityFrameworkCore.Design` 9.0.11.
- Добавлен пустой `AppDbContext` и регистрация через `AddDbContext` + `UseNpgsql`.
- Подключён MassTransit 8.5.12 (`MassTransit.RabbitMQ`) с транспортом RabbitMQ; `ConfigureEndpoints` включён, консьюмеров пока нет.
- Строки подключения: `ConnectionStrings:Postgres`, секция `RabbitMq` в `appsettings.json` (значения для localhost).

**Затронутые пути:**
- `WebApplication1.csproj`
- `Program.cs`
- `Data/AppDbContext.cs`
- `appsettings.json`

**Зачем / контекст:** Базовая инфраструктура под дальнейшие сущности, миграции и обмен сообщениями. Версия MassTransit 8.x выбрана из-за лицензии: 9.x требует коммерческую лицензию.
