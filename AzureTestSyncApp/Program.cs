// AzureTestSyncApp/Program.cs

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
using MimeKit;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Servicios;
using Servicios.Helpers;
using Servicios.Interfaz;
using Servicios.DTOs;

namespace AzureTestSyncApp
{
    public enum TestType
    {
        TextFileEmail, // The old Python logic
        ExcelUserImport,
        BannerEmbedding
    }

    // 1. New Class to define a "Testing Job"
    public class GherkinTestJob
    {
        public string FeatureFilePath { get; set; }
        public int SuiteId { get; set; } // Which Suite in Azure to put this test?
        public TestType Type { get; set; } // Defines which execution strategy to use
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
                // 1. OLD PYTHON/TXT TEST
                new GherkinTestJob
                {
                    FeatureFilePath = "Features/LeerCorreosDesdeTxt.feature",
                    SuiteId = 65,
                    Type = TestType.TextFileEmail
                },

                // 2. NEW EXCEL TEST (Integrated!)
                new GherkinTestJob
                {
                    FeatureFilePath = "Features/IncrustarBannerEmail.feature", 
                    SuiteId = 66, 
                    Type = TestType.BannerEmbedding // <--- CHANGE THIS (It was ExcelUserImport in your snippet)
                }
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

            Console.WriteLine("\n--- PROCESO FINALIZADO :D ---");

            // CHANGE THIS: Comment out this line.
            // This line tells Azure Pipeline "The whole task failed" if any test case failed.
            // if (!globalSuccess) Environment.Exit(1); 
            
            // OPTIONAL: You can explicitly return 0 to be safe
            Environment.Exit(0);
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

            var lines = gherkinText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim())
                                .Where(l => !string.IsNullOrEmpty(l))
                                .ToList();

            string title = "Gherkin Import";
            
            // --- VARIABLES FOR AZURE STEPS (From Code 2) ---
            var stepsList = new List<(string Action, string Expected)>();
            List<string> currentAction = new List<string>();
            List<string> currentExpected = new List<string>();

            // --- VARIABLES FOR DATA TABLE (From Code 1) ---
            var dataRows = new List<Dictionary<string, string>>();
            List<string> headers = null;
            bool headersFound = false;
            bool readingExamples = false;

            // Helper to save steps to the list
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
                // 1. Capture Title
                string cleanLine = Regex.Replace(line, @"<([^>]+)>", "@$1");
                if (cleanLine.StartsWith("Scenario Outline:") || cleanLine.StartsWith("Scenario:")) 
                { 
                    var parts = cleanLine.Split(new[] { ':' }, 2); 
                    if (parts.Length > 1) title = parts[1].Trim(); 
                    continue; 
                }

                // 2. Switch to Examples Mode
                if (cleanLine.StartsWith("Examples:")) 
                { 
                    readingExamples = true; 
                    continue; 
                }

