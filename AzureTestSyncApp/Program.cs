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
    // 1. New Class to define a "Testing Job"
    public class GherkinTestJob
    {
        public string FeatureFilePath { get; set; }
        public int SuiteId { get; set; } // Which Suite in Azure to put this test?
        // This holds the function to run. It takes a file path (string) and returns a List<string>
        public Func<string, List<string>> TestFunction { get; set; } 
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("--- INICIANDO SISTEMA MULTI-VALIDACIÓN GHERKIN ---");

            // --- LOAD ENV ---
            string envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            if (!File.Exists(envPath))
                envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", ".env");
            LoadEnvFile(envPath);
            // ----------------

            // 2. CONFIGURATION AREA: Define your Feature Files and their Logic here
            var jobs = new List<GherkinTestJob>
            {
                // Job 1: Existing Logic (Reading Emails from TXT)
                new GherkinTestJob 
                { 
                    FeatureFilePath = Environment.GetEnvironmentVariable("GHERKIN_PATH") ?? "python/features/mapeo_canales.feature",
                    SuiteId = int.Parse(Environment.GetEnvironmentVariable("SUITE_ID") ?? "9"),
                    // We wrap the helper call in a lambda
                    TestFunction = (tempPath) => EmailFileHelper.LeerCorreosDesdeTxt(tempPath)
                },

                // Example Job 2: Add your new feature here in the future
                /*
                new GherkinTestJob 
                { 
                    FeatureFilePath = "python/features/nuevo_filtro.feature",
                    SuiteId = 10, // A different suite ID if needed
                    TestFunction = (tempPath) => OtraClaseHelper.MiNuevaFuncion(tempPath)
                }
                */
            };

            bool globalSuccess = true;

            try 
            {
                // Initialize the sync engine once
                var tester = new AzureTestSync();

                // 3. LOOP through the jobs
                foreach (var job in jobs)
                {
                    Console.WriteLine($"\n📁 Procesando: {Path.GetFileName(job.FeatureFilePath)}");
                    bool jobResult = await tester.ProcessJobAsync(job);
                    
                    if (!jobResult) globalSuccess = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Error Crítico Global: {ex.Message}");
                globalSuccess = false;
            }

            Console.WriteLine("\n--- PROCESO FINALIZADO ---");
            if (!globalSuccess) Environment.Exit(1);
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
            catch (Exception exc) { Console.WriteLine($"⚠️ No se pudo cargar el archivo .env: {exc.Message}"); }
        }
    }

    public class AzureTestSync
    {
        private readonly string _pat;
        private readonly string _orgUrl;
        private readonly string _projectName;
        private readonly int _planId;

        private WorkItemTrackingHttpClient _witClient;
        private TestManagementHttpClient _testClient;

        public AzureTestSync()
        {
            _pat = GetEnvVar("PERSONAL_ACCESS_TOKEN", required: true);
            _orgUrl = GetEnvVar("ORGANIZATION_URL", "https://dev.azure.com/UTN-FabricaSoftware-2025");
            _projectName = GetEnvVar("PROJECT_NAME", "CorreosMasivos");
            _planId = int.Parse(GetEnvVar("PLAN_ID", "6"));

            Connect();
        }

        private static string GetEnvVar(string key, string defaultValue = null, bool required = false)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (required && string.IsNullOrEmpty(value)) throw new ArgumentException($"Falta la variable: {key}");
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
            if (!File.Exists(filePath)) { Console.WriteLine($"❌ Archivo no encontrado: {filePath}"); return null; }
            return File.ReadAllText(filePath, Encoding.UTF8);
        }

        // --- Gherkin Parsing Logic (Same as before) ---
        private (string Title, string XmlSteps, List<Dictionary<string, string>> DataRows) ParseGherkinRobust(string gherkinText)
        {
            if (string.IsNullOrWhiteSpace(gherkinText)) return ("Error", "", new List<Dictionary<string, string>>());

            var lines = gherkinText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList();
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
                    currentAction.Clear(); currentExpected.Clear();
                }
            }

            foreach (var line in lines)
            {
                string cleanLine = Regex.Replace(line, @"<([^>]+)>", "@$1");
                if (cleanLine.StartsWith("Scenario Outline:") || cleanLine.StartsWith("Scenario:")) { var parts = cleanLine.Split(new[] { ':' }, 2); if (parts.Length > 1) title = parts[1].Trim(); continue; }
                if (cleanLine.StartsWith("Examples:")) { readingExamples = true; continue; }

                if (readingExamples)
                {
                    if (cleanLine.StartsWith("|"))
                    {
                        var rawSegments = cleanLine.Split('|');
                        var parts = rawSegments.Skip(1).Take(rawSegments.Length - 2).Select(p => p.Trim()).ToList();
                        if (parts.Count > 0)
                        {
                            if (headers == null) headers = parts;
                            else
                            {
                                var row = new Dictionary<string, string>();
                                for (int i = 0; i < Math.Min(headers.Count, parts.Count); i++) row[headers[i]] = parts[i];
                                dataRows.Add(row);
                            }
                        }
                    }
                    continue;
                }

                bool isAction = cleanLine.StartsWith("Given") || cleanLine.StartsWith("When");
                bool isResult = cleanLine.StartsWith("Then");
                bool isCont = cleanLine.StartsWith("And") || cleanLine.StartsWith("But");

                if (isAction) { if (currentExpected.Count > 0) SaveStep(); currentAction.Add(cleanLine); }
                else if (isResult) { currentExpected.Add(cleanLine); }
                else if (isCont) { if (currentExpected.Count > 0) currentExpected.Add(cleanLine); else currentAction.Add(cleanLine); }
            }
            SaveStep();

            StringBuilder xmlStepsBuilder = new StringBuilder();
            xmlStepsBuilder.Append($"<steps id=\"0\" last=\"{stepsList.Count + 1}\">");
            for (int i = 0; i < stepsList.Count; i++)
            {
                xmlStepsBuilder.Append($"<step id=\"{i + 2}\" type=\"ActionStep\"><parameterizedString isformatted=\"false\">{stepsList[i].Action}</parameterizedString><parameterizedString isformatted=\"false\">{stepsList[i].Expected}</parameterizedString><description/></step>");
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
            foreach (var k in keys) xml.Append($"<xs:element name=\"{k}\" type=\"xs:string\" minOccurs=\"0\" />");
            xml.Append("</xs:sequence></xs:complexType></xs:element></xs:choice></xs:complexType></xs:element></xs:schema>");
            foreach (var row in parametersList)
            {
                xml.Append("<Table1>");
                foreach (var kvp in row) xml.Append($"<{kvp.Key}>{kvp.Value.Replace("<", "&lt;").Replace(">", "&gt;")}</{kvp.Key}>");
                xml.Append("</Table1>");
            }
            xml.Append("</DataSet>");
            return xml.ToString();
        }

        // --- Azure Operations ---

        private async Task<int?> BuscarExistenteAsync(string title)
        {
            string query = $@"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{_projectName}' AND [System.WorkItemType] = 'Test Case' AND [System.Title] = '{title.Replace("'", "''")}'";
            try { var result = await _witClient.QueryByWiqlAsync(new Wiql { Query = query }); return result.WorkItems.FirstOrDefault()?.Id; }
            catch { return null; }
        }

        public async Task<int?> CrearTestCaseReparadoAsync(string gherkinPath)
        {
            var gherkinRaw = GetFileContent(gherkinPath);
            if (string.IsNullOrEmpty(gherkinRaw)) return null;

            var (title, xmlSteps, dataRows) = ParseGherkinRobust(gherkinRaw);
            var datasourceXml = CreateDatasourceXml(dataRows);
            var existingId = await BuscarExistenteAsync(title);
            var patchDocument = new JsonPatchDocument();

            if (existingId.HasValue)
            {
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = "/fields/Microsoft.VSTS.TCM.Steps", Value = xmlSteps });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = "/fields/Microsoft.VSTS.TCM.LocalDataSource", Value = datasourceXml });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = "Gherkin Updated" });
                await _witClient.UpdateWorkItemAsync(patchDocument, existingId.Value);
                Console.WriteLine($"✅ Test Case Actualizado: {existingId}");
            }
            else
            {
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/Microsoft.VSTS.TCM.Steps", Value = xmlSteps });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/Microsoft.VSTS.TCM.LocalDataSource", Value = datasourceXml });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = "Gherkin Fixed" });
                var workItem = await _witClient.CreateWorkItemAsync(patchDocument, _projectName, "Test Case");
                existingId = workItem.Id;
                Console.WriteLine($"✅ Test Case Creado: {existingId}");
            }
            return existingId;
        }

        public async Task VincularAsync(int? workItemId, int suiteId)
        {
            if (!workItemId.HasValue) return;
            try
            {
                await _testClient.AddTestCasesToSuiteAsync(_projectName, _planId, suiteId, workItemId.Value.ToString());
                Console.WriteLine("✅ Vinculado exitosamente.");
            }
            catch (Exception e)
            {
                if (!e.Message.ToLower().Contains("duplicate")) Console.WriteLine($"⚠️ Error vinculando: {e.Message}");
            }
        }

        public async Task EjecutarTestCaseAsync(int? workItemId, int suiteId, string outcome, string runComment)
        {
            if (!workItemId.HasValue) return;
            
            // Try to get points (Retry logic included)
            List<TestPoint> points = null;
            for (int i = 0; i < 5; i++) {
                try { points = await _testClient.GetPointsAsync(_projectName, _planId, suiteId, testCaseId: workItemId.ToString()); if (points != null && points.Count > 0) break; } catch { } await Task.Delay(1000);
            }

            if (points == null || points.Count == 0) { Console.WriteLine("❌ No se encontró el Test Point."); return; }

            var runCreateModel = new RunCreateModel(name: $"AutoRun - TC {workItemId}", plan: new ShallowReference { Id = _planId.ToString() }, pointIds: new[] { points[0].Id });
            var testRun = await _testClient.CreateTestRunAsync(runCreateModel, _projectName);

            TestCaseResult resultToUpdate = null;
            for (int i = 0; i < 10; i++) {
                var results = await _testClient.GetTestResultsAsync(_projectName, testRun.Id);
                if (results != null && results.Count > 0) { resultToUpdate = results[0]; break; }
                await Task.Delay(1000);
            }

            if (resultToUpdate != null) {
                resultToUpdate.State = "Completed"; resultToUpdate.Outcome = outcome; resultToUpdate.Comment = runComment;
                await _testClient.UpdateTestResultsAsync(new[] { resultToUpdate }, _projectName, testRun.Id);
                await _testClient.UpdateTestRunAsync(new RunUpdateModel(state: "Completed"), _projectName, testRun.Id);
                Console.WriteLine($"✅ Ejecución registrada: {outcome}");
            }
        }

        // 4. MODIFIED: Accepts the Job's specific Test Function
        private (string Outcome, List<string> Log) ValidarYEjecutar(List<Dictionary<string, string>> dataRows, Func<string, List<string>> testFunction)
        {
            string finalOutcome = "Passed";
            var executionLog = new List<string>();
            int passedCount = 0;

            Console.WriteLine($"🧪 Validando {dataRows.Count} casos...");

            for (int i = 0; i < dataRows.Count; i++)
            {
                var row = dataRows[i];
                string rawInput = row.ContainsKey("contenido_txt") ? row["contenido_txt"] : "";
                string expectedString = row.ContainsKey("lista_valida") ? row["lista_valida"] : "";
                
                string tempFilePath = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(tempFilePath, rawInput.Replace(";", Environment.NewLine));

                    // DYNAMIC EXECUTION HERE
                    List<string> actualEmails = testFunction(tempFilePath);

                    var expectedList = expectedString.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).OrderBy(e => e).ToList();
                    var actualListSorted = actualEmails.OrderBy(e => e).ToList();

                    if (Enumerable.SequenceEqual(actualListSorted, expectedList))
                    {
                        executionLog.Add($"✅ Row {i + 1} Passed");
                        passedCount++;
                    }
                    else
                    {
                        finalOutcome = "Failed";
                        executionLog.Add($"❌ Row {i + 1} Failed. Expected: [{expectedString}] vs Actual: [{string.Join(", ", actualListSorted)}]");
                    }
                }
                catch (Exception e)
                {
                    finalOutcome = "Failed";
                    executionLog.Add($"💥 Row {i + 1} Exception: {e.Message}");
                }
                finally
                {
                    if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                }
            }
            return (finalOutcome, executionLog);
        }

        // 5. MODIFIED: Takes the Job object as input
        public async Task<bool> ProcessJobAsync(GherkinTestJob job)
        {
            var workItemId = await CrearTestCaseReparadoAsync(job.FeatureFilePath);
            await VincularAsync(workItemId, job.SuiteId);

            var gherkinRaw = GetFileContent(job.FeatureFilePath);
            if (string.IsNullOrEmpty(gherkinRaw)) return false;

            var (_, _, dataRows) = ParseGherkinRobust(gherkinRaw);
            if (dataRows.Count == 0) return false;

            // Pass the job's function to the validator
            var (finalOutcome, executionLog) = ValidarYEjecutar(dataRows, job.TestFunction);

            if (workItemId.HasValue)
            {
                var fullComment = string.Join("\n", executionLog);
                await EjecutarTestCaseAsync(workItemId, job.SuiteId, finalOutcome, fullComment);
            }

            return finalOutcome == "Passed";
        }
    }
}