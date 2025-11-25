# ⚡ AzureTestSyncApp

Herramienta de consola para sincronizar pruebas automatizadas y Gherkin con **Azure DevOps Test Plans**.

---

## 🚀 Guía de Instalación Rápida

Sigue estos pasos para crear el proyecto desde cero e instalar las dependencias necesarias del SDK de Azure.

### 📦 Inicialización del Proyecto

Ejecuta los siguientes comandos en tu terminal (PowerShell, Bash o CMD):

```bash
# 1. Crear una nueva aplicación de consola
dotnet new console -n AzureTestSyncApp

# 2. Entrar en el directorio del proyecto
cd AzureTestSyncApp

# 3. Instalar el cliente de Azure DevOps (NuGet)
# Incluye: WorkItemTracking, TestManagement y autenticación VSS
dotnet add package Microsoft.TeamFoundationServer.Client

# 4. Instalar Newtonsoft.Json (Recomendado)
# Esencial para manejar objetos JsonPatchDocument y serialización
dotnet add package Newtonsoft.Json