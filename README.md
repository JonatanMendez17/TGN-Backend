# Notification Service

Solución de notificaciones para TGN, compuesta por dos proyectos independientes dentro de la misma solución:

- **Notification.Api** — API REST genérica para que otros sistemas manden mensajes de forma segura mediante un token de autorización, abstrayendo la complejidad de cada proveedor de mensajería. Actualmente soporta **Telegram**, con arquitectura extensible a otros canales (WhatsApp, Email, SMS, etc.).
- **Notification.Engine** — motor que reemplaza los workflows de N8N usados por TGN: envío diario de recordatorios de hitos mensuales, reprogramación, reset mensual, y respuestas interactivas de Telegram (botones OK/Posponer, alta de grupos por `/registrar`). Habla con SQL Server y con Telegram de forma bidireccional — no es un simple gateway de envío.

---

## Tabla de Contenidos

- [Stack Tecnológico](#stack-tecnológico)
- [Notification.Api](#notificationapi)
  - [Arquitectura](#arquitectura)
  - [Endpoints](#endpoints)
  - [Configuración](#configuración)
  - [Instalación y Ejecución](#instalación-y-ejecución)
  - [Cómo Consumirlo](#cómo-consumirlo)
  - [Extensibilidad](#extensibilidad)
- [Notification.Engine](#notificationengine)
  - [Qué hace](#qué-hace)
  - [Cascada de hora de envío diario](#cascada-de-hora-de-envío-diario)
  - [Interacción con Telegram](#interacción-con-telegram)
  - [Configuración](#configuración-1)
  - [Instalación y Ejecución](#instalación-y-ejecución-1)
  - [Logs](#logs)

---

## Stack Tecnológico

| Capa | Tecnología |
|---|---|
| Runtime | .NET 8.0 |
| Lenguaje | C# |
| Framework | ASP.NET Core 8 (Api) / Worker Service — `BackgroundService` (Engine) |
| Acceso a datos (Engine) | ADO.NET directo con `Microsoft.Data.SqlClient`, sin ORM |
| Logging | Serilog (consola + archivo rotativo diario) |
| Configuración | Microsoft.Extensions.Options (Options Pattern) |
| HTTP Client | HttpClientFactory |
| Canal actual | Telegram Bot API |

---

## Notification.Api

API REST construida con **ASP.NET Core 8** que actúa como intermediario centralizado para que otros sistemas envíen mensajes de forma segura mediante un token de autorización.

**Casos de uso:**
- Alertas y notificaciones de sistemas internos
- Avisos automáticos desde pipelines CI/CD
- Notificaciones de errores o eventos críticos en producción

### Arquitectura

El servicio aplica una arquitectura limpia con separación de responsabilidades:

```
Notification-service/
└── Notification.Api/
    ├── Controllers/        # Recibe y valida las solicitudes HTTP
    ├── Services/           # Lógica de negocio (validación de token, formateo)
    ├── Providers/          # Implementaciones de cada canal (Telegram, ...)
    ├── Models/             # Request y Response DTOs
    └── Settings/           # Configuración tipada (ApiSettings, TelegramSettings)
```

**Flujo de una solicitud:**

```
Cliente → Middleware de autenticación (valida token) → Controller → Service (formatea) → Provider → Canal externo
```

La autenticación se aplica de forma centralizada en el pipeline (`Program.cs`, `RequireAuthorization()` sobre todos los controllers) — no queda a criterio de cada endpoint individual, así que cualquier endpoint nuevo queda protegido automáticamente.

**Patrones aplicados:** Provider Pattern, Options Pattern, Dependency Injection, Async/Await.

### Endpoints

#### `POST /api/mensajeria/enviar`

Envía un mensaje al canal configurado (hoy, Telegram).

**Request:**

```http
POST /api/mensajeria/enviar
Content-Type: application/json
Authorization: Bearer tu-token-seguro
```

```json
{
  "sistema": "NombreDelSistema",
  "canal": "Telegram",
  "destino": "-1001234567890",
  "de": "Remitente",
  "para": "Destinatario",
  "titulo": "Asunto del mensaje",
  "mensaje": "Cuerpo del mensaje"
}
```

| Campo | Tipo | Requerido | Descripción |
|---|---|---|---|
| `sistema` | string | Sí | Nombre del sistema que origina la solicitud |
| `canal` | string | Sí | Canal de envío (hoy solo `Telegram`; debe coincidir con el `Canal` de algún `INotificationProvider` registrado) |
| `destino` | string | Sí | Identificador del destino dentro de ese canal (para Telegram, el `chat_id` del grupo/canal) — lo conoce el sistema que llama, la Api no lo resuelve |
| `de` | string | Sí | Remitente (texto libre, se incluye en el cuerpo del mensaje) |
| `para` | string | Sí | Destinatario (texto libre, se incluye en el cuerpo del mensaje — no determina el ruteo, eso lo hace `destino`) |
| `titulo` | string | Sí | Asunto o título del mensaje |
| `mensaje` | string | Sí | Contenido del mensaje |

**El mensaje llega a Telegram con este formato:**

```
👤 De: Remitente
👥 Para: Destinatario
🏷️ Asunto del mensaje

📝 Cuerpo del mensaje
```

**Respuestas:**

| Código | Descripción | Cuerpo |
|---|---|---|
| `200 OK` | Mensaje enviado correctamente | `{ "exitoso": true, "mensaje": "...", "canal": "Telegram", "timestamp": "..." }` |
| `200 OK` | Envío fallido (canal no soportado, o error de entrega tras reintentos) | `{ "exitoso": false, "mensaje": "...", "canal": "...", "timestamp": "..." }` |
| `400 Bad Request` | Algún campo requerido está vacío o ausente | Detalle de errores de validación |
| `401 Unauthorized` | Token de autorización inválido | `{ "exitoso": false, "mensaje": "Token de autorización inválido.", "canal": "", ... }` |
| `500 Internal Server Error` | Error interno al enviar | `{ "exitoso": false, "mensaje": "Error interno del servidor.", ... }` |

> **Nota:** un `canal` no soportado (typo, o un canal sin `INotificationProvider` registrado) no es un `400` — se trata como cualquier otro fallo de envío: `200 OK` con `exitoso: false`.

### Configuración

La configuración se gestiona en `Notification.Api/appsettings.json`:

```json
{
  "ApiSettings": {
    "TokenBearer": "CAMBIAR-POR-TOKEN-SEGURO"
  },
  "Sql": {
    "ConnectionString": "<connection-string-de-la-BD-de-TGN>"
  }
}
```

| Clave | Descripción |
|---|---|
| `ApiSettings:TokenBearer` | Token que deben enviar los consumidores en cada solicitud |
| `Sql:ConnectionString` | Connection string de la BD de TGN — se usa solo para leer el token del bot (ver abajo) |

> **El `chat_id` (u otro identificador de destino) ya no se configura acá.** Cada solicitud lo manda en el campo `destino` del body — el sistema que llama ya sabe a qué grupo/canal le corresponde hablar, la Api no mantiene una lista de destinos.

> **El token del bot ya no se configura acá.** Se lee en vivo desde `dbo.Parametria` (`par_clave = 'telegram_bot_token'`) — la misma fila que usan TGN Web y `Notification.Engine` — y se cachea en memoria por 1 minuto (`Telegram/TelegramTokenProvider.cs`). Esto evita que cada proceso tenga su propia copia del token, que puede quedar desactualizada si se rota en BotFather (pasó el 2026-07-28: el token en `Parametria` estaba revocado mientras el `appsettings` del Engine tenía el vigente). Para actualizar el token, se edita una sola vez desde `conf_parametros.aspx` en TGN.

> **Importante:** En entornos productivos, no almacenes credenciales en `appsettings.json`. Usa variables de entorno o un gestor de secretos (Azure Key Vault, AWS Secrets Manager, etc.).

**Cómo obtener las credenciales de Telegram:**
1. Crea un bot en Telegram hablando con [@BotFather](https://t.me/botfather), cargá el `Token` en `Parametria.telegram_bot_token` (no en `appsettings.json`)
2. Agrega el bot al grupo o canal donde quieres recibir mensajes
3. Obtén el `ChatId` usando `@userinfobot` o consultando la API de Telegram (`/getUpdates`)

### Instalación y Ejecución

**Requisitos:** [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Git.

```bash
# 1. Clonar el repositorio
git clone https://github.com/JonatanMendez17/Notification-service.git
cd Notification-service

# 2. Restaurar dependencias
dotnet restore

# 3. Configurar credenciales en Notification.Api/appsettings.json

# 4. Ejecutar
cd Notification.Api
dotnet run
```

La API queda disponible en:
- **HTTPS:** `https://localhost:51391`
- **HTTP:** `http://localhost:51392`

### Cómo Consumirlo

**PowerShell:**

```powershell
$body = @{
    sistema     = "MiSistema"
    canal       = "Telegram"
    destino     = "-1001234567890"
    de          = "Monitor"
    para        = "Equipo Dev"
    titulo      = "Alerta de produccion"
    mensaje     = "Se detectó un error crítico en el servicio de pagos."
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:51392/api/mensajeria/enviar" `
    -Method POST `
    -Headers @{ Authorization = "Bearer tu-token-aqui" } `
    -ContentType "application/json" `
    -Body $body
```

### Extensibilidad

Nuevos canales de mensajería se agregan implementando `INotificationProvider` en `Notification.Api/Providers/` y registrándolo en `Program.cs` — el resto del pipeline (validación de token, formateo, controller) es agnóstico al canal. `MensajeriaService` resuelve el provider a usar comparando el campo `canal` del request contra el `Canal` de cada `INotificationProvider` registrado (inyectados como `IEnumerable<INotificationProvider>`); si ninguno coincide, responde `200 OK` con `exitoso: false`.

---

## Notification.Engine

Motor de recordatorios de hitos mensuales de TGN. Reemplaza 5 workflows que antes corrían en N8N; cada uno es un `BackgroundService` propio con su timer, más un receptor de updates de Telegram compartido.

### Qué hace

| Job / componente | Cadencia | Reemplaza (N8N) | Qué hace |
|---|---|---|---|
| `EnvioDiarioJob` | 1h | 1. Envío diario | Busca hitos pendientes cuya hora ya llegó (ver cascada abajo), agrupa por chat, manda una cabecera `📅 Recordatorio - fecha` y un mensaje por hito con botones `✅OK` / `⏰+1..+4`, guarda `msg_id` |
| `RespuestaRegistroHandler` + `PollingReceiver` | 5s (dev) | 2. Respuesta y Registro | Recibe updates de Telegram: comando `/registrar` (alta de grupo) y botones OK/Posponer. Descarta callbacks duplicados (doble tap) verificando el estado actual en BD antes de reprocesar |
| `ReprogramarJob` | 1h (`hora_revision`) | 3. Reprogramar | Hitos vencidos sin respuesta hoy se reprograman para mañana y se edita su mensaje a `⏰ {hito}` |
| `ReinicioMensualJob` | 1h (`hora_reset`, días 1 y 15) | 4. Reset mensual | Resetea a `Pendiente` los hitos en estado `OK` (día 1: todos; día 15: solo los de la segunda quincena) |
| `ActualizacionesTiempoRealJob` | 5s | 5. Actualizaciones en tiempo real | Sincroniza a Telegram (edita el mensaje ya enviado) los cambios de estado hechos desde la app web TGN |

En dev, la recepción de updates de Telegram es por **polling** (`PollingReceiver`, `getUpdates` cada 5s). En server debería reemplazarse por un webhook — **todavía no implementado**, es la única pieza pendiente del diseño original.

### Cascada de hora de envío diario

La hora a la que se manda el recordatorio de un hito se resuelve con 3 niveles, de más a menos específico (`HitosRepository.SqlPendientesEnvioDiario`):

1. **Hito** — `Hitos_Mensuales.Hora_Envio` (configurable por hito en `hitos.aspx`, formato `HH:mm`)
2. **Grupo** — `Tg_Grupo.Tgg_Hora_Envio` (configurable en `conf_grupos.aspx`, si el hito no tiene la propia)
3. **Global** — `Parametria.hora_envio_diario` (si ni el hito ni su grupo tienen hora configurada)

`Hitos_Mensuales.Envia_Fin_Semana` (booleano por hito, ya **no** por grupo) controla si ese hito puntual recibe recordatorios los fines de semana.

### Interacción con Telegram

`Notification.Engine` no usa `Notification.Api` para hablar con Telegram — tiene su propio `TelegramBotClient` (`Telegram/TelegramBotClient.cs`), porque necesita botones inline y el `message_id` de vuelta, algo que un gateway genérico no ofrece. Puntos a tener en cuenta si se toca este código:

- `EditarMensajeAsync` siempre manda `reply_markup` (vacío si no hay botones) — Telegram rechaza el campo si viene `null` explícito en vez de omitido.
- Los envíos/ediciones a Telegram (tanto en el Engine como en la Api) reintentan hasta 3 veces ante errores transitorios (excepción, 5xx o 429 rate-limit — este último respetando el `retry_after` que manda Telegram, con tope de 5s). Un error permanente (ej. mensaje demasiado viejo para editar) no se reintenta.
- Las acciones de callback (`RegistrarHitoOkAsync` / `RegistrarHitoPospuestoAsync`) devuelven la cantidad de filas afectadas: `0` significa que el hito ya estaba en ese estado (callback duplicado — doble tap del botón, o dos personas del mismo grupo) y no hay que reprocesarlo ni volver a editar el mensaje.

### Configuración

`Notification.Engine/appsettings.json`:

```json
{
  "Sql": {
    "ConnectionString": ""
  }
}
```

| Clave | Descripción |
|---|---|
| `Sql:ConnectionString` | Connection string a la base de TGN (tablas `Hitos_Mensuales`, `Tg_Grupo`, `Parametria`, `Tg_Receptor`, `Tg_Grupo_Receptor`) |

El token del bot **no** se configura acá — se lee en vivo desde `dbo.Parametria` (`par_clave = 'telegram_bot_token'`) usando el mismo `Sql:ConnectionString`, cacheado 1 minuto (`Telegram/TelegramTokenProvider.cs`). Es la misma fila que usan TGN Web y `Notification.Api`; se administra desde `conf_parametros.aspx`.

> Igual que en el Api: no dejar credenciales reales en `appsettings.json` versionado. Usar `appsettings.Development.json` (ignorado por git) para desarrollo local, y variables de entorno / secret manager en producción.

### Instalación y Ejecución

```bash
cd Notification.Engine
dotnet run
```

Preparado para correr como **servicio de Windows** en producción (`Microsoft.Extensions.Hosting.WindowsServices`, `AddWindowsService()` en `Program.cs`) — instalación y mecanismo de actualización todavía sin implementar (ver notas de diseño en `Deploy/` si existen en el checkout local).

### Logs

Serilog escribe a consola y a archivo rotativo diario en:

```
Notification.Engine/log/Notification.Engine/engine_yyyyMMdd.txt
```

Retención: 30 días. El nivel de detalle por-request de ASP.NET Core (ruidoso) está silenciado; quedan los logs propios de cada job/handler y los eventos de ciclo de vida del proceso.
