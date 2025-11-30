# EnvioMasivoEmail_MVC

Aplicacion ASP.NET Core MVC (NET 8) para carga y envio masivo de correos con soporte de plantillas HTML, adjuntos embebidos y servicios reutilizables. Incluye una herramienta de consola para sincronizar casos de prueba Gherkin con Azure DevOps Test Plans y un pipeline de Azure Pipelines para build, empaquetado y despliegue.

## Tabla de contenido
- [Arquitectura](#arquitectura)
- [Requisitos previos](#requisitos-previos)
- [Estructura del repositorio](#estructura-del-repositorio)
- [Configuracion](#configuracion)
  - [SMTP y plantilla](#smtp-y-plantilla)
  - [Variables de la herramienta de sincronizacion](#variables-de-la-herramienta-de-sincronizacion)
- [Ejecucion](#ejecucion)
  - [Aplicacion web](#aplicacion-web)
  - [Herramienta AzureTestSyncApp](#herramienta-azuretestsyncapp)
- [Tests Gherkin y logica cubierta](#tests-gherkin-y-logica-cubierta)
- [Pipeline CI/CD](#pipeline-cicd)
- [Empaquetado NuGet](#empaquetado-nuget)
- [Consideraciones de seguridad](#consideraciones-de-seguridad)

## Arquitectura
- **EnvioMasivoEmail_MVC**: Aplicacion MVC que permite subir un .txt de correos, validarlos y disparar envios masivos usando plantillas HTML ubicadas en `wwwroot/Plantillas`.
- **Servicios**: Libreria compartida con EmailService (envio SMTP con MailKit, incrustacion de banner), ExcelService (lectura de usuarios desde Excel) y DTOs/ayudantes.
- **AzureTestSyncApp**: Consola que lee features Gherkin, crea/actualiza casos de prueba en Azure DevOps Test Plans, ejecuta validaciones locales (regex de correos, parsing Excel, incrustacion de banner) y registra resultados.

## Requisitos previos
- .NET 8 SDK
- Cuenta y feed de Azure DevOps (para pipeline y push de paquetes)
- SMTP accesible (por ejemplo Gmail con App Password)

## Estructura del repositorio
- `EnvioMasivoEmail_MVC/` aplicacion web MVC
  - `Controllers/HomeController.cs` endpoints para carga de .txt y envio masivo
  - `Views/Home/Index.cshtml` UI para cargar, validar y enviar correos
  - `wwwroot/Plantillas/` HTML base y recursos (miPlantilla, img2.jpg, etc.)
- `Servicios/` libreria compartida
  - `EmailService.cs` envio masivo y incrustacion de banner
  - `ExcelService.cs` lectura de usuarios desde Excel
  - `Helpers/EmailFileHelper.cs` regex para filtrar correos desde .txt
- `AzureTestSyncApp/` consola de sincronizacion con Azure DevOps
  - `Features/*.feature` escenarios Gherkin soportados
  - `Program.cs` orquestacion de sync y ejecucion local de validaciones
- `azure-pipelines.yml` pipeline de build, sync condicional y despliegue
- `nuget.config` fuentes de paquetes (nuget.org y feed interno)

## Configuracion
### SMTP y plantilla
1) Copiar `EnvioMasivoEmail_MVC/appsettings.json` y ajustar la seccion `EmailSettings` con host, puerto, usuario, password (ideal desde variables de entorno o secretos de CI/CD) y remitente.
2) Opcional: editar `wwwroot/Plantillas/miPlantilla.html` y las imagenes (`img2.jpg`, `Logo.png`, etc.). El token `{{img2}}` se reemplaza con un recurso embebido si el archivo existe.

### Variables de la herramienta de sincronizacion
Crear un archivo `.env` (o usar variables de entorno) en `AzureTestSyncApp/`:
```
PERSONAL_ACCESS_TOKEN=           # PAT de Azure DevOps
ORGANIZATION_URL=https://dev.azure.com/TU_ORG
PROJECT_NAME=TuProyecto
PLAN_ID=IDDelPlanDePruebas
```
El template `.env.template` sirve de referencia.

## Ejecucion
### Aplicacion web
```bash
dotnet restore
dotnet run --project EnvioMasivoEmail_MVC/EnvioMasivoEmail_MVC.csproj
```
Flujo principal:
1) Cargar un `.txt` de correos en la UI (usa `UploadEmails` para validar via regex).
2) Revisar la lista validada y enviar (`SendBulkEmails`); usa la plantilla HTML si existe.
3) Ver logs de exito/error en la misma pagina.

### Herramienta AzureTestSyncApp
```bash
dotnet restore
dotnet run --project AzureTestSyncApp/AzureTestSyncApp.csproj
```
Que hace:
- Parsea los features definidos en `AzureTestSyncApp/Features`.
- Crea/actualiza casos de prueba en Azure DevOps y los vincula a suites.
- Ejecuta la logica local asociada (regex de correos, lectura Excel, incrustacion de banner) y reporta resultados al plan de pruebas.

## Tests Gherkin y logica cubierta
- `LeerCorreosDesdeTxt.feature`: valida filtrado y normalizacion de correos desde archivos de texto usando el regex.
- `IncrustarBannerEmail.feature`: valida reemplazo del token `{{img2}}` y manejo de banner embebido o ausente.
La consola ejecuta estos escenarios y publica su resultado en Azure DevOps.

## Pipeline CI/CD
- Archivo: `azure-pipelines.yml`.
- Pasos clave:
  - Cache y restore de NuGet.
  - Deteccion de cambios en `.feature` o `AzureTestSyncApp` para decidir si se ejecuta la herramienta de sync.
  - Build y ejecucion condicional de `AzureTestSyncApp` con variables de Azure DevOps (`MyAzurePAT`, etc.).
  - Publish de la app MVC (zip) y despliegue a la Web App `correos-masivos`.
  - Pack y push de paquete NuGet al feed interno (`UTN-FabricaSoftware-2025`).
  - Publicacion de artefactos.

## Empaquetado NuGet
La app MVC esta marcada como `IsPackable=true`. El pipeline ejecuta:
- `dotnet pack EnvioMasivoEmail_MVC/EnvioMasivoEmail_MVC.csproj --configuration Release`
- Publica `.nupkg` al feed interno (excluye `.symbols.nupkg`).

## Consideraciones de seguridad
- No almacenar contraseñas SMTP ni PAT en el repo. Usar variables seguras en el pipeline o secretos locales.
- El repo trae credenciales de ejemplo en `appsettings.json`; sustituirlas antes de desplegar.
- Revisa permisos del PAT (Work Items/Test Management) y la configuracion de acceso al feed NuGet.
