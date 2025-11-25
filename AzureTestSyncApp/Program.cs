#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Servicios.Helpers;

namespace AzureTestSyncApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("--- INICIANDO SISTEMA DE VALIDACIÓN GHERKIN ---");

            // --- CORRECCIÓN: Cargar las variables del archivo .env primero ---
            // Buscamos el archivo .env en el directorio actual
            string envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            
            // Si estás depurando en Visual Studio, a veces el directorio base es bin/Debug, 
            // así que intentamos buscarlo un nivel arriba si no existe:
            if (!File.Exists(envPath))
            {
                // Intento de buscar en la raíz del proyecto (ajusta según tu estructura)
                envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", ".env");
            }

            LoadEnvFile(envPath); 
            // ----------------------------------------------------------------

            bool success = true;

            // 1. Initialize the sync/test engine
            try 
            {
                var tester = new AzureTestSync();

                // 2. Run the full cycle: 
                success = await tester.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Error Crítico: {ex.Message}");
                success = false;
            }

            Console.WriteLine("--- PROCESO FINALIZADO ---");

            if (!success) Environment.Exit(1);
        }

        // YOUR FUNCTION (Added 'static' to call it from Main)
        // Delegates to EmailFileHelper as requested
        public static List<string> LeerCorreosDesdeTxt(string rutaTxt)
        {
            // Ensure you have the EmailFileHelper class defined in your project
            return EmailFileHelper.LeerCorreosDesdeTxt(rutaTxt);
        }

        // Kept for reference (used by LoadEnvFile logic if needed, but currently Main is replaced)
        static void LoadEnvFile(string envPath)
        {
            if (!File.Exists(envPath)) return;
            try
            {
                foreach (var rawLine in File.ReadLines(envPath, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || !line.Contains("=")) continue;
                    var parts = line.Split(new[] { '=' }, 2);
                    var key = parts[0].Trim();
                    var value = parts[1].Trim().Trim('\'', '"');
                    if (!string.IsNullOrEmpty(key) && Environment.GetEnvironmentVariable(key) == null)
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine($"⚠️ No se pudo cargar el archivo .env: {exc.Message}");
            }
        }
    }

    // --- AZURE SYNC CLASS (Preserved) ---
    public class AzureTestSync
    {
        private readonly string _pat;
        private readonly string _orgUrl;
        private readonly string _projectName;
        private readonly int _planId;
        private readonly int _suiteId;
        private readonly string _gherkinPath;

        private WorkItemTrackingHttpClient _witClient;
        private TestManagementHttpClient _testClient;

        public AzureTestSync()
        {
            _pat = GetEnvVar("PERSONAL_ACCESS_TOKEN", required: true);
            _orgUrl = GetEnvVar("ORGANIZATION_URL", "https://dev.azure.com/UTN-FabricaSoftware-2025");
            _projectName = GetEnvVar("PROJECT_NAME", "CorreosMasivos");
            _planId = int.Parse(GetEnvVar("PLAN_ID", "6"));
            _suiteId = int.Parse(GetEnvVar("SUITE_ID", "9"));
            _gherkinPath = GetEnvVar("GHERKIN_PATH", "python/features/mapeo_canales.feature");

            Connect();
        }

        private static string GetEnvVar(string key, string defaultValue = null, bool required = false)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (required && string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"Falta la variable de entorno requerida: {key}");
            }
            return value ?? defaultValue;
        }

        private void Connect()
        {
            var credentials = new VssBasicCredential(string.Empty, _pat);
            var connection = new VssConnection(new Uri(_orgUrl), credentials);

            _witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            _testClient = connection.GetClient<TestManagementHttpClient>();
        }

        private string GetFileContent(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"❌ Error: File not found at {filePath}");
                    return null;
                }
                return File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ Error reading file: {e.Message}");
                return null;
            }
        }

        // --- INTEGRATED ROBUST PARSER ---
        private (string Title, string XmlSteps, List<Dictionary<string, string>> DataRows) ParseGherkinRobust(string gherkinText)
        {
            if (string.IsNullOrWhiteSpace(gherkinText))
                return ("Error", "", new List<Dictionary<string, string>>());

            var lines = gherkinText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(l => l.Trim())
                                   .Where(l => !string.IsNullOrEmpty(l))
                                   .ToList();

            string title = "Gherkin Import";
            
            var stepsList = new List<(string Action, string Expected)>();
            var dataRows = new List<Dictionary<string, string>>();
            List<string> headers = null;
            bool readingExamples = false;

            List<string> currentAction = new List<string>();
            List<string> currentExpected = new List<string>();

            void SaveStep()
            {
                if (currentAction.Count > 0 || currentExpected.Count > 0)
                {
                    stepsList.Add((string.Join("\n", currentAction), string.Join("\n", currentExpected)));
                    currentAction.Clear();
                    currentExpected.Clear();
                }
            }

            string AzureFormat(string text)
            {
                return Regex.Replace(text, @"<([^>]+)>", "@$1");
            }

            foreach (var line in lines)
            {
                string cleanLine = AzureFormat(line);

                if (cleanLine.StartsWith("Scenario Outline:") || cleanLine.StartsWith("Scenario:"))
                {
                    var parts = cleanLine.Split(new[] { ':' }, 2);
                    if (parts.Length > 1) title = parts[1].Trim();
                    continue;
                }

                if (cleanLine.StartsWith("Examples:"))
                {
                    readingExamples = true;
                    continue;
                }

                if (readingExamples)
                {
                    if (cleanLine.StartsWith("|"))
                    {
                        var rawSegments = cleanLine.Split('|');
                        var parts = rawSegments.Skip(1).Take(rawSegments.Length - 2).Select(p => p.Trim()).ToList();

                        if (parts.Count > 0)
                        {
                            if (headers == null)
                            {
                                headers = parts;
                            }
                            else
                            {
                                var row = new Dictionary<string, string>();
                                for (int i = 0; i < Math.Min(headers.Count, parts.Count); i++)
                                {
                                    row[headers[i]] = parts[i];
                                }
                                dataRows.Add(row);
                            }
                        }
                    }
                    continue;
                }

                bool isAction = cleanLine.StartsWith("Given") || cleanLine.StartsWith("When");
                bool isResult = cleanLine.StartsWith("Then");
                bool isCont = cleanLine.StartsWith("And") || cleanLine.StartsWith("But");

                if (isAction)
                {
                    if (currentExpected.Count > 0) SaveStep();
                    currentAction.Add(cleanLine);
                }
                else if (isResult)
                {
                    currentExpected.Add(cleanLine);
                }
                else if (isCont)
                {
                    if (currentExpected.Count > 0) currentExpected.Add(cleanLine);
                    else currentAction.Add(cleanLine);
                }
            }
            
            SaveStep();

            StringBuilder xmlStepsBuilder = new StringBuilder();
            xmlStepsBuilder.Append($"<steps id=\"0\" last=\"{stepsList.Count + 1}\">");
            
            for (int i = 0; i < stepsList.Count; i++)
            {
                int stepId = i + 2;
                var step = stepsList[i];
                xmlStepsBuilder.Append($"<step id=\"{stepId}\" type=\"ActionStep\">");
                xmlStepsBuilder.Append($"<parameterizedString isformatted=\"false\">{step.Action}</parameterizedString>");
                xmlStepsBuilder.Append($"<parameterizedString isformatted=\"false\">{step.Expected}</parameterizedString>");
                xmlStepsBuilder.Append("<description/></step>");
            }
            xmlStepsBuilder.Append("</steps>");

            return (title, xmlStepsBuilder.ToString(), dataRows);
        }

        private string CreateDatasourceXml(List<Dictionary<string, string>> parametersList)
        {
            if (parametersList == null || parametersList.Count == 0) return "";

            var keys = parametersList[0].Keys.ToList();
            var xml = new StringBuilder();
            
            xml.Append("<DataSet><xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:msdata=\"urn:schemas-microsoft-com:xml-msdata\" id=\"NewDataSet\"><xs:element name=\"NewDataSet\" msdata:IsDataSet=\"true\" msdata:Locale=\"\"><xs:complexType><xs:choice minOccurs=\"0\" maxOccurs=\"unbounded\"><xs:element name=\"Table1\"><xs:complexType><xs:sequence>");
            
            foreach (var k in keys)
            {
                xml.Append($"<xs:element name=\"{k}\" type=\"xs:string\" minOccurs=\"0\" />");
            }
            
            xml.Append("</xs:sequence></xs:complexType></xs:element></xs:choice></xs:complexType></xs:element></xs:schema>");
            
            foreach (var row in parametersList)
            {
                xml.Append("<Table1>");
                foreach (var kvp in row)
                {
                    string safeValue = kvp.Value.Replace("<", "&lt;").Replace(">", "&gt;");
                    xml.Append($"<{kvp.Key}>{safeValue}</{kvp.Key}>");
                }
                xml.Append("</Table1>");
            }
            xml.Append("</DataSet>");

            return xml.ToString();
        }

        private async Task<int?> BuscarExistenteAsync(string title)
        {
            string query = $@"
                SELECT [System.Id]
                FROM WorkItems
                WHERE [System.TeamProject] = '{_projectName}'
                AND [System.WorkItemType] = 'Test Case'
                AND [System.Title] = '{title.Replace("'", "''")}'";

            try
            {
                var wiql = new Wiql { Query = query };
                var result = await _witClient.QueryByWiqlAsync(wiql);
                if (result.WorkItems.Any()) return result.WorkItems.First().Id;
                return null;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ Error buscando existente: {e.Message}");
                return null;
            }
        }

        public async Task<int?> CrearTestCaseReparadoAsync()
        {
            var gherkinRaw = GetFileContent(_gherkinPath);
            Console.WriteLine("🔄 Analizando Gherkin (Robust Parser)...");
            
            if (string.IsNullOrEmpty(gherkinRaw)) return null;

            var (title, xmlSteps, dataRows) = ParseGherkinRobust(gherkinRaw);
            var datasourceXml = CreateDatasourceXml(dataRows);
            
            var existingId = await BuscarExistenteAsync(title);

            var patchDocument = new JsonPatchDocument();
            string operationMsg = "";

            if (existingId.HasValue)
            {
                Console.WriteLine($"ℹ️ Actualizando Test Case ID: {existingId}...");
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = "/fields/Microsoft.VSTS.TCM.Steps", Value = xmlSteps });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = "/fields/Microsoft.VSTS.TCM.LocalDataSource", Value = datasourceXml });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = "Gherkin Updated" });
                
                await _witClient.UpdateWorkItemAsync(patchDocument, existingId.Value);
                operationMsg = $"✅ Test Case Actualizado: {existingId}";
            }
            else
            {
                Console.WriteLine($"🆕 Creando Test Case '{title}'...");
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/Microsoft.VSTS.TCM.Steps", Value = xmlSteps });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/Microsoft.VSTS.TCM.LocalDataSource", Value = datasourceXml });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = "Gherkin Fixed" });

                var workItem = await _witClient.CreateWorkItemAsync(patchDocument, _projectName, "Test Case");
                existingId = workItem.Id;
                operationMsg = $"✅ Test Case Creado: {existingId}";
            }
            Console.WriteLine(operationMsg);
            return existingId;
        }

        public async Task VincularAsync(int? workItemId)
        {
            if (!workItemId.HasValue) return;

            Console.WriteLine($"🔗 Vinculando Test Case {workItemId} a la Suite {_suiteId}...");
            
            try
            {
                await _testClient.AddTestCasesToSuiteAsync(_projectName, _planId, _suiteId, workItemId.Value.ToString());
                Console.WriteLine("✅ Vinculado exitosamente.");
            }
            catch (Exception e)
            {
                if (e.Message.ToLower().Contains("duplicate") || e.Message.ToLower().Contains("exists"))
                    Console.WriteLine("ℹ️ Aviso: Ya estaba vinculado (API reportó existencia).");
                else
                    Console.WriteLine($"⚠️ Posible error vinculando (puede que ya exista): {e.Message}");
            }
        }

        public async Task EjecutarTestCaseAsync(int? workItemId, string outcome = "Passed", string runComment = "Ejecutado via C# Script")
        {
            if (!workItemId.HasValue) return;
            Console.WriteLine($"🚀 Iniciando ejecución en Azure para Test Case ID: {workItemId}...");

            List<TestPoint> points = null;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    points = await _testClient.GetPointsAsync(_projectName, _planId, _suiteId, testCaseId: workItemId.ToString());
                    if (points != null && points.Count > 0) break;
                }
                catch { }
                await Task.Delay(2000);
            }

            if (points == null || points.Count == 0)
            {
                Console.WriteLine("❌ No se encontró el Test Point tras varios intentos.");
                return;
            }

            var pointId = points[0].Id;
            
            var runCreateModel = new RunCreateModel(
                name: $"Automated Run - TC {workItemId}",
                plan: new ShallowReference { Id = _planId.ToString() },
                pointIds: new[] { pointId }
            );

            try
            {
                var testRun = await _testClient.CreateTestRunAsync(runCreateModel, _projectName);
                Console.WriteLine($"🏃 Test Run creado (ID: {testRun.Id})");

                TestCaseResult resultToUpdate = null;

                for (int i = 0; i < 10; i++)
                {
                    var results = await _testClient.GetTestResultsAsync(_projectName, testRun.Id);
                    if (results != null && results.Count > 0)
                    {
                        resultToUpdate = results[0];
                        break;
                    }
                    await Task.Delay(1000);
                }

                if (resultToUpdate == null) return;

                resultToUpdate.State = "Completed";
                resultToUpdate.Outcome = outcome;
                resultToUpdate.Comment = runComment;

                await _testClient.UpdateTestResultsAsync(new[] { resultToUpdate }, _projectName, testRun.Id);

                var runUpdateModel = new RunUpdateModel(state: "Completed");
                await _testClient.UpdateTestRunAsync(runUpdateModel, _projectName, testRun.Id);

                Console.WriteLine($"✅ Ejecución marcada en Azure como: {outcome}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ Error durante la ejecución: {e.Message}");
            }
        }

        private (string Outcome, List<string> Log) ValidarYEjecutar(List<Dictionary<string, string>> dataRows)
        {
            string finalOutcome = "Passed";
            var executionLog = new List<string>();
            int passedCount = 0;

            Console.WriteLine($"\n🧪 Validando {dataRows.Count} casos de prueba del Gherkin...\n");

            for (int i = 0; i < dataRows.Count; i++)
            {
                var row = dataRows[i];
                
                // 1. EXTRACT DATA FROM GHERKIN
                string rawInput = row.ContainsKey("contenido_txt") ? row["contenido_txt"] : "";
                string expectedString = row.ContainsKey("lista_valida") ? row["lista_valida"] : "";
                string note = row.ContainsKey("Notas") ? row["Notas"] : "Sin notas";

                // 2. TRANSFORM INPUT: 
                // The Gherkin rule states ";" represents a new line. 
                // We write this content to a temporary file to simulate the input.
                string fileContent = rawInput.Replace(";", Environment.NewLine);
                string tempFilePath = Path.GetTempFileName(); // Creates a unique .tmp file on disk

                try
                {
                    File.WriteAllText(tempFilePath, fileContent);

                    // 3. EXECUTE THE LOGIC
                    // We call the actual helper using the temp file path
                    List<string> actualEmails = EmailFileHelper.LeerCorreosDesdeTxt(tempFilePath);

                    // 4. PARSE EXPECTED OUTPUT
                    // Expected list is comma-separated in Gherkin (e.g. "a@a.com,b@b.com")
                    // We split, trim, and remove empties to get a clean list
                    var expectedList = expectedString.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                    .Select(e => e.Trim())
                                                    .OrderBy(e => e) // Sort for accurate comparison
                                                    .ToList();

                    var actualListSorted = actualEmails.OrderBy(e => e).ToList();

                    // 5. COMPARE RESULTS
                    bool areEqual = Enumerable.SequenceEqual(actualListSorted, expectedList);

                    if (areEqual)
                    {
                        executionLog.Add($"✅ Row {i + 1} PASSED: {note}");
                        passedCount++;
                    }
                    else
                    {
                        finalOutcome = "Failed";
                        string actualJoined = string.Join(", ", actualListSorted);
                        executionLog.Add($"❌ Row {i + 1} FAILED: {note}");
                        executionLog.Add($"   Expected: [{expectedString}]");
                        executionLog.Add($"   Actual:   [{actualJoined}]");
                        executionLog.Add($"   Input used: {rawInput}");
                    }
                }
                catch (Exception e)
                {
                    finalOutcome = "Failed";
                    executionLog.Add($"💥 Row {i + 1} EXCEPTION: {e.Message}");
                }
                finally
                {
                    // 6. CLEANUP
                    // Always delete the temp file so we don't clutter the drive
                    if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                }
            }

            Console.WriteLine($"\n📊 Resumen: {passedCount}/{dataRows.Count} Tests Pasaron. Resultado General: {finalOutcome}");
            return (finalOutcome, executionLog);
        }

        public async Task<bool> RunAsync()
        {
            var workItemId = await CrearTestCaseReparadoAsync();
            await VincularAsync(workItemId);

            var gherkinRaw = GetFileContent(_gherkinPath);
            if (string.IsNullOrEmpty(gherkinRaw)) return false;

            var (_, _, dataRows) = ParseGherkinRobust(gherkinRaw);
            if (dataRows.Count == 0)
            {
                Console.WriteLine("⚠️ Error: No se encontraron datos de prueba en el Gherkin.");
                return false;
            }

            var (finalOutcome, executionLog) = ValidarYEjecutar(dataRows);

            if (workItemId.HasValue)
            {
                var fullComment = string.Join("\n", executionLog);
                Console.WriteLine($"🚀 Subiendo resultado a Azure: {finalOutcome}");
                await EjecutarTestCaseAsync(workItemId, outcome: finalOutcome, runComment: fullComment);
            }

            return finalOutcome == "Passed";
        }
    }
}
