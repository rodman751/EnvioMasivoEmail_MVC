//Servicios/DTOs/DataUsersDto.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Servicios.DTOs
{
    public class DataUsersDto
    {
        public string Cedula { get; set; } = string.Empty; // Starts as ""
        public string Nombres { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