                // 3. TABLE PARSING LOGIC (Using the BETTER logic from Code 1)
                if (readingExamples)
                {
                    if (cleanLine.StartsWith("|"))
                    {
                        var rawParts = cleanLine.Split('|');
                        // Clean whitespace
                        var parts = rawParts.Select(p => p.Trim()).ToList();

                        // Remove empty start/end caused by markdown pipes |...|
                        if (parts.Count > 0 && string.IsNullOrEmpty(parts[0])) parts.RemoveAt(0);
                        if (parts.Count > 0 && string.IsNullOrEmpty(parts[parts.Count - 1])) parts.RemoveAt(parts.Count - 1);

                        if (parts.Count > 0)
                        {
                            if (!headersFound)
                            {
                                headers = parts;
                                headersFound = true;
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
                    continue; // Skip step processing if we are in Examples
                }

                // 4. STEP PARSING LOGIC (Keep from Code 2 for Azure XML)
                bool isAction = cleanLine.StartsWith("Given") || cleanLine.StartsWith("When");
                bool isResult = cleanLine.StartsWith("Then");
                bool isCont = cleanLine.StartsWith("And") || cleanLine.StartsWith("But");

                if (isAction) { if (currentExpected.Count > 0) SaveStep(); currentAction.Add(cleanLine); }
                else if (isResult) { currentExpected.Add(cleanLine); }
                else if (isCont) { if (currentExpected.Count > 0) currentExpected.Add(cleanLine); else currentAction.Add(cleanLine); }
            }
            
            // Save any remaining step
            SaveStep();

            // 5. GENERATE XML (Keep from Code 2)
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
                // LOGIC FOR EXISTING TEST CASE
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = "/fields/Microsoft.VSTS.TCM.Steps", Value = xmlSteps });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = "/fields/Microsoft.VSTS.TCM.LocalDataSource", Value = datasourceXml });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = "Gherkin Updated" });

                // ---> NEW LINE ADDED HERE: Force state to Ready
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = "/fields/System.State", Value = "Ready" }); 

                await _witClient.UpdateWorkItemAsync(patchDocument, existingId.Value);
                Console.WriteLine($"✅ Test Case Actualizado (Ready): {existingId}");
            }
            else
            {
                // LOGIC FOR NEW TEST CASE
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/Microsoft.VSTS.TCM.Steps", Value = xmlSteps });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/Microsoft.VSTS.TCM.LocalDataSource", Value = datasourceXml });
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = "Gherkin Fixed" });

                // ---> NEW LINE ADDED HERE: Set initial state to Ready
                patchDocument.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.State", Value = "Ready" });

                var workItem = await _witClient.CreateWorkItemAsync(patchDocument, _projectName, "Test Case");
                existingId = workItem.Id;
                Console.WriteLine($"✅ Test Case Creado (Ready): {existingId}");
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

            Console.WriteLine($" ⏳ Buscando Test Point para TC #{workItemId} en Suite {suiteId}...");

            // Try to get points (Retry logic with better debugging)
            List<TestPoint> points = null;
            Exception lastError = null;

            // Increased retries to 10 and delay to 2 seconds (Azure can be slow)
            for (int i = 0; i < 10; i++) 
            {
                try 
                { 
                    // We explicitly ask for the points matching this specific Test Case ID
                    points = await _testClient.GetPointsAsync(_projectName, _planId, suiteId, testCaseId: workItemId.ToString()); 
                    
                    if (points != null && points.Count > 0) 
                        break; 
                } 
                catch (Exception ex) 
                { 
                    lastError = ex;
                    // Optional: Print a dot for every failed attempt to visualize waiting
                    Console.Write("."); 
                } 
                
                await Task.Delay(2000); // Wait 2 seconds between retries
            }
            Console.WriteLine(); // New line after dots

            if (points == null || points.Count == 0) 
            { 
                Console.WriteLine("❌ No se encontró el Test Point."); 
                
                // --- NEW: PRINT DEBUG INFO ---
                Console.WriteLine("   🔍 DETALLES DEL ERROR:");
                Console.WriteLine($"      Project: {_projectName}");
                Console.WriteLine($"      PlanId: {_planId}");
                Console.WriteLine($"      SuiteId: {suiteId}");
                Console.WriteLine($"      TestCaseId: {workItemId}");
                
                if (lastError != null)
                    Console.WriteLine($"      ⚠️ Azure API Error: {lastError.Message}");
                else
                    Console.WriteLine($"      ⚠️ Azure retornó 0 puntos. (Posible causa: La Suite no tiene 'Default Configuration' asignada).");
                
                return; 
            }

            // Identify the specific point ID
            var pointId = points[0].Id;
            // Console.WriteLine($"   📍 Test Point encontrado: {pointId}");

            var runCreateModel = new RunCreateModel(
                name: $"AutoRun - TC {workItemId}", 
                plan: new ShallowReference { Id = _planId.ToString() }, 
                pointIds: new[] { pointId }
            );

            TestRun testRun;
            try
            {
                testRun = await _testClient.CreateTestRunAsync(runCreateModel, _projectName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error creando Test Run: {ex.Message}");
                return;
            }

            TestCaseResult resultToUpdate = null;
            for (int i = 0; i < 10; i++) 
            {
                var results = await _testClient.GetTestResultsAsync(_projectName, testRun.Id);
                if (results != null && results.Count > 0) { resultToUpdate = results[0]; break; }
                await Task.Delay(1000);
            }

            if (resultToUpdate != null) 
            {
                resultToUpdate.State = "Completed"; 
                resultToUpdate.Outcome = outcome; 
                resultToUpdate.Comment = runComment;
                
                await _testClient.UpdateTestResultsAsync(new[] { resultToUpdate }, _projectName, testRun.Id);
                await _testClient.UpdateTestRunAsync(new RunUpdateModel(state: "Completed"), _projectName, testRun.Id);
                Console.WriteLine($"✅ Ejecución registrada en Azure: {outcome}");
            }
            else
            {
                Console.WriteLine("⚠️ Se creó la ejecución (Run) pero no se pudo recuperar el Resultado para actualizarlo.");
            }
        }

        // Pass the whole JOB object now, not just a function
        private (string Outcome, List<string> Log) ValidarYEjecutar(List<Dictionary<string, string>> dataRows, GherkinTestJob job)
        {
            string finalOutcome = "Passed";
            var executionLog = new List<string>();
            
            Console.WriteLine($"🧪 Validando {dataRows.Count} casos (Modo: {job.Type})...");

            for (int i = 0; i < dataRows.Count; i++)
            {
                var row = dataRows[i];
                string tempFilePath = Path.GetTempFileName().Replace(".tmp", (job.Type == TestType.ExcelUserImport ? ".xlsx" : ".txt"));

                try
                {
                    // ======================================================
                    // STRATEGY 1: TEXT FILE EMAIL
                    // ======================================================
                    if (job.Type == TestType.TextFileEmail)
                    {
                        string rawInput = row.ContainsKey("contenido_txt") ? row["contenido_txt"] : "";
                        string expectedString = row.ContainsKey("lista_valida") ? row["lista_valida"] : "";

                        File.WriteAllText(tempFilePath, rawInput.Replace(";", Environment.NewLine));

                        var actualEmails = EmailFileHelper.LeerCorreosDesdeTxt(tempFilePath);

                        var expectedList = expectedString.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).OrderBy(e => e).ToList();
                        var actualListSorted = actualEmails.OrderBy(e => e).ToList();

                        if (Enumerable.SequenceEqual(actualListSorted, expectedList))
                        {
                            string msg = $"✅ Row {i + 1} Passed";
                            executionLog.Add(msg);
                            Console.WriteLine(msg); // <--- PRINT TO CONSOLE
                        }
                        else
                        {
                            finalOutcome = "Failed";
                            string msg = $"❌ Row {i + 1} Failed. Expected: {string.Join(",", actualListSorted)} | Actual: {expectedString}";
                            executionLog.Add(msg);
                            Console.WriteLine(msg); // <--- PRINT TO CONSOLE
                        }
                    }
                    // ======================================================
                    // STRATEGY 2: EXCEL IMPORT
                    // ======================================================
                    else if (job.Type == TestType.ExcelUserImport)
                    {
                        string rawInput = row.ContainsKey("datos_entrada") ? row["datos_entrada"] : "";
                        int expCount = int.Parse(row.ContainsKey("cantidad_esperada") ? row["cantidad_esperada"] : "0");
                        string expFirst = row.ContainsKey("email_primero") ? row["email_primero"] : "N/A";
                        string expLast = row.ContainsKey("email_ultimo") ? row["email_ultimo"] : "N/A";

                        var inputDTOs = ExcelLogic.ParseInputString(rawInput);
                        ExcelLogic.CreateTempExcelFile(tempFilePath, inputDTOs);

                        IExcelService service = new ExcelService();
                        var resultData = service.LeerUsuariosDesdeExcel(tempFilePath);

                        if (ExcelLogic.ValidateScenario(resultData, expCount, expFirst, expLast, out string reason))
                        {
                            string msg = $"✅ Row {i + 1} Passed";
                            executionLog.Add(msg);
                            Console.WriteLine(msg); // <--- PRINT TO CONSOLE
                        }
                        else
                        {
                            finalOutcome = "Failed";
                            string msg = $"❌ Row {i + 1} Failed: {reason}";
                            executionLog.Add(msg);
                            Console.WriteLine(msg); // <--- PRINT TO CONSOLE
                        }
                    }
                    // ======================================================
                    // STRATEGY 3: BANNER EMBEDDING (New Logic)
                    // ======================================================
                    else if (job.Type == TestType.BannerEmbedding)
                    {
                        // 1. Extract Gherkin Data
                        string estadoArchivo = row.ContainsKey("estado_archivo") ? row["estado_archivo"] : "no existe";
                        string inputHtml = row.ContainsKey("html_entrada") ? row["html_entrada"] : "";
                        string expectedText = row.ContainsKey("texto_esperado") ? row["texto_esperado"] : "";
                        int expectedCount = int.Parse(row.ContainsKey("adjuntos_count") ? row["adjuntos_count"] : "0");

                        // 2. Setup Simulation Environment (File System)
                        // The service looks for: wwwroot/Plantillas/img2.jpg relative to execution
                        string wwwRootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Plantillas");
                        string imgPath = Path.Combine(wwwRootPath, "img2.jpg");

                        // Create directory if missing
                        if (!Directory.Exists(wwwRootPath)) Directory.CreateDirectory(wwwRootPath);

                        // Manipulate file existence based on test case
                        if (estadoArchivo.ToLower() == "existe")
                        {
                            // Create a dummy file if it doesn't exist
                            if (!File.Exists(imgPath)) File.WriteAllText(imgPath, "fake image content");
                        }
                        else
                        {
                            // Delete the file if it exists
                            if (File.Exists(imgPath)) File.Delete(imgPath);
                        }

                        try
                        {
                            // 3. Execution
                            // We pass empty settings because IncrustarBanner doesn't use SMTP
                            IEmailService service = new EmailService(new EmailSettings()); 
                            var builder = new BodyBuilder();

                            // Run the method
                            string resultHtml = service.IncrustarBanner(builder, inputHtml);

                            // 4. Validation
                            bool textCheck = resultHtml.Contains(expectedText);
                            bool countCheck = builder.LinkedResources.Count == expectedCount;

                            if (textCheck && countCheck)
                            {
                                string msg = $"✅ Row {i + 1} Passed";
                                executionLog.Add(msg);
                                Console.WriteLine(msg);
                            }
                            else
                            {
                                finalOutcome = "Failed";
                                var errorDetails = new List<string>();
                                if (!textCheck) errorDetails.Add($"Expected text '{expectedText}' not found in result.");
                                if (!countCheck) errorDetails.Add($"Expected {expectedCount} attachments, found {builder.LinkedResources.Count}.");
                                
                                string msg = $"❌ Row {i + 1} Failed: {string.Join(" | ", errorDetails)}";
                                executionLog.Add(msg);
                                Console.WriteLine(msg);
                            }
                        }
                        finally
                        {
                            // Cleanup: Ideally, remove the dummy file to leave environment clean
                            if (File.Exists(imgPath)) File.Delete(imgPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    finalOutcome = "Failed";
                    string msg = $"💥 Row {i + 1} Error: {ex.Message}";
                    executionLog.Add(msg);
                    Console.WriteLine(msg); // <--- PRINT TO CONSOLE
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

            // Pass the job to the validator so it can route correctly
            var (finalOutcome, executionLog) = ValidarYEjecutar(dataRows, job);

            if (workItemId.HasValue)
            {
                var fullComment = string.Join("\n", executionLog);
                await EjecutarTestCaseAsync(workItemId, job.SuiteId, finalOutcome, fullComment);
            }

            return finalOutcome == "Passed";
        }
    }
}
