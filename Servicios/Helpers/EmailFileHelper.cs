//Servicios/Helpers/EmailFileHelper.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Servicios.Helpers
{
    public static class EmailFileHelper
    {
        // Regex compilada para validar correos antes de agregarlos
        private static readonly Regex RegexCorreo = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        public static List<string> LeerCorreosDesdeTxt(string rutaTxt)
        {
            var listaCorreos = new List<string>();

            if (!File.Exists(rutaTxt))
            {
                Console.WriteLine($"El archivo no existe: {rutaTxt}");
                return listaCorreos;
            }

            foreach (var linea in File.ReadLines(rutaTxt))
            {
                var correo = linea.Trim();
                if (!string.IsNullOrEmpty(correo) && RegexCorreo.IsMatch(correo))
                {
                    listaCorreos.Add(correo);
                }
                else
                {
                    Console.WriteLine($"Correo inválido ignorado: {correo}");
                }
            }
            return listaCorreos;
        }
    }
}
