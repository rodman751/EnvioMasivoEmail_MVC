using ClosedXML.Excel;
using Servicios.DTOs;
using Servicios.Interfaz;
using System.Collections.Generic;
using System.Linq;

namespace Servicios
{
    public class ExcelService : IExcelService
    {
        public List<DataUsersDto> LeerUsuariosDesdeExcel(string rutaExcel)
        {
            var lista = new List<DataUsersDto>();

            // Validate file before opening to avoid crashes when the path is wrong
            if (!System.IO.File.Exists(rutaExcel))
            {
                return lista;
            }

            using var workbook = new XLWorkbook(rutaExcel);
            var worksheet = workbook.Worksheet(1); // Primera hoja
            var usedRange = worksheet.RangeUsed();

            if (usedRange == null)
            {
                return lista;
            }

            var rows = usedRange.RowsUsed().Skip(4); // Salta encabezado (skip 4 rows)

            foreach (var row in rows)
            {
                if (row.CellsUsed().Count() >= 3)
                {
                    var usuario = new DataUsersDto
                    {
                        Cedula = row.Cell(1).GetString(),
                        Nombres = row.Cell(2).GetString(),
                        Email = row.Cell(3).GetString()
                    };
                    lista.Add(usuario);
                }
            }
            return lista;
        }
    }
}
