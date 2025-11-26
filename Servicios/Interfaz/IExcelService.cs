// IExcelService.cs

using System.Collections.Generic;
using Servicios.DTOs;

namespace Servicios.Interfaz
{
    public interface IExcelService
    {
        List<DataUsersDto> LeerUsuariosDesdeExcel(string rutaExcel);
    }
}
