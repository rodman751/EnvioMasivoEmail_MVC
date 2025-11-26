using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using Servicios.DTOs;

namespace AzureTestSyncApp
{
    public static class ExcelLogic
    {
        // A. Parse the "1001,Name..." string into DTOs
        public static List<DataUsersDto> ParseInputString(string rawData)
        {
            var list = new List<DataUsersDto>();
            if (string.IsNullOrWhiteSpace(rawData)) return list;

            var rows = rawData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var r in rows)
            {
                var parts = r.Split(','); // Keep empty entries for testing
                string cedula = parts.Length > 0 ? parts[0].Trim() : "";
                string nombre = parts.Length > 1 ? parts[1].Trim() : "";
                string email = parts.Length > 2 ? parts[2].Trim() : "";
                list.Add(new DataUsersDto { Cedula = cedula, Nombres = nombre, Email = email });
            }
            return list;
        }

        // B. Create the Temp Excel File
        public static void CreateTempExcelFile(string path, List<DataUsersDto> data)
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Hoja1"); 

                worksheet.Cell(1, 1).Value = "UNIVERSIDAD TECNICA DEL NORTE"; 
                worksheet.Cell(2, 1).Value = "Fecha: 25/11/2025"; 
                
                // --- FIX STARTS HERE ---
                // We must add content to Row 3, otherwise .RowsUsed() skips it,
                // causing .Skip(4) to eat your first data row.
                worksheet.Cell(3, 1).Value = " "; 
                // --- FIX ENDS HERE ---

                worksheet.Cell(4, 1).Value = "CÉDULA";   
                worksheet.Cell(4, 2).Value = "NOMBRES";
                worksheet.Cell(4, 3).Value = "EMAIL";    

                int row = 5;
                foreach (var item in data)
                {
                    worksheet.Cell(row, 1).SetValue(item.Cedula); 
                    worksheet.Cell(row, 2).SetValue(item.Nombres);
                    worksheet.Cell(row, 3).SetValue(item.Email);
                    row++;
                }
                workbook.SaveAs(path);
            }
        }

        // C. Validate the Result (UPDATED FOR BETTER DEBUGGING)
        public static bool ValidateScenario(List<DataUsersDto> actual, int expCount, string expFirst, string expLast, out string failureReason)
        {
            failureReason = "";

            // Helper to print the list nicely for the error message
            string GetActualListDebug()
            {
                if (actual == null || actual.Count == 0) return "[(Empty)]";
                return "[" + string.Join(", ", actual.Select(x => x.Email)) + "]";
            }

            // 1. Check Count
            if (actual.Count != expCount)
            {
                failureReason = $"Count mismatch. Expected {expCount}, but got {actual.Count}.\n   -> Found: {GetActualListDebug()}";
                return false;
            }

            if (expCount == 0) return true;

            // 2. Check First Email
            if (expFirst != "N/A" && actual.First().Email != expFirst)
            {
                failureReason = $"First Email mismatch. Expected '{expFirst}', but found '{actual.First().Email}'.\n   -> Full List: {GetActualListDebug()}";
                return false;
            }

            // 3. Check Last Email
            if (expLast != "N/A" && actual.Last().Email != expLast)
            {
                failureReason = $"Last Email mismatch. Expected '{expLast}', but found '{actual.Last().Email}'.\n   -> Full List: {GetActualListDebug()}";
                return false;
            }

            return true;
        }
    }
}
