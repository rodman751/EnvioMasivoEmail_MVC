// Desactiva las advertencias estrictas de nulos para simplificar la compilación
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

namespace AzureTestSyncApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var baseDir = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.Parent?.Parent?.Parent?.FullName 
                          ?? AppDomain.CurrentDomain.BaseDirectory;
            
            LoadEnvFile(Path.Combine(baseDir, ".env"));

            var sync = new AzureTestSync();
            await sync.RunAsync();
        }

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

        // --- CORE LOGIC TO TEST (Static) ---
        public static List<string> LeerCorreosDesdeTxt(string contenidoRaw)
        {
            var listaCorreos = new List<string>();
            var regexCorreo = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

            // 1. Check if the content string is empty
            if (string.IsNullOrWhiteSpace(contenidoRaw))
            {
                // Console.WriteLine("⚠️ La cadena de texto está vacía."); // Optional logging
                return listaCorreos;
            }

            // 2. Split the string by common delimiters (semicolon, comma, new lines)
            var delimitadores = new[] { ';', ',', '\r', '\n' };
            var correos = contenidoRaw.Split(delimitadores, StringSplitOptions.RemoveEmptyEntries);

            // 3. Iterate through the array
            foreach (var item in correos)
            {
                var correo = item.Trim(); 

                if (!string.IsNullOrEmpty(correo) && regexCorreo.IsMatch(correo))
                {
                    listaCorreos.Add(correo);
                }
            }

            return listaCorreos;
        }
    }

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
            // Point this to your actual .feature file location
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

        // --- INTEGRATED ROBUST PARSER (From Code A) ---
        private (string Title, string XmlSteps, List<Dictionary<string, string>> DataRows) ParseGherkinRobust(string gherkinText)
        {
            if (string.IsNullOrWhiteSpace(gherkinText))
                return ("Error", "", new List<Dictionary<string, string>>());

            var lines = gherkinText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(l => l.Trim())
                                   .Where(l => !string.IsNullOrEmpty(l))
                                   .ToList();

            string title = "Gherkin Import";
            
            // Helper class for internal step logic
            var stepsList = new List<(string Action, string Expected)>();
            
            var dataRows = new List<Dictionary<string, string>>();
            List<string> headers = null;
            bool readingExamples = false;

            List<string> currentAction = new List<string>();
            List<string> currentExpected = new List<string>();

            // Local function to save current buffer to step list
            void SaveStep()
            {
                if (currentAction.Count > 0 || currentExpected.Count > 0)
                {
                    stepsList.Add((string.Join("\n", currentAction), string.Join("\n", currentExpected)));
                    currentAction.Clear();
                    currentExpected.Clear();
                }
            }

            // Azure Format Helper: Replaces <Var> with @Var
            string AzureFormat(string text)
            {
                return Regex.Replace(text, @"<([^>]+)>", "@$1");
            }

            foreach (var line in lines)
            {
                string cleanLine = AzureFormat(line);

                // Detect Title
                if (cleanLine.StartsWith("Scenario Outline:") || cleanLine.StartsWith("Scenario:"))
                {
                    var parts = cleanLine.Split(new[] { ':' }, 2);
                    if (parts.Length > 1) title = parts[1].Trim();
                    continue;
                }

                // Detect Examples Table Start
                if (cleanLine.StartsWith("Examples:"))
                {
                    readingExamples = true;
                    continue;
                }

                // Process Table
                if (readingExamples)
                {
                    if (cleanLine.StartsWith("|"))
                    {
                        var rawSegments = cleanLine.Split('|');
                        // Clean up the split artifacts
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

                // Gherkin Logic (Given/When/Then)
                bool isAction = cleanLine.StartsWith("Given") || cleanLine.StartsWith("When");
                bool isResult = cleanLine.StartsWith("Then");
                bool isCont = cleanLine.StartsWith("And") || cleanLine.StartsWith("But");

                if (isAction)
                {
                    // If we were building an Expected result, that step is done. Save it.
                    if (currentExpected.Count > 0)
                    {
                        SaveStep();
                    }
                    currentAction.Add(cleanLine);
                }
                else if (isResult)
                {
                    currentExpected.Add(cleanLine);
                }
                else if (isCont)
                {
                    // Attach 'And' to whichever block is active
                    if (currentExpected.Count > 0) currentExpected.Add(cleanLine);
                    else currentAction.Add(cleanLine);
                }
            }
            
            // Save final step
            SaveStep();

            // Generate XML Steps
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
                    // Escape basic XML characters
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
            
            // Try to find existing test case by title
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

            // Retry loop to find points (sometimes API lags after linking)
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

                // Wait for the result object to be created
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

            Console.WriteLine("🧪 Validando lógica localmente...");

            for (int i = 0; i < dataRows.Count; i++)
            {
                var row = dataRows[i];
                
                // UPDATED: Mapping to the column names from your Gherkin snippet (Input A)
                var inputVal = row.ContainsKey("contenido_txt") ? row["contenido_txt"] : "";
                var expectedVal = row.ContainsKey("lista_valida") ? row["lista_valida"] : "";

                try
                {
                    // Execute Static Method
                    var listResult = Program.LeerCorreosDesdeTxt(inputVal);
                    var actualVal = listResult != null ? string.Join(",", listResult) : "";

                    if (actualVal != expectedVal)
                    {
                        finalOutcome = "Failed";
                        executionLog.Add($"[FAIL] Row {i + 1}: Input '{inputVal}' -> Got '{actualVal}', Expected '{expectedVal}'");
                    }
                    else
                    {
                        executionLog.Add($"[PASS] Row {i + 1}: Input '{inputVal}' -> '{actualVal}'");
                    }
                }
                catch (Exception e)
                {
                    finalOutcome = "Failed";
                    executionLog.Add($"[ERROR] Row {i + 1}: Crashed on '{inputVal}'. Error: {e.Message}");
                }
            }

            return (finalOutcome, executionLog);
        }

        public async Task RunAsync()
        {
            // 1. Create or Update Test Case based on Gherkin
            var workItemId = await CrearTestCaseReparadoAsync();
            
            // 2. Link to Suite
            await VincularAsync(workItemId);

            // 3. Run Validation locally to decide Pass/Fail
            var gherkinRaw = GetFileContent(_gherkinPath);
            if (string.IsNullOrEmpty(gherkinRaw)) return;

            var (_, _, dataRows) = ParseGherkinRobust(gherkinRaw);
            var (finalOutcome, executionLog) = ValidarYEjecutar(dataRows);

            // 4. Upload Result to Azure
            if (workItemId.HasValue)
            {
                var fullComment = string.Join("\n", executionLog);
                Console.WriteLine($"🚀 Subiendo resultado a Azure: {finalOutcome}");
                await EjecutarTestCaseAsync(workItemId, outcome: finalOutcome, runComment: fullComment);
            }
        }
    }
}